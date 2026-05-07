# GestureLab 抬手动作标定流程

## 1. 采集规范

1. 在 GestureLab 里点击 `Start` 开始录制。
2. 每次你觉得动作触发正确时，打一次 `MOUTH_HOLD` 标记（可手动 mark 或触发后自动写入）。
3. 每个会话建议采集：
   - 成功样本 >= 30 次
   - 失败样本（故意姿态偏差/速度偏差）>= 20 次
4. 点击 `Stop` 结束会话。
5. 重复多个会话（不同时间段、不同佩戴松紧、不同速度）。

记录文件默认在：

- `%LOCALAPPDATA%/GestureLab/records/*.csv`

## 2. 批量汇总与拟合

运行脚本（Python 3.10+）：

```powershell
python .\codes\ble_gesture_suite\scripts\fit_mouth_calibration.py `
  --input "$env:LOCALAPPDATA\GestureLab\records" `
  --marker MOUTH_HOLD `
  --output .\codes\ble_gesture_suite\scripts\calibration_output
```

输出：

1. `mouth_calibration_MOUTH_HOLD.json`
2. `mouth_calibration_MOUTH_HOLD.md`

## 3. 拟合策略（当前实现）

脚本会自动做两段窗口统计：

1. 稳定段：`[marker-0.45s, marker-0.05s]`
   - 用于拟合 `PosePitchAbsMin/Max`、`PoseRollAbsMax`、`StillGyroThreshold`
2. 抬手段：`[marker-1.2s, marker-0.45s]`
   - 用于拟合 `MoveStartGyroThreshold`

并结合全窗口峰值拟合：

1. `HoldBreakGyroThreshold`
2. `HoldSeconds`
3. `CooldownSeconds`

## 4. 参数落地建议

先替换为报告中的建议常量，跑一轮 A/B 对比：

1. 新参数（建议值）
2. 旧参数（当前线上值）

比较以下指标：

1. 触发成功率（成功动作被识别）
2. 误触发率（无意动作触发）
3. 触发延迟（用户到位到触发的时间）

## 5. 下一步可升级的拟合方式

当前是稳健分位数拟合。后续可升级：

1. ROC/F1 网格搜索（多阈值联合）
2. 轻量二分类模型（Logistic Regression）
3. 用户级自适应阈值（每人单独微调）
