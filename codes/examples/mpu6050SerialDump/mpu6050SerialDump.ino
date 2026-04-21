#include <Wire.h>
#include <Adafruit_MPU6050.h>
#include <Adafruit_Sensor.h>

// 这是 MPU6050 设备对象，后续通过它读取加速度/角速度/温度。
Adafruit_MPU6050 mpu;

// ESP32-C3 SuperMini 建议先用 4/5，避开部分启动相关脚位干扰。
constexpr int I2C_SDA_PIN = 4;
constexpr int I2C_SCL_PIN = 5;

// 串口输出波特率。
constexpr unsigned long SERIAL_BAUD = 115200;

// 采样输出周期（毫秒）。
constexpr unsigned long SAMPLE_INTERVAL_MS = 100;

// WHO_AM_I 寄存器地址。
constexpr uint8_t REG_WHO_AM_I = 0x75;

// 尝试按给定地址初始化 MPU6050。
bool tryInitMpuAtAddress(uint8_t address)
{
  if (mpu.begin(address, &Wire)) {
    Serial.print("MPU6050 init success, I2C address = 0x");
    Serial.println(address, HEX);
    return true;
  }
  return false;
}

// 扫描当前 I2C 总线，打印所有应答地址。
bool scanI2C()
{
  bool foundAny = false;
  Serial.println("I2C scan result:");

  for (uint8_t addr = 1; addr < 127; ++addr) {
    Wire.beginTransmission(addr);
    uint8_t err = Wire.endTransmission();
    if (err == 0) {
      foundAny = true;
      Serial.print("  found device at 0x");
      if (addr < 16) {
        Serial.print('0');
      }
      Serial.println(addr, HEX);
    }
  }

  if (!foundAny) {
    Serial.println("  no I2C device found");
  }
  return foundAny;
}

bool readRegister(uint8_t address, uint8_t reg, uint8_t& value)
{
  Wire.beginTransmission(address);
  Wire.write(reg);
  if (Wire.endTransmission(false) != 0) {
    return false;
  }

  if (Wire.requestFrom(static_cast<int>(address), 1) != 1) {
    return false;
  }

  value = Wire.read();
  return true;
}

// 打印一行表头，便于在串口监视器中对齐查看。
void printHeader()
{
  Serial.println("time_ms,ax,ay,az,gx,gy,gz,temp_c");
}

// 读取一次传感器并输出 CSV 格式。
void printOneSample()
{
  sensors_event_t accel;
  sensors_event_t gyro;
  sensors_event_t temp;

  mpu.getEvent(&accel, &gyro, &temp);

  Serial.print(millis());
  Serial.print(',');
  Serial.print(accel.acceleration.x, 3);
  Serial.print(',');
  Serial.print(accel.acceleration.y, 3);
  Serial.print(',');
  Serial.print(accel.acceleration.z, 3);
  Serial.print(',');
  Serial.print(gyro.gyro.x, 3);
  Serial.print(',');
  Serial.print(gyro.gyro.y, 3);
  Serial.print(',');
  Serial.print(gyro.gyro.z, 3);
  Serial.print(',');
  Serial.println(temp.temperature, 2);
}

void setup()
{
  Serial.begin(SERIAL_BAUD);
  delay(1000);

  Serial.println();
  Serial.println("ESP32-C3 MPU6050 serial dump start");

  Serial.print("Use I2C pins: SDA=");
  Serial.print(I2C_SDA_PIN);
  Serial.print(", SCL=");
  Serial.println(I2C_SCL_PIN);

  Wire.begin(I2C_SDA_PIN, I2C_SCL_PIN);
  Wire.setClock(100000);

  bool foundAny = scanI2C();
  if (!foundAny) {
    Serial.println("ERROR: no I2C device found on bus.");
    while (true) {
      delay(1000);
    }
  }

  uint8_t targetAddr = 0;
  if (readRegister(0x68, REG_WHO_AM_I, targetAddr)) {
    targetAddr = 0x68;
  } else if (readRegister(0x69, REG_WHO_AM_I, targetAddr)) {
    targetAddr = 0x69;
  }

  if (targetAddr == 0) {
    Serial.println("ERROR: device exists on I2C, but 0x68/0x69 register read failed.");
    while (true) {
      delay(1000);
    }
  }

  uint8_t who = 0;
  if (readRegister(targetAddr, REG_WHO_AM_I, who)) {
    Serial.print("WHO_AM_I @0x");
    Serial.print(targetAddr, HEX);
    Serial.print(" = 0x");
    if (who < 16) {
      Serial.print('0');
    }
    Serial.println(who, HEX);

    if (who != 0x68) {
      Serial.println("WARNING: this chip may not be MPU6050 (possibly MPU6500/other clone).");
    }
  }

  bool ok = tryInitMpuAtAddress(targetAddr);

  if (!ok) {
    Serial.println("ERROR: Adafruit MPU6050 init failed at detected address.");
    Serial.println("This module may not be a true MPU6050 chip.");
    while (true) {
      delay(1000);
    }
  }

  // 设置量程和滤波带宽，先用一组稳妥默认值。
  mpu.setAccelerometerRange(MPU6050_RANGE_8_G);
  mpu.setGyroRange(MPU6050_RANGE_500_DEG);
  mpu.setFilterBandwidth(MPU6050_BAND_21_HZ);

  printHeader();
}

void loop()
{
  printOneSample();
  delay(SAMPLE_INTERVAL_MS);
}
