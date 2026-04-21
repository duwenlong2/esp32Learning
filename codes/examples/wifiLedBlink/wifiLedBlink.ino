/*
 WiFi Web Server LED Blink

 A simple web server that lets you blink an LED via the web.
 This sketch will print the IP address of your WiFi Shield (once connected)
 to the Serial monitor. From there, you can open that address in a web browser
 to turn on and off the LED on pin 5.

 If the IP address of your shield is yourAddress:
 http://yourAddress/H turns the LED on
 http://yourAddress/L turns it off

 This example is written for a network using WPA2 encryption. For insecure
 WEP or WPA, change the Wifi.begin() call and use Wifi.setMinSecurity() accordingly.

 Circuit:
 * WiFi shield attached
 * LED attached to pin 5

 created for arduino 25 Nov 2012
 by Tom Igoe

ported for sparkfun esp32 
31.01.2017 by Jan Hendrik Berlin
*/
#include<WiFi.h>
const char* ssid ="duwenlong"; //设置wifi名称
const char* password ="11111111";//设置wifi密码
const int LED_PIN = 8;
const int LED_ON_LEVEL = LOW;
const int LED_OFF_LEVEL = HIGH;

WiFiServer server(80);
bool serverStarted = false;
bool wifiConnecting = false;
unsigned long wifiConnectStartMs = 0;
unsigned long nextWifiRetryMs = 0;

const char* authModeToString(wifi_auth_mode_t mode)
{
  switch (mode) {
    case WIFI_AUTH_OPEN:
      return "OPEN";
    case WIFI_AUTH_WEP:
      return "WEP";
    case WIFI_AUTH_WPA_PSK:
      return "WPA_PSK";
    case WIFI_AUTH_WPA2_PSK:
      return "WPA2_PSK";
    case WIFI_AUTH_WPA_WPA2_PSK:
      return "WPA_WPA2_PSK";
    case WIFI_AUTH_WPA2_ENTERPRISE:
      return "WPA2_ENTERPRISE";
    case WIFI_AUTH_WPA3_PSK:
      return "WPA3_PSK";
    case WIFI_AUTH_WPA2_WPA3_PSK:
      return "WPA2_WPA3_PSK";
    default:
      return "OTHER";
  }
}

void scanAndReportTargetAP()
{
  Serial.println("Scanning nearby WiFi...");
  int count = WiFi.scanNetworks();

  if (count <= 0) {
    Serial.println("No AP found in scan.");
    return;
  }

  bool targetFound = false;
  for (int i = 0; i < count; i++) {
    String foundSsid = WiFi.SSID(i);
    if (foundSsid == ssid) {
      targetFound = true;
      Serial.print("Target AP found: ");
      Serial.println(foundSsid);
      Serial.print("  RSSI: ");
      Serial.println(WiFi.RSSI(i));
      Serial.print("  Channel: ");
      Serial.println(WiFi.channel(i));
      Serial.print("  Auth: ");
      Serial.println(authModeToString(WiFi.encryptionType(i)));
      break;
    }
  }

  if (!targetFound) {
    Serial.print("Target AP not found in scan: ");
    Serial.println(ssid);
    Serial.println("Tips: check hotspot name, 2.4 GHz mode, and hidden SSID setting.");
  }

  WiFi.scanDelete();
}

const char* wifiStatusToString(wl_status_t status)
{
  switch (status) {
    case WL_IDLE_STATUS:
      return "IDLE";
    case WL_NO_SSID_AVAIL:
      return "NO_SSID_AVAIL";
    case WL_SCAN_COMPLETED:
      return "SCAN_COMPLETED";
    case WL_CONNECTED:
      return "CONNECTED";
    case WL_CONNECT_FAILED:
      return "CONNECT_FAILED";
    case WL_CONNECTION_LOST:
      return "CONNECTION_LOST";
    case WL_DISCONNECTED:
      return "DISCONNECTED";
    default:
      return "UNKNOWN";
  }
}

void startWiFiConnect()
{
  if (wifiConnecting || WiFi.status() == WL_CONNECTED) {
    return;
  }

  WiFi.mode(WIFI_STA);
  WiFi.disconnect(true, true);
  delay(600);

  Serial.println();
  Serial.print("Connecting to ");
  Serial.println(ssid);
  Serial.println("Only 2.4 GHz WiFi is supported.");
  scanAndReportTargetAP();

  WiFi.begin(ssid, password);
  wifiConnecting = true;
  wifiConnectStartMs = millis();
}

void setup()
{
Serial.begin(115200);
pinMode(LED_PIN,OUTPUT);  // set the LED pin mode
digitalWrite(LED_PIN, LED_OFF_LEVEL);

delay(10);

startWiFiConnect();

}



void loop() {
wl_status_t status = (wl_status_t)WiFi.status();

if (status == WL_CONNECTED) {
  if (!serverStarted) {
    server.begin();
    serverStarted = true;
    wifiConnecting = false;
    Serial.println("WiFi connected.");
    Serial.print("IP Address: ");
    Serial.println(WiFi.localIP());
    Serial.println("HTTP server started.");
  }
} else {
  serverStarted = false;

  if (wifiConnecting) {
    static unsigned long lastDotMs = 0;
    if (millis() - lastDotMs > 500) {
      lastDotMs = millis();
      Serial.print(".");
    }

    if (millis() - wifiConnectStartMs > 30000) {
      Serial.println();
      Serial.print("WiFi status: ");
      Serial.println(wifiStatusToString(status));
      Serial.println("WiFi connect timeout.");
      Serial.println("Check SSID/password, hotspot band (2.4 GHz), and security mode (WPA2).");
      wifiConnecting = false;
      nextWifiRetryMs = millis() + 10000;
    }
  } else if (millis() >= nextWifiRetryMs) {
    Serial.println("Retrying WiFi connection...");
    startWiFiConnect();
  }

  delay(50);
  return;
}

WiFiClient client =server.available();
if(client){
  Serial.println("New Client.");
  String currentLine="";
  String requestLine="";
  while(client.connected()){
    if(client.available())
    {
      char c=client.read();
      Serial.write(c);
      if(c=='\n'){
        if(currentLine.length()==0)
        {
          bool needRedirect = false;
          if (requestLine.startsWith("GET /H ")) {
            digitalWrite(LED_PIN, LED_ON_LEVEL);
            Serial.println("[LED] ON");
            needRedirect = true;
          }
          if (requestLine.startsWith("GET /L ")) {
            digitalWrite(LED_PIN, LED_OFF_LEVEL);
            Serial.println("[LED] OFF");
            needRedirect = true;
          }

          if (needRedirect) {
            client.println("HTTP/1.1 302 Found");
            client.println("Location: /");
          } else {
            client.println("HTTP/1.1 200 OK");
          }
          client.println("Content-type:text/html; charset=utf-8");
          client.println("Cache-Control: no-store, no-cache, must-revalidate, max-age=0");
          client.println("Pragma: no-cache");
          client.println("Expires: 0");
          client.println("Connection: close");
          client.println();
          client.print("<html><body>");
          client.print("<h3>ESP32-C3 LED Control</h3>");
          client.print("<a href=\"/H\"><button style='font-size:20px;padding:10px 18px;'>LED ON</button></a><br><br>");
          client.print("<a href=\"/L\"><button style='font-size:20px;padding:10px 18px;'>LED OFF</button></a><br><br>");
          client.print("<a href=\"/\">Refresh</a>");
          client.println("</body></html>");

          break;
        }
        else
        {
          if (requestLine.length() == 0) {
            requestLine = currentLine;
          }
          currentLine="";
        }
      }
      else if (c!='\r'){
        currentLine+=c;
      }
    }
  }
  client.stop();
  Serial.println("[Client] Disconnected.");
}
}
