#pragma once

#include <Arduino.h>
#include <BLE2902.h>
#include <BLEDevice.h>
#include <BLEServer.h>
#include <BLEUtils.h>

// 这是 BLE 事件回调接口，AppController 通过实现它来接收连接状态变化。
// 之所以不用 BleTransport 直接持有 AppController 具体类型，是为了降低耦合；
// 以后换成别的控制器或写单元测试时，只要实现这组接口就能复用传输层。
class BleTransportCallbacks {
 public:
  virtual ~BleTransportCallbacks() = default;
  // 当有客户端连接时触发。
  virtual void onBleConnected() {}
  // 当客户端断开时触发。
  virtual void onBleDisconnected() {}
};

// 这个类只负责 BLE 通信层：启动广播、维护连接状态、发布状态特征。
// 它不负责决定“连接后 LED 应该怎么亮”，这样业务规则就不会和 BLE 库 API 绑死。
class BleTransport {
 public:
  // 初始化 BLE 服务并开始广播。
  // 外部只需调用一次 begin()，不用知道 server/service/characteristic 的具体创建顺序。
  void begin(BleTransportCallbacks* callbacks);
  // 更新状态特征内容，连接时会主动 notify。
  // 这里的职责是“发送”，不负责决定发送什么文本。
  void publishStatus(const String& status);
  // 查询当前是否有 BLE 客户端连接。
  bool isClientConnected() const;

  // 下面两个函数由 BLE 服务器回调调用，放在 public 便于回调类访问。
  // 它们相当于把 BLE 库的回调事件，转换成 BleTransport 自己的内部事件入口。
  void handleConnect();
  void handleDisconnect();

 private:
  // 上层业务回调对象，允许传输层把连接变化往上报告。
  BleTransportCallbacks* callbacks_ = nullptr;
  // BLE server 代表整台设备作为 GATT 服务端的入口对象。
  BLEServer* server_ = nullptr;
  // 用于对外暴露状态字符串的特征。
  BLECharacteristic* statusCharacteristic_ = nullptr;
  // 本地缓存连接状态，避免每次都从底层对象推断。
  bool clientConnected_ = false;
};