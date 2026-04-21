#include <WiFi.h>

const char* ssid = "duwenlong";
const char* password = "11111111";

const uint16_t TCP_PORT = 5000;
const int LED_PIN = 8;
const int LED_ON_LEVEL = LOW;
const int LED_OFF_LEVEL = HIGH;

WiFiServer server(TCP_PORT);

bool connectToWiFi(unsigned long timeoutMs)
{
  WiFi.mode(WIFI_STA);
  WiFi.disconnect(true, true);
  delay(300);

  Serial.println();
  Serial.print("Connecting to WiFi: ");
  Serial.println(ssid);
  Serial.println("ESP32-C3 supports 2.4 GHz only.");

  WiFi.begin(ssid, password);

  unsigned long startMs = millis();
  while (WiFi.status() != WL_CONNECTED && millis() - startMs < timeoutMs) {
    Serial.print(".");
    delay(500);
  }
  Serial.println();

  if (WiFi.status() != WL_CONNECTED) {
    Serial.println("WiFi connect failed.");
    return false;
  }

  Serial.println("WiFi connected.");
  Serial.print("IP Address: ");
  Serial.println(WiFi.localIP());
  return true;
}

void handleClient(WiFiClient& client)
{
  String line = client.readStringUntil('\n');
  line.trim();

  if (line.length() == 0) {
    return;
  }

  Serial.print("RX: ");
  Serial.println(line);

  if (line.equalsIgnoreCase("on")) {
    digitalWrite(LED_PIN, LED_ON_LEVEL);
    client.println("ACK LED ON");
    Serial.println("TX: ACK LED ON");
    return;
  }

  if (line.equalsIgnoreCase("off")) {
    digitalWrite(LED_PIN, LED_OFF_LEVEL);
    client.println("ACK LED OFF");
    Serial.println("TX: ACK LED OFF");
    return;
  }

  client.print("ECHO: ");
  client.println(line);
  Serial.print("TX: ECHO: ");
  Serial.println(line);
}

void setup()
{
  pinMode(LED_PIN, OUTPUT);
  digitalWrite(LED_PIN, LED_OFF_LEVEL);

  Serial.begin(115200);
  delay(1000);

  if (connectToWiFi(20000)) {
    server.begin();
    Serial.print("TCP echo server listening on port ");
    Serial.println(TCP_PORT);
    Serial.println("Send text lines. Special commands: on, off");
  } else {
    Serial.println("Open Serial Monitor, fix WiFi, then press RST.");
  }
}

void loop()
{
  if (WiFi.status() != WL_CONNECTED) {
    delay(1000);
    return;
  }

  WiFiClient client = server.available();
  if (!client) {
    delay(20);
    return;
  }

  Serial.println("Client connected.");
  client.println("ESP32 TCP Echo Ready");
  client.println("Type a line and press Enter.");

  unsigned long lastDataMs = millis();
  while (client.connected()) {
    if (client.available()) {
      handleClient(client);
      lastDataMs = millis();
    }

    if (millis() - lastDataMs > 300000) {
      client.println("Timeout, closing connection.");
      break;
    }

    delay(10);
  }

  client.stop();
  Serial.println("Client disconnected.");
}