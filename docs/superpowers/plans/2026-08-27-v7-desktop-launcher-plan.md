# V7 桌面启动器 Implementation Plan

**Goal:** 固定桌面快捷方式始终启动 V7 主目录的最新成功构建。

### Task 1: 自动构建启动器

- Create `启动六合分析软件V7.ps1`：检测源码时间、按需 Release 构建、构建成功后启动 `六合分析软件V7.exe`。
- Update 桌面 `六合分析软件 V7.lnk`：目标为 PowerShell 启动器，工作目录为 V7 主目录。

### Task 2: 将自动学习显示修正落到 V7

- Modify `AIPredictHistoryForm.cs`：AI预测历史排除 `V7 AutoLearning` 并保留 `V6.5 AutoLearning`。
- Modify `Tests/Program.cs`：验证 V7 自动学习仍保存，但不显示于 AI预测历史。
