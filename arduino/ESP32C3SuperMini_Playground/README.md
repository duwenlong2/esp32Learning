# ESP32-C3 SuperMini Playground (Arduino IDE)

This folder is designed to be shared between Arduino IDE and VS Code.

## Hardware

1. Connect ESP32-C3 SuperMini to PC with a data-capable USB Type-C cable.
2. Use USB only during development (do not power from external battery at the same time).

## Arduino IDE Setup

1. Install Arduino IDE 2.x.
2. Open Preferences and add this Additional Boards Manager URL:
   https://raw.githubusercontent.com/espressif/arduino-esp32/gh-pages/package_esp32_index.json
3. Open Boards Manager, install latest `esp32` by Espressif.
4. Select board: `ESP32C3 Dev Module`.
5. Select port: `COM3`.
6. Set `USB CDC On Boot` to `Enabled`.

## Debug Order

1. Upload `01_Blink/01_Blink.ino`.
   - Expected: onboard LED on GPIO8 blinks every 500 ms.
2. Upload `02_SerialEcho/02_SerialEcho.ino`.
   - Open Serial Monitor, baud 115200.
   - Type text and press Enter.
   - Expected: board prints `RX:` and text length, LED toggles each message.
3. Upload `03_WiFiHttpGet/03_WiFiHttpGet.ino`.
   - Replace `YOUR_WIFI_SSID` and `YOUR_WIFI_PASSWORD`.
   - Expected: board prints IP and periodic HTTP response status/payload.

## If Upload Fails

1. Hold BOOT, press RESET, release RESET, then release BOOT.
2. Re-select COM port and try upload again.
3. Press RESET once after successful upload if app does not start.

## Next Step

After these 3 pass, we will add MPU6050 via I2C and verify sensor data.
