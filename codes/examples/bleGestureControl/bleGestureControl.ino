#include "AppConfig.h"
#include "AppController.h"

// 这个示例把业务状态都收拢到 AppController，setup/loop 只保留 Arduino
// 固定入口，后续想把灯效、按键、传感器扩展进去时不会把全局流程写散。
AppController app;

// Arduino 启动入口：这里只做一次性初始化。
// 具体原因是把初始化顺序统一放在 AppController::begin()，避免后面功能增多后
// setup() 同时关心硬件、BLE、业务状态，导致职责混杂。
void setup()
{
  Serial.begin(115200);
  delay(1000);

  Serial.println();
  Serial.println("ESP32-C3 BLE indicator firmware start");
  app.begin();
}

// Arduino 主循环：保持极简，只驱动应用层 tick。
// 这样做的目的是让 loop() 更像调度器，而不是把具体逻辑写死在这里；
// 将来即使增加更多状态机，也还是由 AppController 统一管理节奏。
void loop()
{
  app.tick();
  delay(AppConfig::kLoopDelayMs);
}