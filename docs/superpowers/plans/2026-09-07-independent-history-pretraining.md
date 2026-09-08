# 独立学习模型历史预训练 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 对2026233—2026249的17期冻结基础快照完成独立、可重放的历史预训练，并在数据中心只读展示每期学习成果。

**Architecture:** `IndependentLearningHistoricalTraining` 只接收经过验证的冻结样本，调用既有独立模型的`Predict`和`Learn`，输出独立数据库、训练报告和archive。`IndependentLearningHistoryForm` 仅读取报告，不接触正式预测表或旧学习记忆。2026244作为显式的云端UTC来源样本保存在冻结清单内。

**Tech Stack:** .NET 10、WinForms、System.Data.SQLite、System.Text.Json、既有SmokeTests。

## Global Constraints

- 不修改V6.5/V7正式评分、权重、排序、PredictionHistory或旧AutoLearning。
- 训练只写入`experiments/v7-independent-learning-v1/history-pretraining`。
- 训练输入固定为50/100/全历史的完整12生肖排序；每期必须在开奖前生成。
- 2026244必须保留云端UTC来源与提交证据。
- 不下载新依赖，使用本地NuGet缓存；测试和构建使用`--no-restore`。

---

### Task 1: 冻结清单与训练器契约

**Files:** Create `IndependentLearningHistoricalTraining.cs`; create `experiments/v7-independent-learning-v1/history-pretraining-2026233-2026249.json`; modify `Tests/IndependentLearningTests.cs`.

**Interface:** `Train(string dataRoot, IReadOnlyList<HistoricalTrainingSample> samples)`按期号执行预测、再学习并返回每期审计项。

- [ ] 写入一个会失败的测试：两期连续样本训练后，两个收据存在，最终版本为2。
- [ ] 运行 `dotnet run --project Tests/六合分析软件.SmokeTests.csproj --no-restore -- --independent-learning-smoke`，确认因训练器不存在而失败。
- [ ] 最小实现：校验每期连续、截止期小于目标期、生成早于开奖、三份完整排名；空分支中调用`Predict`再调用`Learn`。
- [ ] 重跑同一测试，确认通过；提交训练器与测试。

### Task 2: 报告、重放和拒绝规则

**Files:** Modify `IndependentLearningHistoricalTraining.cs` and `Tests/IndependentLearningTests.cs`.

**Interface:** 训练将报告和archive写入`history-pretraining/report.json`和`history-pretraining/archive.json`，每行包括预测排名、真实名次、Top3/Top6、前后权重与来源。

- [ ] 写入会失败的测试：报告文件存在；重复训练同一分支、缺期、无效排名或训练后生成的快照均被拒绝。
- [ ] 运行独立学习Smoke测试并确认失败。
- [ ] 最小实现原子写入报告与archive；拒绝非空实验分支，确保异常不会写入正式数据库。
- [ ] 重跑测试确认通过；提交报告和安全校验代码。

### Task 3: 数据中心只读观察窗口

**Files:** Create `IndependentLearningHistoryForm.cs`; modify `V65ExperimentScoreboardView.cs` and `Tests/Program.cs`.

**Interface:** `LoadReport(string dataRoot)`读取独立报告；表格显示期号、真实生肖、Top3、Top6、实际名次、命中、三项权重前后和来源，不提供训练或修改按钮。

- [ ] 写入会失败的UI测试：成绩榜包含文字“独立学习历史实验”的入口。
- [ ] 运行完整SmokeTests并确认失败。
- [ ] 最小实现入口和只读表格；无报告时仅显示“尚未完成历史实验”。
- [ ] 重跑完整SmokeTests确认通过；提交窗口和测试。

### Task 4: 运行17期历史实验并验收

**Files:** Modify `PredictionRunner/Program.cs`; create `experiments/v7-independent-learning-v1/history-pretraining/report.json` and `archive.json`.

**Interface:** `--independent-history-train <manifest>`只读取版本化冻结清单，训练到独立实验目录。

- [ ] 写入会失败的命令行测试：缺失清单以非零退出且没有创建实验分支。
- [ ] 使用`PredictionRunner`运行该测试并确认失败。
- [ ] 实现清单加载与训练命令；用已核实清单运行实际17期训练。
- [ ] 运行 `dotnet run --project Tests/六合分析软件.SmokeTests.csproj --no-restore` 和 `dotnet build 六合分析软件.csproj -c Release --no-restore --verbosity quiet`。报告必须有17期、最终版本17、最终期2026249，并检查正式数据库未写入。
- [ ] 分开提交运行器代码和生成的实验报告。
