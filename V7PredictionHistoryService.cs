namespace 六合分析软件;

public sealed record AutoLearningFormalPrediction(AutoLearningSnapshot Snapshot, ColorPredictionResult Color);

public static class V7PredictionHistoryService
{
    public const int LongTermHistoryKey = 7000;
    public const int ShortTermHistoryKey = 7050;
    public const int MediumTermHistoryKey = 7100;
    public const int MlHistoryKey = 7200;
    public const int AutoLearningHistoryKey = 7250;
    public const int AutoLearningValidationHistoryKey = 7300;

    /// <summary>
    /// V6.5 预测历史只展示正式展示档（100期 + 自动学习）；
    /// 50期与全部历史（长期）仅在后台计算供自动学习学习，不进入历史展示。
    /// </summary>
    public static bool IsV65DisplayedModel(string modelVersion, int analysisPeriods) =>
        modelVersion == "V6.5 AutoLearning" ||
        (modelVersion == "V6.5" && analysisPeriods == 100);

    public static void SaveAll(string targetPeriod, IReadOnlyList<DatabaseHelper.HistoryRecord> history)
    {
        if (string.IsNullOrWhiteSpace(targetPeriod)) throw new ArgumentException("预测期号不能为空", nameof(targetPeriod));
        var v7 = V7Engine.Predict(history);
        ModelMemoryState memory = new ModelMemory(ExperimentModels.IntelligentHistory).LoadOrCreate();
        var color = ColorEngine.Predict(history, memory.ColorLearning.Weights);
        var report = AIReportEngine.Generate(history, new[] { v7 }, color: color);

        SaveEngine(targetPeriod, v7, LongTermHistoryKey, "V7", report.Text);

        SaveIntelligentAutoLearning(targetPeriod, history, color, report.Text);
    }

    public static AutoLearningFormalPrediction SaveAutoLearning(string targetPeriod,
        IReadOnlyList<DatabaseHelper.HistoryRecord> history, ColorPredictionResult? color = null,
        string learningDetails = "自动学习模型正式预测")
    {
        // V6.5 自动学习仅由已开奖的 V6.5 四模型记录更新；不能用另一套手写快照预训练。
        // The formal V6.5 meta model must begin with chronological historical
        // experience, not wait for one hundred future live draws to accumulate.
        ModelMemoryState colorMemory = AutoLearningTrainer.EnsureInitialTraining(history, ExperimentModels.AutoLearning);
        color ??= ColorEngine.Predict(history, colorMemory.ColorLearning.Weights);
        string colorDetails = $"波色排除:{color.Excluded};主:{color.Main};防:{color.Defense}";
        string colorSnapshot = ColorPredictionSnapshotCodec.Encode(targetPeriod, color);
        ModelMemoryState memory = colorMemory;
        var saved = DatabaseHelper.GetPredictionHistory(int.MaxValue);
        if (!HasCompleteV65BaseSnapshots(targetPeriod, saved))
            throw new InvalidOperationException("V6.5自动学习必须先取得同一期50期、100期和全部历史三条基础预测快照。");
        var v7 = V7Engine.Predict(history);
        string[] v7Ranking = v7.Probabilities.OrderByDescending(x => x.Value).ThenBy(x => x.Key)
            .Select(x => x.Key).ToArray();
        AutoLearningSnapshot auto = AutoLearningSnapshotBuilder.BuildFromBasePredictions(
            targetPeriod, saved, memory, v7Ranking);
        string autoScores = string.Join(";", auto.Result.Ranking.Select(item => $"{item.Zodiac}:{item.Probability:F4}"));
        DatabaseHelper.SavePrediction(targetPeriod,
            string.Join(",", auto.Result.Ranking.Take(3).Select(item => item.Zodiac)),
            string.Join(",", auto.Result.Ranking.Take(6).Select(item => item.Zodiac)), "",
            "V6.5 AutoLearning", AutoLearningHistoryKey,
            $"{autoScores}|{colorDetails}|{colorSnapshot}", learningDetails,
            auto.FinalRankingJson, auto.BaseModelScoresJson, auto.FeatureSnapshotJson, auto.WeightSnapshotJson);
        return new AutoLearningFormalPrediction(auto, color);
    }

    public static bool HasCompleteV65BaseSnapshots(string targetPeriod,
        IReadOnlyList<DatabaseHelper.PredictionRecord> records) =>
        records.Where(row => row.Issue == targetPeriod && row.ModelVersion == "V6.5")
            .Select(row => ExperimentModels.ForPeriods(row.AnalysisPeriods))
            .Distinct(StringComparer.Ordinal)
            .Count() == 3;

    private static void SaveIntelligentAutoLearning(string targetPeriod,
        IReadOnlyList<DatabaseHelper.HistoryRecord> history, ColorPredictionResult color, string learningDetails)
    {
        ModelMemoryState memory = new ModelMemory(ExperimentModels.IntelligentHistory).LoadOrCreate();
        string colorDetails = $"波色排除:{color.Excluded};主:{color.Main};防:{color.Defense}";
        string colorSnapshot = ColorPredictionSnapshotCodec.Encode(targetPeriod, color);
        MetaPredictionInput input = HistoricalMetaSnapshotBuilder.Build(history, targetPeriod);
        IReadOnlyList<string> baseline = HistoricalMetaSnapshotBuilder.Baseline(input);
        MetaPredictionResult result = new MetaPredictionEngine().Predict(input, memory, baseline);
        var snapshot = new AutoLearningSnapshot(input, baseline, result, memory.Weights);
        string scores = string.Join(";", result.Ranking.Select(item => $"{item.Zodiac}:{item.Probability:F4}"));
        DatabaseHelper.SavePrediction(targetPeriod,
            string.Join(",", result.Ranking.Take(3).Select(item => item.Zodiac)),
            string.Join(",", result.Ranking.Take(6).Select(item => item.Zodiac)), "",
            "V7 AutoLearning", AutoLearningHistoryKey,
            $"{scores}|{colorDetails}|{colorSnapshot}", learningDetails,
            snapshot.FinalRankingJson, snapshot.BaseModelScoresJson, snapshot.FeatureSnapshotJson, snapshot.WeightSnapshotJson);
    }

    public static AutoLearningSnapshot BuildAutoLearningSnapshot(string targetPeriod,
        IReadOnlyList<DatabaseHelper.HistoryRecord> history)
    {
        // 此方法只服务于独立的智能预测历史；不能读写 V6.5 四模型的记忆库。
        ModelMemoryState memory = new ModelMemory(ExperimentModels.IntelligentHistory).LoadOrCreate();
        MetaPredictionInput input = HistoricalMetaSnapshotBuilder.Build(history, targetPeriod);
        IReadOnlyList<string> baseline = HistoricalMetaSnapshotBuilder.Baseline(input);
        MetaPredictionResult result = new MetaPredictionEngine().Predict(input, memory, baseline);
        return new AutoLearningSnapshot(input, baseline, result, memory.Weights);
    }

    public static string FormatAnalysisLabel(int analysisPeriods, string modelVersion) => analysisPeriods switch
    {
        AutoLearningHistoryKey when modelVersion == "V6.5 AutoLearning" => "自动学习",
        ShortTermHistoryKey when modelVersion.StartsWith("V7") => "50期",
        MediumTermHistoryKey when modelVersion.StartsWith("V7") => "100期",
        LongTermHistoryKey when modelVersion.StartsWith("V7") => "长期",
        MlHistoryKey when modelVersion.StartsWith("V7") => "ML",
        AutoLearningHistoryKey when modelVersion.StartsWith("V7") => "自动学习",
        AutoLearningValidationHistoryKey when modelVersion.StartsWith("V7") => "验证",
        > 0 => $"{analysisPeriods}期",
        _ => "旧记录"
    };

    public static string FormatModelName(string modelVersion) => modelVersion switch
    {
        "V6.5" => "V6.5基础模型",
        "V6.5 AutoLearning" => "自动学习模型",
        "V7" => "V7长期模型",
        "V7 ShortTerm" => "短期模型",
        "V7 MediumTerm" => "中期模型",
        "V7 LongTerm" => "长期模型",
        "V7 ML LightGBM" => "ML LightGBM",
        "V7 AutoLearning" => "自动学习模型",
        "V7 AutoLearning Validation" => "自动学习验证",
        _ when modelVersion.StartsWith("V7 ", StringComparison.OrdinalIgnoreCase) => modelVersion[3..],
        _ => modelVersion
    };

    public static List<DatabaseHelper.PredictionRecord> GetHistory(int limit = 100) =>
        DatabaseHelper.GetPredictionHistory(int.MaxValue)
            .Where(x => x.ModelVersion.StartsWith("V7", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Issue)
            .ThenBy(x => ModelDisplayOrder(x.ModelVersion))
            .Take(Math.Max(0, limit))
            .ToList();

    private static int ModelDisplayOrder(string modelVersion) => modelVersion switch
    {
        "V7" => 0,
        "V7 ShortTerm" => 0,
        "V7 MediumTerm" => 1,
        "V7 ML LightGBM" => 2,
        "V7 AutoLearning" => 3,
        "V7 LongTerm" => 4,
        "V7 AutoLearning Validation" => 5,
        _ => 5
    };

    public static string ExtractColorPrediction(string scoreDetails)
    {
        if (string.IsNullOrWhiteSpace(scoreDetails)) return "-";
        int marker = scoreDetails.IndexOf("波色排除:", StringComparison.Ordinal);
        if (marker < 0) return "-";

        string value = scoreDetails[marker..];
        int separator = value.IndexOf('|');
        if (separator >= 0) value = value[..separator];

        string main = string.Empty;
        string defense = string.Empty;
        foreach (string item in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = item.IndexOf(':');
            if (colon < 0) continue;

            string key = item[..colon].Trim();
            string color = item[(colon + 1)..].Trim();
            if (key == "主") main = color;
            else if (key == "防") defense = color;
        }

        return main.Length > 0 && defense.Length > 0
            ? $"主：{main}　防：{defense}"
            : "-";
    }

    private static void SaveEngine(string targetPeriod, V7PredictionResult result, int historyKey, string modelVersion, string report)
    {
        string scores = string.Join(";", result.Probabilities.OrderByDescending(x => x.Value)
            .Select(x => $"{x.Key}:{x.Value:F4}"));
        DatabaseHelper.SavePrediction(targetPeriod, string.Join(",", result.Top3), string.Join(",", result.Top6), "",
            modelVersion, historyKey, scores, report,
            System.Text.Json.JsonSerializer.Serialize(result.Probabilities.OrderByDescending(x => x.Value).ThenBy(x => x.Key).Select(x => x.Key).ToArray()),
            "", System.Text.Json.JsonSerializer.Serialize(result.Features));
    }
}
