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

        [DllImport("user32.dll", SetLastError = true)]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

        private const string HostName = "appassets.local";
        private bool _webViewInitialized = false;
        private readonly BleGestureClient _bleClient = new();
        private readonly GestureDataRecorder _recorder = new();
        private readonly ImuAutoCalibrator _autoCalibrator = new();
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
        private MouthActionState _mouthActionState = MouthActionState.Idle;
        private long _mouthHoldStartTicks;
        private long _mouthCooldownUntilTicks;
        private bool _mouthTriggerDialogOpen;

        public MainWindow()
        {
            InitializeComponent();
            LogList.ItemsSource = _logItems;
            Browser.Loaded += Browser_Loaded;

            _bleClient.Log += message => EnqueueUi(() => AppendLog($"BLE: {message}"));
            _bleClient.StatusPayloadReceived += payload => EnqueueUi(() =>
            {
                AppendLog($"RX status: {payload}");
                _recorder.LogStatus(payload);
            });
            _bleClient.ImuPayloadReceived += payload => EnqueueUi(() => HandleImuPayload(payload));

            AppWindow.Closing += AppWindow_Closing;
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

        private async void ConnectBleButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ConnectBleButton.IsEnabled = false;
                BleStatusText.Text = "BLE: scanning...";
                _bleConnectCts?.Cancel();
                _bleConnectCts = new CancellationTokenSource();

                await _bleClient.ConnectAsync(TimeSpan.FromSeconds(35), _bleConnectCts.Token);
                BleStatusText.Text = "BLE: connected";
                AppendLog("BLE connection ready.");
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
            finally
            {
                ConnectBleButton.IsEnabled = true;
            }
        }

        private async void DisconnectBleButton_Click(object sender, RoutedEventArgs e)
        {
            await DisconnectBleAsync();
            BleStatusText.Text = "BLE: disconnected";
            AppendLog("BLE disconnected.");
        }

        private async void ManualHotkeyButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowHotkeyToastAsync(0, 0, "手动触发");
        }

        private void ClearLogButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            _logItems.Clear();
        }

        private void StartRecordButton_Click(object sender, RoutedEventArgs e)
        {
            string label = string.IsNullOrWhiteSpace(MarkerTextBox.Text) ? "alignment" : MarkerTextBox.Text;
            string path = _recorder.StartSession(label);
            AppendLog($"Record started: {path}");
        }

        private void StopRecordButton_Click(object sender, RoutedEventArgs e)
        {
            string? path = _recorder.CurrentFilePath;
            _recorder.StopSession();
            AppendLog(path is null ? "Record already stopped." : $"Record stopped: {path}");
        }

        private void MarkButton_Click(object sender, RoutedEventArgs e)
        {
            string note = string.IsNullOrWhiteSpace(MarkerTextBox.Text)
                ? $"mark-{DateTime.Now:HHmmss}"
                : MarkerTextBox.Text.Trim();

            LogMarker(note);
        }

        private void MarkLButton_Click(object sender, RoutedEventArgs e) => LogMarker("L");
        private void MarkRButton_Click(object sender, RoutedEventArgs e) => LogMarker("R");
        private void MarkFButton_Click(object sender, RoutedEventArgs e) => LogMarker("F");
        private void MarkBButton_Click(object sender, RoutedEventArgs e) => LogMarker("B");
        private void MarkUButton_Click(object sender, RoutedEventArgs e) => LogMarker("U");
        private void MarkDButton_Click(object sender, RoutedEventArgs e) => LogMarker("D");

        private async void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
        {
            await DisconnectBleAsync();
            await _bleClient.DisposeAsync();
            _recorder.Dispose();
        }

        private void AppendLog(string line)
        {
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            _logItems.Add($"[{timestamp}] {line}");

            while (_logItems.Count > MaxLogItems)
            {
                _logItems.RemoveAt(0);
            }
        }

        private void LogMarker(string note)
        {
            _recorder.LogMarker(note);
            MarkerTextBox.Text = note;
            AppendLog($"Mark: {note}");
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
                await _bleClient.DisconnectAsync();
            }
            catch (Exception ex)
            {
                AppendLog($"BLE disconnect warning: {ex.Message}");
            }
        }

        private void HandleImuPayload(string payload)
        {
            if (!TryParseImuRaw(payload, out ImuRawSample rawSample))
            {
                _recorder.LogImu(payload, null, null, null, null, null, null, null, null, null, null, "parse-failed");
                return;
            }

            ImuCalibratedSample calibrated = _autoCalibrator.Update(rawSample);

            if (!TryBuildPoseFromCalibratedGravity(calibrated, out double roll, out double pitch, out double yaw))
            {
                _recorder.LogImu(payload, rawSample.Ax, rawSample.Ay, rawSample.Az, rawSample.Gx, rawSample.Gy, rawSample.Gz, rawSample.Temp, null, null, null, "pose-failed");
                return;
            }

            string note = calibrated.IsReady ? "imu-auto-cal-ready" : "imu-auto-cal-learning";
            _recorder.LogImu(payload, rawSample.Ax, rawSample.Ay, rawSample.Az, rawSample.Gx, rawSample.Gy, rawSample.Gz, rawSample.Temp, roll, pitch, yaw, note);

            UpdateMotionPosition(calibrated);
            UpdateMouthActionDetector(rawSample, calibrated, roll, pitch);

            if (Browser.CoreWebView2 is null)
            {
                return;
            }

            string message = BuildPoseMessageJson(roll, pitch, yaw, "imu-live", _positionX, _positionY, _positionZ);
            Browser.CoreWebView2.PostWebMessageAsString(message);

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

        private void UpdateMouthActionDetector(ImuRawSample raw, ImuCalibratedSample calibrated, double roll, double pitch)
        {
            // Base version: prioritize smooth triggering over strict filtering.
            const double moveStartGyroThreshold = 900.0;
            const double stillGyroThreshold = 450.0;
            const double holdBreakGyroThreshold = 1400.0;
            const double posePitchAbsMin = 24.0;
            const double posePitchAbsMax = 88.0;
            const double poseRollAbsMax = 86.0;
            const double holdSeconds = 0.30;
            const double cooldownSeconds = 1.2;

            long nowTicks = _imuClock.ElapsedTicks;
            double nowSeconds = nowTicks / (double)Stopwatch.Frequency;

            double correctedGx = raw.Gx - calibrated.GyroBiasX;
            double correctedGy = raw.Gy - calibrated.GyroBiasY;
            double correctedGz = raw.Gz - calibrated.GyroBiasZ;
            double gyroMagnitude = Math.Sqrt((correctedGx * correctedGx) + (correctedGy * correctedGy) + (correctedGz * correctedGz));

            bool inMouthPose = Math.Abs(pitch) >= posePitchAbsMin
                && Math.Abs(pitch) <= posePitchAbsMax
                && Math.Abs(roll) <= poseRollAbsMax;

            switch (_mouthActionState)
            {
                case MouthActionState.Idle:
                    if (gyroMagnitude >= moveStartGyroThreshold)
                    {
                        _mouthActionState = MouthActionState.Moving;
                    }
                    break;

                case MouthActionState.Moving:
                    if (inMouthPose && gyroMagnitude <= stillGyroThreshold)
                    {
                        _mouthActionState = MouthActionState.Holding;
                        _mouthHoldStartTicks = nowTicks;
                    }
                    else if (gyroMagnitude <= stillGyroThreshold && !inMouthPose)
                    {
                        _mouthActionState = MouthActionState.Idle;
                    }
                    break;

                case MouthActionState.Holding:
                    if (!inMouthPose)
                    {
                        _mouthActionState = gyroMagnitude >= moveStartGyroThreshold ? MouthActionState.Moving : MouthActionState.Idle;
                        break;
                    }

                    if (gyroMagnitude >= holdBreakGyroThreshold)
                    {
                        _mouthActionState = MouthActionState.Moving;
                        break;
                    }

                    if (((nowTicks - _mouthHoldStartTicks) / (double)Stopwatch.Frequency) >= holdSeconds
                        && nowTicks >= _mouthCooldownUntilTicks)
                    {
                        TriggerMouthAction(roll, pitch);
                        _mouthCooldownUntilTicks = nowTicks + (long)(cooldownSeconds * Stopwatch.Frequency);
                        _mouthActionState = MouthActionState.Cooldown;
                    }
                    break;

                case MouthActionState.Cooldown:
                    if (nowSeconds >= (_mouthCooldownUntilTicks / (double)Stopwatch.Frequency)
                        && gyroMagnitude <= stillGyroThreshold)
                    {
                        _mouthActionState = MouthActionState.Idle;
                    }
                    break;
            }
        }

        private void TriggerMouthAction(double roll, double pitch)
        {
            AppendLog(string.Create(
                CultureInfo.InvariantCulture,
                $"Gesture trigger: MOUTH_HOLD (roll={roll:F1}, pitch={pitch:F1})"));

            _recorder.LogMarker("MOUTH_HOLD");

            if (Browser.CoreWebView2 is not null)
            {
                Browser.CoreWebView2.PostWebMessageAsString(BuildGestureMessageJson("MOUTH_HOLD", roll, pitch));
            }

            _ = ShowHotkeyToastAsync(roll, pitch, "动作触发");
        }

        private async Task ShowHotkeyToastAsync(double roll, double pitch, string source)
        {
            if (_mouthTriggerDialogOpen)
            {
                return;
            }

            FrameworkElement? root = Content as FrameworkElement;
            if (root?.XamlRoot is null)
            {
                return;
            }

            _mouthTriggerDialogOpen = true;
            try
            {
                SendSystemHotkey(VirtualKeyControl, VirtualKeyMenu, VirtualKeyQ);
                ContentDialog dialog = new()
                {
                    XamlRoot = root.XamlRoot,
                    Title = "热键已发送",
                    Content = string.Create(
                        CultureInfo.InvariantCulture,
                        $"{source}: Ctrl+Alt+Q\nroll={roll:F1}, pitch={pitch:F1}"),
                };

                _ = Task.Run(async () =>
                {
                    await Task.Delay(1200);
                    EnqueueUi(() =>
                    {
                        try
                        {
                            dialog.Hide();
                        }
                        catch
                        {
                            // Ignore if dialog is already closed.
                        }
                    });
                });

                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                AppendLog($"Dialog warning: {ex.Message}");
            }
            finally
            {
                _mouthTriggerDialogOpen = false;
            }
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

            AppendLog("Hotkey sent: Ctrl+Alt+Q");
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

        private static bool TryBuildPoseFromCalibratedGravity(ImuCalibratedSample calibrated, out double roll, out double pitch, out double yaw)
        {
            roll = 0;
            pitch = 0;
            yaw = 0;

            (double mx, double my, double mz) = MapAccelToViewerAxes(
                calibrated.GravityX,
                calibrated.GravityY,
                calibrated.GravityZ);

            // 先用加速度估计姿态角，作为实时可视化基线。
            double radToDeg = 180.0 / Math.PI;
            roll = Math.Atan2(my, mz) * radToDeg;
            pitch = Math.Atan2(-mx, Math.Sqrt(my * my + mz * mz)) * radToDeg;
            yaw = 0;
            return true;
        }

        private static (double x, double y, double z) MapAccelToViewerAxes(double ax, double ay, double az)
        {
            // Pair mapping baseline (from current LR/FB captures):
            // - LR pair dominates sensor X, used as viewer Y (roll input), inverted for natural hand tilt.
            // - FB pair dominates sensor Y, used as viewer X (pitch input), inverted for forward-down behavior.
            // - viewer Z keeps sensor Z.
            return (-ay, -ax, az);
        }

        private static bool TryParseImuRaw(
            string payload,
            out ImuRawSample sample)
        {
            sample = default;
            string[] parts = payload.Split(',');
            if (parts.Length < 9 || !string.Equals(parts[0], "IMU", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ax)
                || !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ay)
                || !int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int az)
                || !int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int gx)
                || !int.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int gy)
                || !int.TryParse(parts[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out int gz)
                || !int.TryParse(parts[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out int temp))
            {
                return false;
            }

            sample = new ImuRawSample(ax, ay, az, gx, gy, gz, temp);
            return true;
        }

        private void EnqueueUi(Action action)
        {
            DispatcherQueue.TryEnqueue(() => action());
        }

        private static string EscapeJson(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
