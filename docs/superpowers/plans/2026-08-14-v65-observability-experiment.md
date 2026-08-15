# V6.5 错因学习与旁路实验 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在不改变正式 V6.5 输出、历史或学习内存的前提下，建立预测追踪、错误观察、候选赛马和残差实验。

**Architecture:** 研究功能使用独立 SQLite 表与旁路服务。正式生成只额外触发不可变 Trace 捕获；开奖验证只额外触发观察；所有回测从显式历史前缀运行并写入独立 ExperimentRun。

**Tech Stack:** .NET 10 / C#，System.Data.SQLite，System.Text.Json，现有 SmokeTests 控制台测试。

## Global Constraints

- 不改正式基础模型和正式 AutoLearning 的评分、权重、排序、学习行为。
- 不修改或补写旧 `PredictionHistory`、开奖历史或正式 `ModelMemory`。
- Trace 仅捕获今后真实生成的 Live 快照；实验重跑只写 `ExperimentRun`。
- 所有实验默认关闭且不得改正式 Top3/Top6。
- 回测每一目标期只能使用目标期前的数据；报告必须区分训练、验证和最终留出。

---

## File Structure

- Create `PredictionTraceModels.cs`：Trace/归因/实验值对象与 JSON 契约。
- Create `PredictionTraceService.cs`：独立表建表、不可变写入、读取、Auto 追踪捕获。
- Create `ErrorAttributionObserver.cs`：标签和 F/T/O/H/P/B 反事实计算。
- Create `ExperimentRaceService.cs`：严格前缀赛马、指标、相关性、RescueRate、随机基准。
- Create `ResidualCorrectionModel.cs`：默认禁用的有界残差修正。
- Create `V65ObservabilityReportService.cs`：综合报告和数据完整性只读检查。
- Modify `DatabaseHelper.cs`：只增加研究表 schema 和旁路访问入口。
- Modify `DailyPredictionAutomation.cs`：正式保存后捕获 Live Trace；开奖验证后调用 observer，均不影响正式失败处理。
- Modify `Tests/Program.cs`：研究设施的真实行为回归测试。

### Task 1: 研究表契约与不可变 Trace

**Files:** Create `PredictionTraceModels.cs`, `PredictionTraceService.cs`; Modify `DatabaseHelper.cs`; Test `Tests/Program.cs`.

- [ ] **Step 1: Write the failing test**：构造 12 生肖、三基础结果和 Auto 快照；保存一次后读取应保持 3×12 明细、截止期、权重和哈希；以同一键不同 payload 再保存应抛出且原值不变。
- [ ] **Step 2: Run test to verify it fails**：`dotnet run --project Tests\\六合分析软件.SmokeTests.csproj --no-restore`；预期编译失败，缺少 `PredictionTraceService`。
- [ ] **Step 3: Write minimal implementation**：建 `PredictionTrace` 和 `PredictionTraceModel` 研究表，以 `Issue+Schema+CaptureKind` 唯一；把每生肖 raw/贡献/名次和 Auto 输入输出序列化为 canonical JSON，并计算 SHA-256；用 `INSERT` 而非 update。
- [ ] **Step 4: Run test to verify it passes**：执行同一命令；预期新增测试 PASS。
- [ ] **Step 5: Commit**：`git add ... && git commit -m "feat: add immutable V6.5 prediction traces"`。

### Task 2: 正式链旁路捕获与开奖后 Auto 结果

**Files:** Modify `DailyPredictionAutomation.cs`, `DatabaseHelper.cs`, `PredictionTraceService.cs`; Test `Tests/Program.cs`.

- [ ] **Step 1: Write the failing test**：以临时库运行一轮自动化预测，断言正式四条预测保持原有字段，额外存在一条 Live Trace；验证开奖后 Trace 存储实际生肖、四个实际名次、命中与 Auto 权重/系数前后值。
- [ ] **Step 2: Run test to verify it fails**：运行 smoke tests；预期 Live Trace 或开奖观察字段缺失。
- [ ] **Step 3: Write minimal implementation**：仅在三基础和正式 Auto 均保存成功后捕获同一次真实前缀的 Trace；在正式验证完成后从 immutable Trace 追加研究性 outcome，不更新 Trace 主 payload，不改原有验证或学习顺序。
- [ ] **Step 4: Run test to verify it passes**：运行 smoke tests；正式快照比较不变、旁路断言 PASS。
- [ ] **Step 5: Commit**：提交“capture live trace without altering production predictions”。

### Task 3: 错因观察与反事实分析

**Files:** Create `ErrorAttributionObserver.cs`; Modify `PredictionTraceService.cs`, `DatabaseHelper.cs`; Test `Tests/Program.cs`.

- [ ] **Step 1: Write failing tests**：分别构造同向误杀、边界 7/8、严重方向错误、基础 Top6 而 Auto 排除、统一错误候选一致性陷阱和去 P 后从 10 到 5 的 trace；断言多标签、证据和 `RankImpact=5`。
- [ ] **Step 2: Verify RED**：运行 smoke tests；预期 observer 类型不存在。
- [ ] **Step 3: Implement**：使用设计文档 v1 的确定性阈值；对 F/T/O/H/P/B 只移除该贡献后稳定排序（原名次为 tie-breaker），写 `ErrorAttribution`、`FactorCounterfactual` 的 insert-if-absent；无 Live Trace 返回未观察，不触碰旧记录。
- [ ] **Step 4: Verify GREEN**：运行 smoke tests；每个标签/反事实断言通过。
- [ ] **Step 5: Commit**：提交“add read-only error attribution observer”。

### Task 4: 统计、独立性与完整性报告

**Files:** Create `V65ObservabilityReportService.cs`; Test `Tests/Program.cs`.

- [ ] **Step 1: Write failing tests**：以已观察的 20+ 条 fixture 验证 20/50/100/全部窗口、Top1/3/6、MRR、中位数、最大连败、标签频率、Spearman、Top3/6 overlap 和同时失败率；构造缺 Trace/缺 Auto/待开奖/旧 V7 混用各一条。
- [ ] **Step 2: Verify RED**：运行 smoke tests；预期报告服务不存在。
- [ ] **Step 3: Implement**：所有聚合仅读取 Trace/研究表/正式历史，输出标有数据区间、样本数和局限的 Markdown；完整性检查只列问题，不写库。
- [ ] **Step 4: Verify GREEN**：运行 smoke tests；指标与 fixture 手算值一致。
- [ ] **Step 5: Commit**：提交“add V6.5 observability statistics and integrity report”。

### Task 5: 有界残差实验

**Files:** Create `ResidualCorrectionModel.cs`; Test `Tests/Program.cs`.

- [ ] **Step 1: Write failing tests**：给定基础标准化分和历史观察统计，测试 5/10/15% 参数上限、最多跨两名、关闭时精确返回原排序、目标期标签不可作为特征。
- [ ] **Step 2: Verify RED**：运行 smoke tests；预期模型不存在。
- [ ] **Step 3: Implement**：以显式 `ResidualExperimentConfig(Enabled=false, MaxPercent)` 和只读历史派生的残差特征计算 correction；输出独立详细解释，永不访问/保存正式 memory 或 PredictionHistory。
- [ ] **Step 4: Verify GREEN**：运行 smoke tests；边界与关闭行为 PASS。
- [ ] **Step 5: Commit**：提交“add bounded residual correction experiment”。

### Task 6: 严格赛马场与 A–G Walk-Forward

**Files:** Create `ExperimentRaceService.cs`; Test `Tests/Program.cs`.

- [ ] **Step 1: Write failing tests**：对有意插入未来行的两份历史重放相同期号，断言预测相同；验证 A–G 都使用同一期集合，B 的 Rule 上限参数、C/D 残差、E 平均、F 单模、G seed 随机；断言 RescueRate 和留出集不参与选择参数。
- [ ] **Step 2: Verify RED**：运行 smoke tests；预期赛马服务不存在。
- [ ] **Step 3: Implement**：`ExperimentRun` 固化参数/切分/hash；按时间顺序训练→验证→最终留出；每期先预测、再读标签、再更新仅实验状态；先适配并前缀验证现有候选模型，失败者标记拒绝而不入榜。
- [ ] **Step 4: Verify GREEN**：运行 smoke tests 和项目 build；所有模型指标、随机基准与泄漏回归通过。
- [ ] **Step 5: Commit**：提交“add isolated candidate model race and walk-forward evaluation”。

### Task 7: 每日实验预测与综合报告

**Files:** Modify `DailyPredictionAutomation.cs`; Modify/Create report service; Test `Tests/Program.cs`.

- [ ] **Step 1: Write failing test**：开启实验生成但不启用生产开关，断言生成独立实验预测；正式 daily record 和正式四模型预测字节不变。
- [ ] **Step 2: Verify RED**：运行 smoke tests；预期实验输出缺失。
- [ ] **Step 3: Implement**：每日正式预测完成后写独立 ExperimentPrediction，默认只生成可参考的残差候选；生成《V6.5 错因学习与旁路实验综合报告》，包括所有要求章节、训练/验证/留出和“不自动上线”结论。
- [ ] **Step 4: Verify GREEN**：完整 smoke tests、Release build、`git diff --check`。
- [ ] **Step 5: Commit**：提交“add V6.5 error learning experiment report”。

## Self-Review

- 覆盖：Trace、Auto 输入/学习结果、八类观察、反事实、20/50/100/全量统计、旧模型候选检查、A–G、5/10/15% 残差、严格 walk-forward、留出、随机、独立性、完整性和最终报告均有对应任务。
- 隔离：每项持久化均落在研究表；Task 2/7 测试明确检查正式预测不变。
- 泄漏：Task 6 将未来记录注入作为回归测试；所有计算接口显式接收 prefix。

## Execution Handoff

用户已选择在当前会话内执行。执行时逐任务 TDD，遇到任何正式链输出变化立即停止并报告。
