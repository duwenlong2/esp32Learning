using System;

namespace GestureLab.Ble;

public readonly struct ImuRawSample
{
    public ImuRawSample(int ax, int ay, int az, int gx, int gy, int gz, int temp)
    {
        Ax = ax;
        Ay = ay;
        Az = az;
        Gx = gx;
        Gy = gy;
        Gz = gz;
        Temp = temp;
    }

    public int Ax { get; }
    public int Ay { get; }
    public int Az { get; }
    public int Gx { get; }
    public int Gy { get; }
    public int Gz { get; }
    public int Temp { get; }
}

public readonly struct ImuCalibratedSample
{
    public ImuCalibratedSample(
        double gravityX,
        double gravityY,
        double gravityZ,
        double linearX,
        double linearY,
        double linearZ,
        double gyroBiasX,
        double gyroBiasY,
        double gyroBiasZ,
        bool isStationary,
        bool isReady)
    {
        GravityX = gravityX;
        GravityY = gravityY;
        GravityZ = gravityZ;
        LinearX = linearX;
        LinearY = linearY;
        LinearZ = linearZ;
        GyroBiasX = gyroBiasX;
        GyroBiasY = gyroBiasY;
        GyroBiasZ = gyroBiasZ;
        IsStationary = isStationary;
        IsReady = isReady;
    }

    public double GravityX { get; }
    public double GravityY { get; }
    public double GravityZ { get; }
    public double LinearX { get; }
    public double LinearY { get; }
    public double LinearZ { get; }
    public double GyroBiasX { get; }
    public double GyroBiasY { get; }
    public double GyroBiasZ { get; }
    public bool IsStationary { get; }
    public bool IsReady { get; }
}

// 无感自动校准：静止时更新陀螺偏置，持续低通估计重力方向。
public sealed class ImuAutoCalibrator
{
    private const double OneG = 16384.0;
    private const double GyroStillThreshold = 320.0;
    private const double AccelMagTolerance = 2000.0;
    private const double GyroBiasAlpha = 0.03;
    private const double GravityAlphaStill = 0.14;
    private const double GravityAlphaMove = 0.22;
    private const int ReadyStableFrames = 20;

    private bool _initialized;
    private int _stableFrames;
    private double _gyroBiasX;
    private double _gyroBiasY;
    private double _gyroBiasZ;
    private double _gravityX;
    private double _gravityY;
    private double _gravityZ;

    public ImuCalibratedSample Update(ImuRawSample raw)
    {
        if (!_initialized)
        {
            _gravityX = raw.Ax;
            _gravityY = raw.Ay;
            _gravityZ = raw.Az;
            _initialized = true;
        }

        double gyroMag = Math.Sqrt((raw.Gx * raw.Gx) + (raw.Gy * raw.Gy) + (raw.Gz * raw.Gz));
        double accelMag = Math.Sqrt((raw.Ax * raw.Ax) + (raw.Ay * raw.Ay) + (raw.Az * raw.Az));
        bool isStationary = gyroMag < GyroStillThreshold && Math.Abs(accelMag - OneG) < AccelMagTolerance;

        if (isStationary)
        {
            _gyroBiasX = Lerp(_gyroBiasX, raw.Gx, GyroBiasAlpha);
            _gyroBiasY = Lerp(_gyroBiasY, raw.Gy, GyroBiasAlpha);
            _gyroBiasZ = Lerp(_gyroBiasZ, raw.Gz, GyroBiasAlpha);
            _stableFrames++;
        }
        else
        {
            _stableFrames = 0;
        }

        double gravityAlpha = isStationary ? GravityAlphaStill : GravityAlphaMove;
        _gravityX = Lerp(_gravityX, raw.Ax, gravityAlpha);
        _gravityY = Lerp(_gravityY, raw.Ay, gravityAlpha);
        _gravityZ = Lerp(_gravityZ, raw.Az, gravityAlpha);

        double linearX = raw.Ax - _gravityX;
        double linearY = raw.Ay - _gravityY;
        double linearZ = raw.Az - _gravityZ;

        bool isReady = _stableFrames >= ReadyStableFrames;
        return new ImuCalibratedSample(
            gravityX: _gravityX,
            gravityY: _gravityY,
            gravityZ: _gravityZ,
            linearX: linearX,
            linearY: linearY,
            linearZ: linearZ,
            gyroBiasX: _gyroBiasX,
            gyroBiasY: _gyroBiasY,
            gyroBiasZ: _gyroBiasZ,
            isStationary: isStationary,
            isReady: isReady);
    }

    private static double Lerp(double from, double to, double alpha)
    {
        return from + ((to - from) * alpha);
    }
}