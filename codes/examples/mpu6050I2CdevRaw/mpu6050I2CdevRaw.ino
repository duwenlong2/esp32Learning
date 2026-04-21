#include <Wire.h>
#include "I2Cdev.h"
#include "MPU6050.h"

// 固定使用 ESP32-C3 SuperMini 的 I2C 引脚。
constexpr int I2C_SDA_PIN = 4;
constexpr int I2C_SCL_PIN = 5;

constexpr unsigned long SERIAL_BAUD = 115200;
constexpr unsigned long SAMPLE_INTERVAL_MS = 100;
constexpr uint8_t IMU_ADDR = 0x68;

// 默认地址 0x68（AD0 拉高时为 0x69）。
MPU6050 mpu(IMU_ADDR);

int16_t ax = 0;
int16_t ay = 0;
int16_t az = 0;
int16_t gx = 0;
int16_t gy = 0;
int16_t gz = 0;

bool useDirectRawMode = false;

bool writeReg(uint8_t addr, uint8_t reg, uint8_t value)
{
  Wire.beginTransmission(addr);
  Wire.write(reg);
  Wire.write(value);
  return Wire.endTransmission() == 0;
}

bool readRegs(uint8_t addr, uint8_t startReg, uint8_t* buf, uint8_t len)
{
  Wire.beginTransmission(addr);
  Wire.write(startReg);
  if (Wire.endTransmission(false) != 0) {
    return false;
  }

  uint8_t n = Wire.requestFrom((int)addr, (int)len, (int)true);
  if (n != len) {
    return false;
  }

  for (uint8_t i = 0; i < len; ++i) {
    buf[i] = Wire.read();
  }
  return true;
}

int16_t toInt16(uint8_t hi, uint8_t lo)
{
  return (int16_t)((hi << 8) | lo);
}

// 扫描 I2C 设备，确认总线连通。
void scanI2C()
{
  bool found = false;
  Serial.println("I2C scan start...");

  for (uint8_t addr = 1; addr < 127; ++addr) {
    Wire.beginTransmission(addr);
    uint8_t err = Wire.endTransmission();
    if (err == 0) {
      found = true;
      Serial.print("found @ 0x");
      if (addr < 16) {
        Serial.print('0');
      }
      Serial.println(addr, HEX);
    }
  }

  if (!found) {
    Serial.println("no I2C device found");
  }
}

void setup()
{
  Serial.begin(SERIAL_BAUD);
  delay(1000);

  Serial.println();
  Serial.println("ESP32-C3 MPU6050 I2Cdev raw demo start");

  Wire.begin(I2C_SDA_PIN, I2C_SCL_PIN);
  Wire.setClock(100000);

  scanI2C();

  Serial.println("Initializing MPU6050...");
  mpu.initialize();

  // 先打印身份寄存器信息，便于判断芯片和库是否匹配。
  uint8_t rawWho = 0;
  Wire.beginTransmission(IMU_ADDR);
  Wire.write(0x75);
  if (Wire.endTransmission(false) == 0 && Wire.requestFrom((int)IMU_ADDR, 1, (int)true) == 1) {
    rawWho = Wire.read();
  }
  Serial.print("WHO_AM_I raw(0x75)=0x");
  if (rawWho < 16) {
    Serial.print('0');
  }
  Serial.println(rawWho, HEX);

  Serial.print("getDeviceID()=0x");
  Serial.println(mpu.getDeviceID(), HEX);

  Serial.println("Testing MPU6050 connection...");
  if (mpu.testConnection()) {
    Serial.println("MPU6050 connection successful");
    useDirectRawMode = false;
  } else {
    Serial.println("MPU6050 connection failed, switch to direct raw mode.");
    Serial.println("Likely non-MPU6050 chip (e.g. MPU6500/9250 family). ");
    useDirectRawMode = true;

    // PWR_MGMT_1 = 0x00, wake up chip for direct register reads.
    if (!writeReg(IMU_ADDR, 0x6B, 0x00)) {
      Serial.println("ERROR: wakeup register write failed.");
      while (true) {
        delay(1000);
      }
    }
  }

  Serial.println("time_ms,ax,ay,az,gx,gy,gz");
}

void loop()
{
  if (!useDirectRawMode) {
    mpu.getMotion6(&ax, &ay, &az, &gx, &gy, &gz);
  } else {
    uint8_t raw[14] = {0};
    if (!readRegs(IMU_ADDR, 0x3B, raw, sizeof(raw))) {
      Serial.println("read raw data failed");
      delay(SAMPLE_INTERVAL_MS);
      return;
    }

    ax = toInt16(raw[0], raw[1]);
    ay = toInt16(raw[2], raw[3]);
    az = toInt16(raw[4], raw[5]);
    gx = toInt16(raw[8], raw[9]);
    gy = toInt16(raw[10], raw[11]);
    gz = toInt16(raw[12], raw[13]);
  }

  Serial.print(millis());
  Serial.print(',');
  Serial.print(ax);
  Serial.print(',');
  Serial.print(ay);
  Serial.print(',');
  Serial.print(az);
  Serial.print(',');
  Serial.print(gx);
  Serial.print(',');
  Serial.print(gy);
  Serial.print(',');
  Serial.println(gz);

  delay(SAMPLE_INTERVAL_MS);
}
