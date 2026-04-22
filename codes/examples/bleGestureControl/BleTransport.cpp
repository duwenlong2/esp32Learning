#include "BleTransport.h"

#include "AppConfig.h"

namespace {

// BLE 服务器回调适配器：把库层事件转给 BleTransport。
// 单独加一层适配器，而不是让 BleTransport 直接继承 BLEServerCallbacks，
// 是为了把“自己的传输逻辑”和“第三方库要求的回调基类”解耦开。
class ServerCallbacksImpl : public BLEServerCallbacks {
 public:
  explicit ServerCallbacksImpl(BleTransport* owner) : owner_(owner) {}

  // 系统检测到 BLE 客户端连接时调用。
  void onConnect(BLEServer* server) override
  {
    (void)server;
    owner_->handleConnect();
  }

  // 系统检测到 BLE 客户端断开时调用。
  void onDisconnect(BLEServer* server) override
  {
    (void)server;
    owner_->handleDisconnect();
  }

 private:
  BleTransport* owner_;
};

}  // namespace

// 初始化 BLE：创建设备名、服务和状态特征，然后开始广播。
// 这里把 BLE 初始化流程封装掉，调用方不需要了解底层对象图，只关心“BLE 可用了”。
void BleTransport::begin(BleTransportCallbacks* callbacks)
{
  callbacks_ = callbacks;

  BLEDevice::init(AppConfig::kBleDeviceName);
  server_ = BLEDevice::createServer();
  server_->setCallbacks(new ServerCallbacksImpl(this));

  // 一个 service 对应一组相关能力；当前示例只做状态上报，所以只挂一个特征。
  BLEService* service = server_->createService(AppConfig::kServiceUuid);

  statusCharacteristic_ = service->createCharacteristic(
    AppConfig::kStatusUuid,
    BLECharacteristic::PROPERTY_READ | BLECharacteristic::PROPERTY_NOTIFY);
  // BLE2902 描述符让很多客户端正确识别 notify 能力。
  statusCharacteristic_->addDescriptor(new BLE2902());
  statusCharacteristic_->setValue("BOOTING");

  service->start();

  // 启动广播后，外部设备才能扫描到这台板子并发起连接。
  BLEAdvertising* advertising = BLEDevice::getAdvertising();
  advertising->addServiceUUID(AppConfig::kServiceUuid);
  advertising->setScanResponse(true);
  BLEDevice::startAdvertising();

  Serial.print("BLE advertising name: ");
  Serial.println(AppConfig::kBleDeviceName);
}

// 写入状态特征；如果已连接，则立即通知客户端。
// READ 解决“客户端稍后主动读取”的场景，NOTIFY 解决“状态变化主动推送”的场景。
void BleTransport::publishStatus(const String& status)
{
  if (statusCharacteristic_ == nullptr) {
    return;
  }

  statusCharacteristic_->setValue(status.c_str());
  if (clientConnected_) {
    statusCharacteristic_->notify();
  }
}

// 返回当前连接标志，供上层决定是否执行需要在线的逻辑。
// 这里暴露简单布尔值，而不是底层连接对象，目的是维持接口稳定。
bool BleTransport::isClientConnected() const
{
  return clientConnected_;
}

// 处理连接事件：更新状态并通知上层。
// 事件先落到传输层，再由传输层转发给业务层，职责链会更清楚。
void BleTransport::handleConnect()
{
  clientConnected_ = true;
  Serial.println("BLE client connected.");
  if (callbacks_ != nullptr) {
    callbacks_->onBleConnected();
  }
}

// 处理断开事件：更新状态、重启广播并通知上层。
// 断开后立即重新广播，是为了让设备重新进入“可被发现、可重连”的状态。
void BleTransport::handleDisconnect()
{
  clientConnected_ = false;
  Serial.println("BLE client disconnected.");
  BLEDevice::startAdvertising();
  if (callbacks_ != nullptr) {
    callbacks_->onBleDisconnected();
  }
}
