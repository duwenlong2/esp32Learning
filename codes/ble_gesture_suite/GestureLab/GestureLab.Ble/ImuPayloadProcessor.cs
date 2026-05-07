using System;
using System.Globalization;

namespace GestureLab.Ble;

public enum ImuProcessingStatus
{
    Success,
    ParseFailed,
    PoseFailed,
}

public readonly struct ImuPoseSample
{
    public ImuPoseSample(double roll, double pitch, double yaw)
    {
        Roll = roll;
        Pitch = pitch;
        Yaw = yaw;
    }

    public double Roll { get; }
    public double Pitch { get; }
    public double Yaw { get; }
}

public readonly struct ImuProcessingResult
{
    public ImuProcessingResult(string rawPayload, ImuRawSample raw, ImuCalibratedSample calibrated, ImuPoseSample pose)
    {
        RawPayload = rawPayload;
        Raw = raw;
        Calibrated = calibrated;
        Pose = pose;
    }

    public string RawPayload { get; }
    public ImuRawSample Raw { get; }
    public ImuCalibratedSample Calibrated { get; }
    public ImuPoseSample Pose { get; }
}

public sealed class ImuPayloadProcessor
{
    private readonly ImuAutoCalibrator _autoCalibrator = new();

    public ImuProcessingStatus Process(string payload, out ImuProcessingResult result)
    {
        result = default;
        if (!TryParseImuRaw(payload, out ImuRawSample rawSample))
        {
            return ImuProcessingStatus.ParseFailed;
        }

        ImuCalibratedSample calibrated = _autoCalibrator.Update(rawSample);
        if (!TryBuildPoseFromCalibratedGravity(calibrated, out ImuPoseSample pose))
        {
            return ImuProcessingStatus.PoseFailed;
        }

        result = new ImuProcessingResult(payload, rawSample, calibrated, pose);
        return ImuProcessingStatus.Success;
    }

    private static bool TryBuildPoseFromCalibratedGravity(ImuCalibratedSample calibrated, out ImuPoseSample pose)
    {
        (double mx, double my, double mz) = MapAccelToViewerAxes(
            calibrated.GravityX,
            calibrated.GravityY,
            calibrated.GravityZ);

        // 以加速度计估计出的重力方向作为实时姿态的稳定基线。
        double radToDeg = 180.0 / Math.PI;
        double roll = Math.Atan2(my, mz) * radToDeg;
        double pitch = Math.Atan2(-mx, Math.Sqrt(my * my + mz * mz)) * radToDeg;
        double yaw = 0;
        pose = new ImuPoseSample(roll, pitch, yaw);
        return true;
    }

    private static (double x, double y, double z) MapAccelToViewerAxes(double ax, double ay, double az)
    {
        return (-ay, -ax, az);
    }

    private static bool TryParseImuRaw(string payload, out ImuRawSample sample)
    {
        sample = default;
        string[] parts = payload.Split(',');
        if (parts.Length < 9 || !string.Equals(parts[0], "IMU", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ax)
            || !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int ay)
            || !int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int az)
            || !int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int gx)
            || !int.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int gy)
            || !int.TryParse(parts[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out int gz)
            || !int.TryParse(parts[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out int temp))
        {
            return false;
        }

        sample = new ImuRawSample(ax, ay, az, gx, gy, gz, temp);
        return true;
    }
}