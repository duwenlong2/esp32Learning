// BleGestureClient.cpp : 此文件包含 "main" 函数。程序执行将在此处开始并结束。
//

#pragma comment(lib, "windowsapp.lib")
#pragma comment(lib, "runtimeobject.lib")

#include <atomic>
#include <chrono>
#include <iomanip>
#include <future>
#include <iostream>
#include <sstream>
#include <thread>

#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Devices.Bluetooth.h>
#include <winrt/Windows.Devices.Bluetooth.Advertisement.h>
#include <winrt/Windows.Devices.Bluetooth.GenericAttributeProfile.h>
#include <winrt/Windows.Security.Cryptography.h>

using namespace std::chrono_literals;

using winrt::Windows::Devices::Bluetooth::BluetoothLEDevice;
using winrt::Windows::Devices::Bluetooth::Advertisement::BluetoothLEAdvertisementWatcher;
using winrt::Windows::Devices::Bluetooth::Advertisement::BluetoothLEAdvertisementWatcherStatus;
using winrt::Windows::Devices::Bluetooth::Advertisement::BluetoothLEScanningMode;
using winrt::Windows::Devices::Bluetooth::GenericAttributeProfile::GattCharacteristic;
using winrt::Windows::Devices::Bluetooth::GenericAttributeProfile::GattCharacteristicProperties;
using winrt::Windows::Devices::Bluetooth::GenericAttributeProfile::GattClientCharacteristicConfigurationDescriptorValue;
using winrt::Windows::Devices::Bluetooth::GenericAttributeProfile::GattCommunicationStatus;
using winrt::Windows::Devices::Bluetooth::GenericAttributeProfile::GattWriteOption;
using winrt::Windows::Security::Cryptography::BinaryStringEncoding;
using winrt::Windows::Security::Cryptography::CryptographicBuffer;

namespace {

constexpr wchar_t kTargetDeviceName[] = L"ESP32C3-BLE-LED";
const winrt::guid kServiceUuid{ L"6a5a0001-0000-4bcd-1234-1234567890ab" };
const winrt::guid kCommandUuid{ L"6a5a0002-0000-4bcd-1234-1234567890ab" };

std::string HResultToString(winrt::hresult_error const& e)
{
    std::ostringstream oss;
    oss << "0x" << std::hex << std::uppercase << static_cast<uint32_t>(e.code().value)
        << " " << winrt::to_string(e.message());
    return oss.str();
}

uint64_t ScanTargetAddress(std::chrono::seconds timeout)
{
    BluetoothLEAdvertisementWatcher watcher;
    std::promise<uint64_t> foundPromise;
    auto foundFuture = foundPromise.get_future();
    std::atomic_bool found{ false };

    // Active mode requests scan response data, which often contains local name and service UUIDs.
    watcher.ScanningMode(BluetoothLEScanningMode::Active);

    auto revoker = watcher.Received(winrt::auto_revoke,
        [&foundPromise, &found](auto const&, auto const& args) {
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
                    foundPromise.set_value(args.BluetoothAddress());
                }
            }
        });

    std::wcout << L"Scanning BLE device: " << kTargetDeviceName << L"\n";
    watcher.Start();

    if (foundFuture.wait_for(timeout) != std::future_status::ready) {
        watcher.Stop();
        throw std::runtime_error("scan timeout: target BLE device not found");
    }

    auto address = foundFuture.get();
    watcher.Stop();
    return address;
}

GattCharacteristic GetCommandCharacteristic(BluetoothLEDevice const& device)
{
    for (int attempt = 1; attempt <= 5; ++attempt) {
        try {
            std::wcout << L"Discover service... (attempt " << attempt << L"/5)\n";
            auto serviceResult = device.GetGattServicesAsync().get();
            if (serviceResult.Status() != GattCommunicationStatus::Success) {
                std::this_thread::sleep_for(500ms);
                continue;
            }

            bool serviceFound = false;
            for (auto const& service : serviceResult.Services()) {
                if (service.Uuid() != kServiceUuid) {
                    continue;
                }

                serviceFound = true;
                std::wcout << L"Discover command characteristic...\n";
                auto charResult = service.GetCharacteristicsAsync().get();
                if (charResult.Status() != GattCommunicationStatus::Success) {
                    break;
                }

                for (auto const& characteristic : charResult.Characteristics()) {
                    if (characteristic.Uuid() != kCommandUuid) {
                        continue;
                    }

                    auto properties = characteristic.CharacteristicProperties();
                    bool canWrite = (properties & GattCharacteristicProperties::Write) == GattCharacteristicProperties::Write;
                    bool canWriteWithoutResponse =
                        (properties & GattCharacteristicProperties::WriteWithoutResponse) == GattCharacteristicProperties::WriteWithoutResponse;

                    if (!canWrite && !canWriteWithoutResponse) {
                        throw std::runtime_error("command characteristic is not writable");
                    }

                    return characteristic;
                }
            }

            if (!serviceFound) {
                std::wcout << L"Service UUID not found in this attempt.\n";
            }
        }
        catch (winrt::hresult_error const& e) {
            std::cerr << "Discover GATT WinRT error: " << HResultToString(e) << "\n";
        }

        std::this_thread::sleep_for(500ms);
    }

    throw std::runtime_error("failed to discover service/characteristic after retries");
}

void WriteCommand(GattCharacteristic const& commandCharacteristic, wchar_t const* command)
{
    auto buffer = CryptographicBuffer::ConvertStringToBinary(command, BinaryStringEncoding::Utf8);

    auto option = GattWriteOption::WriteWithResponse;
    auto properties = commandCharacteristic.CharacteristicProperties();
    bool canWrite = (properties & GattCharacteristicProperties::Write) == GattCharacteristicProperties::Write;
    if (!canWrite) {
        option = GattWriteOption::WriteWithoutResponse;
    }

    auto writeStatus = commandCharacteristic.WriteValueAsync(buffer, option).get();
    if (writeStatus != GattCommunicationStatus::Success) {
        throw std::runtime_error("failed to write BLE command");
    }

    std::wcout << L"TX command: " << command << L"\n";
}

} // namespace

int main()
{
    try {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);

        uint64_t address = ScanTargetAddress(30s);
        std::wcout << L"Open BLE device from address...\n";
        auto device = BluetoothLEDevice::FromBluetoothAddressAsync(address).get();
        if (device == nullptr) {
            throw std::runtime_error("failed to connect to BLE device");
        }

        std::wcout << L"Connected: " << device.Name().c_str() << L"\n";

        // Some BLE stacks need a short settling delay before GATT discovery.
        std::this_thread::sleep_for(800ms);

        auto commandCharacteristic = GetCommandCharacteristic(device);

        std::wcout << L"Start loop: ON every 1s, OFF after 2s. Press Ctrl+C to stop.\n";
        while (true) {
            try {
                WriteCommand(commandCharacteristic, L"ON");
            }
            catch (winrt::hresult_error const& e) {
                std::cerr << "Write ON WinRT error: " << HResultToString(e) << "\n";
                break;
            }
            std::this_thread::sleep_for(1s);

            try {
                WriteCommand(commandCharacteristic, L"OFF");
            }
            catch (winrt::hresult_error const& e) {
                std::cerr << "Write OFF WinRT error: " << HResultToString(e) << "\n";
                break;
            }
            std::this_thread::sleep_for(2s);
        }
    }
    catch (winrt::hresult_error const& e) {
        std::cerr << "WinRT error: " << HResultToString(e) << "\n";
        return 1;
    }
    catch (std::exception const& e) {
        std::cerr << "Error: " << e.what() << "\n";
        return 1;
    }
}

// 运行程序: Ctrl + F5 或调试 >“开始执行(不调试)”菜单
// 调试程序: F5 或调试 >“开始调试”菜单

// 入门使用技巧: 
//   1. 使用解决方案资源管理器窗口添加/管理文件
//   2. 使用团队资源管理器窗口连接到源代码管理
//   3. 使用输出窗口查看生成输出和其他消息
//   4. 使用错误列表窗口查看错误
//   5. 转到“项目”>“添加新项”以创建新的代码文件，或转到“项目”>“添加现有项”以将现有代码文件添加到项目
//   6. 将来，若要再次打开此项目，请转到“文件”>“打开”>“项目”并选择 .sln 文件
