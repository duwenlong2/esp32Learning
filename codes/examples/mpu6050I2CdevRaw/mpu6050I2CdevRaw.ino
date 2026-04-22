#include <Wire.h>
#include "I2Cdev.h"
#include "MPU6050.h"

// 固定使用 ESP32-C3 SuperMini 的 I2C 引脚（SDA=4, SCL=5）。
constexpr int I2C_SDA_PIN = 4;
constexpr int I2C_SCL_PIN = 5;

constexpr unsigned long SERIAL_BAUD = 115200;
constexpr unsigned long SAMPLE_INTERVAL_MS = 100;
constexpr uint8_t IMU_ADDR = 0x68;
constexpr int CALIBRATION_SAMPLES = 200;
constexpr unsigned long CALIBRATION_DELAY_MS = 10;

// 默认地址 0x68（AD0 拉高时为 0x69）。
MPU6050 mpu(IMU_ADDR);

int16_t ax = 0;
int16_t ay = 0;
int16_t az = 0;
int16_t gx = 0;
int16_t gy = 0;
int16_t gz = 0;

float gyroBiasX = 0.0f;
float gyroBiasY = 0.0f;
float gyroBiasZ = 0.0f;

bool useDirectRawMode = false;
int activeSdaPin = I2C_SDA_PIN;
int activeSclPin = I2C_SCL_PIN;

void beginI2CBus(int sdaPin, int sclPin)
{
  activeSdaPin = sdaPin;
  activeSclPin = sclPin;

  Wire.end();
  delay(5);
  Wire.begin(activeSdaPin, activeSclPin);
  Wire.setClock(100000);

  Serial.print("Use I2C pins: SDA=");
  Serial.print(activeSdaPin);
  Serial.print(", SCL=");
  Serial.println(activeSclPin);
}

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

// 读取一帧 6 轴原始数据。
bool readMotionRaw()
{
  if (!useDirectRawMode) {
    mpu.getMotion6(&ax, &ay, &az, &gx, &gy, &gz);
    return true;
  }

  uint8_t raw[14] = {0};
  if (!readRegs(IMU_ADDR, 0x3B, raw, sizeof(raw))) {
    return false;
  }

  ax = toInt16(raw[0], raw[1]);
  ay = toInt16(raw[2], raw[3]);
  az = toInt16(raw[4], raw[5]);
  gx = toInt16(raw[8], raw[9]);
  gy = toInt16(raw[10], raw[11]);
  gz = toInt16(raw[12], raw[13]);
  return true;
}

bool readChipIdentity(uint8_t& rawWho, uint8_t& devId)
{
  rawWho = 0;
  devId = 0;

  Wire.beginTransmission(IMU_ADDR);
  Wire.write(0x75);
  if (Wire.endTransmission(false) != 0) {
    return false;
  }
  if (Wire.requestFrom((int)IMU_ADDR, 1, (int)true) != 1) {
    return false;
  }
  rawWho = Wire.read();
  devId = mpu.getDeviceID();
  return true;
}

// 开机时保持静止，求出陀螺仪零偏。
void calibrateGyroBias()
{
  long sumX = 0;
  long sumY = 0;
  long sumZ = 0;

  Serial.println("Keep sensor still. Start gyro bias calibration...");

  for (int i = 0; i < CALIBRATION_SAMPLES; ++i) {
    if (!readMotionRaw()) {
      Serial.println("Calibration read failed, retry...");
      delay(CALIBRATION_DELAY_MS);
      continue;
    }

    sumX += gx;
    sumY += gy;
    sumZ += gz;
    delay(CALIBRATION_DELAY_MS);
  }

  gyroBiasX = (float)sumX / CALIBRATION_SAMPLES;
  gyroBiasY = (float)sumY / CALIBRATION_SAMPLES;
  gyroBiasZ = (float)sumZ / CALIBRATION_SAMPLES;

  Serial.print("Gyro bias X = ");
  Serial.println(gyroBiasX, 2);
  Serial.print("Gyro bias Y = ");
  Serial.println(gyroBiasY, 2);
  Serial.print("Gyro bias Z = ");
  Serial.println(gyroBiasZ, 2);
}

// 扫描 I2C 设备，确认总线连通。
bool scanI2C()
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

  return found;
}

void setup()
{
  Serial.begin(SERIAL_BAUD);
  delay(1000);

  Serial.println();
  Serial.println("ESP32-C3 MPU6050 I2Cdev raw demo start");

  beginI2CBus(I2C_SDA_PIN, I2C_SCL_PIN);

  bool foundAny = scanI2C();
  if (!foundAny) {
    Serial.println("ERROR: stop because no I2C device was detected.");
    while (true) {
      delay(1000);
    }
  }

  Serial.println("Initializing MPU6050...");
  mpu.initialize();

  // 先打印身份寄存器信息，便于判断芯片和库是否匹配。
  uint8_t rawWho = 0;
  uint8_t devId = 0;
  bool idOk = readChipIdentity(rawWho, devId);

  Serial.print("WHO_AM_I raw(0x75)=0x");
  if (rawWho < 16) {
    Serial.print('0');
  }
  Serial.println(rawWho, HEX);

  Serial.print("getDeviceID()=0x");
  Serial.println(devId, HEX);

  if (!idOk) {
    Serial.println("ERROR: identity read failed. Check wiring/contact first.");
  }

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
      Serial.print("Active pins were SDA=");
      Serial.print(activeSdaPin);
      Serial.print(", SCL=");
      Serial.println(activeSclPin);
      while (true) {
        delay(1000);
      }
    }
  }

  calibrateGyroBias();

  Serial.println("time_ms,ax,ay,az,gx_raw,gy_raw,gz_raw,gx_zero,gy_zero,gz_zero");
}

void loop()
{
  if (!readMotionRaw()) {
    Serial.println("read raw data failed");
    delay(SAMPLE_INTERVAL_MS);
    return;
  }

  float gxZero = gx - gyroBiasX;
  float gyZero = gy - gyroBiasY;
  float gzZero = gz - gyroBiasZ;

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
  Serial.print(gz);
  Serial.print(',');
  Serial.print(gxZero, 2);
  Serial.print(',');
  Serial.print(gyZero, 2);
  Serial.print(',');
  Serial.println(gzZero, 2);

  delay(SAMPLE_INTERVAL_MS);
}
