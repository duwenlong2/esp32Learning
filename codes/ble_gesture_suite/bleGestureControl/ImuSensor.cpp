#include "ImuSensor.h"

#include <Wire.h>

#include "AppConfig.h"

namespace {
constexpr uint8_t kRegPwrMgmt1 = 0x6B;
constexpr uint8_t kRegAccelStart = 0x3B;

int16_t toInt16(uint8_t high, uint8_t low)
{
  return static_cast<int16_t>((static_cast<int16_t>(high) << 8) | low);
}
}  // namespace

bool ImuSensor::begin()
{
  Wire.begin(AppConfig::kI2cSdaPin, AppConfig::kI2cSclPin);
  Wire.setClock(400000);

  deviceAddress_ = 0;
  ready_ = false;

  const uint8_t candidates[] = {AppConfig::kImuAddressPrimary, AppConfig::kImuAddressSecondary};
  for (uint8_t address : candidates) {
    Wire.beginTransmission(address);
    if (Wire.endTransmission() == 0) {
      deviceAddress_ = address;
      break;
    }
  }

  if (deviceAddress_ == 0) {
    Serial.println("IMU not found on I2C (0x68/0x69)");
    return false;
  }

  if (!writeReg(kRegPwrMgmt1, 0x00)) {
    Serial.println("IMU wake-up failed");
    return false;
  }

  delay(100);
  ready_ = true;

  Serial.print("IMU ready on address 0x");
  Serial.println(deviceAddress_, HEX);
  return true;
}

bool ImuSensor::isReady() const
{
  return ready_;
}

bool ImuSensor::readSample(ImuSample* outSample)
{
  if (!ready_ || outSample == nullptr) {
    return false;
  }

  uint8_t raw[14] = {0};
  if (!readRegs(kRegAccelStart, raw, sizeof(raw))) {
    return false;
  }

  outSample->ax = toInt16(raw[0], raw[1]);
  outSample->ay = toInt16(raw[2], raw[3]);
  outSample->az = toInt16(raw[4], raw[5]);
  outSample->temp = toInt16(raw[6], raw[7]);
  outSample->gx = toInt16(raw[8], raw[9]);
  outSample->gy = toInt16(raw[10], raw[11]);
  outSample->gz = toInt16(raw[12], raw[13]);
  outSample->timestampMs = millis();

  return true;
}

bool ImuSensor::writeReg(uint8_t reg, uint8_t value)
{
  Wire.beginTransmission(deviceAddress_);
  Wire.write(reg);
  Wire.write(value);
  return Wire.endTransmission() == 0;
}

bool ImuSensor::readRegs(uint8_t startReg, uint8_t* buffer, size_t length)
{
  Wire.beginTransmission(deviceAddress_);
  Wire.write(startReg);
  if (Wire.endTransmission(false) != 0) {
    return false;
  }

  size_t received = Wire.requestFrom(static_cast<int>(deviceAddress_), static_cast<int>(length), static_cast<int>(true));
  if (received != length) {
    return false;
  }

  for (size_t i = 0; i < length; ++i) {
    buffer[i] = Wire.read();
  }

  return true;
}
