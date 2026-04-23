#pragma once

#include <Arduino.h>

struct ImuSample {
  int16_t ax = 0;
  int16_t ay = 0;
  int16_t az = 0;
  int16_t gx = 0;
  int16_t gy = 0;
  int16_t gz = 0;
  int16_t temp = 0;
  unsigned long timestampMs = 0;
};

// MPU6050 原始数据采集器：负责初始化与寄存器读取。
class ImuSensor {
 public:
  bool begin();
  bool isReady() const;
  bool readSample(ImuSample* outSample);

 private:
  bool writeReg(uint8_t reg, uint8_t value);
  bool readRegs(uint8_t startReg, uint8_t* buffer, size_t length);

  uint8_t deviceAddress_ = 0;
  bool ready_ = false;
};
