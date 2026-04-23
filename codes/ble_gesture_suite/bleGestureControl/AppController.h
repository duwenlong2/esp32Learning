#pragma once

#include <Arduino.h>

#include "BleTransport.h"
#include "ImuSensor.h"

// 蓝灯状态机：把“连接状态”映射成“用户看得见的灯效”。
// 这里不用 bool connected_ 之类的简单标志，是因为视觉表现有 3 种状态，
// 后续若加入错误态、升级态，也能继续扩展而不用重写 if/else 结构。
enum class BleIndicatorMode {
  Pairing,
  Connected,
  Disconnected,
};

// AppController 是业务层协调者。
// 它不直接处理底层 BLE API，而是专注回答两个问题：
// 1. 当前系统应该处于什么状态。
// 2. 这个状态应该如何反馈给用户（LED / 状态字符串）。
// 这样做是为了把“通信细节”和“产品行为”拆开，减少后续改动互相影响。
class AppController : public BleTransportCallbacks {
 public:
  // 在 setup 中调用：初始化硬件和业务状态，然后启动 BLE。
  // begin() 集中管理启动顺序，避免外部调用者需要知道“先配灯还是先开广播”。
  void begin();
  // 在 loop 中周期调用：驱动需要持续刷新的逻辑。
  // 目前只有灯效，但以后如果增加超时检测、传感器轮询，也适合继续放这里。
  void tick();

  // BLE 连接回调：由传输层通知业务层“链路建立了”。
  // 这里不传 BLEServer 指针等底层对象，是为了让 AppController 不依赖 BLE 库细节。
  void onBleConnected() override;
  // BLE 断开回调：由传输层通知业务层“链路断开了”。
  void onBleDisconnected() override;

 private:
  // 切换状态机模式并重置与该模式相关的内部变量。
  // 统一从这里切状态，而不是各处直接改成员变量，避免漏掉计时器和输出同步。
  void setBleIndicatorMode(BleIndicatorMode mode);
  // 根据当前模式更新灯亮度。
  // 这个函数只负责“把状态翻译成亮度”，不负责 BLE 通信，职责保持单一。
  void updateBleIndicator();
  // 统一输出亮度到板载 LED（处理低电平点亮）。
  // 之所以单独封装，是为了把板级差异隔离起来，其他逻辑可以一直用 0~255 的正常亮度语义。
  void writeIndicatorLevel(uint8_t brightness);
  // 对外发布 BLE 状态字符串。
  // 单独抽出来，是为了以后除了连接/断开，还可以在一个地方扩展更多状态文本规则。
  void publishStatus();
  // 对外发布当前六轴原始数据。
  void publishImuSample(const ImuSample& sample);

  // BLE 传输层成员：负责广播、服务和特征，不直接承载业务决策。
  BleTransport bleTransport_;
  // IMU 采集成员：负责 I2C 初始化和寄存器读取。
  ImuSensor imuSensor_;

  // 当前灯效模式是状态机核心。
  BleIndicatorMode bleIndicatorMode_ = BleIndicatorMode::Disconnected;
  // Pairing 模式下使用的闪烁相位。
  bool blinkOutputOn_ = false;
  // 记录上次翻转闪烁或切换模式的时间点。
  unsigned long lastBlinkToggleMs_ = 0;
  // 记录上次发送 IMU 数据的时间点。
  unsigned long lastImuPublishMs_ = 0;
};