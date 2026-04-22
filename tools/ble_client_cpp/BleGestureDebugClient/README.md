# BleGestureDebugClient

A C++/WinRT BLE debug client for ESP32 gesture demos.

## Purpose

- Scan and connect to ESP32 BLE device by name/service UUID.
- Subscribe to status notifications.
- Print incoming payloads to console.
- Append logs to `gesture_ble_log.txt` for protocol debugging.

## Target BLE Profile

- Device name: `ESP32C3-BLE-GESTURE`
- Service UUID: `7b1e0001-0000-4bcd-1234-1234567890ab`
- Status characteristic UUID: `7b1e0003-0000-4bcd-1234-1234567890ab`

## Build

1. Open `BleGestureDebugClient.slnx` in Visual Studio 2022.
2. Select `x64` and `Debug`.
3. Build and run.

Or build from PowerShell with absolute MSBuild path:

`"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" "BleGestureDebugClient.slnx" /t:Build /p:Configuration=Debug /p:Platform=x64`

## Run behavior

- The app scans for target advertisements.
- On connection, it discovers the status characteristic.
- It enables `Notify` when available.
- It performs one initial `Read` when available.
- It starts a 1.5s read-poll fallback and only logs changed payloads.
- It logs each RX payload with timestamp.
- Press `Enter` to stop listening.
- If BLE session fails, it retries up to 10 times.

## Next extension points

- Add another characteristic for gesture data stream.
- Serialize logs as JSON for easier post analysis.
- Move BLE logic into a reusable class for future WinUI app.
