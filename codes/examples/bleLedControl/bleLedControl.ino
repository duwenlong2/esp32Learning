#include <BLEDevice.h>
#include <BLEServer.h>
#include <BLEUtils.h>
#include <BLE2902.h>

// 这是板载 LED 的引脚。
const int LED_PIN = 8;

// 这块板子的板载蓝灯通常是低电平点亮。
const int LED_ON_LEVEL = LOW;
const int LED_OFF_LEVEL = HIGH;

// 这是蓝牙设备名称，Windows 端会按这个名字去找设备。
const char* BLE_DEVICE_NAME = "ESP32C3-BLE-LED";

// 这是自定义 BLE 服务 UUID。
static BLEUUID serviceUuid("6a5a0001-0000-4bcd-1234-1234567890ab");

// 这是“命令”特征 UUID，Windows 端往这里写入命令。
static BLEUUID commandUuid("6a5a0002-0000-4bcd-1234-1234567890ab");

// 这是“状态”特征 UUID，Windows 端从这里读取 LED 状态。
static BLEUUID statusUuid("6a5a0003-0000-4bcd-1234-1234567890ab");

// 这是状态特征对象，后面会创建出来。
BLECharacteristic* statusCharacteristic = nullptr;

// 这是当前是否有 BLE 客户端连接的标志。
bool deviceConnected = false;

// 这是当前 LED 的逻辑状态，true 表示亮，false 表示灭。
bool ledIsOn = false;

// 这个函数把布尔状态转换成字符串，便于打印和回传给 Windows。
String getLedStateText()
{
  if (ledIsOn) {
    return "ON";
  }

  return "OFF";
}

// 这个函数把当前 LED 状态真正写到引脚上。
void applyLedState()
{
  if (ledIsOn) {
    digitalWrite(LED_PIN, LED_ON_LEVEL);
  } else {
    digitalWrite(LED_PIN, LED_OFF_LEVEL);
  }

  // 每次状态变化后，都把最新状态写入 BLE 状态特征。
  if (statusCharacteristic != nullptr) {
    String text = getLedStateText();
    statusCharacteristic->setValue(text.c_str());

    // 如果 Windows 端已连接，就主动通知一次。
    if (deviceConnected) {
      statusCharacteristic->notify();
    }
  }

  // 同时把状态打印到串口，便于你调试。
  Serial.print("LED state: ");
  Serial.println(getLedStateText());
}

// 这个类用于处理 BLE 连接和断开事件。
class MyServerCallbacks : public BLEServerCallbacks {
  void onConnect(BLEServer* server) override
  {
    deviceConnected = true;
    Serial.println("BLE client connected.");
  }

  void onDisconnect(BLEServer* server) override
  {
    deviceConnected = false;
    Serial.println("BLE client disconnected.");

    // 断开后重新开始广播，方便 Windows 下次再次连接。
    BLEDevice::startAdvertising();
  }
};

// 这个类用于处理 Windows 写过来的命令。
class CommandCallbacks : public BLECharacteristicCallbacks {
  void onWrite(BLECharacteristic* characteristic) override
  {
    // 先读取 BLE 写入的字符串命令。
    String command = characteristic->getValue();

    // 去掉首尾空白字符。
    command.trim();

    // 转成大写，便于统一判断。
    command.toUpperCase();

    Serial.print("RX command: ");
    Serial.println(command);

    // STATUS 表示查询当前灯状态。
    if (command == "STATUS") {
      applyLedState();
      return;
    }

    // ON 表示点亮 LED。
    if (command == "ON") {
      ledIsOn = true;
      applyLedState();
      return;
    }

    // OFF 表示熄灭 LED。
    if (command == "OFF") {
      ledIsOn = false;
      applyLedState();
      return;
    }

    // 未知命令时，把状态特征设置成 ERROR。
    if (statusCharacteristic != nullptr) {
      statusCharacteristic->setValue("ERROR: UNKNOWN COMMAND");
      if (deviceConnected) {
        statusCharacteristic->notify();
      }
    }

    Serial.println("Unknown command.");
  }
};

void setup()
{
  // 先初始化 LED 引脚。
  pinMode(LED_PIN, OUTPUT);

  // 默认先让 LED 熄灭。
  digitalWrite(LED_PIN, LED_OFF_LEVEL);

  // 初始化串口，方便看日志。
  Serial.begin(115200);

  // 稍微等一会儿，避免刚上电日志丢失。
  delay(1000);

  Serial.println();
  Serial.println("ESP32-C3 BLE LED demo start");

  // 初始化 BLE 设备名称。
  BLEDevice::init(BLE_DEVICE_NAME);

  // 创建 BLE 服务器。
  BLEServer* server = BLEDevice::createServer();

  // 给服务器挂上连接回调。
  server->setCallbacks(new MyServerCallbacks());

  // 创建一个自定义服务。
  BLEService* service = server->createService(serviceUuid);

  // 创建“状态”特征，支持读取和通知。
  statusCharacteristic = service->createCharacteristic(
    statusUuid,
    BLECharacteristic::PROPERTY_READ | BLECharacteristic::PROPERTY_NOTIFY
  );

  // 这个描述符可以让很多手机和电脑更好地识别 notify。
  statusCharacteristic->addDescriptor(new BLE2902());

  // 创建“命令”特征，支持写入。
  BLECharacteristic* commandCharacteristic = service->createCharacteristic(
    commandUuid,
    BLECharacteristic::PROPERTY_WRITE
  );

  // 给命令特征挂上写入回调。
  commandCharacteristic->setCallbacks(new CommandCallbacks());

  // 先把当前灯状态写进去。
  statusCharacteristic->setValue(getLedStateText().c_str());

  // 启动服务。
  service->start();

  // 开始广播，让 Windows 可以扫描到它。
  BLEAdvertising* advertising = BLEDevice::getAdvertising();
  advertising->addServiceUUID(serviceUuid);
  advertising->setScanResponse(true);
  BLEDevice::startAdvertising();

  Serial.print("BLE advertising name: ");
  Serial.println(BLE_DEVICE_NAME);
  Serial.println("Supported commands: STATUS, ON, OFF");
}

void loop()
{
  // 这个 Demo 的主逻辑都在 BLE 回调里，所以主循环保持空闲即可。
  delay(200);
}
