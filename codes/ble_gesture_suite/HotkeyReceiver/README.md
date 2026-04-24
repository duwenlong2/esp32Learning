# HotkeyReceiver

Standalone listener for `Ctrl+Alt+Q` based on `RegisterHotKey`.

## Build

```powershell
dotnet build .\codes\ble_gesture_suite\HotkeyReceiver\HotkeyReceiver.csproj
```

## Run

```powershell
dotnet run --project .\codes\ble_gesture_suite\HotkeyReceiver\HotkeyReceiver.csproj
```

When hotkey is received, it prints:

`[HotkeyReceiver] HH:mm:ss.fff Ctrl+Alt+Q fired`
