#pragma once

#include <BLEUUID.h>

namespace AppConfig {

// 这个文件只放“全局不变的配置”，不放运行时状态。
// 这样做的目的是把硬件差异和协议常量集中管理，调板子或改 BLE 协议时只看这里。

// 板载蓝灯引脚与电平定义（本板通常低电平点亮）。
constexpr int kLedPin = 8;
constexpr int kLedOnLevel = LOW;
constexpr int kLedOffLevel = HIGH;

// ESP32-C3 SuperMini 常用 I2C 引脚。
constexpr int kI2cSdaPin = 4;
constexpr int kI2cSclPin = 5;

// MPU6050 常见地址。
constexpr uint8_t kImuAddressPrimary = 0x68;
constexpr uint8_t kImuAddressSecondary = 0x69;

// 主循环间隔（毫秒），越小动画越平滑，但 CPU 唤醒更频繁。
constexpr unsigned long kLoopDelayMs = 5;

// 配对中快闪间隔（毫秒）。
constexpr unsigned long kPairingBlinkIntervalMs = 150;

// 呼吸灯一个完整周期（毫秒）。
constexpr unsigned long kBreathingPeriodMs = 2200;

// 六轴数据上报间隔（毫秒）。
constexpr unsigned long kImuPublishIntervalMs = 20;

// BLE 广播名称，Windows 端会按这个名字扫描设备。
constexpr const char* kBleDeviceName = "ESP32C3-BLE-GESTURE";

// BLE 服务和状态特征 UUID（自定义私有协议）。
// 这里拆成 service + characteristic，是因为 BLE 通常按“一个服务下面多个特征”组织。
// 当前示例只暴露状态特征，后面如果加手势数据、配置项，可以继续往同一个服务下扩展。
static BLEUUID kServiceUuid("7b1e0001-0000-4bcd-1234-1234567890ab");
static BLEUUID kStatusUuid("7b1e0003-0000-4bcd-1234-1234567890ab");
static BLEUUID kImuDataUuid("7b1e0004-0000-4bcd-1234-1234567890ab");

}  // namespace AppConfig