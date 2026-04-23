using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace GestureLab;

public sealed class BleGestureClient : IAsyncDisposable
{
    public static readonly Guid ServiceUuid = Guid.Parse("7b1e0001-0000-4bcd-1234-1234567890ab");
    public static readonly Guid StatusUuid = Guid.Parse("7b1e0003-0000-4bcd-1234-1234567890ab");
    public static readonly Guid ImuDataUuid = Guid.Parse("7b1e0004-0000-4bcd-1234-1234567890ab");

    private const string TargetDeviceName = "ESP32C3-BLE-GESTURE";

    private BluetoothLEDevice? _device;
    private GattCharacteristic? _statusCharacteristic;
    private GattCharacteristic? _imuCharacteristic;

    public bool IsConnected => _device is not null && _device.ConnectionStatus == BluetoothConnectionStatus.Connected;

    public event Action<string>? Log;
    public event Action<string>? StatusPayloadReceived;
    public event Action<string>? ImuPayloadReceived;

    public async Task ConnectAsync(TimeSpan scanTimeout, CancellationToken cancellationToken)
    {
        await DisconnectAsync();

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
        await ReadAndEmitIfSupportedAsync(_imuCharacteristic, ImuPayloadReceived, cancellationToken);

        Log?.Invoke("BLE notify subscriptions are active.");
    }

    public async Task DisconnectAsync()
    {
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
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }

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
            await using var _ = timeoutCts.Token.Register(() => tcs.TrySetCanceled(timeoutCts.Token));
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

    private static string FormatAddress(ulong address)
    {
        Span<byte> bytes = stackalloc byte[6];
        for (int i = 0; i < 6; i++)
        {
            bytes[5 - i] = (byte)((address >> (8 * i)) & 0xFF);
        }

        return string.Join(':', bytes.ToArray().Select(b => b.ToString("X2")));
    }

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

    private async Task DisableNotifySafeAsync(GattCharacteristic characteristic)
    {
        try
        {
            await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.None);
        }
        catch
        {
            // ignore shutdown path errors
        }
    }

    private void Device_ConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        Log?.Invoke($"Link status: {sender.ConnectionStatus}");
    }

    private void StatusCharacteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        StatusPayloadReceived?.Invoke(BufferToUtf8OrHex(args.CharacteristicValue));
    }

    private void ImuCharacteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        ImuPayloadReceived?.Invoke(BufferToUtf8OrHex(args.CharacteristicValue));
    }

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
