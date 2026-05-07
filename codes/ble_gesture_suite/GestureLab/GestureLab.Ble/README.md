# GestureLab.Ble integration quick guide

## Required startup sequence

1. Create `BleGestureClient`.
2. Register events before starting reconnect:
   - `ConnectionStateChanged` (required for status UI)
   - `ImuFrameProcessed` (required for typed IMU data)
   - `Log` (recommended for diagnostics)
3. Call `StartAutoReconnectLoopAsync(scanTimeout)` once at app startup.
4. On app exit, call `StopAutoReconnectLoopAsync()` (or `DisposeAsync()`).

## Recommended callback for integrators

For most application integrations, subscribing to `GestureTriggered` is sufficient.

- `GestureTriggered`: Primary business callback. Includes gesture name, whether hotkey send succeeded, trigger count, timestamp, and pose.
- `HotkeySent`: Compatibility callback for hotkey completion only.

## Minimal example

```csharp
using GestureLab.Ble;

var ble = new BleGestureClient();

ble.ConnectionStateChanged += state => Console.WriteLine($"BLE state: {state}");
ble.Log += line => Console.WriteLine($"BLE log: {line}");
ble.ImuFrameProcessed += frame =>
{
    Console.WriteLine($"pose: roll={frame.Pose.Roll:F1}, pitch={frame.Pose.Pitch:F1}, yaw={frame.Pose.Yaw:F1}");
};
ble.ImuFrameProcessingFailed += (status, payload) =>
{
    Console.WriteLine($"IMU process failed: {status}, payload={payload}");
};

await ble.StartAutoReconnectLoopAsync(TimeSpan.FromSeconds(35));

// ...

await ble.StopAutoReconnectLoopAsync();
```
