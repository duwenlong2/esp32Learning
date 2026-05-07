using GestureLab.Ble;
using Microsoft.UI.Xaml;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace GestureLab;

public sealed partial class BleSdkSampleWindow : Window
{
    private const double ImuValidationTimeoutSeconds = 6.0;

    private readonly BleGestureClient _bleClient = new();
    private readonly GestureDataRecorder _recorder = new();
    private readonly ObservableCollection<string> _logItems = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private CancellationTokenSource? _bleConnectCts;
    private CancellationTokenSource? _imuValidationCts;
    private long _lastPoseUiTicks;
    private int _imuRawPacketCount;
    private int _imuParsedPacketCount;
    private string _lastImuPayloadPreview = "<none>";

    public BleSdkSampleWindow()
    {
        InitializeComponent();
        LogList.ItemsSource = _logItems;

        string recordPath = _recorder.StartSession("blesdk_auto");
        AppendLog($"Record started: {recordPath}");

        // SDK 初始化时必须先注册事件，再启动重连。
        _bleClient.Log += line => EnqueueUi(() => AppendLog($"BLE: {line}"));
        _bleClient.ConnectionStateChanged += state => EnqueueUi(() => HandleConnectionState(state));
        _bleClient.StatusPayloadReceived += payload => EnqueueUi(() =>
        {
            FirmwareStatusText.Text = payload;
            _recorder.LogStatus(payload);
        });
        _bleClient.ImuPayloadReceived += payload => EnqueueUi(() =>
        {
            _imuRawPacketCount++;
            _lastImuPayloadPreview = TrimForLog(payload);
        });
        _bleClient.ImuFrameProcessed += frame => EnqueueUi(() => HandleImuFrame(frame));
        _bleClient.GestureStateChanged += (state, reason) => EnqueueUi(() =>
        {
            GestureStateText.Text = state;
            AppendLog($"手势状态: {state} ({reason})");
        });
        _bleClient.GestureTriggered += result => EnqueueUi(() =>
        {
            string sendResult = result.HotkeySent ? "成功" : "失败";
            HotkeyStatusText.Text = result.HotkeySent ? "已发送 Ctrl+Alt+Q" : "热键发送失败";
            HotkeyCountText.Text = $"发送次数: {result.TriggerCount}";
            LastHotkeyTimeText.Text = $"最近发送: {result.TriggeredAt:HH:mm:ss}";
            AppendLog($"回调: {result.GestureName} 触发，热键发送{sendResult}，pose=({result.Pose.Roll:F1},{result.Pose.Pitch:F1},{result.Pose.Yaw:F1})");
            _recorder.LogMarker(result.GestureName);
        });
        _bleClient.HotkeySent += (gesture, count, sentTime) => EnqueueUi(() =>
        {
            HotkeyStatusText.Text = "已发送 Ctrl+Alt+Q";
            HotkeyCountText.Text = $"发送次数: {count}";
            LastHotkeyTimeText.Text = $"最近发送: {sentTime:HH:mm:ss}";
            AppendLog($"热键发送完成: {gesture} -> Ctrl+Alt+Q");
        });
        _bleClient.ImuFrameProcessingFailed += (status, payload) => EnqueueUi(() =>
        {
            _recorder.LogMarker($"IMU_PARSE_FAIL:{status}");
            AppendLog($"IMU 处理失败: {status}, payload={TrimForLog(payload)}");
        });

        AppWindow.Closing += AppWindow_Closing;

        _ = ArmBleAutoReconnectAsync();
    }

    private async Task ArmBleAutoReconnectAsync()
    {
        try
        {
            BleStateText.Text = "scanning";
            _bleConnectCts?.Cancel();
            _bleConnectCts = new CancellationTokenSource();
            await _bleClient.StartAutoReconnectLoopAsync(TimeSpan.FromSeconds(35), _bleConnectCts.Token);
            AppendLog("已启动自动重连。ESP32 断电再上电后会自动回连。");
        }
        catch (Exception ex)
        {
            AppendLog($"启动自动重连失败: {ex.Message}");
            BleStateText.Text = "error";
        }
    }

    private async void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        _bleConnectCts?.Cancel();
        _imuValidationCts?.Cancel();
        await _bleClient.StopAutoReconnectLoopAsync();
        await _bleClient.DisposeAsync();

        string? path = _recorder.CurrentFilePath;
        _recorder.StopSession();
        _recorder.Dispose();
        if (!string.IsNullOrWhiteSpace(path))
        {
            AppendLog($"Record stopped: {path}");
        }
    }

    private void HandleConnectionState(BleConnectionLifecycleState state)
    {
        BleStateText.Text = state.ToString();
        if (state == BleConnectionLifecycleState.Connected)
        {
            AppendLog("BLE 已连接。开始接收结构化 IMU 帧。");
            StartImuValidation();
        }
        else if (state == BleConnectionLifecycleState.Disconnected)
        {
            AppendLog("BLE 已断开，等待自动重连。");
            _imuValidationCts?.Cancel();
        }
    }

    private void HandleImuFrame(ImuProcessingResult frame)
    {
        ImuRawSample raw = frame.Raw;
        ImuCalibratedSample calibrated = frame.Calibrated;
        ImuPoseSample pose = frame.Pose;
        string note = calibrated.IsReady ? "imu-auto-cal-ready" : "imu-auto-cal-learning";

        _recorder.LogImu(
            frame.RawPayload,
            raw.Ax,
            raw.Ay,
            raw.Az,
            raw.Gx,
            raw.Gy,
            raw.Gz,
            raw.Temp,
            pose.Roll,
            pose.Pitch,
            pose.Yaw,
            note);

        _imuParsedPacketCount++;

        long nowTicks = _clock.ElapsedTicks;
        if (_lastPoseUiTicks == 0 || ((nowTicks - _lastPoseUiTicks) / (double)Stopwatch.Frequency) >= 0.2)
        {
            PoseText.Text = string.Create(
                CultureInfo.InvariantCulture,
                $"roll={frame.Pose.Roll:F1}, pitch={frame.Pose.Pitch:F1}, yaw={frame.Pose.Yaw:F1}");
            _lastPoseUiTicks = nowTicks;
        }
    }

    private static string TrimForLog(string payload)
    {
        return payload.Length <= 100 ? payload : payload[..100] + "...";
    }

    private void StartImuValidation()
    {
        _imuValidationCts?.Cancel();
        _imuValidationCts = new CancellationTokenSource();

        _imuRawPacketCount = 0;
        _imuParsedPacketCount = 0;
        _lastImuPayloadPreview = "<none>";

        _ = ValidateImuStreamAsync(_imuValidationCts.Token);
    }

    private async Task ValidateImuStreamAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(ImuValidationTimeoutSeconds), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        EnqueueUi(() =>
        {
            if (!_bleClient.IsConnected)
            {
                return;
            }

            if (_imuParsedPacketCount > 0)
            {
                return;
            }

            if (_imuRawPacketCount == 0)
            {
                const string diag = "IMU_VALIDATION:NO_PAYLOAD";
                _recorder.LogMarker(diag);
                AppendLog($"{diag} in {ImuValidationTimeoutSeconds:F0}s after connect. Check notify/CCCD/UUID and firmware stream gate.");
                return;
            }

            string parseDiag = string.Create(
                CultureInfo.InvariantCulture,
                $"IMU_VALIDATION:PARSE_ZERO raw={_imuRawPacketCount} in {ImuValidationTimeoutSeconds:F0}s, last={_lastImuPayloadPreview}");
            _recorder.LogMarker(parseDiag);
            AppendLog(parseDiag);
        });
    }

    private void EnqueueUi(Action action)
    {
        DispatcherQueue.TryEnqueue(() => action());
    }

    private void AppendLog(string line)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        _logItems.Add($"[{timestamp}] {line}");
        while (_logItems.Count > 400)
        {
            _logItems.RemoveAt(0);
        }
    }
}
