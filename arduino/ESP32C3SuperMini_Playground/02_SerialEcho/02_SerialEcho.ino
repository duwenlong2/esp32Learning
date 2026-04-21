// ESP32-C3 SuperMini serial echo test
// In Arduino IDE, enable Tools -> USB CDC On Boot -> Enabled

const int LED_PIN = 8;

void setup() {
  pinMode(LED_PIN, OUTPUT);
  Serial.begin(115200);
  unsigned long t0 = millis();
  while (!Serial && (millis() - t0 < 5000)) {
    delay(10);
  }
  Serial.println("Serial Echo ready. Type text and press Enter.");
}

void loop() {
  if (Serial.available() > 0) {
    String line = Serial.readStringUntil('\n');
    line.trim();
    if (line.length() > 0) {
      digitalWrite(LED_PIN, !digitalRead(LED_PIN));
      Serial.print("RX: ");
      Serial.println(line);
      Serial.print("LEN: ");
      Serial.println(line.length());
    }
  }
}
