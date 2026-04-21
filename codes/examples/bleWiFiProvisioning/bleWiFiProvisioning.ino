#include <WiFi.h>
#include <BLEDevice.h>
#include <BLEServer.h>
#include <BLEUtils.h>
#include <BLE2902.h>
#include <Preferences.h>

const int LED_PIN = 8;
const int LED_ON_LEVEL = LOW;
const int LED_OFF_LEVEL = HIGH;

const char* BLE_DEVICE_NAME = "ESP32C3-WiFi-Setup";

static BLEUUID serviceUuid("12345678-1234-1234-1234-1234567890ab");
static BLEUUID ssidUuid("12345678-1234-1234-1234-1234567890ac");
static BLEUUID passUuid("12345678-1234-1234-1234-1234567890ad");
static BLEUUID commandUuid("12345678-1234-1234-1234-1234567890ae");
static BLEUUID statusUuid("12345678-1234-1234-1234-1234567890af");

Preferences prefs;

BLEServer* bleServer = nullptr;
BLECharacteristic* ssidCharacteristic = nullptr;
BLECharacteristic* passCharacteristic = nullptr;
BLECharacteristic* commandCharacteristic = nullptr;
BLECharacteristic* statusCharacteristic = nullptr;

String pendingSsid;
String pendingPassword;
bool bleClientConnected = false;
bool serverStarted = false;

void updateLed()
{
  digitalWrite(LED_PIN, WiFi.status() == WL_CONNECTED ? LED_ON_LEVEL : LED_OFF_LEVEL);
}

void updateStatus(const String& message)
{
  Serial.println(message);
  if (statusCharacteristic != nullptr) {
    statusCharacteristic->setValue(message.c_str());
    statusCharacteristic->notify();
  }
}

bool saveCredentials(const String& ssid, const String& password)
{
  prefs.putString("ssid", ssid);
  prefs.putString("pass", password);
  return true;
}

void clearCredentials()
{
  prefs.remove("ssid");
  prefs.remove("pass");
  pendingSsid = "";
  pendingPassword = "";
  updateStatus("Credentials cleared.");
}

String loadSsid()
{
  return prefs.getString("ssid", "");
}

String loadPassword()
{
  return prefs.getString("pass", "");
}

bool connectToWiFi(const String& ssid, const String& password, unsigned long timeoutMs)
{
  if (ssid.length() == 0) {
    updateStatus("WiFi connect skipped: SSID is empty.");
    return false;
  }

  updateStatus("Connecting to WiFi: " + ssid);

  WiFi.mode(WIFI_STA);
  WiFi.disconnect(true, true);
  delay(300);
  WiFi.begin(ssid.c_str(), password.c_str());

  unsigned long startMs = millis();
  while (WiFi.status() != WL_CONNECTED && millis() - startMs < timeoutMs) {
    delay(500);
    Serial.print(".");
  }
  Serial.println();

  if (WiFi.status() != WL_CONNECTED) {
    updateStatus("WiFi connect failed.");
    updateLed();
    return false;
  }

  updateStatus("WiFi connected. IP: " + WiFi.localIP().toString());

  if (!serverStarted) {
    serverStarted = true;
  }

  updateLed();
  return true;
}

void publishCurrentStatus()
{
  if (WiFi.status() == WL_CONNECTED) {
    updateStatus("STATUS CONNECTED IP=" + WiFi.localIP().toString());
  } else {
    updateStatus("STATUS DISCONNECTED");
  }
}

class ServerCallbacks : public BLEServerCallbacks {
  void onConnect(BLEServer* server) override
  {
    bleClientConnected = true;
    updateStatus("BLE client connected.");
  }

  void onDisconnect(BLEServer* server) override
  {
    bleClientConnected = false;
    updateStatus("BLE client disconnected.");
    BLEDevice::startAdvertising();
  }
};

class StringWriteCallbacks : public BLECharacteristicCallbacks {
public:
  explicit StringWriteCallbacks(String* target) : targetString(target) {}

  void onWrite(BLECharacteristic* characteristic) override
  {
    std::string value = characteristic->getValue();
    String text = String(value.c_str());
    text.trim();
    *targetString = text;

    if (characteristic == ssidCharacteristic) {
      updateStatus("SSID updated: " + *targetString);
    } else if (characteristic == passCharacteristic) {
      updateStatus("Password updated.");
    }
  }

private:
  String* targetString;
};

class CommandCallbacks : public BLECharacteristicCallbacks {
  void onWrite(BLECharacteristic* characteristic) override
  {
    std::string value = characteristic->getValue();
    String command = String(value.c_str());
    command.trim();
    command.toUpperCase();

    if (command == "STATUS") {
      publishCurrentStatus();
      return;
    }

    if (command == "CLEAR") {
      WiFi.disconnect(true, true);
      clearCredentials();
      publishCurrentStatus();
      return;
    }

    if (command == "DISCONNECT") {
      WiFi.disconnect(true, true);
      updateLed();
      publishCurrentStatus();
      return;
    }

    if (command == "CONNECT") {
      if (pendingSsid.length() == 0) {
        pendingSsid = loadSsid();
      }
      if (pendingPassword.length() == 0) {
        pendingPassword = loadPassword();
      }

      if (pendingSsid.length() == 0) {
        updateStatus("CONNECT failed: no SSID provided.");
        return;
      }

      saveCredentials(pendingSsid, pendingPassword);
      connectToWiFi(pendingSsid, pendingPassword, 20000);
      return;
    }

    updateStatus("Unknown command: " + command);
  }
};

void setupBle()
{
  BLEDevice::init(BLE_DEVICE_NAME);
  bleServer = BLEDevice::createServer();
  bleServer->setCallbacks(new ServerCallbacks());

  BLEService* service = bleServer->createService(serviceUuid);

  ssidCharacteristic = service->createCharacteristic(
    ssidUuid,
    BLECharacteristic::PROPERTY_READ | BLECharacteristic::PROPERTY_WRITE
  );
  ssidCharacteristic->setCallbacks(new StringWriteCallbacks(&pendingSsid));

  passCharacteristic = service->createCharacteristic(
    passUuid,
    BLECharacteristic::PROPERTY_WRITE
  );
  passCharacteristic->setCallbacks(new StringWriteCallbacks(&pendingPassword));

  commandCharacteristic = service->createCharacteristic(
    commandUuid,
    BLECharacteristic::PROPERTY_WRITE
  );
  commandCharacteristic->setCallbacks(new CommandCallbacks());

  statusCharacteristic = service->createCharacteristic(
    statusUuid,
    BLECharacteristic::PROPERTY_READ | BLECharacteristic::PROPERTY_NOTIFY
  );
  statusCharacteristic->addDescriptor(new BLE2902());
  statusCharacteristic->setValue("Booting...");

  service->start();

  BLEAdvertising* advertising = BLEDevice::getAdvertising();
  advertising->addServiceUUID(serviceUuid);
  advertising->setScanResponse(true);
  advertising->setMinPreferred(0x06);
  advertising->setMinPreferred(0x12);
  BLEDevice::startAdvertising();

  updateStatus("BLE advertising started: ESP32C3-WiFi-Setup");
}

void setup()
{
  pinMode(LED_PIN, OUTPUT);
  digitalWrite(LED_PIN, LED_OFF_LEVEL);

  Serial.begin(115200);
  delay(1000);
  Serial.println();
  Serial.println("ESP32-C3 BLE WiFi Provisioning Demo");

  prefs.begin("wifi-demo", false);

  pendingSsid = loadSsid();
  pendingPassword = loadPassword();

  setupBle();

  if (pendingSsid.length() > 0) {
    updateStatus("Found saved SSID: " + pendingSsid);
    connectToWiFi(pendingSsid, pendingPassword, 15000);
  } else {
    updateStatus("No saved WiFi credentials.");
  }

  updateLed();
}

void loop()
{
  static wl_status_t lastStatus = WL_IDLE_STATUS;
  wl_status_t currentStatus = (wl_status_t)WiFi.status();

  if (currentStatus != lastStatus) {
    lastStatus = currentStatus;
    publishCurrentStatus();
    updateLed();
  }

  delay(200);
}