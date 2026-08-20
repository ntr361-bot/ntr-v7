using System.Text.Json;

namespace 六合分析软件;

public sealed record EvaluationMetrics(int Samples, double Top3, double Top6, double Mrr, int MaximumTop6Misses);
public sealed record ColorEvaluationMetrics(int Samples, double MainHitRate, double DualHitRate,
    int MaximumMainMisses, int MaximumDualMisses);
public sealed record AutoLearningValidationRecord(
    string Issue,
    IReadOnlyList<string> Top3,
    IReadOnlyList<string> Top6,
    string ActualZodiac,
    int ActualRank,
    bool Top3Hit,
    bool Top6Hit,
    ModelWeights Weights,
    string ActualNumber,
    string MainColor,
    string DefenseColor,
    string ActualColor,
    bool MainColorHit,
    bool DualColorHit,
    ColorLearningWeights ColorWeights);
public sealed record AutoLearningEvaluationResult(
    int TrainingSamples,
    int ColorTrainingSamples,
    int TestSamples,
    EvaluationMetrics Baseline,
    EvaluationMetrics Learning,
    int FallbackCount,
    bool FutureDataLeakageDetected,
    string Conclusion,
    ColorEvaluationMetrics BaselineColor,
    ColorEvaluationMetrics LearningColor,
    IReadOnlyList<AutoLearningValidationRecord> Latest50);

public static class HistoricalMetaSnapshotBuilder
{
    public static MetaPredictionInput Build(IReadOnlyList<DatabaseHelper.HistoryRecord> prefix, string issue)
    {
        var features = FeatureEngine.BuildFeatures(prefix);
        MarketStateResult state = MarketStateEngine.Detect(prefix);
        var raw = new List<ZodiacMetaFeatures>(12);
        foreach (ZodiacFeature feature in features)
        {
            double ai = 0.35*feature.Recent20Rate + 0.20*feature.HistoricalRate
                + 0.20*Math.Clamp(feature.OmissionRatio/3, 0, 1)
                + 0.25*Math.Clamp(0.5+feature.Momentum10Vs50*2, 0, 1);
            double ml = Sigmoid(-0.2 + feature.Recent10Rate*2.8 + feature.Recent50Rate*1.5
                + feature.OmissionXMomentum5Vs20*0.08 + feature.RepeatXOmission*0.04
                + feature.LongXShortTrend*1.5 - (feature.ShortForbidden ? 0.5 : 0));
            double stateScore = state.PrimaryState switch
            {
                MarketStateKind.ShortCycleRepeat => Math.Clamp(feature.ShortCycleRepeatCount/8d, 0, 1),
                MarketStateKind.HotColdTransition => Math.Clamp(0.5+feature.Momentum5Vs20*3, 0, 1),
                MarketStateKind.OmissionRelease => Math.Clamp(feature.OmissionRatio/3, 0, 1),
                _ => Math.Clamp(feature.HistoricalRate*12, 0, 1)
            };
            double v7 = Math.Clamp(feature.Recent20Rate*2 + feature.OmissionRatio*0.15
                - (feature.ShortForbidden ? 0.45 : 0), 0, 1);
            raw.Add(new ZodiacMetaFeatures(feature.Zodiac,
                new Dictionary<string,double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["AI"] = ai, ["ML"] = ml, ["State"] = stateScore, ["V7"] = v7
                }, AutoLearningSnapshotBuilder.BuildGroups(feature, state.Confidence)));
        }
        AddConsensus(raw);
        return new MetaPredictionInput(issue, raw);
    }

    public static IReadOnlyList<string> Baseline(MetaPredictionInput input) => input.Zodiacs
        .OrderByDescending(item => item.BaseScores["AI"]).ThenBy(item => item.Zodiac)
        .Select(item => item.Zodiac).ToArray();

    private static void AddConsensus(IReadOnlyList<ZodiacMetaFeatures> rows)
    {
        string[] sources = { "AI", "ML", "State", "V7" };
        var ranks = sources.ToDictionary(source => source,
            source => rows.OrderByDescending(row => row.BaseScores[source]).Select((row,index)=>(row.Zodiac,index))
                .ToDictionary(item => item.Zodiac, item => item.index+1));
        foreach (ZodiacMetaFeatures row in rows)
        {
            double[] values = sources.Select(source => (double)ranks[source][row.Zodiac]).ToArray();
            double average = values.Average();
            double variance = values.Select(value => Math.Pow(value-average,2)).Average();
            if (row.FeatureGroups is Dictionary<string,double> groups)
                groups["model_consensus"] = Math.Clamp(1-Math.Sqrt(variance)/6, -1, 1);
        }
    }

    private static double Sigmoid(double value) => 1/(1+Math.Exp(-Math.Clamp(value, -20, 20)));
}

public static class AutoLearningTrainer
{
    public static ModelMemoryState EnsureInitialTraining(
        IReadOnlyList<DatabaseHelper.HistoryRecord>? history = null,
        string experimentKey = ExperimentModels.AutoLearning)
    {
        var memoryStore = new ModelMemory(experimentKey);
        ModelMemoryState memory = memoryStore.LoadOrCreate();
        bool changed = false;
        history ??= DatabaseHelper.GetHistory();
        if (memory.LearnedSamples == 0)
        {
            TrainOnce(history, memory);
            changed = true;
        }
        if (memory.ColorLearning.LearnedSamples == 0)
        {
            TrainColorOnce(history, memory.ColorLearning);
            changed = true;
        }
        if (changed) memoryStore.Save(memory);
        return memory;
    }

    public static int TrainOnce(IReadOnlyList<DatabaseHelper.HistoryRecord> records, ModelMemoryState memory)
    {
        var chronological = Normalize(records);
        int trained = 0;
        for (int target = 30; target < chronological.Count; target++)
        {
            var draw = chronological[target];
            if (Year(draw) is < 2023 or > 2025 || string.IsNullOrWhiteSpace(draw.SpecialZodiac)) continue;
            var prefix = chronological.Take(target).ToArray();
            AutoLearningSnapshot snapshot = V65ExperimentPipeline.BuildSnapshot(prefix, draw.Period, memory);
            LearnOne(snapshot.Input, snapshot.Result.Ranking.Select(item => item.Zodiac).ToArray(), draw.SpecialZodiac, memory);
            trained++;
        }
        return trained;
    }

    public static void LearnOne(MetaPredictionInput input, IReadOnlyList<string> ranking,
        string actualZodiac, ModelMemoryState memory)
    {
        int actualRank = ranking.ToList().FindIndex(item => item == actualZodiac)+1;
        if (actualRank <= 0) return;
        string[] sources = { "AI", "ML", "State", "V7" };
        var ranks = sources.ToDictionary(source => source, source =>
            input.Zodiacs.OrderByDescending(item => item.BaseScores.GetValueOrDefault(source))
                .Select((item,index)=>(item.Zodiac, Rank:index+1)).First(item => item.Zodiac == actualZodiac).Rank,
            StringComparer.OrdinalIgnoreCase);
        ZodiacMetaFeatures actual = input.Zodiacs.First(item => item.Zodiac == actualZodiac);
        new MetaPredictionEngine().Learn(input, actualZodiac, memory);
        new AutoLearningEngine().ApplyFeedback(memory,
            new PredictionFeedback(input.Issue, actualRank, ranks, actual.FeatureGroups));
    }

    public static int TrainColorOnce(IReadOnlyList<DatabaseHelper.HistoryRecord> records, ColorLearningState state)
    {
        var chronological = Normalize(records);
        int trained = 0;
        for (int target = 30; target < chronological.Count; target++)
        {
            DatabaseHelper.HistoryRecord draw = chronological[target];
            string actualColor = ColorEngine.ColorOf(draw);
            if (Year(draw) is < 2023 or > 2025 || string.IsNullOrWhiteSpace(actualColor)) continue;
            var prefix = chronological.Take(target).ToArray();
            ColorPredictionResult prediction = ColorEngine.Predict(prefix, state.Weights);
            LearnColorOne(draw.Period, prediction, actualColor, state);
            trained++;
        }
        return trained;
    }

    public static ColorLearningOutcome LearnColorOne(string issue, ColorPredictionResult prediction,
        string actualColor, ColorLearningState state) => new ColorAutoLearningEngine().ApplyFeedback(state,
            new ColorPredictionFeedback(issue, actualColor, prediction.Main, prediction.Defense,
                prediction.FeatureSignals));

    internal static List<DatabaseHelper.HistoryRecord> Normalize(IReadOnlyList<DatabaseHelper.HistoryRecord> records) =>
        records.Where(item => long.TryParse(item.Period, out _) && !string.IsNullOrWhiteSpace(item.SpecialZodiac))
            .OrderBy(item => long.Parse(item.Period)).ToList();

    internal static int Year(DatabaseHelper.HistoryRecord record)
    {
        if (record.OpenTime.Length >= 4 && int.TryParse(record.OpenTime[..4], out int year)) return year;
        return record.Period.Length >= 4 && int.TryParse(record.Period[..4], out year) ? year : 0;
    }
}

public static class AutoLearningEvaluation
{
    public static AutoLearningEvaluationResult Run(IReadOnlyList<DatabaseHelper.HistoryRecord> records)
    {
        var data = AutoLearningTrainer.Normalize(records);
        var memory = new ModelMemoryState();
        int training = AutoLearningTrainer.TrainOnce(data, memory);
        int colorTraining = AutoLearningTrainer.TrainColorOnce(data, memory.ColorLearning);
        var baselineRanks = new List<int>();
        var learningRanks = new List<int>();
        int fallback = 0;
        bool leakage = false;
        var baselineColorMain = new List<bool>();
        var baselineColorDual = new List<bool>();
        var learningColorMain = new List<bool>();
        var learningColorDual = new List<bool>();
        var validation = new List<AutoLearningValidationRecord>();
        for (int target = 30; target < data.Count; target++)
        {
            var draw = data[target];
            if (AutoLearningTrainer.Year(draw) != 2026) continue;
            var prefix = data.Take(target).ToArray();
            leakage |= prefix.Any(item => long.Parse(item.Period) >= long.Parse(draw.Period));
            AutoLearningSnapshot snapshot = V65ExperimentPipeline.BuildSnapshot(prefix, draw.Period, memory);
            MetaPredictionInput input = snapshot.Input;
            IReadOnlyList<string> baseline = snapshot.BaselineRanking;
            MetaPredictionResult learned = snapshot.Result;
            if (learned.UsedFallback) fallback++;
            string[] learnedOrder = learned.Ranking.Select(item => item.Zodiac).ToArray();
            baselineRanks.Add(baseline.ToList().FindIndex(item => item == draw.SpecialZodiac)+1);
            learningRanks.Add(Array.FindIndex(learnedOrder, item => item == draw.SpecialZodiac)+1);
            int learnedRank = learningRanks[^1];
            string actualColor = ColorEngine.ColorOf(draw);
            ColorPredictionResult baselineColor = ColorEngine.Predict(prefix, ColorLearningWeights.Default);
            ColorPredictionResult learnedColor = ColorEngine.Predict(prefix, memory.ColorLearning.Weights);
            bool baselineMainHit = baselineColor.Main == actualColor;
            bool baselineDualHit = baselineMainHit || baselineColor.Defense == actualColor;
            bool learnedMainHit = learnedColor.Main == actualColor;
            bool learnedDualHit = learnedMainHit || learnedColor.Defense == actualColor;
            baselineColorMain.Add(baselineMainHit);
            baselineColorDual.Add(baselineDualHit);
            learningColorMain.Add(learnedMainHit);
            learningColorDual.Add(learnedDualHit);
            validation.Add(new AutoLearningValidationRecord(draw.Period,
                learnedOrder.Take(3).ToArray(), learnedOrder.Take(6).ToArray(), draw.SpecialZodiac,
                learnedRank, learnedRank is >0 and <=3, learnedRank is >0 and <=6, memory.Weights,
                draw.SpecialNumber, learnedColor.Main, learnedColor.Defense, actualColor,
                learnedMainHit, learnedDualHit, memory.ColorLearning.Weights));
            AutoLearningTrainer.LearnOne(input, learnedOrder, draw.SpecialZodiac, memory);
            AutoLearningTrainer.LearnColorOne(draw.Period, learnedColor, actualColor, memory.ColorLearning);
        }
        EvaluationMetrics baselineMetrics = Metrics(baselineRanks);
        EvaluationMetrics learningMetrics = Metrics(learningRanks);
        string conclusion = learningMetrics.Top6 > baselineMetrics.Top6 || learningMetrics.Mrr > baselineMetrics.Mrr
            ? "自动学习至少一项核心指标提升；仍需继续观察稳定性。"
            : "自动学习未产生可验证提升，系统应继续使用安全回退。";
        return new AutoLearningEvaluationResult(training, colorTraining, learningRanks.Count,
            baselineMetrics, learningMetrics, fallback, leakage, conclusion,
            ColorMetrics(baselineColorMain, baselineColorDual),
            ColorMetrics(learningColorMain, learningColorDual), validation.TakeLast(50).ToArray());
    }

    public static void SaveLatest50ToPredictionHistory(AutoLearningEvaluationResult result)
    {
        foreach (AutoLearningValidationRecord row in result.Latest50)
        {
            string details = $"严格滚动验证|实际排名:{row.ActualRank}|TOP3:{(row.Top3Hit ? "命中" : "未命中")}|" +
                $"TOP6:{(row.Top6Hit ? "命中" : "未命中")}|权重:AI={row.Weights.AI:P1},ML={row.Weights.ML:P1}," +
                $"状态={row.Weights.State:P1},冷侧V7={row.Weights.V7:P1}|" +
                $"波色排除:{ExcludedColor(row.MainColor, row.DefenseColor)};主:{row.MainColor};防:{row.DefenseColor}|" +
                $"实际波色:{row.ActualColor}|主波:{(row.MainColorHit ? "命中" : "未命中")}|" +
                $"双波:{(row.DualColorHit ? "命中" : "未命中")}|" +
                $"波色权重:频率={row.ColorWeights.Frequency:P1},转换={row.ColorWeights.Transition:P1},遗漏={row.ColorWeights.Omission:P1}";
            DatabaseHelper.SaveVerifiedValidationPrediction(row.Issue, string.Join(",", row.Top3),
                string.Join(",", row.Top6), row.ActualZodiac, row.ActualNumber, row.ActualRank,
                V7PredictionHistoryService.AutoLearningValidationHistoryKey,
                "V7 AutoLearning Validation", details, row.MainColorHit, row.DualColorHit, row.ActualColor);
        }
    }

    public static void SaveReports(AutoLearningEvaluationResult result, string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Auto Learning Evaluation Results.json"),
            JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented=true }));
        string markdown = $"""
            # Auto Learning Evaluation Report

            - 训练协议：2023–2025 按期顺序学习一次
            - 测试协议：2026 先预测、再揭晓、再学习
            - 训练样本：{result.TrainingSamples}
            - 2026测试样本：{result.TestSamples}
            - 未来数据泄漏：{(result.FutureDataLeakageDetected ? "是" : "否")}

            | 指标 | 原模型 | 自动学习 |
            |---|---:|---:|
            | TOP3 | {result.Baseline.Top3:P2} | {result.Learning.Top3:P2} |
            | TOP6 | {result.Baseline.Top6:P2} | {result.Learning.Top6:P2} |
            | MRR | {result.Baseline.Mrr:F4} | {result.Learning.Mrr:F4} |
            | TOP6最大连续未命中 | {result.Baseline.MaximumTop6Misses} | {result.Learning.MaximumTop6Misses} |

            | 波色指标 | 固定权重 | 自动学习 |
            |---|---:|---:|
            | 主波命中 | {result.BaselineColor.MainHitRate:P2} | {result.LearningColor.MainHitRate:P2} |
            | 双波命中 | {result.BaselineColor.DualHitRate:P2} | {result.LearningColor.DualHitRate:P2} |
            | 主波最大连续未中 | {result.BaselineColor.MaximumMainMisses} | {result.LearningColor.MaximumMainMisses} |
            | 双波最大连续未中 | {result.BaselineColor.MaximumDualMisses} | {result.LearningColor.MaximumDualMisses} |

            结论：{result.Conclusion}
            """;
        File.WriteAllText(Path.Combine(directory, "Auto Learning Evaluation Report.md"), markdown);
    }

    private static EvaluationMetrics Metrics(IReadOnlyList<int> ranks)
    {
        int samples = ranks.Count;
        int misses=0, maximum=0;
        foreach (int rank in ranks)
        {
            misses = rank is >0 and <=6 ? 0 : misses+1;
            maximum = Math.Max(maximum, misses);
        }
        return new EvaluationMetrics(samples,
            samples == 0 ? 0 : ranks.Count(rank => rank is >0 and <=3)/(double)samples,
            samples == 0 ? 0 : ranks.Count(rank => rank is >0 and <=6)/(double)samples,
            samples == 0 ? 0 : ranks.Where(rank => rank>0).Select(rank => 1d/rank).DefaultIfEmpty().Average(), maximum);
    }

    private static ColorEvaluationMetrics ColorMetrics(IReadOnlyList<bool> mainHits, IReadOnlyList<bool> dualHits) =>
        new(mainHits.Count,
            mainHits.Count == 0 ? 0 : mainHits.Count(hit => hit) / (double)mainHits.Count,
            dualHits.Count == 0 ? 0 : dualHits.Count(hit => hit) / (double)dualHits.Count,
            MaximumMisses(mainHits), MaximumMisses(dualHits));

    private static int MaximumMisses(IReadOnlyList<bool> hits)
    {
        int current = 0, maximum = 0;
        foreach (bool hit in hits)
        {
            current = hit ? 0 : current + 1;
            maximum = Math.Max(maximum, current);
        }
        return maximum;
    }

    private static string ExcludedColor(string main, string defense) =>
        new[] { "红", "蓝", "绿" }.First(color => color != main && color != defense);
}
