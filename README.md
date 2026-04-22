# esp32Learning Workspace

This repository contains ESP32 learning code, experiments, and host-side tooling.

## Directory Layout

- `arduino/`
  - Small Arduino sketches for ESP32-C3 bring-up and basics.
- `codes/`
  - Main learning examples (BLE, Wi-Fi, TCP, provisioning, etc.).
- `tools/`
  - Host-side helper tools.
  - Includes C++ BLE client used to test and co-debug firmware behavior.
- `doc/`
  - Reference documents and third-party study materials.
- `esp-idf/`
  - Full ESP-IDF source tree for IDF-based development and reference.

## Recommended Development Flow

1. Firmware side
   - Start from Arduino examples under `codes/examples/` or `arduino/`.
   - Validate serial logs first, then BLE/Wi-Fi behavior.
2. Host-side co-debug
   - Use `tools/ble_client_cpp/BleGestureClient` to test BLE connection and command exchange from Windows.
3. IDF migration path
   - Keep feature prototypes in Arduino first.
   - Move stable features to ESP-IDF projects when needed.

## BLE Co-Debug Entry Points

- ESP32 Arduino example:
  - `codes/examples/bleLedControl/bleLedControl.ino`
- Windows C++ BLE client:
  - `tools/ble_client_cpp/BleGestureClient/BleGestureClient/BleGestureClient.cpp`

## Git Notes

- Repository root is `esp32/`.
- Ignore rules are centralized in root `.gitignore`.
- Keep generated files and IDE cache out of commits.

## Next Step (Formal Development)

Suggested immediate target:
- Define one stable BLE command protocol (request/response + error codes), then implement the same protocol on both:
  - ESP32 firmware command handler
  - Windows BLE C++ client
