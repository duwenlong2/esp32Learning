using System;

namespace GestureLab.Ble;

/// <summary>
/// 手势触发结果：用于上层应用回调消费。
/// </summary>
public readonly struct GestureTriggerResult
{
    public GestureTriggerResult(
        string gestureName,
        bool hotkeySent,
        int triggerCount,
        DateTime triggeredAt,
        ImuPoseSample pose)
    {
        GestureName = gestureName;
        HotkeySent = hotkeySent;
        TriggerCount = triggerCount;
        TriggeredAt = triggeredAt;
        Pose = pose;
    }

    public string GestureName { get; }
    public bool HotkeySent { get; }
    public int TriggerCount { get; }
    public DateTime TriggeredAt { get; }
    public ImuPoseSample Pose { get; }
}