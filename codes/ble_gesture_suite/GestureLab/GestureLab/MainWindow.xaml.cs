using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using GestureLab.Ble;

namespace GestureLab
{
    public sealed partial class MainWindow : Window
    {
        private enum MouthActionState
        {
            Idle,
            Moving,
            Holding,
            Cooldown,
        }

        private const uint KeyEventFKeyUp = 0x0002;
        private const ushort VirtualKeyControl = 0x11;
        private const ushort VirtualKeyMenu = 0x12;
        private const ushort VirtualKeyQ = 0x51;
        private const double MoveStartGyroThreshold = 700.0;
        private const double StillGyroThreshold = 900.0;
        private const double HoldBreakGyroThreshold = 3500.0;
        private const double PosePitchAbsMin = 20.0;
        private const double PosePitchAbsMax = 88.0;
        private const double PoseRollAbsMax = 165.0;
        private const double HoldSeconds = 0.22;
        private const double CooldownSeconds = 1.2;
        private const double PosePublishIntervalSeconds = 1.0 / 30.0;
        private const double DebugUiRefreshIntervalSeconds = 1.0 / 20.0;
        private const double ImuValidationTimeoutSeconds = 5.0;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

        private const string HostName = "appassets.local";
        private bool _webViewInitialized = false;
        private readonly BleGestureClient _bleClient = new();
        private readonly GestureDataRecorder _recorder = new();
        private readonly Stopwatch _imuClock = Stopwatch.StartNew();
        private CancellationTokenSource? _bleConnectCts;
        private readonly ObservableCollection<string> _logItems = new();
        private const int MaxLogItems = 500;
        private double _positionX;
        private double _positionY;
        private double _positionZ;
        private double _velocityX;
        private double _velocityY;
        private double _velocityZ;
        private long _lastImuTicks;
        private long _lastTelemetryLogTicks;
        private long _lastPosePublishTicks;
        private MouthActionState _mouthActionState = MouthActionState.Idle;
        private MouthActionState _lastMouthDebugUiState = MouthActionState.Idle;
        private long _mouthHoldStartTicks;
        private long _mouthCooldownUntilTicks;
        private long _lastMouthDebugUiTicks;
        private int _imuRawPacketCount;
        private int _imuParsedPacketCount;
        private bool _imuReadyLogged;
        private bool _isImuCalibrationReady;
        private bool _hasImuStream;
        private string _lastImuPayloadPreview = "<none>";
        private CancellationTokenSource? _imuValidationCts;
        private bool _isSampleCollecting;
        private string? _activeSampleFilePath;
        private int _activeSampleTriggerCount;
        private bool _isAppUiLogEnabled = true;

        public MainWindow()
        {
            InitializeComponent();
            _isAppUiLogEnabled = AppLogToggle.IsOn;
            LogList.ItemsSource = _logItems;
            Browser.Loaded += Browser_Loaded;
            InitializeMouthDebugUi();
            UpdateSampleCollectAvailability();

            // Caller-side required wiring summary:
            // 1) Register connection state/log/status/frame events before starting reconnect.
            // 2) Start auto reconnect once at startup; the host will keep scanning/reconnecting.
            _bleClient.Log += message => EnqueueUi(() => AppendLog($"BLE: {message}"));
            _bleClient.ConnectionStateChanged += state => EnqueueUi(() => HandleBleConnectionStateChanged(state));
            _bleClient.StatusPayloadReceived += payload => EnqueueUi(() =>
            {
                AppendLog($"RX status: {payload}");
                _recorder.LogStatus(payload);
            });
            _bleClient.ImuPayloadReceived += payload => EnqueueUi(() => HandleRawImuPayload(payload));
            _bleClient.ImuFrameProcessed += result => EnqueueUi(() => HandleImuFrameProcessed(result));
            _bleClient.ImuFrameProcessingFailed += (status, payload) => EnqueueUi(() => HandleImuProcessingFailed(status, payload));

            AppWindow.Closing += AppWindow_Closing;

            // Auto arm BLE reconnect on app startup so host can recover after ESP32 power cycle.
            _ = ArmBleAutoReconnectAsync();
        }

        private async void Browser_Loaded(object sender, RoutedEventArgs e)
        {
            if (_webViewInitialized)
            {
                return;
            }
            _webViewInitialized = true;

            await Browser.EnsureCoreWebView2Async();
            Browser.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
            Browser.CoreWebView2.SetVirtualHostNameToFolderMapping(
              HostName,
              Path.Combine(AppContext.BaseDirectory, "Web"),
              CoreWebView2HostResourceAccessKind.Allow);

            Browser.CoreWebView2.Navigate($"https://{HostName}/index.html");
            AppendLog("WebView2 initialized and local HTML page loaded.");
        }

        private void CoreWebView2_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            string message = args.TryGetWebMessageAsString();
            AppendLog($"Page -> C#: {message}");
        }

        private async Task ArmBleAutoReconnectAsync()
        {
            try
            {
                BleStatusText.Text = "BLE: scanning...";
                _bleConnectCts?.Cancel();
                _bleConnectCts = new CancellationTokenSource();

                await _bleClient.StartAutoReconnectLoopAsync(TimeSpan.FromSeconds(35), _bleConnectCts.Token);
                BleStatusText.Text = "BLE: reconnect armed";
                AppendLog("BLE auto reconnect armed.");
            }
            catch (OperationCanceledException)
            {
                BleStatusText.Text = "BLE: canceled";
                AppendLog("BLE connect canceled.");
            }
            catch (TimeoutException ex)
            {
                BleStatusText.Text = "BLE: timeout";
                AppendLog($"BLE connect timeout: {ex.Message}");
            }
            catch (Exception ex)
            {
                BleStatusText.Text = "BLE: error";
                AppendLog($"BLE connect failed: {ex.Message}");
            }
        }


        private async void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
        {
            if (_isSampleCollecting)
            {
                // Ensure an unfinished sample is still closed and persisted on exit.
                _recorder.LogMarker("SAMPLE_RESULT:UNFINISHED_EXIT");
                _recorder.StopSession();
            }

            await DisconnectBleAsync();
            await _bleClient.DisposeAsync();
            _recorder.Dispose();
        }

        private async void CollectSampleButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isSampleCollecting)
            {
                if (!_isImuCalibrationReady && !_hasImuStream)
                {
                    SampleCollectStatusText.Text = "等待 IMU ready";
                    AppendLog("Sample capture blocked: IMU calibration is not ready yet.");
                    UpdateSampleCollectAvailability();
                    return;
                }

                if (!_isImuCalibrationReady && _hasImuStream)
                {
                    AppendLog("Sample capture started without IMU ready. Data is valid for collection, but quality may be lower.");
                }

                string sampleLabel = $"sample_{DateTime.Now:HHmmss}";
                string path = _recorder.StartSession(sampleLabel);
                _isSampleCollecting = true;
                _activeSampleFilePath = path;
                _activeSampleTriggerCount = 0;

                CollectSampleButton.Content = "结束采集";
                SampleCollectStatusText.Text = "采集中";
                UpdateSampleCollectAvailability();
                AppendLog($"Sample capture started: {path}");
                return;
            }

            // End the sample immediately when the user clicks stop so later UI interaction
            // does not continue recording extra IMU frames or gesture triggers.
            _recorder.LogMarker("SAMPLE_STOP_REQUESTED");
            string? sourcePath = _activeSampleFilePath;
            _recorder.StopSession();
            _isSampleCollecting = false;
            _activeSampleFilePath = null;
            CollectSampleButton.Content = "开始采集";
            SampleCollectStatusText.Text = "等待标注";

            FrameworkElement? root = Content as FrameworkElement;
            string sampleResultTag = "UNKNOWN";
            if (root?.XamlRoot is not null)
            {
                ContentDialog dialog = new()
                {
                    XamlRoot = root.XamlRoot,
                    Title = "这次采集结果",
                    Content = "请选择这次手势操作是正确还是错误。",
                    PrimaryButtonText = "正确",
                    SecondaryButtonText = "错误",
                    CloseButtonText = "未标注",
                    DefaultButton = ContentDialogButton.Primary,
                };

                ContentDialogResult result = await dialog.ShowAsync();
                sampleResultTag = result switch
                {
                    ContentDialogResult.Primary => "CORRECT",
                    ContentDialogResult.Secondary => "WRONG",
                    _ => "UNLABELED",
                };
            }

            string triggerResultTag = _activeSampleTriggerCount > 0 ? "TRIGGERED" : "MISSED";
            string? renamedPath = TryRenameSampleFile(sourcePath, sampleResultTag);
            renamedPath = TryRenameSampleFile(renamedPath, triggerResultTag);
            SampleCollectStatusText.Text = $"已结束: {sampleResultTag}/{triggerResultTag}";
            UpdateSampleCollectAvailability();
            AppendLog($"Sample capture stopped: {renamedPath ?? sourcePath ?? "<unknown path>"}");
            AppendLog($"Sample label selected: {sampleResultTag}");
            AppendLog($"Sample trigger result: {triggerResultTag} (count={_activeSampleTriggerCount})");
            _activeSampleTriggerCount = 0;
        }

        private void UpdateSampleCollectAvailability()
        {
            bool canStart = _bleClient.IsConnected && (_isImuCalibrationReady || _hasImuStream);
            CollectSampleButton.IsEnabled = _isSampleCollecting || canStart;

            if (_isSampleCollecting)
            {
                return;
            }

            if (!_bleClient.IsConnected)
            {
                SampleCollectStatusText.Text = "等待 BLE";
                return;
            }

            if (!_isImuCalibrationReady)
            {
                if (_hasImuStream)
                {
                    SampleCollectStatusText.Text = "IMU流已到达(未ready)";
                    return;
                }

                SampleCollectStatusText.Text = "等待 IMU ready";
                return;
            }

            if (SampleCollectStatusText.Text == "未采集"
                || SampleCollectStatusText.Text == "等待 BLE"
                || SampleCollectStatusText.Text == "等待 IMU ready")
            {
                SampleCollectStatusText.Text = "可开始采集";
            }

        }

        private static string? TryRenameSampleFile(string? sourcePath, string sampleResultTag)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                return sourcePath;
            }

            try
            {
                string directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
                string fileNameNoExt = Path.GetFileNameWithoutExtension(sourcePath);
                string extension = Path.GetExtension(sourcePath);
                string targetName = $"{fileNameNoExt}_{sampleResultTag}{extension}";
                string targetPath = Path.Combine(directory, targetName);

                if (!string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(sourcePath, targetPath, overwrite: true);
                    return targetPath;
                }
            }
            catch
            {
                // Keep original file if rename fails; data has already been persisted.
            }

            return sourcePath;
        }

        private void AppendLog(string line)
        {
            if (!_isAppUiLogEnabled)
            {
                return;
            }

            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            _logItems.Insert(0, $"[{timestamp}] {line}");

            while (_logItems.Count > MaxLogItems)
            {
                _logItems.RemoveAt(_logItems.Count - 1);
            }
        }

        private void AppLogToggle_Toggled(object sender, RoutedEventArgs e)
        {
            bool enabled = AppLogToggle.IsOn;
            if (!enabled)
            {
                AppendLog("APP界面日志已关闭。");
            }

            _isAppUiLogEnabled = enabled;

            if (enabled)
            {
                AppendLog("APP界面日志已开启。");
            }
        }

        private static string BuildPoseMessageJson(double roll, double pitch, double yaw, string label, double tx, double ty, double tz)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"source\":\"host\",\"type\":\"pose\",\"label\":\"{EscapeJson(label)}\",\"roll\":{roll},\"pitch\":{pitch},\"yaw\":{yaw},\"tx\":{tx:F4},\"ty\":{ty:F4},\"tz\":{tz:F4}}}");
        }

        private static string BuildGestureMessageJson(string gesture, double roll, double pitch)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{{\"source\":\"host\",\"type\":\"gesture\",\"name\":\"{EscapeJson(gesture)}\",\"roll\":{roll:F2},\"pitch\":{pitch:F2}}}");
        }

        private async Task DisconnectBleAsync()
        {
            try
            {
                _bleConnectCts?.Cancel();
                _imuValidationCts?.Cancel();
                await _bleClient.StopAutoReconnectLoopAsync();
            }
            catch (Exception ex)
            {
                AppendLog($"BLE disconnect warning: {ex.Message}");
            }
        }

        private void HandleRawImuPayload(string payload)
        {
            _imuRawPacketCount++;
            _lastImuPayloadPreview = payload.Length <= 120 ? payload : payload[..120] + "...";
        }

        private void HandleImuProcessingFailed(ImuProcessingStatus status, string payload)
        {
            if (status == ImuProcessingStatus.ParseFailed)
            {
                _recorder.LogImu(payload, null, null, null, null, null, null, null, null, null, null, "parse-failed");
                return;
            }

            if (status == ImuProcessingStatus.PoseFailed)
            {
                _recorder.LogImu(payload, null, null, null, null, null, null, null, null, null, null, "pose-failed");
            }
        }

        private void HandleImuFrameProcessed(ImuProcessingResult result)
        {
            ImuRawSample rawSample = result.Raw;
            ImuCalibratedSample calibrated = result.Calibrated;
            double roll = result.Pose.Roll;
            double pitch = result.Pose.Pitch;
            double yaw = result.Pose.Yaw;
            string payload = result.RawPayload;

            _imuParsedPacketCount++;
            if (!_hasImuStream)
            {
                _hasImuStream = true;
                UpdateSampleCollectAvailability();
            }

            if (!_imuReadyLogged)
            {
                _imuReadyLogged = true;
                AppendLog(string.Create(
                    CultureInfo.InvariantCulture,
                    $"IMU stream ready: raw={_imuRawPacketCount}, parsed={_imuParsedPacketCount}"));
            }

            string note = calibrated.IsReady ? "imu-auto-cal-ready" : "imu-auto-cal-learning";
            if (_isImuCalibrationReady != calibrated.IsReady)
            {
                _isImuCalibrationReady = calibrated.IsReady;
                UpdateSampleCollectAvailability();
                AppendLog(calibrated.IsReady
                    ? "IMU calibration ready. Sample capture unlocked."
                    : "IMU calibration returned to learning. Sample capture locked.");
            }

            _recorder.LogImu(payload, rawSample.Ax, rawSample.Ay, rawSample.Az, rawSample.Gx, rawSample.Gy, rawSample.Gz, rawSample.Temp, roll, pitch, yaw, note);

            UpdateMotionPosition(calibrated);
            UpdateMouthActionDetector(rawSample, calibrated, roll, pitch);

            if (Browser.CoreWebView2 is null)
            {
                return;
            }

            if (ShouldPublishPoseToWeb(nowTicks: _imuClock.ElapsedTicks))
            {
                string message = BuildPoseMessageJson(roll, pitch, yaw, "imu-live", _positionX, _positionY, _positionZ);
                Browser.CoreWebView2.PostWebMessageAsString(message);
            }

            MaybeLogTelemetry(roll, pitch, yaw, calibrated.IsReady);
        }

        private void UpdateMotionPosition(ImuCalibratedSample calibrated)
        {
            long nowTicks = _imuClock.ElapsedTicks;
            if (_lastImuTicks == 0)
            {
                _lastImuTicks = nowTicks;
                return;
            }

            double dt = (nowTicks - _lastImuTicks) / (double)Stopwatch.Frequency;
            _lastImuTicks = nowTicks;

            if (dt <= 0 || dt > 0.2)
            {
                return;
            }

            // Convert calibrated linear acceleration to m/s^2 and map it to viewer axes.
            const double countsPerG = 16384.0;
            const double gravity = 9.80665;

            (double lx, double ly, double lz) = MapAccelToViewerAxes(
                calibrated.LinearX,
                calibrated.LinearY,
                calibrated.LinearZ);

            double ax = (lx / countsPerG) * gravity;
            double ay = (ly / countsPerG) * gravity;
            double az = (lz / countsPerG) * gravity;

            // Small residual linear acceleration mostly comes from sensor noise and imperfect gravity
            // estimation; treat it as zero so the preview does not keep drifting after the gesture ends.
            ax = ApplyDeadband(ax, 0.18);
            ay = ApplyDeadband(ay, 0.18);
            az = ApplyDeadband(az, 0.18);

            _velocityX = (_velocityX + (ax * dt)) * 0.94;
            _velocityY = (_velocityY + (ay * dt)) * 0.94;
            _velocityZ = (_velocityZ + (az * dt)) * 0.94;

            _positionX = (_positionX + (_velocityX * dt)) * 0.998;
            _positionY = (_positionY + (_velocityY * dt)) * 0.998;
            _positionZ = (_positionZ + (_velocityZ * dt)) * 0.998;

            const double maxPos = 1.5;
            _positionX = Math.Clamp(_positionX, -maxPos, maxPos);
            _positionY = Math.Clamp(_positionY, -maxPos, maxPos);
            _positionZ = Math.Clamp(_positionZ, -maxPos, maxPos);

            if (calibrated.IsStationary)
            {
                _velocityX *= 0.25;
                _velocityY *= 0.25;
                _velocityZ *= 0.25;

                _positionX *= 0.985;
                _positionY *= 0.985;
                _positionZ *= 0.985;
            }
        }

        private static double ApplyDeadband(double value, double threshold)
        {
            return Math.Abs(value) < threshold ? 0.0 : value;
        }

        private void InitializeMouthDebugUi()
        {
            PitchRangeText.Text = string.Create(CultureInfo.InvariantCulture, $"范围: |pitch| in [{PosePitchAbsMin:F0}, {PosePitchAbsMax:F0}]");
            RollRangeText.Text = string.Create(CultureInfo.InvariantCulture, $"范围: |roll| <= {PoseRollAbsMax:F0}");
            GyroRangeText.Text = string.Create(
                CultureInfo.InvariantCulture,
                $"阈值: start>={MoveStartGyroThreshold:F0}, still<={StillGyroThreshold:F0}, break<{HoldBreakGyroThreshold:F0}");
            HoldTargetText.Text = string.Create(CultureInfo.InvariantCulture, $"目标: {HoldSeconds:F2}s");
            MouthStateText.Text = MouthActionState.Idle.ToString();
            MouthReasonText.Text = "等待抬手动作";
            HoldProgressBar.Value = 0;
            HoldProgressText.Text = "0%";
            CooldownText.Text = "0.00s";
        }

        private static string BuildPrimaryFailureReason(bool inMouthPose, double roll, double pitch, double gyroMagnitude)
        {
            if (Math.Abs(pitch) < PosePitchAbsMin)
            {
                return string.Create(CultureInfo.InvariantCulture, $"pitch 偏小: |{pitch:F1}| < {PosePitchAbsMin:F1}");
            }

            if (Math.Abs(pitch) > PosePitchAbsMax)
            {
                return string.Create(CultureInfo.InvariantCulture, $"pitch 偏大: |{pitch:F1}| > {PosePitchAbsMax:F1}");
            }

            if (Math.Abs(roll) > PoseRollAbsMax)
            {
                return string.Create(CultureInfo.InvariantCulture, $"roll 超限: |{roll:F1}| > {PoseRollAbsMax:F1}");
            }

            if (!inMouthPose)
            {
                return "姿态未进入目标窗口";
            }

            return string.Create(CultureInfo.InvariantCulture, $"等待稳定: gyro={gyroMagnitude:F0}");
        }

        private void UpdateMouthDebugUi(
            double roll,
            double pitch,
            double gyroMagnitude,
            string reason,
            double holdProgress,
            double cooldownRemainingSeconds)
        {
            MouthStateText.Text = _mouthActionState.ToString();
            MouthReasonText.Text = reason;

            PitchProgressBar.Value = Math.Clamp(pitch, -90, 90);
            RollProgressBar.Value = Math.Clamp(roll, -90, 90);
            GyroProgressBar.Value = Math.Clamp(gyroMagnitude, 0, GyroProgressBar.Maximum);

            PitchValueText.Text = string.Create(CultureInfo.InvariantCulture, $"{pitch:F1}°");
            RollValueText.Text = string.Create(CultureInfo.InvariantCulture, $"{roll:F1}°");
            GyroValueText.Text = string.Create(CultureInfo.InvariantCulture, $"{gyroMagnitude:F0}");

            HoldProgressBar.Value = Math.Clamp(holdProgress, 0.0, 1.0);
            HoldProgressText.Text = string.Create(CultureInfo.InvariantCulture, $"{Math.Clamp(holdProgress * 100.0, 0.0, 100.0):F0}%");
            CooldownText.Text = string.Create(CultureInfo.InvariantCulture, $"{Math.Max(cooldownRemainingSeconds, 0.0):F2}s");
        }

        private void UpdateMouthActionDetector(ImuRawSample raw, ImuCalibratedSample calibrated, double roll, double pitch)
        {
            long nowTicks = _imuClock.ElapsedTicks;
            double nowSeconds = nowTicks / (double)Stopwatch.Frequency;

            double correctedGx = raw.Gx - calibrated.GyroBiasX;
            double correctedGy = raw.Gy - calibrated.GyroBiasY;
            double correctedGz = raw.Gz - calibrated.GyroBiasZ;
            double gyroMagnitude = Math.Sqrt((correctedGx * correctedGx) + (correctedGy * correctedGy) + (correctedGz * correctedGz));

            bool inMouthPose = Math.Abs(pitch) >= PosePitchAbsMin
                && Math.Abs(pitch) <= PosePitchAbsMax
                && Math.Abs(roll) <= PoseRollAbsMax;
            string reason = BuildPrimaryFailureReason(inMouthPose, roll, pitch, gyroMagnitude);
            double holdProgress = 0;
            double cooldownRemainingSeconds = Math.Max(0.0, (_mouthCooldownUntilTicks - nowTicks) / (double)Stopwatch.Frequency);

            switch (_mouthActionState)
            {
                case MouthActionState.Idle:
                    if (gyroMagnitude >= MoveStartGyroThreshold)
                    {
                        _mouthActionState = MouthActionState.Moving;
                        reason = string.Create(CultureInfo.InvariantCulture, $"检测到抬手速度: gyro={gyroMagnitude:F0} >= {MoveStartGyroThreshold:F0}");
                    }
                    else
                    {
                        reason = string.Create(CultureInfo.InvariantCulture, $"等待抬手: gyro={gyroMagnitude:F0} < {MoveStartGyroThreshold:F0}");
                    }
                    break;

                case MouthActionState.Moving:
                    if (inMouthPose && gyroMagnitude <= StillGyroThreshold)
                    {
                        _mouthActionState = MouthActionState.Holding;
                        _mouthHoldStartTicks = nowTicks;
                        reason = "进入 Holding，开始计时";
                    }
                    else if (gyroMagnitude <= StillGyroThreshold && !inMouthPose)
                    {
                        _mouthActionState = MouthActionState.Idle;
                        reason = BuildPrimaryFailureReason(inMouthPose, roll, pitch, gyroMagnitude);
                    }
                    else if (!inMouthPose)
                    {
                        reason = BuildPrimaryFailureReason(inMouthPose, roll, pitch, gyroMagnitude);
                    }
                    else
                    {
                        reason = string.Create(CultureInfo.InvariantCulture, $"姿态已进入，等待更稳定: gyro={gyroMagnitude:F0} > {StillGyroThreshold:F0}");
                    }
                    break;

                case MouthActionState.Holding:
                    holdProgress = Math.Clamp((nowTicks - _mouthHoldStartTicks) / (double)Stopwatch.Frequency / HoldSeconds, 0.0, 1.0);
                    if (!inMouthPose)
                    {
                        _mouthActionState = gyroMagnitude >= MoveStartGyroThreshold ? MouthActionState.Moving : MouthActionState.Idle;
                        reason = BuildPrimaryFailureReason(inMouthPose, roll, pitch, gyroMagnitude);
                        break;
                    }

                    if (gyroMagnitude >= HoldBreakGyroThreshold)
                    {
                        _mouthActionState = MouthActionState.Moving;
                        reason = string.Create(CultureInfo.InvariantCulture, $"Holding 中断: gyro={gyroMagnitude:F0} >= {HoldBreakGyroThreshold:F0}");
                        break;
                    }

                    if (((nowTicks - _mouthHoldStartTicks) / (double)Stopwatch.Frequency) >= HoldSeconds
                        && nowTicks >= _mouthCooldownUntilTicks)
                    {
                        TriggerMouthAction(roll, pitch);
                        _mouthCooldownUntilTicks = nowTicks + (long)(CooldownSeconds * Stopwatch.Frequency);
                        _mouthActionState = MouthActionState.Cooldown;
                        cooldownRemainingSeconds = CooldownSeconds;
                        reason = "触发成功，进入 Cooldown";
                    }
                    else
                    {
                        reason = string.Create(CultureInfo.InvariantCulture, $"保持中: {Math.Clamp((nowTicks - _mouthHoldStartTicks) / (double)Stopwatch.Frequency, 0.0, HoldSeconds):F2}/{HoldSeconds:F2}s");
                    }
                    break;

                case MouthActionState.Cooldown:
                    if (nowSeconds >= (_mouthCooldownUntilTicks / (double)Stopwatch.Frequency)
                        && gyroMagnitude <= StillGyroThreshold)
                    {
                        _mouthActionState = MouthActionState.Idle;
                        reason = "Cooldown 结束，可进行下一次动作";
                        cooldownRemainingSeconds = 0;
                    }
                    else
                    {
                        reason = string.Create(CultureInfo.InvariantCulture, $"冷却中: {cooldownRemainingSeconds:F2}s");
                    }
                    break;
            }

            bool stateChanged = _mouthActionState != _lastMouthDebugUiState;
            bool intervalReached = _lastMouthDebugUiTicks == 0
                || ((nowTicks - _lastMouthDebugUiTicks) / (double)Stopwatch.Frequency) >= DebugUiRefreshIntervalSeconds;

            if (stateChanged || intervalReached)
            {
                UpdateMouthDebugUi(roll, pitch, gyroMagnitude, reason, holdProgress, cooldownRemainingSeconds);
                _lastMouthDebugUiTicks = nowTicks;
                _lastMouthDebugUiState = _mouthActionState;
            }
        }

        private bool ShouldPublishPoseToWeb(long nowTicks)
        {
            if (_lastPosePublishTicks == 0)
            {
                _lastPosePublishTicks = nowTicks;
                return true;
            }

            if (((nowTicks - _lastPosePublishTicks) / (double)Stopwatch.Frequency) < PosePublishIntervalSeconds)
            {
                return false;
            }

            _lastPosePublishTicks = nowTicks;
            return true;
        }

        private void TriggerMouthAction(double roll, double pitch)
        {
            AppendLog(string.Create(
                CultureInfo.InvariantCulture,
                $"Gesture trigger: MOUTH_HOLD (roll={roll:F1}, pitch={pitch:F1})"));

            if (_isSampleCollecting)
            {
                _activeSampleTriggerCount++;
                AppendLog($"Sample trigger count updated: {_activeSampleTriggerCount}");
            }

            _recorder.LogMarker("MOUTH_HOLD");

            if (Browser.CoreWebView2 is not null)
            {
                Browser.CoreWebView2.PostWebMessageAsString(BuildGestureMessageJson("MOUTH_HOLD", roll, pitch));
            }

            SendSystemHotkey(VirtualKeyControl, VirtualKeyMenu, VirtualKeyQ);
            AppendLog(string.Create(
                CultureInfo.InvariantCulture,
                $"热键已发送: Ctrl+Alt+Q (source=动作触发, roll={roll:F1}, pitch={pitch:F1})"));
        }

        private void SendSystemHotkey(params ushort[] virtualKeys)
        {
            if (virtualKeys.Length == 0)
            {
                return;
            }

            for (int index = 0; index < virtualKeys.Length; index++)
            {
                keybd_event((byte)virtualKeys[index], 0, 0, 0);
            }

            Thread.Sleep(20);

            for (int index = 0; index < virtualKeys.Length; index++)
            {
                ushort key = virtualKeys[virtualKeys.Length - 1 - index];
                keybd_event((byte)key, 0, KeyEventFKeyUp, 0);
            }

            // Explicit completion log for integrators observing SDK/app behavior.
            AppendLog("Hotkey send completed: Ctrl+Alt+Q");
        }

        private void MaybeLogTelemetry(double roll, double pitch, double yaw, bool isReady)
        {
            long now = _imuClock.ElapsedTicks;
            if (_lastTelemetryLogTicks != 0
                && ((now - _lastTelemetryLogTicks) / (double)Stopwatch.Frequency) < 1.0)
            {
                return;
            }

            _lastTelemetryLogTicks = now;
            AppendLog(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"IMU {(isReady ? "ready" : "learning")} pose({roll:F1},{pitch:F1},{yaw:F1}) pos({_positionX:F2},{_positionY:F2},{_positionZ:F2})"));
        }

        private static (double x, double y, double z) MapAccelToViewerAxes(double ax, double ay, double az)
        {
            // Pair mapping baseline (from current LR/FB captures):
            // - LR pair dominates sensor X, used as viewer Y (roll input), inverted for natural hand tilt.
            // - FB pair dominates sensor Y, used as viewer X (pitch input), inverted for forward-down behavior.
            // - viewer Z keeps sensor Z.
            return (-ay, -ax, az);
        }

        private void EnqueueUi(Action action)
        {
            DispatcherQueue.TryEnqueue(() => action());
        }

        private void HandleBleConnectionStateChanged(BleConnectionLifecycleState state)
        {
            switch (state)
            {
                case BleConnectionLifecycleState.Scanning:
                    BleStatusText.Text = "BLE: scanning...";
                    break;

                case BleConnectionLifecycleState.Connected:
                    BleStatusText.Text = "BLE: connected";
                    AppendLog("BLE connection ready.");
                    StartImuValidation();
                    break;

                case BleConnectionLifecycleState.Disconnected:
                default:
                    _imuValidationCts?.Cancel();
                    _isImuCalibrationReady = false;
                    BleStatusText.Text = _bleClient.IsAutoReconnectActive ? "BLE: waiting for reconnect" : "BLE: disconnected";
                    UpdateSampleCollectAvailability();
                    if (_bleClient.IsAutoReconnectActive)
                    {
                        AppendLog("BLE disconnected. Waiting for ESP32 advertisement to reconnect.");
                    }
                    break;
            }
        }

        private void StartImuValidation()
        {
            _imuValidationCts?.Cancel();
            _imuValidationCts = new CancellationTokenSource();

            _imuRawPacketCount = 0;
            _imuParsedPacketCount = 0;
            _imuReadyLogged = false;
            _isImuCalibrationReady = false;
            _hasImuStream = false;
            _lastImuPayloadPreview = "<none>";
            UpdateSampleCollectAvailability();

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
                    AppendLog(string.Create(
                        CultureInfo.InvariantCulture,
                        $"IMU validation failed: no IMU payload received in {ImuValidationTimeoutSeconds:F0}s after BLE connected. Check firmware notify/CCCD/UUID and I2C sensor wiring."));
                    return;
                }

                AppendLog(string.Create(
                    CultureInfo.InvariantCulture,
                    $"IMU validation failed: received {_imuRawPacketCount} payload(s) but parse=0 in {ImuValidationTimeoutSeconds:F0}s. Last payload preview: {_lastImuPayloadPreview}"));
                AppendLog("Expected payload format: IMU,<tick>,<ax>,<ay>,<az>,<gx>,<gy>,<gz>,<temp>");
            });
        }

        private static string EscapeJson(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
