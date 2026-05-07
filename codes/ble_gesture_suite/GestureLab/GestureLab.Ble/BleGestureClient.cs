using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private const double MoveStartGyroThreshold = 900.0;
    private const double StillGyroThreshold = 450.0;
    private const double HoldBreakGyroThreshold = 1400.0;
    private const double PosePitchAbsMin = 24.0;
    private const double PosePitchAbsMax = 88.0;
    private const double PoseRollAbsMax = 86.0;
    private const double HoldSeconds = 0.30;
    private const double CooldownSeconds = 1.2;

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
    private long _holdStartTicks;
    private long _cooldownUntilTicks;
    private int _hotkeySentCount;
    private GestureState _lastPublishedGestureState = GestureState.Idle;

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

        bool inMouthPose = Math.Abs(frame.Pose.Pitch) >= PosePitchAbsMin
            && Math.Abs(frame.Pose.Pitch) <= PosePitchAbsMax
            && Math.Abs(frame.Pose.Roll) <= PoseRollAbsMax;

        string reason;
        switch (_gestureState)
        {
            case GestureState.Idle:
                if (gyroMagnitude >= MoveStartGyroThreshold)
                {
                    _gestureState = GestureState.Moving;
                    reason = "检测到抬手运动";
                }
                else
                {
                    reason = "等待抬手";
                }
                break;

            case GestureState.Moving:
                if (inMouthPose && gyroMagnitude <= StillGyroThreshold)
                {
                    _gestureState = GestureState.Holding;
                    _holdStartTicks = nowTicks;
                    reason = "进入 Holding";
                }
                else if (gyroMagnitude <= StillGyroThreshold && !inMouthPose)
                {
                    _gestureState = GestureState.Idle;
                    reason = "运动结束但未进入目标姿态";
                }
                else
                {
                    reason = "运动中";
                }
                break;

            case GestureState.Holding:
                if (!inMouthPose)
                {
                    _gestureState = gyroMagnitude >= MoveStartGyroThreshold ? GestureState.Moving : GestureState.Idle;
                    reason = "Holding 退出：姿态离开目标窗口";
                    break;
                }

                if (gyroMagnitude >= HoldBreakGyroThreshold)
                {
                    _gestureState = GestureState.Moving;
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