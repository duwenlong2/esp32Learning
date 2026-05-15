#include "AppController.h"

#include "AppConfig.h"

#include <esp_system.h>
#include <stdio.h>

namespace {

// 把状态枚举转换成便于串口和 BLE 传输的文本。
// 单独做成函数，避免状态文案散落在多个调用点，后面改协议文本时只改一处。
const char* bleModeToText(BleIndicatorMode mode)
{
  switch (mode) {
    case BleIndicatorMode::Pairing:
      return "PAIRING";
    case BleIndicatorMode::Connected:
      return "CONNECTED";
    case BleIndicatorMode::Disconnected:
    default:
      return "DISCONNECTED";
  }
}

// 组装状态特征字符串，供 Windows 端读取。
// 这里保留一个简单前缀，方便上位机在后续把更多字段继续拼接进同一条消息。
String buildStatusText(BleIndicatorMode mode, bool imuReady, unsigned long imuReadFailCount)
{
  String text = "BLE:";
  text += bleModeToText(mode);
  text += ",IMU:";
  text += imuReady ? "READY" : "NOT_READY";
  text += ",IMU_FAIL:";
  text += String(imuReadFailCount);
  return text;
}

const char* resetReasonToText(esp_reset_reason_t reason)
{
  switch (reason) {
    case ESP_RST_POWERON:
      return "POWERON";
    case ESP_RST_SW:
      return "SW";
    case ESP_RST_PANIC:
      return "PANIC";
    case ESP_RST_INT_WDT:
      return "INT_WDT";
    case ESP_RST_TASK_WDT:
      return "TASK_WDT";
    case ESP_RST_WDT:
      return "WDT";
    case ESP_RST_DEEPSLEEP:
      return "DEEPSLEEP";
    case ESP_RST_BROWNOUT:
      return "BROWNOUT";
    case ESP_RST_SDIO:
      return "SDIO";
    default:
      return "UNKNOWN";
  }
}

// 六轴原始数据使用 CSV 文本，便于串口和上位机直接观察。
String buildImuText(const ImuSample& sample)
{
  char payload[128] = {0};
  snprintf(
    payload,
    sizeof(payload),
    "IMU,%lu,%d,%d,%d,%d,%d,%d,%d",
    sample.timestampMs,
    sample.ax,
    sample.ay,
    sample.az,
    sample.gx,
    sample.gy,
    sample.gz,
    sample.temp);
  return String(payload);
}

}  // namespace

// 系统启动入口：初始化引脚、启动 BLE、切到配对态并发布状态。
// 顺序上先把 LED 置成已知状态，再启动 BLE，这样上电时不会先出现随机亮度。
// BLE 启动后立刻进入 Pairing，是因为设备刚开机时最合理的默认行为就是等待连接。
void AppController::begin()
{
  bootResetReason_ = resetReasonToText(esp_reset_reason());
  Serial.print("Boot reset reason: ");
  Serial.println(bootResetReason_);

  pinMode(AppConfig::kLedPin, OUTPUT);
  writeIndicatorLevel(0);

  // 上电自检灯：不依赖 BLE 连接，便于区分“固件没跑起来”还是“仅 BLE 没连上”。
  for (int i = 0; i < 2; ++i) {
    writeIndicatorLevel(255);
    delay(90);
    writeIndicatorLevel(0);
    delay(90);
  }
  Serial.println("LED self-test done");

  if (!imuSensor_.begin()) {
    Serial.println("IMU init failed, BLE will continue without motion stream");
  }

  // 传入 this，让传输层通过回调把连接事件反向通知回来。
  bleTransport_.begin(this);
  setBleIndicatorMode(BleIndicatorMode::Pairing);
  lastImuPublishMs_ = millis();

  Serial.println("BLE indicator demo start");
  publishStatus();
}

// 主循环入口：当前只做灯效刷新。
// 这里故意不直接写 while/阻塞等待 BLE 事件，避免 LED 动画在无连接时停止更新。
void AppController::tick()
{
  updateBleIndicator();

  if (!bleTransport_.isClientConnected() || !imuSensor_.isReady()) {
    return;
  }

  unsigned long now = millis();
  if (now - lastImuPublishMs_ < AppConfig::kImuPublishIntervalMs) {
    return;
  }

  ImuSample sample;
  if (!imuSensor_.readSample(&sample)) {
    imuReadFailCount_++;
    if (now - lastImuReadFailLogMs_ >= 1000) {
      lastImuReadFailLogMs_ = now;
      Serial.print("IMU read failed, count=");
      Serial.println(imuReadFailCount_);
      publishStatus();
    }
    return;
  }

  lastImuPublishMs_ = now;
  if (imuReadFailCount_ != 0) {
    imuReadFailCount_ = 0;
    publishStatus();
  }
  publishImuSample(sample);
}

// BLE 已连接：切到呼吸灯。
// 连接成功后发布一次状态，方便上位机确认链路建立后的最终状态。
void AppController::onBleConnected()
{
  setBleIndicatorMode(BleIndicatorMode::Connected);
  publishStatus();
}

// BLE 已断开：切回配对态快闪，表示设备正在等待重连。
// publishStatus() 会更新特征值，虽然此时没有 notify 接收端，但新客户端连接后 read 能看到最新状态。
void AppController::onBleDisconnected()
{
  setBleIndicatorMode(BleIndicatorMode::Pairing);
  publishStatus();
}

// 切换状态模式并重置相关计时变量。
// 每次切状态都重置计时，可以保证从新模式的起点开始表现，避免沿用旧模式相位。
void AppController::setBleIndicatorMode(BleIndicatorMode mode)
{
  bleIndicatorMode_ = mode;
  blinkOutputOn_ = false;
  lastBlinkToggleMs_ = millis();

  if (mode == BleIndicatorMode::Disconnected) {
    writeIndicatorLevel(0);
  }

  Serial.print("BLE indicator mode: ");
  Serial.println(bleModeToText(mode));
}

// 根据当前模式计算亮度：
// Pairing=快闪，Connected=慢呼吸，Disconnected=熄灭。
// 这里把“状态到视觉效果”的映射集中管理，后续调整交互反馈时不必去改 BLE 代码。
void AppController::updateBleIndicator()
{
  unsigned long now = millis();

  if (bleIndicatorMode_ == BleIndicatorMode::Disconnected) {
    writeIndicatorLevel(0);
    return;
  }

  if (bleIndicatorMode_ == BleIndicatorMode::Pairing) {
    // 配对态选用简单快闪：计算量低，而且远距离也容易看出设备在等待连接。
    if (now - lastBlinkToggleMs_ >= AppConfig::kPairingBlinkIntervalMs) {
      lastBlinkToggleMs_ = now;
      blinkOutputOn_ = !blinkOutputOn_;
      writeIndicatorLevel(blinkOutputOn_ ? 255 : 0);
    }
    return;
  }

  // 已连接时用三角波做呼吸灯，而不是查表或正弦函数。
  // 原因是它足够直观、实现简单，也避免在小 MCU 上引入额外浮点开销和复杂度。
  unsigned long phaseMs = now % AppConfig::kBreathingPeriodMs;
  float halfPeriod = AppConfig::kBreathingPeriodMs / 2.0f;
  float brightness = 0.0f;
  if (phaseMs < halfPeriod) {
    brightness = phaseMs / halfPeriod;
  } else {
    brightness = (AppConfig::kBreathingPeriodMs - phaseMs) / halfPeriod;
  }

  writeIndicatorLevel(static_cast<uint8_t>(brightness * 255.0f));
}

// 输出亮度到板载 LED。此板子是低电平点亮，所以需要反相。
// 上层一律按“255 更亮”思考，硬件电平差异在这里消化，代码可读性更稳定。
void AppController::writeIndicatorLevel(uint8_t brightness)
{
  uint8_t output = 255 - brightness;
  analogWrite(AppConfig::kLedPin, output);
}

// 把当前 BLE 状态写入 status 特征。
// 单独封装 publishStatus()，是为了让“状态文本格式”依旧由 AppController 决定，
// 传输层只负责发送，不关心业务字符串长什么样。
void AppController::publishStatus()
{
  String status = buildStatusText(bleIndicatorMode_, imuSensor_.isReady(), imuReadFailCount_);
  status += ",RST:";
  status += bootResetReason_;
  bleTransport_.publishStatus(status);
}

// 通过 BLE 六轴特征推送一帧原始数据。
void AppController::publishImuSample(const ImuSample& sample)
{
  bleTransport_.publishImuData(buildImuText(sample));
}