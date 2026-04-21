// ESP32-C3 SuperMini WiFi + HTTP GET demo
// Fill in your SSID and password below.

#include <WiFi.h>
#include <HTTPClient.h>

const char* WIFI_SSID = "YOUR_WIFI_SSID";
const char* WIFI_PASS = "YOUR_WIFI_PASSWORD";
const char* TEST_URL = "http://httpbin.org/get";

void connectWiFi() {
  WiFi.mode(WIFI_STA);
  WiFi.begin(WIFI_SSID, WIFI_PASS);

  Serial.print("Connecting WiFi");
  int retries = 0;
  while (WiFi.status() != WL_CONNECTED && retries < 40) {
    delay(500);
    Serial.print(".");
    retries++;
  }
  Serial.println();

  if (WiFi.status() == WL_CONNECTED) {
    Serial.print("WiFi connected, IP: ");
    Serial.println(WiFi.localIP());
  } else {
    Serial.println("WiFi connect failed");
  }
}

void httpGetOnce() {
  if (WiFi.status() != WL_CONNECTED) {
    Serial.println("WiFi not connected, skip HTTP");
    return;
  }

  HTTPClient http;
  http.begin(TEST_URL);
  int code = http.GET();

  Serial.print("HTTP code: ");
  Serial.println(code);

  if (code > 0) {
    String payload = http.getString();
    Serial.println("Payload head (first 300 chars):");
    Serial.println(payload.substring(0, min((int)payload.length(), 300)));
  }

  http.end();
}

void setup() {
  Serial.begin(115200);
  unsigned long t0 = millis();
  while (!Serial && (millis() - t0 < 5000)) {
    delay(10);
  }

  Serial.println("WiFi HTTP demo start");
  connectWiFi();
  httpGetOnce();
}

void loop() {
  delay(5000);
  httpGetOnce();
}
