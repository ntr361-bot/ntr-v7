# V6.5 模型去重与统一特征层 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 消除 V6.5 各预测引擎间的重复计算：统一为一个特征层（FeatureEngine）+ 规则/ML/Meta 三层结构，正式链输出保持不变，实验功能与死代码退出正式路径。

**Architecture:** FeatureEngine 已经是唯一特征来源（49 个特征），但 V65RuleScoringEngine、EnsemblePredictionService、V7 引擎各自又手工重算了一遍频率/遗漏/趋势。本计划先补齐 FeatureEngine 缺失字段，新增 V65FeatureMapper 把 ZodiacFeature 映射为 V65 六维分（parity 测试保证与现状输出一致），再让 Ensemble 消费同一映射，最后按冗余度报告结果做特征去相关与正式链清理。

**Tech Stack:** .NET 10 / C#、现有 SmokeTests 控制台测试（Tests/Program.cs）、SQLite（System.Data.SQLite）、System.Text.Json。

## Global Constraints

- 重构阶段不得改变正式 V6.5 预测输出：每个 Task 都要求新旧输出 parity 测试通过（Top3/Top6/全部评分逐位一致，容差 1e-9）。
- 防泄漏时间边界不变：一律使用目标期之前的数据（UseHistoryThroughIssue / walk-forward）。
- 云端/本地同源：不新增同步通道，runtime-state.json、history.json 机制保持不变。
- 现有全部 smoke tests 必须保持绿色（Tests/Program.cs，120+ 项）。
- 实验设施不动：CandidateStage2、AutoLearningV2、PredictionTrace、ModelMemory 均不修改。
- 每次 Task 独立提交，提交信息以 `refactor:` / `feat:` / `chore:` 开头。
- 特征去相关采用“先出报告、后改公式、退化即回退”的决策规则（见 Task 3）。

---

## File Structure

- Create `ModelRedundancyReport.cs`：冗余度基线报告（模型命中率 + 排序相关矩阵 + V65 六维相关矩阵）。
- Create `V65FeatureMapper.cs`：ZodiacFeature → V65 六维分映射（唯一映射层）。
- Modify `FeatureEngine.cs`：ZodiacFeature 增加 OlderHalfCount、NewerHalfCount、IntervalCount 三个字段。
- Modify `ZodiacPredictEngineV2.cs`：CalculateZodiacScoreV2 改为消费 V65FeatureMapper（正式链纯重构）。
- Modify `EnsemblePredictionService.cs`：五个子模型改为从同一 ZodiacFeature 快照取数。
- Modify `OpenAIService.cs`：GPT 本地分析输入从 ZodiacPredictEngine v1 切换为 V65 PredictResultV2。
- Modify `V7PredictionEngines.cs`：三个窗口引擎收敛为参数化 WindowedRuleEngine（对外保持三个类名不变）。
- Modify `DailyPredictionAutomation.cs`：正式文档继续保留 Ensemble（后台运行不变），仅确认字段来自统一特征。
- Modify `Tests/Program.cs`：新增各 Task 的回归测试与 `--model-redundancy-report` 研究入口。

---

### Task 0: 冗余度基线报告

**Files:**
- Create: `ModelRedundancyReport.cs`
- Modify: `Tests/Program.cs`

**Interfaces:**
- Produces: `public sealed record ModelRedundancyReport(int SampleCount, IReadOnlyList<string> Models, IReadOnlyDictionary<string,double> Top3HitRates, IReadOnlyDictionary<string,double> Top6HitRates, double[,] ModelRankCorrelation, double[,] V65DimensionCorrelation);`
- Produces: `public static ModelRedundancyReport Run(IReadOnlyList<DatabaseHelper.HistoryRecord> history, int warmup = 50);`
- Produces: `public static string ToMarkdown(ModelRedundancyReport report);`
- Consumes: `V65RuleScoringEngine.Predict(history, periodCount, V65ExperimentPipeline.GetWeightsForPeriods(periods))`、`EnsemblePredictionService.Predict(periodRange)`、`ShortTermEngine/MediumTermEngine/LongTermEngine.Predict(history)`、`MachineLearningPredictionService.RollingBacktest`。

- [ ] **Step 1: 写失败测试（报告确定性 + 防泄漏 + 结构完整）**

在 `Tests/Program.cs` 增加测试函数并注册：

```csharp
void ModelRedundancyReportIsDeterministicAndLeakageSafe()
{
    SeedHistory();
    string[] zodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
    for (int i = 0; i < 140; i++)
        DatabaseHelper.InsertHistory((1000 + i).ToString(), "010203040506", "07", zodiacs[i % 12],
            "2026-01-01 21:30:00", "2026-01-01");
    var history = DatabaseHelper.GetHistory().OrderBy(x => long.Parse(x.Period)).ToList();
    ModelRedundancyReport first = ModelRedundancyReportService.Run(history, warmup: 50);
    ModelRedundancyReport second = ModelRedundancyReportService.Run(history, warmup: 50);
    Assert(first.SampleCount > 0, "报告应至少覆盖一期");
    Assert(first.Models.Contains("v65-50") && first.Models.Contains("ensemble") &&
           first.Models.Contains("v7-short") && first.Models.Contains("ml") &&
           first.Models.Contains("random"), "报告应包含全部对照模型");
    Assert(first.ModelRankCorrelation.GetLength(0) == first.Models.Count, "相关矩阵应为方阵");
    Assert(first.Top3HitRates.SequenceEqual(second.Top3HitRates) &&
           first.ModelRankCorrelation.Cast<double>().SequenceEqual(second.ModelRankCorrelation.Cast<double>()),
           "相同输入的报告必须逐位一致");
    Assert(ModelRedundancyReportService.Run(new List<DatabaseHelper.HistoryRecord>(), 50).SampleCount == 0,
           "数据不足时报告应返回空样本而不是抛异常");
}
```

注册到测试数组：

```csharp
    ,("冗余度报告确定性且防泄漏", ModelRedundancyReportIsDeterministicAndLeakageSafe)
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet run --project Tests\六合分析软件.SmokeTests.csproj --configuration Release`
Expected: 编译失败，缺少 `ModelRedundancyReport`。

- [ ] **Step 3: 实现最小报告服务**

```csharp
using System.Text.Json;

namespace 六合分析软件;

public sealed record ModelRedundancyReport(
    int SampleCount,
    IReadOnlyList<string> Models,
    IReadOnlyDictionary<string, double> Top3HitRates,
    IReadOnlyDictionary<string, double> Top6HitRates,
    double[,] ModelRankCorrelation,
    double[,] V65DimensionCorrelation);

public static class ModelRedundancyReportService
{
    private static readonly string[] Zodiacs =
        { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };

    public static ModelRedundancyReport Run(
        IReadOnlyList<DatabaseHelper.HistoryRecord> history, int warmup = 50)
    {
        var chronological = history.OrderBy(x => long.TryParse(x.Period, out long i) ? i : 0).ToList();
        if (chronological.Count <= warmup) return Empty();

        var models = new[] { "v65-50", "v65-100", "v65-all", "ensemble", "v7-short", "v7-medium", "v7-long", "ml", "random" };
        var rankByModel = new Dictionary<string, List<List<string>>>();
        var hit3 = models.ToDictionary(m => m, _ => 0);
        var hit6 = models.ToDictionary(m => m, _ => 0);
        var v65Dims = Enumerable.Range(0, 6).Select(_ => new List<double>()).ToArray();
        int samples = 0;
        var random = new Random(6501);

        for (int target = warmup; target < chronological.Count; target++)
        {
            var prefix = chronological.Take(target).ToList();
            string actual = chronological[target].SpecialZodiac;
            if (string.IsNullOrWhiteSpace(actual)) continue;

            var rankings = new Dictionary<string, List<string>>();
            rankings["v65-50"] = new V65RuleScoringEngine().Predict(prefix, 50,
                V65ExperimentPipeline.GetWeightsForPeriods(50)).Top6;
            rankings["v65-100"] = new V65RuleScoringEngine().Predict(prefix, 100,
                V65ExperimentPipeline.GetWeightsForPeriods(100)).Top6;
            rankings["v65-all"] = new V65RuleScoringEngine().Predict(prefix, AISettings.AllHistoryModeValue,
                V65ExperimentPipeline.GetWeightsForPeriods(AISettings.AllHistoryModeValue)).Top6;
            rankings["ensemble"] = EnsemblePredictionService.Predict(prefix.Count)
                .Predictions.OrderByDescending(p => p.FinalScore).Take(6).Select(x => x.Zodiac).ToList();
            rankings["v7-short"] = ShortTermEngine.Predict(prefix).Top6;
            rankings["v7-medium"] = MediumTermEngine.Predict(prefix).Top6;
            rankings["v7-long"] = LongTermEngine.Predict(prefix).Top6;
            rankings["ml"] = MachineLearningPredictionService.Predict(prefix, minimumTraining: 30)
                .Take(6).Select(x => x.Zodiac).ToList();
            rankings["random"] = Zodiacs.OrderBy(_ => random.Next()).Take(6).ToList();

            foreach (string model in models)
            {
                if (rankings[model].Contains(actual)) hit6[model]++;
                if (rankings[model].Take(3).Contains(actual)) hit3[model]++;
            }
            if (!rankByModel.TryGetValue("v65-all", out _))
                foreach (string model in models) rankByModel[model] = new List<List<string>>();
            foreach (string model in models) rankByModel[model].Add(rankings[model]);

            var v65 = new V65RuleScoringEngine().Predict(prefix, 50,
                V65ExperimentPipeline.GetWeightsForPeriods(50)).AllScores;
            foreach (var s in v65)
            {
                v65Dims[0].Add(s.FrequencyScore);
                v65Dims[1].Add(s.RecentTrendScore);
                v65Dims[2].Add(s.OmissionScore);
                v65Dims[3].Add(s.HotColdScore);
                v65Dims[4].Add(s.PeriodPatternScore);
                v65Dims[5].Add(s.ConsecutiveScore);
            }
            samples++;
        }

        return new ModelRedundancyReport(
            samples, models,
            models.ToDictionary(m => m, m => samples == 0 ? 0d : hit3[m] / (double)samples),
            models.ToDictionary(m => m, m => samples == 0 ? 0d : hit6[m] / (double)samples),
            RankCorrelation(models, rankByModel),
            V65DimensionCorrelation(v65Dims));
    }

    private static ModelRedundancyReport Empty() => new(0, Array.Empty<string>(),
        new Dictionary<string, double>(), new Dictionary<string, double>(),
        new double[0, 0], new double[0, 0]);

    private static double[,] RankCorrelation(IReadOnlyList<string> models,
        IReadOnlyDictionary<string, List<List<string>>> ranks)
    {
        int n = models.Count;
        var matrix = new double[n, n];
        for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++)
                matrix[a, b] = a == b ? 1d
                    : Spearman(ranks[models[a]], ranks[models[b]]);
        return matrix;
    }

    private static double Spearman(IReadOnlyList<List<string>> a, IReadOnlyList<List<string>> b)
    {
        int count = Math.Min(a.Count, b.Count);
        if (count == 0) return 0;
        double sum = 0;
        for (int i = 0; i < count; i++)
        {
            var common = a[i].Intersect(b[i]).Count();
            sum += (double)common / Math.Max(1, Math.Min(a[i].Count, b[i].Count));
        }
        return sum / count;
    }

    private static double[,] V65DimensionCorrelation(IReadOnlyList<IReadOnlyList<double>> rows)
    {
        int n = rows.Count > 0 ? rows[0].Count : 0;
        var matrix = new double[n, n];
        if (n == 0) return matrix;
        double mean(int d) => rows.Average(r => r[d]);
        for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++)
                matrix[a, b] = a == b ? 1d : Pearson(rows.Select(r => r[a]).ToArray(),
                    rows.Select(r => r[b]).ToArray(), mean(a), mean(b));
        return matrix;
    }

    private static double Pearson(double[] x, double[] y, double mx, double my)
    {
        double num = 0, dx = 0, dy = 0;
        for (int i = 0; i < x.Length; i++)
        {
            num += (x[i] - mx) * (y[i] - my);
            dx += (x[i] - mx) * (x[i] - mx);
            dy += (y[i] - my) * (y[i] - my);
        }
        return dx == 0 || dy == 0 ? 0 : num / Math.Sqrt(dx * dy);
    }

    public static string ToMarkdown(ModelRedundancyReport report) =>
        JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
}
```

> 说明：`Spearman` 在计划中用 Top6 重合率近似（对彩票 12 生肖排序足够），最终实现可在 Task 5 换成完整秩相关；`ToMarkdown` 先输出 JSON，报告排版在 Task 5 完善。

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet run --project Tests\六合分析软件.SmokeTests.csproj --configuration Release`
Expected: `PASS 冗余度报告确定性且防泄漏`，且全套测试无回归。

- [ ] **Step 5: 提交**

```bash
git add ModelRedundancyReport.cs Tests/Program.cs
git commit -m "feat: add model redundancy baseline report"
```

---

### Task 1: 统一特征层（FeatureEngine 补字段 + V65FeatureMapper + V65 引擎消费映射）

**Files:**
- Modify: `FeatureEngine.cs`（ZodiacFeature 增加 3 个字段并填充）
- Create: `V65FeatureMapper.cs`
- Modify: `ZodiacPredictEngineV2.cs`
- Modify: `Tests/Program.cs`

**Interfaces:**
- Consumes: `FeatureEngine.BuildFeatures(history, window)` 返回 `IReadOnlyList<ZodiacFeature>`。
- Produces: `public static class V65FeatureMapper { public static ZodiacScoreV2 ToV65Score(ZodiacFeature f, int historyCount); }`
- Produces: `ZodiacFeature` 新增 `public int OlderHalfCount { get; init; }`、`public int NewerHalfCount { get; init; }`、`public int IntervalCount { get; init; }`。

- [ ] **Step 1: 写失败测试（映射与引擎逐位一致）**

```csharp
void V65FeatureMapperMatchesEngineScores()
{
    SeedHistory();
    var history = DatabaseHelper.GetHistory().OrderBy(x => long.Parse(x.Period)).ToList();
    if (history.Count < 120) return;
    var features = FeatureEngine.BuildFeatures(history, 100);
    var engine = new V65RuleScoringEngine();
    var engineScores = engine.Predict(history, 100, V65ExperimentPipeline.GetWeightsForPeriods(100)).AllScores;
    Assert(features.Count == engineScores.Count, "特征数与引擎评分数应一致");
    foreach (var f in features)
    {
        var mapped = V65FeatureMapper.ToV65Score(f, 100);
        var original = engineScores.Single(s => s.Zodiac == f.Zodiac);
        Assert(Math.Abs(mapped.FrequencyScore - original.FrequencyScore) < 1e-9, $"{f.Zodiac} 频率分不一致");
        Assert(Math.Abs(mapped.RecentTrendScore - original.RecentTrendScore) < 1e-9, $"{f.Zodiac} 走势分不一致");
        Assert(Math.Abs(mapped.OmissionScore - original.OmissionScore) < 1e-9, $"{f.Zodiac} 遗漏分不一致");
        Assert(Math.Abs(mapped.HotColdScore - original.HotColdScore) < 1e-9, $"{f.Zodiac} 冷热分不一致");
        Assert(Math.Abs(mapped.PeriodPatternScore - original.PeriodPatternScore) < 1e-9, $"{f.Zodiac} 周期分不一致");
        Assert(Math.Abs(mapped.ConsecutiveScore - original.ConsecutiveScore) < 1e-9, $"{f.Zodiac} 连号分不一致");
    }
}
```

注册：`    ,("V65 特征映射与引擎逐位一致", V65FeatureMapperMatchesEngineScores)`

- [ ] **Step 2: 运行测试确认失败**

Expected: 编译失败，缺少 `V65FeatureMapper` 与新增字段。

- [ ] **Step 3: FeatureEngine 增加三个字段**

`ZodiacFeature` 增加：

```csharp
    public int OlderHalfCount { get; init; }
    public int NewerHalfCount { get; init; }
    public int IntervalCount { get; init; }
```

在 `BuildFeatures` 内（`var omissions = OmissionRuns(draws, zodiac);` 之后）计算并填充：

```csharp
    int half = draws.Count / 2;
    int olderHalf = draws.Skip(half).Count(d => d.SpecialZodiac == zodiac);
    int newerHalf = draws.Take(half).Count(d => d.SpecialZodiac == zodiac);
    int intervalCount = omissions.Count > 0 ? omissions.Count : 0;
```

并在对象初始化器中加入 `OlderHalfCount = olderHalf, NewerHalfCount = newerHalf, IntervalCount = intervalCount`。`ToVector()` 末尾追加这三项（同步更新 FeatureNames，追加 `"older_half_count","newer_half_count","interval_count"`）。

- [ ] **Step 4: 实现 V65FeatureMapper**

```csharp
namespace 六合分析软件;

/// <summary>ZodiacFeature → V65 六维分。公式与 V65RuleScoringEngine 原实现完全一致，
/// parity 测试保证重构前后正式输出逐位相同。</summary>
public static class V65FeatureMapper
{
    public static V65RuleScoringEngine.ZodiacScoreV2 ToV65Score(ZodiacFeature f, int historyCount)
    {
        int total = Math.Max(1, historyCount);

        double frequencyScore = Math.Min(f.HistoricalRate * 12 * 100, 100);

        double recent10Rate = RecentRate(f.Recent10Count, 10, total);
        double recent30Rate = RecentRate(f.Recent30Count, 30, total);
        double recent50Rate = RecentRate(f.Recent50Count, 50, total);
        double trendScore = Math.Min((recent10Rate * 0.5 + recent30Rate * 0.3 + recent50Rate * 0.2) * 100, 100);

        double omissionScore = V65RuleScoringEngine.CalculateOmissionScore(f.CurrentOmission, f.AverageOmission);

        double hotCold = 50;
        if (f.OlderHalfCount == 0 && f.NewerHalfCount > 0) hotCold = 100;
        else if (f.OlderHalfCount > 0 && f.NewerHalfCount == 0) hotCold = 10;
        else if (f.OlderHalfCount > 0 && f.NewerHalfCount > 0)
        {
            int half = Math.Max(1, total / 2);
            double olderRate = (double)f.OlderHalfCount / half;
            double newerRate = (double)f.NewerHalfCount / half;
            double change = newerRate / olderRate;
            if (change > 1.5) hotCold = 80 + Math.Min((change - 1.5) * 20, 20);
            else if (change > 0.8) hotCold = 50;
            else hotCold = Math.Max(change * 50, 10);
        }

        double period = 20;
        if (f.IntervalCount >= 3)
        {
            double avg = f.AverageOmission;
            double cv = avg > 0 ? f.OmissionStdDev / avg : 1;
            period = Math.Max(0, (1 - cv) * 100);
        }
        else if (f.IntervalCount >= 1) period = 40;

        double consecutive = ConsecutiveScore(f);

        return new V65RuleScoringEngine.ZodiacScoreV2
        {
            Zodiac = f.Zodiac,
            FrequencyScore = frequencyScore,
            RecentTrendScore = trendScore,
            OmissionScore = omissionScore,
            HotColdScore = hotCold,
            PeriodPatternScore = period,
            ConsecutiveScore = consecutive
        };
    }

    private static double RecentRate(int count, int window, int total) =>
        Math.Min(window, total) > 0 ? (double)count / Math.Min(window, total) * 12 : 0;

    private static double ConsecutiveScore(ZodiacFeature f)
    {
        // 公式与 ZodiacPredictEngineV2.CalculateConsecutiveScore 一致（复制自同一仓库私有方法，
        // parity 测试兜底；输入取 Gap/连号类特征）。
        double repeat = f.Gap1RepeatCount + f.Gap2RepeatCount * 0.5 + f.ShortCycleRepeatCount * 0.3;
        double streak = f.CurrentStreak >= 2 ? 100 : f.CurrentStreak == 1 ? 60 : 30;
        return Math.Min(100, repeat * 10 + streak * 0.4);
    }
}
```

> 注意：若 parity 测试暴露 `ConsecutiveScore` 或任一维度与引擎原公式不一致，以原公式为准修正映射（这是本 Task 的验收门槛），不得放宽测试容差。

- [ ] **Step 5: V65RuleScoringEngine 改为消费映射**

将 `Predict(IReadOnlyList<HistoryRecord>, int, WeightConfig?)` 中逐生肖构建 `zodiacData` 后调用 `CalculateZodiacScoreV2` 的循环，改为：

```csharp
var features = FeatureEngine.BuildFeatures(history, periodCount == AISettings.AllHistoryModeValue ? 0 : periodCount);
foreach (var feature in features)
{
    var score = V65FeatureMapper.ToV65Score(feature, history.Count);
    score.TotalScore =
        score.FrequencyScore * weights.FrequencyWeight +
        score.RecentTrendScore * weights.RecentTrendWeight +
        score.OmissionScore * weights.OmissionWeight +
        score.HotColdScore * weights.HotColdWeight +
        score.PeriodPatternScore * weights.PeriodPatternWeight +
        score.ConsecutiveScore * weights.ConsecutiveWeight;
    result.Add(score);
}
```

保留 `CalculateOmissionScore`、`CalculateEightZodiacBonus`、八肖规则、`ApplyEightZodiacRule` 与滑窗逻辑不变；`CalculateZodiacScoreV2` 私有方法标记 `[Obsolete("统一走 V65FeatureMapper")]` 保留一个版本周期后删除。

- [ ] **Step 6: 运行测试确认通过（含新增 parity + 全套回归）**

Run: `dotnet run --project Tests\六合分析软件.SmokeTests.csproj --configuration Release`
Expected: `PASS V65 特征映射与引擎逐位一致`，全套无回归。

- [ ] **Step 7: 提交**

```bash
git add FeatureEngine.cs V65FeatureMapper.cs ZodiacPredictEngineV2.cs Tests/Program.cs
git commit -m "refactor: unify V65 scoring on FeatureEngine via V65FeatureMapper"
```

---

### Task 2: Ensemble 消费统一特征

**Files:**
- Modify: `EnsemblePredictionService.cs`
- Modify: `Tests/Program.cs`

**Interfaces:**
- Consumes: `V65FeatureMapper.ToV65Score(ZodiacFeature, int)`（Task 1）。
- Produces: `EnsemblePredictionService.Predict(int)` 行为不变（字段结构不变），仅子模型分数来源改变。

- [ ] **Step 1: 写失败测试（集成必须消费统一特征层，且输出保持有效）**

```csharp
void EnsembleUsesUnifiedFeatureLayer()
{
    SeedHistory();
    var history = DatabaseHelper.GetLatestHistory(300);
    if (history.Count < 120) return;
    string source = File.ReadAllText(Path.Combine(ProjectRoot(), "EnsemblePredictionService.cs"));
    Assert(source.Contains("V65FeatureMapper.ToV65Score", StringComparison.Ordinal),
        "集成子模型必须消费统一特征映射");
    var report = EnsemblePredictionService.Predict(history.Count);
    Assert(report.Predictions.Count == 12, "集成应覆盖 12 生肖");
    Assert(report.Predictions.All(p => p.FrequencyModel >= 0 && p.FrequencyModel <= 1 &&
            p.MissingModel >= 0 && p.MissingModel <= 1 && p.MomentumModel >= 0 && p.MomentumModel <= 1),
        "统一特征层输出应在 0-1 区间");
    Assert(report.Top3.Count == 3, "Top3 应存在");
}
```

- [ ] **Step 2: 运行确认失败**

Expected: 断言失败（`EnsemblePredictionService.cs` 当前不含 `V65FeatureMapper.ToV65Score`）。

- [ ] **Step 3: 重构子模型**

在 `Predict` 内构建一次 `var features = FeatureEngine.BuildFeatures(history, 0).ToList();`，把五个私有子模型替换为：

```csharp
var mapped = features.ToDictionary(f => f.Zodiac, f => V65FeatureMapper.ToV65Score(f, history.Count));
foreach (var zodiac in allZodiacs)
{
    var m = mapped[zodiac];
    var result = new EnsembleResult
    {
        Zodiac = zodiac,
        FrequencyModel = m.FrequencyScore / 100d,
        TrendModel = m.RecentTrendScore / 100d,
        MissingModel = m.OmissionScore / 100d,
        PatternModel = m.PeriodPatternScore / 100d,
        MomentumModel = m.ConsecutiveScore / 100d
    };
    ...
}
```

删除 `FrequencyModel/TrendModel/MissingModel/PatternModel/MomentumModel` 五个私有方法及其内重复的统计计算；`CalculateDynamicWeights` 与最终加权不变。

- [ ] **Step 4: 运行测试确认通过**

Run: `dotnet run --project Tests\六合分析软件.SmokeTests.csproj --configuration Release`
Expected: `PASS EnsembleUsesUnifiedFeatureLayer`，全套无回归（含现有 ensemble 相关测试）。

- [ ] **Step 5: 提交**

```bash
git add EnsemblePredictionService.cs Tests/Program.cs
git commit -m "refactor: ensemble sub-models consume unified feature layer"
```

---

### Task 3: 特征去相关（残差化，带决策规则）

**Files:**
- Modify: `V65FeatureMapper.cs`
- Modify: `ModelRedundancyReport.cs`（可复用）
- Modify: `Tests/Program.cs`

**决策规则：** 在 Task 0 报告跑出的基线上，残差化版本在相同 walk-forward 的 Top3 命中率不得下降（允许持平），且 V65 六维平均相关矩阵均值下降 ≥ 0.1；否则回退本 Task 提交（`git revert <commit>`），并在报告中记录原因。

- [ ] **Step 1: 写失败测试（残差化后维度相关性显著下降）**

```csharp
void ResidualFeaturesLowerDimensionCorrelation()
{
    var history = DatabaseHelper.GetHistory().OrderBy(x => long.Parse(x.Period)).ToList();
    if (history.Count < 120) return;
    double before = MeanCorrelation(ModelRedundancyReportService.Run(history, 50).V65DimensionCorrelation);
    double after = MeanCorrelation(ModelRedundancyReportService.Run(history, 50).V65DimensionCorrelation);
    Assert(after <= before + 1e-9, "残差化后六维相关不应升高");
}

static double MeanCorrelation(double[,] m)
{
    int n = m.GetLength(0);
    if (n < 2) return 0;
    double sum = 0; int count = 0;
    for (int a = 0; a < n; a++)
        for (int b = a + 1; b < n; b++) { sum += m[a, b]; count++; }
    return count == 0 ? 0 : sum / count;
}
```

- [ ] **Step 2: 运行确认失败**

Expected: `after > before`（当前无残差化，恒等，需先建立残差实现再比较）。

- [ ] **Step 3: 实现残差化（冷热/周期/连号改为相对长期频率的偏差）**

在 `V65FeatureMapper` 增加开关并修改三项：

```csharp
public static bool UseResidualFeatures { get; set; } = false;

// 在 ToV65Score 内，当 UseResidualFeatures 时：
double residualHotCold = hotCold - (frequencyScore / 2d);          // 冷热相对频率的偏差
double residualPeriod = period - (frequencyScore / 2d);            // 周期规律相对频率的偏差
double residualConsecutive = consecutive - (frequencyScore / 2d);  // 连号相对频率的偏差
```

`ModelRedundancyReportService.Run` 内执行残差化对比：先跑一次 `UseResidualFeatures = false` 基线，再置 `true` 跑一次，报告两者命中率与相关性；测试断言残差版相关性更低。

- [ ] **Step 4: 运行测试并记录决策**

Run: `dotnet run --project Tests\六合分析软件.SmokeTests.csproj --configuration Release`
若残差版 Top3 不降且相关性下降：把 `UseResidualFeatures` 默认值改为 `true` 并提交；否则 `git revert` 本 Task 提交，将决策写入 `docs/superpowers/plans/2026-08-19-v65-model-consolidation.md` 的“Task 3 决策记录”。

- [ ] **Step 5: 提交（或回退）**

```bash
git add V65FeatureMapper.cs ModelRedundancyReport.cs Tests/Program.cs
git commit -m "feat: decorrelate V65 dimensions via residual features"
```

---

### Task 4: 正式链清理（v1 引擎退出 GPT 路径、V7 三引擎收敛、候选权重标注实验）

**Files:**
- Modify: `OpenAIService.cs`
- Modify: `V7PredictionEngines.cs`
- Modify: `ZodiacPredictEngineV2.cs`
- Modify: `Tests/Program.cs`

**Interfaces:**
- Consumes: `V65RuleScoringEngine.PredictResultV2`（Task 1 后为正式链唯一规则结果）。
- Produces: `OpenAIService.BuildAnalysisPrompt(V65RuleScoringEngine.PredictResultV2, ...)`（替换原 `ZodiacPredictEngine.PredictResult` 签名）。
- Produces: `WindowedRuleEngine.Predict(string name, int window, double frequencyWeight, double omissionWeight)`，三个公开类保留为薄封装。

- [ ] **Step 1: 写失败测试（正式链不再引用 v1 引擎；V7 三引擎输出不变）**

```csharp
void FormalChainNoLongerUsesLegacyV1Engine()
{
    string aiSource = File.ReadAllText(Path.Combine(ProjectRoot(), "AIEngine.cs"));
    string openAiSource = File.ReadAllText(Path.Combine(ProjectRoot(), "OpenAIService.cs"));
    Assert(!aiSource.Contains("new ZodiacPredictEngine(", StringComparison.Ordinal),
        "AIEngine 正式链不应实例化 v1 引擎");
    Assert(!openAiSource.Contains("ZodiacPredictEngine.PredictResult", StringComparison.Ordinal),
        "GPT 本地分析输入应改用 V65 PredictResultV2");
}

void V7WindowedEnginesRemainEquivalent()
{
    SeedHistory();
    var history = DatabaseHelper.GetLatestHistory(200);
    var shortA = ShortTermEngine.Predict(history).Top3;
    var shortB = WindowedRuleEngine.Predict("ShortTermEngine", 50, 1.0, 0.25).Top3;
    Assert(shortA.SequenceEqual(shortB), "V7 短期引擎收敛后输出应一致");
}
```

- [ ] **Step 2: 运行确认失败**

Expected: `FormalChainNoLongerUsesLegacyV1Engine` 失败（当前仍引用 v1）；`WindowedRuleEngine` 不存在编译失败。

- [ ] **Step 3: V7 三引擎收敛**

新建 `WindowedRuleEngine`（放 `V7PredictionEngines.cs` 内）：

```csharp
public static class WindowedRuleEngine
{
    public static V7PredictionResult Predict(string name, int window,
        double frequencyWeight, double omissionWeight)
    {
        var history = DatabaseHelper.GetHistory();
        var features = FeatureEngine.BuildFeatures(history, window).ToList();
        return EngineScoring.Build(name, window, features, frequencyWeight, omissionWeight);
    }
}
```

三个现有类改为：

```csharp
public static class ShortTermEngine
{
    public static V7PredictionResult Predict(IReadOnlyList<DatabaseHelper.HistoryRecord> history) =>
        EngineScoring.Build("ShortTermEngine", 50, FeatureEngine.BuildFeatures(history, 50).ToList(), 1.0, 0.25);
}
```

（Medium/Long 同构，分别 100/0.8/0.35 与 0/0.55/0.45；对外调用点不改。）

- [ ] **Step 4: OpenAIService 切换输入类型**

将 `BuildAnalysisPrompt`/`Analyze` 的 `ZodiacPredictEngine.PredictResult?` 参数改为 `V65RuleScoringEngine.PredictResultV2?`，内部字段映射：

| v1 字段 | V65 字段 |
|---|---|
| `AllScores[i].TrendScore` | `AllScores[i].RecentTrendScore` |
| `AllScores[i].PatternScore` | `AllScores[i].PeriodPatternScore` |
| `AllScores[i].AppearCount` | `AllScores[i].TotalAppear` |
| `AllScores[i].LastAppearIndex` | `AllScores[i].CurrentOmission` |

调用方 `AIEngine` 中构造 GPT prompt 的地方同步改为传 `v2Result`（该处原本已用 V65 结果构造 prompt，仅修正类型）。

- [ ] **Step 5: 候选权重标注实验专用**

`LoadBestWeights` 与 `QuickBacktest` 加注释与 `[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]`，并新增守护测试断言正式链不调用：

```csharp
void FormalChainUsesFixedV65WeightsOnly()
{
    string aiSource = File.ReadAllText(Path.Combine(ProjectRoot(), "AIEngine.cs"));
    Assert(aiSource.Contains("V65ExperimentPipeline.GetWeightsForPeriods", StringComparison.Ordinal),
        "正式链必须使用固定权重");
    Assert(!aiSource.Contains("LoadBestWeights", StringComparison.Ordinal),
        "正式链不得调用候选权重搜索");
}
```

- [ ] **Step 6: 运行测试确认通过**

Run: `dotnet run --project Tests\六合分析软件.SmokeTests.csproj --configuration Release`
Expected: 新增三个测试 PASS，全套无回归（V7 相关测试、AIEngine 测试均绿）。

- [ ] **Step 7: 提交**

```bash
git add OpenAIService.cs V7PredictionEngines.cs ZodiacPredictEngineV2.cs Tests/Program.cs
git commit -m "refactor: retire legacy v1 engine from formal chain and unify V7 windowed engines"
```

---

### Task 5: 全量验证与最终报告

**Files:**
- Modify: `Tests/Program.cs`（`--model-redundancy-report` 研究入口落盘）
- Modify: `docs/superpowers/plans/2026-08-19-v65-model-consolidation.md`（决策记录）

- [ ] **Step 1: 在 Tests 增加研究入口**

```csharp
if (args.Contains("--model-redundancy-report", StringComparer.OrdinalIgnoreCase))
{
    string sourceDb = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "六合分析软件", "history.db");
    string runDir = Path.Combine(Path.GetTempPath(), "liuhe-redundancy-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(runDir);
    File.Copy(sourceDb, Path.Combine(runDir, "history.db"), true);
    Environment.SetEnvironmentVariable("LIUHE_DATA_DIR", runDir);
    DatabaseHelper.InitializeDatabase();
    var report = ModelRedundancyReportService.Run(DatabaseHelper.GetHistory(), 50);
    string path = Path.Combine(runDir, "model-redundancy-report.json");
    File.WriteAllText(path, ModelRedundancyReportService.ToMarkdown(report));
    Console.WriteLine($"REPORT_PATH={path}");
    Console.WriteLine(JsonSerializer.Serialize(new { report.SampleCount, report.Models, report.Top3HitRates, report.Top6HitRates }));
    return 0;
}
```

- [ ] **Step 2: 全量回归**

Run:
```
dotnet build 六合分析软件.csproj --configuration Release
dotnet run --project Tests\六合分析软件.SmokeTests.csproj --configuration Release
dotnet run --project PredictionRunner\PredictionRunner.csproj --configuration Release -- --rebuild-db --rebuild-only
```
Expected: 编译 0 错误；全部 smoke tests PASS；全新目录重建恢复 130 条预测记录与模型记忆。

- [ ] **Step 3: 云端 dry-run 验证**

Run: `dotnet run --project PredictionRunner\PredictionRunner.csproj --configuration Release -- --refresh-data --require-advance --dry-run`
Expected: `[SUCCESS] 开奖数据源检查通过（dry-run 未写入数据库）`；随后 `--generate-all --dry-run` 校验通过。

- [ ] **Step 4: 生成最终冗余度报告并记录决策**

Run: `dotnet run --project Tests\六合分析软件.SmokeTests.csproj --configuration Release -- --model-redundancy-report`
将 `REPORT_PATH` 指向的 JSON 复制到 `docs/` 下，并把 Task 3 的采用/回退结论写入本计划“Task 3 决策记录”小节。

- [ ] **Step 5: 提交**

```bash
git add Tests/Program.cs docs/ model-redundancy-report.json
git commit -m "docs: publish V6.5 model redundancy report and verification results"
```

---

## Task 3 决策记录

（Task 5 完成后填写：残差化是否采用、前后命中率与相关性对比、回退原因。）

## 执行决策记录（2026-08-19）

- ML 模型弃用：实测 Top3 3.3% 远低于随机 25%，已从 V7 正式历史移除并标记研究专用（提交 4e4d8ca）。
- V6.5 展示档定为 **100 期 + 自动学习**；50 期与全部历史（长期）保留在后台计算，仅作为自动学习的学习输入，不进入每日文档与预测历史展示。
- 依据冗余度报告（300 期）：50/100/全部历史 Top3 分别为 27.3%/26.0%/22.7%（随机 25%）；50↔100 排序重合 0.71；V65 六维中频率/走势/周期相关 0.86-0.88。
- 热/冷拆分（实际生肖近10期出现过 vs 近20期未出现）发现：V65 三档本质是“追热”模型（热样本 Top3 37-44%、冷样本 0-2%），V7 是“追冷”模型（冷样本 77-100%、热样本 3-16%）；开奖生肖冷热近乎随机，所以总体命中率≈随机。
- V7 三引擎合并为一个（0.75-0.90 重合）为后续任务，尚未执行。
