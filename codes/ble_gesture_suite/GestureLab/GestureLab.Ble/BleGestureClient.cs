using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace GestureLab.Ble;

/// <summary>
/// ESP32 手势数据流可复用 BLE 客户端。
/// 调用方推荐顺序：
/// 1) 创建实例。
/// 2) 先注册关键事件（至少 ConnectionStateChanged、Log、ImuFrameProcessed）。
/// 3) 应用启动时调用一次 StartAutoReconnectLoopAsync。
/// 4) 应用退出时调用 StopAutoReconnectLoopAsync。
/// </summary>
public sealed class BleGestureClient : IAsyncDisposable
{
    private enum GestureState
    {
        Idle,
        Moving,
        Holding,
        Cooldown,
    }

    public static readonly Guid ServiceUuid = Guid.Parse("7b1e0001-0000-4bcd-1234-1234567890ab");
    public static readonly Guid StatusUuid = Guid.Parse("7b1e0003-0000-4bcd-1234-1234567890ab");
    public static readonly Guid ImuDataUuid = Guid.Parse("7b1e0004-0000-4bcd-1234-1234567890ab");

    private const string TargetDeviceName = "ESP32C3-BLE-GESTURE";
    private const uint KeyEventFKeyUp = 0x0002;
    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyMenu = 0x12;
    private const ushort VirtualKeyQ = 0x51;
    private const double MoveStartGyroThreshold = 650.0;
    private const double StillGyroThreshold = 700.0;
    private const double HoldBreakGyroThreshold = 2300.0;
    private const double StopPosePitchMin = 14.0;
    private const double StopPosePitchMax = 42.0;
    private const double StopPoseRollMin = -186.0;
    private const double StopPoseRollMax = -120.0;
    private const double StopPoseRollCenter = (StopPoseRollMin + StopPoseRollMax) / 2.0;
    private const double TrajectoryPeakPitchMin = 16.0;
    private const double TrajectoryPitchSpanMin = 2.0;
    private const double TrajectoryAccelRmsMin = 30.0;
    private const double TrajectoryAccelRmsMax = 9000.0;
    private const double TrajectoryMinSeconds = 0.12;
    private const double TrajectoryMaxSeconds = 4.80;
    private const double StableWindowSeconds = 0.24;
    private const int StableWindowMinFrames = 5;
    private const double WritingPrecheckSeconds = 0.16;
    private const int WritingPrecheckMinFrames = 3;
    private const double WritingPosePitchMin = -20.0;
    private const double WritingPosePitchMax = 20.0;
    private const double WritingPoseRollMin = -40.0;
    private const double WritingPoseRollMax = 40.0;
    private const double StableBelowRatioMin = 0.58;
    private const double StableBelowRatioStrong = 0.75;
    private const double StableGyroP50Max = 900.0;
    private const double StableGyroP90Max = 1600.0;
    private const double StableRollSpanMax = 24.0;
    private const double StableHighGyroSpikeThreshold = 1800.0;
    private const int StableHighGyroSpikeMax = 6;
    private const int TrajectoryScoreMin = 3;
    private const double HoldSeconds = 0.18;
    private const double MovingMinSeconds = 0.06;
    private const double JitterWindowSeconds = 0.40;
    private const double JitterGyroRmsMax = 1700.0;
    private const int JitterRollFlipMax = 5;
    private const double CooldownSeconds = 0.8;
    private const double FeatureBufferRetentionSeconds = 4.0;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly ImuPayloadProcessor _imuPayloadProcessor = new();
    private BluetoothLEDevice? _device;
    private GattCharacteristic? _statusCharacteristic;
    private GattCharacteristic? _imuCharacteristic;
    private TaskCompletionSource<bool>? _disconnectSignal;
    private CancellationTokenSource? _autoReconnectCts;
    private Task? _autoReconnectTask;
    private readonly Stopwatch _gestureClock = Stopwatch.StartNew();
    private GestureState _gestureState = GestureState.Idle;
    private long _movingStartTicks;
    private long _holdStartTicks;
    private long _cooldownUntilTicks;
    private double _movingPeakGyro;
    private int _hotkeySentCount;
    private GestureState _lastPublishedGestureState = GestureState.Idle;
    private readonly Queue<MotionFrameFeature> _recentFeatures = new();

    private readonly struct MotionFrameFeature
    {
        public MotionFrameFeature(long ticks, double roll, double pitch, double gyroMagnitude, double linearAccelMagnitude)
        {
            Ticks = ticks;
            Roll = roll;
            Pitch = pitch;
            GyroMagnitude = gyroMagnitude;
            LinearAccelMagnitude = linearAccelMagnitude;
        }

        public long Ticks { get; }
        public double Roll { get; }
        public double Pitch { get; }
        public double GyroMagnitude { get; }
        public double LinearAccelMagnitude { get; }
    }

    private readonly struct TrajectorySummary
    {
        public TrajectorySummary(
            double durationSeconds,
            double endPitch,
            double endRoll,
            double peakPitch,
            double minPitch,
            double accelRms,
            double accelPeak)
        {
            DurationSeconds = durationSeconds;
            EndPitch = endPitch;
            EndRoll = endRoll;
            PeakPitch = peakPitch;
            MinPitch = minPitch;
            AccelRms = accelRms;
            AccelPeak = accelPeak;
        }

        public double DurationSeconds { get; }
        public double EndPitch { get; }
        public double EndRoll { get; }
        public double PeakPitch { get; }
        public double MinPitch { get; }
        public double AccelRms { get; }
        public double AccelPeak { get; }
        public double PitchSpan => PeakPitch - MinPitch;
    }

    private readonly struct StableWindowSummary
    {
        public StableWindowSummary(
            long startTicks,
            long endTicks,
            double endPitch,
            double endRoll,
            double belowRatio,
            double gyroP50,
            double gyroP90,
            double rollSpan,
            int highGyroSpikeCount)
        {
            StartTicks = startTicks;
            EndTicks = endTicks;
            EndPitch = endPitch;
            EndRoll = endRoll;
            BelowRatio = belowRatio;
            GyroP50 = gyroP50;
            GyroP90 = gyroP90;
            RollSpan = rollSpan;
            HighGyroSpikeCount = highGyroSpikeCount;
        }

        public long StartTicks { get; }
        public long EndTicks { get; }
        public double EndPitch { get; }
        public double EndRoll { get; }
        public double BelowRatio { get; }
        public double GyroP50 { get; }
        public double GyroP90 { get; }
        public double RollSpan { get; }
        public int HighGyroSpikeCount { get; }
    }

    /// <summary>
    /// 当前是否已经建立 BLE 连接。
    /// 说明：仅表示链路层连接状态，不代表 IMU 一定有有效数据。
    /// </summary>
    public bool IsConnected => _device is not null && _device.ConnectionStatus == BluetoothConnectionStatus.Connected;

    /// <summary>
    /// 自动重连循环是否处于运行中。
    /// 运行中时，断开后会继续扫描并等待设备重新广播。
    /// </summary>
    public bool IsAutoReconnectActive => _autoReconnectTask is not null && !_autoReconnectTask.IsCompleted;

    /// <summary>
    /// 诊断日志事件：连接尝试、CCCD 配置、重连循环等文本信息。
    /// </summary>
    public event Action<string>? Log;

    /// <summary>
    /// 固件状态特征原始文本事件。
    /// 如果调用方只关心结构化 IMU 帧，可不订阅。
    /// </summary>
    public event Action<string>? StatusPayloadReceived;

    /// <summary>
    /// IMU 特征原始文本事件。
    /// 可选；推荐优先使用 ImuFrameProcessed 获取结构化数据。
    /// </summary>
    public event Action<string>? ImuPayloadReceived;

    /// <summary>
    /// 结构化 IMU 帧事件（已完成解析、自动校准和姿态估计）。
    /// 一般 SDK 调用方应订阅该事件。
    /// </summary>
    public event Action<ImuProcessingResult>? ImuFrameProcessed;

    /// <summary>
    /// IMU 处理失败事件（解析失败或姿态计算失败）。
    /// 适合做现场排障和遥测统计。
    /// </summary>
    public event Action<ImuProcessingStatus, string>? ImuFrameProcessingFailed;

    /// <summary>
    /// BLE 生命周期事件：Disconnected/Scanning/Connected。
    /// 需要做 UI 状态同步时应订阅。
    /// </summary>
    public event Action<BleConnectionLifecycleState>? ConnectionStateChanged;

    /// <summary>
    /// 手势状态变化事件（例如 Idle/Moving/Holding/Cooldown），含简要原因。
    /// </summary>
    public event Action<string, string>? GestureStateChanged;

    /// <summary>
    /// 热键发送完成事件：手势名、累计次数、发送时间。
    /// </summary>
    public event Action<string, int, DateTime>? HotkeySent;

    /// <summary>
    /// 手势触发回调事件：上层可注册该事件直接获取触发结果。
    /// 这比监听全局热键更直接，推荐作为业务触发信号。
    /// </summary>
    public event Action<GestureTriggerResult>? GestureTriggered;

    /// <summary>
    /// 单次连接流程：扫描、连设备、发现服务与特征、开启通知。
    /// 一般不建议业务层直接循环调用；推荐使用 StartAutoReconnectLoopAsync 持续维护连接。
    /// </summary>
    /// <param name="scanTimeout">本次扫描超时时间。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task ConnectAsync(TimeSpan scanTimeout, CancellationToken cancellationToken)
    {
        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            await DisconnectCoreAsync();
            RaiseConnectionState(BleConnectionLifecycleState.Scanning);

            ulong address = await ScanTargetAddressAsync(scanTimeout, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (_device is null)
            {
                throw new InvalidOperationException("Failed to open BLE device from discovered address.");
            }

            _device.ConnectionStatusChanged += Device_ConnectionStatusChanged;
            Log?.Invoke($"Connected device: {_device.Name}");

            (_statusCharacteristic, _imuCharacteristic) = await DiscoverCharacteristicsAsync(_device, cancellationToken);

            await EnableNotifyIfSupportedAsync(_statusCharacteristic, StatusCharacteristic_ValueChanged, cancellationToken);
            await EnableNotifyIfSupportedAsync(_imuCharacteristic, ImuCharacteristic_ValueChanged, cancellationToken);

            await ReadAndEmitIfSupportedAsync(_statusCharacteristic, StatusPayloadReceived, cancellationToken);
            await ReadAndEmitIfSupportedAsync(_imuCharacteristic, HandleImuPayloadText, cancellationToken);

            _disconnectSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            RaiseConnectionState(BleConnectionLifecycleState.Connected);
            Log?.Invoke("BLE notify subscriptions are active.");
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    /// <summary>
    /// 启动自动重连循环。
    /// 典型用法：应用启动时调用一次，之后由 SDK 自动处理“断电重启后的回连”。
    /// </summary>
    /// <param name="scanTimeout">每次扫描的超时时间。</param>
    /// <param name="cancellationToken">外部取消令牌（例如应用退出时取消）。</param>
    public Task StartAutoReconnectLoopAsync(TimeSpan scanTimeout, CancellationToken cancellationToken = default)
    {
        if (IsAutoReconnectActive)
        {
            Log?.Invoke("Auto reconnect is already active.");
            return Task.CompletedTask;
        }

        _autoReconnectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _autoReconnectTask = MaintainConnectionLoopAsync(scanTimeout, _autoReconnectCts.Token);
        Log?.Invoke("Auto reconnect armed. Host will keep waiting for BLE advertisements.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 停止重连循环并释放当前 BLE 连接。
    /// 主程序退出时必须调用。
    /// </summary>
    public async Task StopAutoReconnectLoopAsync()
    {
        if (_autoReconnectCts is not null)
        {
            _autoReconnectCts.Cancel();
        }

        if (_autoReconnectTask is not null)
        {
            try
            {
                await _autoReconnectTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _autoReconnectTask = null;
        _autoReconnectCts?.Dispose();
        _autoReconnectCts = null;

        await DisconnectAsync();
    }

    /// <summary>
    /// 主动断开当前连接。
    /// 通常配合 StopAutoReconnectLoopAsync 使用；单独调用不会停止自动重连循环本身。
    /// </summary>
    public async Task DisconnectAsync()
    {
        await _connectionGate.WaitAsync();
        try
        {
            await DisconnectCoreAsync();
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    /// <summary>
    /// 释放 SDK 资源。
    /// 等价于先停止重连，再断开并释放 BLE 相关对象。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await StopAutoReconnectLoopAsync();
    }

    /// <summary>
    /// 自动重连主循环：连接成功后等待断开，断开后继续扫描重试。
    /// </summary>
    private async Task MaintainConnectionLoopAsync(TimeSpan scanTimeout, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConnectAsync(scanTimeout, cancellationToken);
                await WaitForDisconnectAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (TimeoutException ex)
            {
                Log?.Invoke($"Auto reconnect scan timeout: {ex.Message}");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Auto reconnect attempt failed: {ex.Message}");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            RaiseConnectionState(BleConnectionLifecycleState.Scanning);
            await Task.Delay(500, cancellationToken);
        }
    }

    /// <summary>
    /// 在已连接状态下等待链路断开信号。
    /// 用于让重连循环在“断开事件发生后”进入下一次扫描。
    /// </summary>
    private Task WaitForDisconnectAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            return Task.CompletedTask;
        }

        _disconnectSignal ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return _disconnectSignal.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// 执行底层断开与清理：取消订阅、关闭通知、释放设备对象。
    /// </summary>
    private async Task DisconnectCoreAsync()
    {
        _disconnectSignal?.TrySetResult(true);
        _disconnectSignal = null;

        if (_statusCharacteristic is not null)
        {
            _statusCharacteristic.ValueChanged -= StatusCharacteristic_ValueChanged;
            await DisableNotifySafeAsync(_statusCharacteristic);
        }

        if (_imuCharacteristic is not null)
        {
            _imuCharacteristic.ValueChanged -= ImuCharacteristic_ValueChanged;
            await DisableNotifySafeAsync(_imuCharacteristic);
        }

        if (_device is not null)
        {
            _device.ConnectionStatusChanged -= Device_ConnectionStatusChanged;
            _device.Dispose();
            _device = null;
        }

        _statusCharacteristic = null;
        _imuCharacteristic = null;
        RaiseConnectionState(BleConnectionLifecycleState.Disconnected);
    }

    /// <summary>
    /// 扫描目标设备地址。
    /// 匹配条件：设备名或服务 UUID 任一命中即可。
    /// </summary>
    private async Task<ulong> ScanTargetAddressAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };

        TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs> handler =
            (sender, args) =>
            {
                bool nameMatched = string.Equals(args.Advertisement.LocalName, TargetDeviceName, StringComparison.Ordinal);
                bool serviceMatched = args.Advertisement.ServiceUuids.Any(uuid => uuid == ServiceUuid);

                if (nameMatched || serviceMatched)
                {
                    if (tcs.TrySetResult(args.BluetoothAddress))
                    {
                        Log?.Invoke($"Found target advertisement addr={FormatAddress(args.BluetoothAddress)} reason={(nameMatched ? "name" : "service")}");
                    }
                }
            };

        watcher.Received += handler;
        Log?.Invoke($"Scanning BLE target: {TargetDeviceName}");
        watcher.Start();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            using var _ = timeoutCts.Token.Register(() => tcs.TrySetCanceled(timeoutCts.Token));
            return await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException("Scan timeout: target BLE device not found.");
        }
        finally
        {
            watcher.Stop();
            watcher.Received -= handler;
        }
    }

    /// <summary>
    /// 根据蓝牙地址生成可读字符串（AA:BB:CC:DD:EE:FF）。
    /// </summary>
    private static string FormatAddress(ulong address)
    {
        Span<byte> bytes = stackalloc byte[6];
        for (int i = 0; i < 6; i++)
        {
            bytes[5 - i] = (byte)((address >> (8 * i)) & 0xFF);
        }

        return string.Join(':', bytes.ToArray().Select(b => b.ToString("X2")));
    }

    /// <summary>
    /// 发现服务与关键特征（状态特征、IMU 特征）。
    /// 内部带重试，避免设备刚上电时服务枚举尚未稳定。
    /// </summary>
    private async Task<(GattCharacteristic status, GattCharacteristic imu)> DiscoverCharacteristicsAsync(
        BluetoothLEDevice device,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= 6; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Log?.Invoke($"Discover GATT service... attempt {attempt}/6");

            var serviceResult = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            if (serviceResult.Status != GattCommunicationStatus.Success)
            {
                await Task.Delay(400, cancellationToken);
                continue;
            }

            var service = serviceResult.Services.FirstOrDefault(s => s.Uuid == ServiceUuid);
            if (service is null)
            {
                await Task.Delay(400, cancellationToken);
                continue;
            }

            var characteristicResult = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (characteristicResult.Status != GattCommunicationStatus.Success)
            {
                await Task.Delay(400, cancellationToken);
                continue;
            }

            var map = characteristicResult.Characteristics.ToDictionary(c => c.Uuid, c => c);
            if (!map.TryGetValue(StatusUuid, out var statusChar))
            {
                throw new InvalidOperationException("Status characteristic not found.");
            }

            if (!map.TryGetValue(ImuDataUuid, out var imuChar))
            {
                throw new InvalidOperationException("IMU characteristic not found.");
            }

            Log?.Invoke("Status and IMU characteristics discovered.");
            return (statusChar, imuChar);
        }

        throw new InvalidOperationException("Failed to discover service/characteristics after retries.");
    }

    /// <summary>
    /// 为特征开启 Notify/Indicate 并绑定回调。
    /// </summary>
    private async Task EnableNotifyIfSupportedAsync(
        GattCharacteristic characteristic,
        TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> valueChangedHandler,
        CancellationToken cancellationToken)
    {
        var props = characteristic.CharacteristicProperties;
        bool canNotify = (props & GattCharacteristicProperties.Notify) == GattCharacteristicProperties.Notify;
        bool canIndicate = (props & GattCharacteristicProperties.Indicate) == GattCharacteristicProperties.Indicate;

        characteristic.ValueChanged += valueChangedHandler;

        if (!canNotify && !canIndicate)
        {
            Log?.Invoke($"Characteristic {characteristic.Uuid} does not support notify/indicate.");
            return;
        }

        var cccdValue = canNotify
            ? GattClientCharacteristicConfigurationDescriptorValue.Notify
            : GattClientCharacteristicConfigurationDescriptorValue.Indicate;

        var cccdResult = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(cccdValue).AsTask(cancellationToken);
        if (cccdResult == GattCommunicationStatus.Success)
        {
            Log?.Invoke($"CCCD enabled for {characteristic.Uuid} ({cccdValue}).");
            return;
        }

        throw new InvalidOperationException($"Failed to enable CCCD for characteristic {characteristic.Uuid}.");
    }

    /// <summary>
    /// 若特征支持 Read，则读一次初值并上抛。
    /// </summary>
    private async Task ReadAndEmitIfSupportedAsync(GattCharacteristic characteristic, Action<string>? onPayload, CancellationToken cancellationToken)
    {
        var props = characteristic.CharacteristicProperties;
        bool canRead = (props & GattCharacteristicProperties.Read) == GattCharacteristicProperties.Read;
        if (!canRead)
        {
            return;
        }

        var readResult = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken);
        if (readResult.Status == GattCommunicationStatus.Success)
        {
            onPayload?.Invoke(BufferToUtf8OrHex(readResult.Value));
        }
    }

    /// <summary>
    /// 安全关闭特征通知，忽略回收路径中的异常。
    /// </summary>
    private async Task DisableNotifySafeAsync(GattCharacteristic characteristic)
    {
        try
        {
            await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.None);
        }
        catch
        {
        }
    }

    /// <summary>
    /// 设备连接状态变更回调。
    /// 断开时触发等待任务完成，并发布 Disconnected 生命周期事件。
    /// </summary>
    private void Device_ConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        Log?.Invoke($"Link status: {sender.ConnectionStatus}");

        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
        {
            _disconnectSignal?.TrySetResult(true);
            RaiseConnectionState(BleConnectionLifecycleState.Disconnected);
        }
    }

    /// <summary>
    /// 固件状态特征通知回调。
    /// </summary>
    private void StatusCharacteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        StatusPayloadReceived?.Invoke(BufferToUtf8OrHex(args.CharacteristicValue));
    }

    /// <summary>
    /// IMU 特征通知回调：进入统一文本处理管线。
    /// </summary>
    private void ImuCharacteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        HandleImuPayloadText(BufferToUtf8OrHex(args.CharacteristicValue));
    }

    /// <summary>
    /// IMU 文本统一处理入口：
    /// 1) 上抛原始文本（可选）
    /// 2) 解析+校准+姿态估计
    /// 3) 成功则触发结构化帧与手势判定；失败则触发失败事件
    /// </summary>
    private void HandleImuPayloadText(string payload)
    {
        ImuPayloadReceived?.Invoke(payload);

        ImuProcessingStatus status = _imuPayloadProcessor.Process(payload, out ImuProcessingResult result);
        if (status == ImuProcessingStatus.Success)
        {
            ImuFrameProcessed?.Invoke(result);
            UpdateGestureAndMaybeSendHotkey(result);
            return;
        }

        ImuFrameProcessingFailed?.Invoke(status, payload);
    }

    /// <summary>
    /// 发布 BLE 生命周期状态。
    /// </summary>
    private void RaiseConnectionState(BleConnectionLifecycleState state)
    {
        ConnectionStateChanged?.Invoke(state);
    }

    /// <summary>
    /// SDK 内置手势状态机：根据姿态窗口与角速度阈值进行触发。
    /// 命中后自动发送热键，并通过 GestureTriggered/HotkeySent 对外回调。
    /// </summary>
    private void UpdateGestureAndMaybeSendHotkey(ImuProcessingResult frame)
    {
        long nowTicks = _gestureClock.ElapsedTicks;
        double correctedGx = frame.Raw.Gx - frame.Calibrated.GyroBiasX;
        double correctedGy = frame.Raw.Gy - frame.Calibrated.GyroBiasY;
        double correctedGz = frame.Raw.Gz - frame.Calibrated.GyroBiasZ;
        double gyroMagnitude = Math.Sqrt((correctedGx * correctedGx) + (correctedGy * correctedGy) + (correctedGz * correctedGz));
        double linearAccelMagnitude = Math.Sqrt(
            (frame.Calibrated.LinearX * frame.Calibrated.LinearX)
            + (frame.Calibrated.LinearY * frame.Calibrated.LinearY)
            + (frame.Calibrated.LinearZ * frame.Calibrated.LinearZ));
        double canonicalRoll = CanonicalizeRoll(frame.Pose.Roll);
        PushFeatureFrame(nowTicks, canonicalRoll, frame.Pose.Pitch, gyroMagnitude, linearAccelMagnitude);
        AnalyzeRecentJitter(nowTicks, out int rollFlipCount, out double gyroRms);
        bool inStopPose = IsInStopPose(frame.Pose.Pitch, canonicalRoll);

        string reason;
        switch (_gestureState)
        {
            case GestureState.Idle:
                if (gyroMagnitude >= MoveStartGyroThreshold)
                {
                    if (IsInWritingStartPose(nowTicks, out string precheckReason))
                    {
                        _gestureState = GestureState.Moving;
                        _movingStartTicks = nowTicks;
                        _movingPeakGyro = gyroMagnitude;
                        reason = "检测到抬手运动";
                    }
                    else
                    {
                        reason = precheckReason;
                    }
                }
                else
                {
                    reason = "等待抬手";
                }
                break;

            case GestureState.Moving:
                _movingPeakGyro = Math.Max(_movingPeakGyro, gyroMagnitude);
                double movingElapsedSeconds = (nowTicks - _movingStartTicks) / (double)Stopwatch.Frequency;
                bool movingPrepared = movingElapsedSeconds >= MovingMinSeconds && _movingPeakGyro >= MoveStartGyroThreshold;
                bool jitterAcceptable = rollFlipCount <= JitterRollFlipMax && gyroRms <= JitterGyroRmsMax;

                if (movingPrepared && TryFindStableWindowSummary(nowTicks, out StableWindowSummary stableWindow, out string stableReason))
                {
                    if (!jitterAcceptable)
                    {
                        _gestureState = GestureState.Idle;
                        _movingPeakGyro = 0;
                        reason = string.Create(
                            CultureInfo.InvariantCulture,
                            $"停止后抖动过大: flips={rollFlipCount}, rms={gyroRms:F0}");
                        break;
                    }

                    if (TryBuildTrajectorySummary(stableWindow, out TrajectorySummary summary, out string summaryReason))
                    {
                        _gestureState = GestureState.Holding;
                        _holdStartTicks = nowTicks;
                        reason = string.Create(
                            CultureInfo.InvariantCulture,
                            $"停止轨迹匹配: end=({summary.EndPitch:F1},{summary.EndRoll:F1}) p90={stableWindow.GyroP90:F0}");
                    }
                    else
                    {
                        _gestureState = GestureState.Idle;
                        _movingPeakGyro = 0;
                        reason = summaryReason;
                    }
                }
                else if (movingElapsedSeconds >= (TrajectoryMaxSeconds + 0.8) && gyroMagnitude <= StableGyroP50Max)
                {
                    _gestureState = GestureState.Idle;
                    _movingPeakGyro = 0;
                    reason = "运动周期超时，重置为 Idle";
                }
                else if (!movingPrepared)
                {
                    reason = string.Create(
                        CultureInfo.InvariantCulture,
                        $"过渡不足: t={movingElapsedSeconds:F2}s, peak={_movingPeakGyro:F0}");
                }
                else
                {
                    TryFindStableWindowSummary(nowTicks, out _, out string stableReasonPreview);
                    reason = stableReasonPreview;
                }
                break;

            case GestureState.Holding:
                if (!inStopPose)
                {
                    _gestureState = gyroMagnitude >= MoveStartGyroThreshold ? GestureState.Moving : GestureState.Idle;
                    if (_gestureState == GestureState.Moving)
                    {
                        _movingStartTicks = nowTicks;
                        _movingPeakGyro = gyroMagnitude;
                    }
                    reason = "Holding 退出：姿态离开停止窗口";
                    break;
                }

                if (rollFlipCount > JitterRollFlipMax || gyroRms > JitterGyroRmsMax)
                {
                    _gestureState = GestureState.Moving;
                    _movingStartTicks = nowTicks;
                    _movingPeakGyro = gyroMagnitude;
                    reason = string.Create(
                        CultureInfo.InvariantCulture,
                        $"Holding 中断：jitter flips={rollFlipCount}, rms={gyroRms:F0}");
                    break;
                }

                if (gyroMagnitude >= HoldBreakGyroThreshold)
                {
                    _gestureState = GestureState.Moving;
                    _movingStartTicks = nowTicks;
                    _movingPeakGyro = gyroMagnitude;
                    reason = "Holding 中断：角速度过大";
                    break;
                }

                if (((nowTicks - _holdStartTicks) / (double)Stopwatch.Frequency) >= HoldSeconds
                    && nowTicks >= _cooldownUntilTicks)
                {
                    bool hotkeySendOk = SendSystemHotkey(VirtualKeyControl, VirtualKeyMenu, VirtualKeyQ);
                    _hotkeySentCount++;
                    _cooldownUntilTicks = nowTicks + (long)(CooldownSeconds * Stopwatch.Frequency);
                    _gestureState = GestureState.Cooldown;
                    _movingPeakGyro = 0;

                    DateTime sentTime = DateTime.Now;
                    var triggerResult = new GestureTriggerResult(
                        gestureName: "MOUTH_HOLD",
                        hotkeySent: hotkeySendOk,
                        triggerCount: _hotkeySentCount,
                        triggeredAt: sentTime,
                        pose: frame.Pose);
                    GestureTriggered?.Invoke(triggerResult);

                    if (hotkeySendOk)
                    {
                        HotkeySent?.Invoke("MOUTH_HOLD", _hotkeySentCount, sentTime);
                        Log?.Invoke("热键发送完成: Ctrl+Alt+Q");
                    }
                    else
                    {
                        Log?.Invoke("热键发送失败: Ctrl+Alt+Q");
                    }
                    reason = "触发成功，进入 Cooldown";
                }
                else
                {
                    reason = "Holding 计时中";
                }
                break;

            case GestureState.Cooldown:
                if (nowTicks >= _cooldownUntilTicks && gyroMagnitude <= StillGyroThreshold)
                {
                    _gestureState = GestureState.Idle;
                    _movingPeakGyro = 0;
                    reason = "Cooldown 结束";
                }
                else
                {
                    reason = "Cooldown 中";
                }
                break;

            default:
                reason = "状态未知";
                break;
        }

        if (_gestureState != _lastPublishedGestureState)
        {
            _lastPublishedGestureState = _gestureState;
            GestureStateChanged?.Invoke(_gestureState.ToString(), reason);
        }
    }

    private static bool IsInStopPose(double pitch, double roll)
    {
        double canonicalRoll = CanonicalizeRoll(roll);
        return pitch >= StopPosePitchMin
            && pitch <= StopPosePitchMax
            && canonicalRoll >= StopPoseRollMin
            && canonicalRoll <= StopPoseRollMax;
    }

    private static double CanonicalizeRoll(double roll)
    {
        return NormalizeAngleToReference(roll, StopPoseRollCenter);
    }

    private static double NormalizeAngleToReference(double angle, double reference)
    {
        double delta = angle - reference;
        while (delta > 180.0)
        {
            delta -= 360.0;
        }

        while (delta < -180.0)
        {
            delta += 360.0;
        }

        return reference + delta;
    }

    private bool IsInWritingStartPose(long nowTicks, out string reason)
    {
        reason = "起笔预检未通过";
        long windowTicks = (long)(WritingPrecheckSeconds * Stopwatch.Frequency);
        long minTicks = nowTicks - windowTicks;

        List<MotionFrameFeature> preWindow = new();
        foreach (MotionFrameFeature frame in _recentFeatures)
        {
            if (frame.Ticks >= minTicks && frame.Ticks < nowTicks)
            {
                preWindow.Add(frame);
            }
        }

        if (preWindow.Count < WritingPrecheckMinFrames)
        {
            reason = "起笔预检采样不足";
            return false;
        }

        double avgPitch = preWindow.Average(x => x.Pitch);
        double avgRoll = preWindow.Average(x => x.Roll);
        if (avgPitch < WritingPosePitchMin || avgPitch > WritingPosePitchMax
            || avgRoll < WritingPoseRollMin || avgRoll > WritingPoseRollMax)
        {
            reason = string.Create(
                CultureInfo.InvariantCulture,
                $"起笔姿态非书写态: pre=({avgPitch:F1},{avgRoll:F1})");
            return false;
        }

        reason = "起笔预检通过";
        return true;
    }

    private bool TryFindStableWindowSummary(long nowTicks, out StableWindowSummary summary, out string reason)
    {
        summary = default;
        reason = "等待稳定窗口";

        long windowTicks = (long)(StableWindowSeconds * Stopwatch.Frequency);
        long minTicks = nowTicks - windowTicks;

        List<MotionFrameFeature> window = new();
        foreach (MotionFrameFeature frame in _recentFeatures)
        {
            if (frame.Ticks >= minTicks && frame.Ticks <= nowTicks && frame.Ticks >= _movingStartTicks)
            {
                window.Add(frame);
            }
        }

        if (window.Count < StableWindowMinFrames)
        {
            reason = "稳定窗口采样不足";
            return false;
        }

        List<double> gyros = window.Select(x => x.GyroMagnitude).OrderBy(x => x).ToList();
        double gyroP50 = PercentileSorted(gyros, 0.5);
        double gyroP90 = PercentileSorted(gyros, 0.9);
        double belowRatio = window.Count(x => x.GyroMagnitude <= StableGyroP50Max) / (double)window.Count;
        double rollSpan = window.Max(x => x.Roll) - window.Min(x => x.Roll);
        int highGyroSpikeCount = window.Count(x => x.GyroMagnitude >= StableHighGyroSpikeThreshold);
        double endPitch = window.Average(x => x.Pitch);
        double endRoll = window.Average(x => x.Roll);

        summary = new StableWindowSummary(
            startTicks: window[0].Ticks,
            endTicks: window[^1].Ticks,
            endPitch: endPitch,
            endRoll: endRoll,
            belowRatio: belowRatio,
            gyroP50: gyroP50,
            gyroP90: gyroP90,
            rollSpan: rollSpan,
            highGyroSpikeCount: highGyroSpikeCount);

        if (belowRatio < StableBelowRatioMin
            || gyroP50 > StableGyroP50Max
            || gyroP90 > StableGyroP90Max
            || rollSpan > StableRollSpanMax
            || highGyroSpikeCount > StableHighGyroSpikeMax)
        {
            reason = string.Create(
                CultureInfo.InvariantCulture,
                $"稳定不足: ratio={belowRatio:F2}, p50={gyroP50:F0}, p90={gyroP90:F0}, rSpan={rollSpan:F1}, spikes={highGyroSpikeCount}");
            return false;
        }

        reason = "末端稳定窗口已满足";
        return true;
    }

    private bool TryBuildTrajectorySummary(StableWindowSummary stableWindow, out TrajectorySummary summary, out string reason)
    {
        summary = default;
        reason = "轨迹不足";

        if (_movingStartTicks <= 0)
        {
            reason = "无有效起始轨迹";
            return false;
        }

        List<MotionFrameFeature> segment = new();
        foreach (MotionFrameFeature frame in _recentFeatures)
        {
            if (frame.Ticks >= _movingStartTicks && frame.Ticks <= stableWindow.EndTicks)
            {
                segment.Add(frame);
            }
        }

        if (segment.Count < 6)
        {
            reason = "轨迹采样点不足";
            return false;
        }

        double durationSeconds = (stableWindow.EndTicks - _movingStartTicks) / (double)Stopwatch.Frequency;
        if (durationSeconds < TrajectoryMinSeconds || durationSeconds > TrajectoryMaxSeconds)
        {
            reason = string.Create(
                CultureInfo.InvariantCulture,
                $"轨迹时长异常: {durationSeconds:F2}s");
            return false;
        }

        double endPitch = stableWindow.EndPitch;
        double endRoll = stableWindow.EndRoll;
        double peakPitch = segment.Max(x => x.Pitch);
        double minPitch = segment.Min(x => x.Pitch);
        double accelPeak = segment.Max(x => x.LinearAccelMagnitude);
        double accelRms = Math.Sqrt(segment.Average(x => x.LinearAccelMagnitude * x.LinearAccelMagnitude));
        summary = new TrajectorySummary(durationSeconds, endPitch, endRoll, peakPitch, minPitch, accelRms, accelPeak);

        if (summary.AccelRms < TrajectoryAccelRmsMin || summary.AccelRms > TrajectoryAccelRmsMax)
        {
            reason = string.Create(
                CultureInfo.InvariantCulture,
                $"加速度轨迹不匹配: rms={summary.AccelRms:F0}, peak={summary.AccelPeak:F0}");
            return false;
        }

        int score = 0;
        if (IsInStopPose(endPitch, endRoll))
        {
            score++;
        }

        if (summary.DurationSeconds >= TrajectoryMinSeconds && summary.DurationSeconds <= TrajectoryMaxSeconds)
        {
            score++;
        }

        if (summary.PeakPitch >= TrajectoryPeakPitchMin)
        {
            score++;
        }

        if (summary.PitchSpan >= TrajectoryPitchSpanMin)
        {
            score++;
        }

        if (stableWindow.BelowRatio >= StableBelowRatioStrong)
        {
            score++;
        }

        if (stableWindow.GyroP90 <= 1100)
        {
            score++;
        }

        if (stableWindow.HighGyroSpikeCount <= 2)
        {
            score++;
        }

        if (score < TrajectoryScoreMin)
        {
            reason = string.Create(
                CultureInfo.InvariantCulture,
                $"轨迹评分不足: score={score}/{TrajectoryScoreMin}, end=({endPitch:F1},{endRoll:F1}), peak={summary.PeakPitch:F1}, span={summary.PitchSpan:F1}, ratio={stableWindow.BelowRatio:F2}, spikes={stableWindow.HighGyroSpikeCount}");
            return false;
        }

        return true;
    }

    private static double PercentileSorted(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        int index = (int)Math.Floor((sortedValues.Count - 1) * percentile);
        index = Math.Clamp(index, 0, sortedValues.Count - 1);
        return sortedValues[index];
    }

    private void PushFeatureFrame(long nowTicks, double roll, double pitch, double gyroMagnitude, double linearAccelMagnitude)
    {
        _recentFeatures.Enqueue(new MotionFrameFeature(nowTicks, roll, pitch, gyroMagnitude, linearAccelMagnitude));

        long retentionTicks = (long)(FeatureBufferRetentionSeconds * Stopwatch.Frequency);
        while (_recentFeatures.Count > 0 && (nowTicks - _recentFeatures.Peek().Ticks) > retentionTicks)
        {
            _recentFeatures.Dequeue();
        }
    }

    private void AnalyzeRecentJitter(long nowTicks, out int rollFlipCount, out double gyroRms)
    {
        rollFlipCount = 0;
        gyroRms = 0;

        long windowTicks = (long)(JitterWindowSeconds * Stopwatch.Frequency);
        long minTicks = nowTicks - windowTicks;
        int prevSign = 0;
        int count = 0;
        double gyroSquaredSum = 0;

        foreach (MotionFrameFeature frame in _recentFeatures)
        {
            if (frame.Ticks < minTicks)
            {
                continue;
            }

            count++;
            gyroSquaredSum += frame.GyroMagnitude * frame.GyroMagnitude;

            int sign = Math.Abs(frame.Roll) < 12.0 ? 0 : Math.Sign(frame.Roll);
            if (sign == 0)
            {
                continue;
            }

            if (prevSign != 0 && prevSign != sign)
            {
                rollFlipCount++;
            }

            prevSign = sign;
        }

        if (count > 0)
        {
            gyroRms = Math.Sqrt(gyroSquaredSum / count);
        }
    }

    /// <summary>
    /// 发送系统热键。
    /// 返回值表示调用链是否成功执行（不代表目标应用一定消费该热键）。
    /// </summary>
    private static bool SendSystemHotkey(params ushort[] virtualKeys)
    {
        if (virtualKeys.Length == 0)
        {
            return false;
        }

        try
        {
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

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 将 GATT Buffer 尽量解码成 UTF-8 文本；失败则回退为 HEX 文本。
    /// </summary>
    private static string BufferToUtf8OrHex(IBuffer buffer)
    {
        if (buffer is null || buffer.Length == 0)
        {
            return "<empty>";
        }

        var data = new byte[buffer.Length];
        DataReader.FromBuffer(buffer).ReadBytes(data);

        try
        {
            string text = Encoding.UTF8.GetString(data);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text.Trim('\0', '\r', '\n');
            }
        }
        catch
        {
        }

        return "HEX:" + BitConverter.ToString(data).Replace("-", string.Empty);
    }
}