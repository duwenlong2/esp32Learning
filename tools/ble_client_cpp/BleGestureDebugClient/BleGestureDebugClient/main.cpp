#pragma comment(lib, "windowsapp.lib")
#pragma comment(lib, "runtimeobject.lib")

#include <atomic>
#include <chrono>
#include <fstream>
#include <future>
#include <iomanip>
#include <iostream>
#include <mutex>
#include <optional>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>

#include <winrt/Windows.Devices.Bluetooth.h>
#include <winrt/Windows.Devices.Bluetooth.Advertisement.h>
#include <winrt/Windows.Devices.Bluetooth.GenericAttributeProfile.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Security.Cryptography.h>
#include <winrt/Windows.Storage.Streams.h>
#include <winrt/base.h>

using namespace std::chrono_literals;

using winrt::Windows::Devices::Bluetooth::BluetoothLEDevice;
using winrt::Windows::Devices::Bluetooth::BluetoothConnectionStatus;
using winrt::Windows::Devices::Bluetooth::Advertisement::BluetoothLEAdvertisementWatcher;
using winrt::Windows::Devices::Bluetooth::Advertisement::BluetoothLEScanningMode;
using winrt::Windows::Devices::Bluetooth::GenericAttributeProfile::GattCharacteristic;
using winrt::Windows::Devices::Bluetooth::GenericAttributeProfile::GattCharacteristicProperties;
using winrt::Windows::Devices::Bluetooth::GenericAttributeProfile::GattClientCharacteristicConfigurationDescriptorValue;
using winrt::Windows::Devices::Bluetooth::GenericAttributeProfile::GattCommunicationStatus;
using winrt::Windows::Security::Cryptography::BinaryStringEncoding;
using winrt::Windows::Security::Cryptography::CryptographicBuffer;

namespace {

constexpr wchar_t kTargetDeviceName[] = L"ESP32C3-BLE-GESTURE";
const winrt::guid kServiceUuid{ L"7b1e0001-0000-4bcd-1234-1234567890ab" };
const winrt::guid kStatusUuid{ L"7b1e0003-0000-4bcd-1234-1234567890ab" };

std::mutex gLogMutex;

std::string AddressToString(uint64_t address)
{
    std::ostringstream oss;
    oss << std::hex << std::setfill('0') << std::uppercase;
    for (int byte = 5; byte >= 0; --byte) {
        oss << std::setw(2) << ((address >> (byte * 8)) & 0xFF);
        if (byte > 0) {
            oss << ":";
        }
    }
    return oss.str();
}

std::string HResultToString(winrt::hresult_error const& e)
{
    std::ostringstream oss;
    oss << "0x" << std::hex << std::uppercase << static_cast<uint32_t>(e.code().value)
        << " " << winrt::to_string(e.message());
    return oss.str();
}

std::string NowTimestamp()
{
    auto now = std::chrono::system_clock::now();
    auto t = std::chrono::system_clock::to_time_t(now);

    std::tm tm{};
    localtime_s(&tm, &t);

    std::ostringstream oss;
    oss << std::put_time(&tm, "%Y-%m-%d %H:%M:%S");
    return oss.str();
}

void LogLine(std::ofstream& file, std::string const& line)
{
    std::lock_guard<std::mutex> lock(gLogMutex);
    std::cout << line << "\n";
    file << line << "\n";
    file.flush();
}

uint64_t ScanTargetAddress(std::chrono::seconds timeout, std::ofstream& file)
{
    BluetoothLEAdvertisementWatcher watcher;
    std::promise<uint64_t> foundPromise;
    auto foundFuture = foundPromise.get_future();
    std::atomic_bool found{ false };

    watcher.ScanningMode(BluetoothLEScanningMode::Active);

    auto revoker = watcher.Received(winrt::auto_revoke,
        [&foundPromise, &found, &file](auto const&, auto const& args) {
            auto advertisement = args.Advertisement();
            auto localName = advertisement.LocalName();

            bool nameMatched = (localName == kTargetDeviceName);
            bool serviceMatched = false;
            for (auto const& uuid : advertisement.ServiceUuids()) {
                if (uuid == kServiceUuid) {
                    serviceMatched = true;
                    break;
                }
            }

            if (nameMatched || serviceMatched) {
                if (!found.exchange(true)) {
                    std::ostringstream line;
                    line << "[" << NowTimestamp() << "] Found target advertisement"
                        << " addr=" << AddressToString(args.BluetoothAddress())
                        << " reason=" << (nameMatched ? "name" : "service");
                    if (!localName.empty()) {
                        line << " name=" << winrt::to_string(localName);
                    }
                    LogLine(file, line.str());
                    foundPromise.set_value(args.BluetoothAddress());
                }
            }
        });

    LogLine(file, "[" + NowTimestamp() + "] Scanning BLE target: ESP32C3-BLE-GESTURE");
    watcher.Start();

    if (foundFuture.wait_for(timeout) != std::future_status::ready) {
        watcher.Stop();
        throw std::runtime_error("scan timeout: target BLE device not found");
    }

    auto address = foundFuture.get();
    watcher.Stop();
    return address;
}

GattCharacteristic GetStatusCharacteristic(BluetoothLEDevice const& device, std::ofstream& file)
{
    for (int attempt = 1; attempt <= 6; ++attempt) {
        try {
            {
                std::ostringstream line;
                line << "[" << NowTimestamp() << "] Discover GATT service... attempt " << attempt << "/6";
                LogLine(file, line.str());
            }

            auto serviceResult = device.GetGattServicesAsync().get();
            if (serviceResult.Status() != GattCommunicationStatus::Success) {
                std::this_thread::sleep_for(400ms);
                continue;
            }

            for (auto const& service : serviceResult.Services()) {
                if (service.Uuid() != kServiceUuid) {
                    continue;
                }

                auto charResult = service.GetCharacteristicsAsync().get();
                if (charResult.Status() != GattCommunicationStatus::Success) {
                    break;
                }

                for (auto const& characteristic : charResult.Characteristics()) {
                    if (characteristic.Uuid() != kStatusUuid) {
                        continue;
                    }

                    auto props = characteristic.CharacteristicProperties();
                    bool canRead = (props & GattCharacteristicProperties::Read) == GattCharacteristicProperties::Read;
                    bool canNotify = (props & GattCharacteristicProperties::Notify) == GattCharacteristicProperties::Notify;
                    if (!canRead && !canNotify) {
                        throw std::runtime_error("status characteristic has neither READ nor NOTIFY");
                    }

                    LogLine(file, "[" + NowTimestamp() + "] Status characteristic discovered.");
                    return characteristic;
                }
            }
        }
        catch (winrt::hresult_error const& e) {
            LogLine(file, "[" + NowTimestamp() + "] Discover GATT failed: " + HResultToString(e));
        }

        std::this_thread::sleep_for(500ms);
    }

    throw std::runtime_error("failed to discover status characteristic after retries");
}

std::string BufferToUtf8OrHex(winrt::Windows::Storage::Streams::IBuffer const& buffer)
{
    if (buffer == nullptr || buffer.Length() == 0) {
        return "<empty>";
    }

    try {
        auto text = CryptographicBuffer::ConvertBinaryToString(BinaryStringEncoding::Utf8, buffer);
        auto s = winrt::to_string(text);
        if (!s.empty()) {
            return s;
        }
    }
    catch (...) {
    }

    auto hex = CryptographicBuffer::EncodeToHexString(buffer);
    return "HEX:" + winrt::to_string(hex);
}

void SubscribeStatusNotifications(
    GattCharacteristic const& statusChar,
    BluetoothLEDevice const& device,
    std::ofstream& file)
{
    auto props = statusChar.CharacteristicProperties();
    bool canNotify = (props & GattCharacteristicProperties::Notify) == GattCharacteristicProperties::Notify;
    bool canRead = (props & GattCharacteristicProperties::Read) == GattCharacteristicProperties::Read;
    std::atomic_bool stopRequested{ false };
    std::mutex stateMutex;
    std::optional<std::string> lastPolledPayload;

    auto connectionRevoker = device.ConnectionStatusChanged(winrt::auto_revoke,
        [&file](BluetoothLEDevice const& changedDevice, auto const&) {
            auto status = changedDevice.ConnectionStatus();
            if (status == BluetoothConnectionStatus::Connected) {
                LogLine(file, "[" + NowTimestamp() + "] Link status: CONNECTED");
            }
            else {
                LogLine(file, "[" + NowTimestamp() + "] Link status: DISCONNECTED");
            }
        });

    auto valueRevoker = statusChar.ValueChanged(winrt::auto_revoke,
        [&file](GattCharacteristic const&, auto const& args) {
            auto payload = BufferToUtf8OrHex(args.CharacteristicValue());
            LogLine(file, "[" + NowTimestamp() + "] RX notify: " + payload);
        });

    std::thread readPollingThread;
    if (canRead) {
        readPollingThread = std::thread([&]() {
            while (!stopRequested.load()) {
                std::this_thread::sleep_for(1500ms);
                if (stopRequested.load()) {
                    break;
                }

                try {
                    auto readResult = statusChar.ReadValueAsync().get();
                    if (readResult.Status() != GattCommunicationStatus::Success) {
                        continue;
                    }

                    auto payload = BufferToUtf8OrHex(readResult.Value());
                    bool changed = false;
                    {
                        std::lock_guard<std::mutex> lock(stateMutex);
                        if (!lastPolledPayload.has_value() || lastPolledPayload.value() != payload) {
                            lastPolledPayload = payload;
                            changed = true;
                        }
                    }

                    if (changed) {
                        LogLine(file, "[" + NowTimestamp() + "] RX poll: " + payload);
                    }
                }
                catch (winrt::hresult_error const& e) {
                    LogLine(file, "[" + NowTimestamp() + "] Poll read WinRT error: " + HResultToString(e));
                }
            }
            });
    }

    if (canNotify) {
        auto cccd = statusChar.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue::Notify).get();
        if (cccd == GattCommunicationStatus::Success) {
            LogLine(file, "[" + NowTimestamp() + "] Notify enabled.");
        } else {
            LogLine(file, "[" + NowTimestamp() + "] Failed to enable notify.");
        }
    }

    if (canRead) {
        auto readResult = statusChar.ReadValueAsync().get();
        if (readResult.Status() == GattCommunicationStatus::Success) {
            auto payload = BufferToUtf8OrHex(readResult.Value());
            LogLine(file, "[" + NowTimestamp() + "] RX initial read: " + payload);
            {
                std::lock_guard<std::mutex> lock(stateMutex);
                lastPolledPayload = payload;
            }
        }
    }

    LogLine(file, "[" + NowTimestamp() + "] Listening... Press ENTER to stop.");
    std::string line;
    std::getline(std::cin, line);
    stopRequested.store(true);

    if (readPollingThread.joinable()) {
        readPollingThread.join();
    }

    if (canNotify) {
        statusChar.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue::None).get();
        LogLine(file, "[" + NowTimestamp() + "] Notify disabled.");
    }
}

} // namespace

int main()
{
    std::ofstream logFile("gesture_ble_log.txt", std::ios::app);
    if (!logFile.is_open()) {
        std::cerr << "Cannot open gesture_ble_log.txt for writing.\n";
        return 1;
    }

    try {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);

        LogLine(logFile, "============================================================");
        LogLine(logFile, "[" + NowTimestamp() + "] BleGestureDebugClient started.");

        for (int session = 1; session <= 10; ++session) {
            try {
                LogLine(logFile, "[" + NowTimestamp() + "] Start BLE session attempt " + std::to_string(session) + "/10");

                uint64_t address = ScanTargetAddress(35s, logFile);
                LogLine(logFile, "[" + NowTimestamp() + "] Open BLE device from address: " + AddressToString(address));

                auto device = BluetoothLEDevice::FromBluetoothAddressAsync(address).get();
                if (device == nullptr) {
                    throw std::runtime_error("failed to open BLE device");
                }

                LogLine(logFile, "[" + NowTimestamp() + "] Connected device: " + winrt::to_string(device.Name()));

                std::this_thread::sleep_for(700ms);
                auto statusChar = GetStatusCharacteristic(device, logFile);
                SubscribeStatusNotifications(statusChar, device, logFile);

                LogLine(logFile, "[" + NowTimestamp() + "] Client exit.");
                return 0;
            }
            catch (winrt::hresult_error const& e) {
                LogLine(logFile, "[" + NowTimestamp() + "] Session WinRT error: " + HResultToString(e));
            }
            catch (std::exception const& e) {
                LogLine(logFile, "[" + NowTimestamp() + "] Session error: " + std::string(e.what()));
            }

            if (session < 10) {
                LogLine(logFile, "[" + NowTimestamp() + "] Retry after 2 seconds...");
                std::this_thread::sleep_for(2s);
            }
        }

        LogLine(logFile, "[" + NowTimestamp() + "] Reach max retries, exit with failure.");
        return 1;
    }
    catch (winrt::hresult_error const& e) {
        LogLine(logFile, "[" + NowTimestamp() + "] WinRT error: " + HResultToString(e));
        return 1;
    }
    catch (std::exception const& e) {
        LogLine(logFile, "[" + NowTimestamp() + "] Error: " + std::string(e.what()));
        return 1;
    }
}
