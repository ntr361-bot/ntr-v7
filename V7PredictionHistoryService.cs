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

    public static void SaveAll(string targetPeriod, IReadOnlyList<DatabaseHelper.HistoryRecord> history)
    {
        if (string.IsNullOrWhiteSpace(targetPeriod)) throw new ArgumentException("预测期号不能为空", nameof(targetPeriod));
        // 旧 V7 短中长期/ML 已淘汰；保留入口兼容性，但只生成 V6.5 的第四条自动学习预测。
        SaveAutoLearning(targetPeriod, history);
    }

    public static AutoLearningFormalPrediction SaveAutoLearning(string targetPeriod,
        IReadOnlyList<DatabaseHelper.HistoryRecord> history, ColorPredictionResult? color = null,
        string learningDetails = "自动学习模型正式预测")
    {
        ModelMemoryState colorMemory = AutoLearningTrainer.EnsureInitialTraining();
        color ??= ColorEngine.Predict(history, colorMemory.ColorLearning.Weights);
        string colorDetails = $"波色排除:{color.Excluded};主:{color.Main};防:{color.Defense}";
        string colorSnapshot = ColorPredictionSnapshotCodec.Encode(targetPeriod, color);
        ModelMemoryState memory = new ModelMemory(ExperimentModels.AutoLearning).LoadOrCreate();
        var saved = DatabaseHelper.GetPredictionHistory(int.MaxValue);
        AutoLearningSnapshot auto = saved.Count(row => row.Issue == targetPeriod && row.ModelVersion == "V6.5" &&
            (row.AnalysisPeriods is 50 or 100 || row.AnalysisPeriods == AISettings.AllHistoryModeValue)) >= 3
            ? AutoLearningSnapshotBuilder.BuildFromBasePredictions(targetPeriod, saved, memory)
            : BuildAutoLearningSnapshot(targetPeriod, history);
        string autoScores = string.Join(";", auto.Result.Ranking.Select(item => $"{item.Zodiac}:{item.Probability:F4}"));
        DatabaseHelper.SavePrediction(targetPeriod,
            string.Join(",", auto.Result.Ranking.Take(3).Select(item => item.Zodiac)),
            string.Join(",", auto.Result.Ranking.Take(6).Select(item => item.Zodiac)), "",
            "V6.5 AutoLearning", AutoLearningHistoryKey,
            $"{autoScores}|{colorDetails}|{colorSnapshot}", learningDetails,
            auto.FinalRankingJson, auto.BaseModelScoresJson, auto.FeatureSnapshotJson, auto.WeightSnapshotJson);
        return new AutoLearningFormalPrediction(auto, color);
    }

    public static AutoLearningSnapshot BuildAutoLearningSnapshot(string targetPeriod,
        IReadOnlyList<DatabaseHelper.HistoryRecord> history)
    {
        ModelMemoryState memory = AutoLearningTrainer.EnsureInitialTraining();
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
            .Where(x => string.Equals(x.ModelVersion, "V6.5", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(x.ModelVersion, "V6.5 AutoLearning", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Issue)
            .ThenBy(x => ModelDisplayOrder(x.ModelVersion))
            .Take(Math.Max(0, limit))
            .ToList();

    private static int ModelDisplayOrder(string modelVersion) => modelVersion switch
    {
        "V6.5" => 0,
        "V6.5 AutoLearning" => 1,
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
            modelVersion, historyKey, scores, report);
    }
}
