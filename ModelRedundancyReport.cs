using System.Text.Json;

namespace 六合分析软件;

/// <summary>
/// 冗余度基线报告：各模型严格 Walk-Forward 命中率、模型间排序相关矩阵、V65 六维相关矩阵。
/// 仅用于研究，不写入正式预测链。
/// </summary>
public sealed record ModelRedundancyReport(
    int SampleCount,
    IReadOnlyList<string> Models,
    IReadOnlyDictionary<string, double> Top3HitRates,
    IReadOnlyDictionary<string, double> Top6HitRates,
    double[,] ModelRankCorrelation,
    double[,] V65DimensionCorrelation,
    IReadOnlyList<ModelRecencyRow> RecencyBreakdown);

public sealed record ModelRecencyRow(
    string Model,
    int HotSamples,
    double HotTop3Rate,
    int ColdSamples,
    double ColdTop3Rate);

public static class ModelRedundancyReportService
{
    private static readonly string[] Zodiacs =
        { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };

    public static ModelRedundancyReport Run(
        IReadOnlyList<DatabaseHelper.HistoryRecord> history, int warmup = 50, int maxTargets = int.MaxValue,
        int mlMaxTargets = int.MaxValue)
    {
        var chronological = history
            .OrderBy(x => long.TryParse(x.Period, out long issue) ? issue : 0)
            .ToList();
        if (chronological.Count <= warmup) return Empty();
        int firstTarget = Math.Max(warmup, chronological.Count - maxTargets);

        var models = new[]
        {
            "v65-50", "v65-100", "v65-all", "ensemble", "v7-short", "v7-medium", "v7-long", "ml", "random"
        };
        var rankByModel = models.ToDictionary(m => m, _ => new List<List<string>>());
        var hit3 = models.ToDictionary(m => m, _ => 0);
        var hit6 = models.ToDictionary(m => m, _ => 0);
        var hotHits = models.ToDictionary(m => m, _ => 0);
        var hotSamples = models.ToDictionary(m => m, _ => 0);
        var coldHits = models.ToDictionary(m => m, _ => 0);
        var coldSamples = models.ToDictionary(m => m, _ => 0);
        var v65Dims = Enumerable.Range(0, 6).Select(_ => new List<double>()).ToArray();
        int samples = 0;
        var random = new Random(6501);
        int mlEvalCount = 0;

        for (int target = firstTarget; target < chronological.Count; target++)
        {
            var prefix = chronological.Take(target).ToList();
            string actual = chronological[target].SpecialZodiac;
            if (string.IsNullOrWhiteSpace(actual)) continue;
            bool evalMl = mlEvalCount < mlMaxTargets;

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
            rankings["ml"] = evalMl
                ? MachineLearningPredictionService.Predict(prefix, minimumTraining: 30)
                    .Take(6).Select(x => x.Zodiac).ToList()
                : new List<string>();
            if (evalMl) mlEvalCount++;
            rankings["random"] = Zodiacs.OrderBy(_ => random.Next()).Take(6).ToList();

            var recent10 = prefix.Skip(Math.Max(0, prefix.Count - 10)).Select(r => r.SpecialZodiac).ToList();
            var recent20 = prefix.Skip(Math.Max(0, prefix.Count - 20)).Select(r => r.SpecialZodiac).ToList();
            bool hot = recent10.Contains(actual);
            bool cold = !recent20.Contains(actual);
            foreach (string model in models)
            {
                if (model == "ml" && !evalMl) continue;
                if (rankings[model].Contains(actual)) hit6[model]++;
                if (rankings[model].Take(3).Contains(actual)) hit3[model]++;
                if (hot)
                {
                    hotSamples[model]++;
                    if (rankings[model].Take(3).Contains(actual)) hotHits[model]++;
                }
                if (cold)
                {
                    coldSamples[model]++;
                    if (rankings[model].Take(3).Contains(actual)) coldHits[model]++;
                }
                rankByModel[model].Add(rankings[model]);
            }

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
            samples,
            models,
            models.ToDictionary(m => m, m => samples == 0 ? 0d : hit3[m] / (double)samples),
            models.ToDictionary(m => m, m => samples == 0 ? 0d : hit6[m] / (double)samples),
            RankCorrelation(models, rankByModel),
            V65DimensionCorrelation(v65Dims),
            models.Select(m => new ModelRecencyRow(m, hotSamples[m],
                hotSamples[m] == 0 ? 0d : hotHits[m] / (double)hotSamples[m],
                coldSamples[m], coldSamples[m] == 0 ? 0d : coldHits[m] / (double)coldSamples[m])).ToList());
    }

    private static ModelRedundancyReport Empty() => new(
        0, Array.Empty<string>(),
        new Dictionary<string, double>(), new Dictionary<string, double>(),
        new double[0, 0], new double[0, 0], Array.Empty<ModelRecencyRow>());

    private static double[,] RankCorrelation(IReadOnlyList<string> models,
        IReadOnlyDictionary<string, List<List<string>>> ranks)
    {
        int n = models.Count;
        var matrix = new double[n, n];
        for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++)
                matrix[a, b] = a == b ? 1d : RankingOverlap(ranks[models[a]], ranks[models[b]]);
        return matrix;
    }

    private static double RankingOverlap(IReadOnlyList<List<string>> a, IReadOnlyList<List<string>> b)
    {
        int count = Math.Min(a.Count, b.Count);
        if (count == 0) return 0;
        double sum = 0;
        for (int i = 0; i < count; i++)
            sum += (double)a[i].Intersect(b[i]).Count() / Math.Max(1, Math.Min(a[i].Count, b[i].Count));
        return sum / count;
    }

    private static double[,] V65DimensionCorrelation(IReadOnlyList<IReadOnlyList<double>> rows)
    {
        int n = rows.Count;
        var matrix = new double[n, n];
        if (n == 0) return matrix;
        for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++)
                matrix[a, b] = a == b ? 1d : Pearson(
                    rows.Select(r => r[a]).ToArray(),
                    rows.Select(r => r[b]).ToArray());
        return matrix;
    }

    private static double Pearson(double[] x, double[] y)
    {
        double mx = x.Average(), my = y.Average();
        double num = 0, dx = 0, dy = 0;
        for (int i = 0; i < x.Length; i++)
        {
            num += (x[i] - mx) * (y[i] - my);
            dx += (x[i] - mx) * (x[i] - mx);
            dy += (y[i] - my) * (y[i] - my);
        }
        return dx == 0 || dy == 0 ? 0 : num / Math.Sqrt(dx * dy);
    }

    public static string ToJson(ModelRedundancyReport report) =>
        JsonSerializer.Serialize(new
        {
            report.SampleCount,
            report.Models,
            report.Top3HitRates,
            report.Top6HitRates,
            ModelRankCorrelation = ToJagged(report.ModelRankCorrelation),
            V65DimensionCorrelation = ToJagged(report.V65DimensionCorrelation),
            report.RecencyBreakdown
        }, new JsonSerializerOptions { WriteIndented = true });

    private static double[][] ToJagged(double[,] matrix)
    {
        int rows = matrix.GetLength(0), cols = matrix.GetLength(1);
        var result = new double[rows][];
        for (int r = 0; r < rows; r++)
        {
            result[r] = new double[cols];
            for (int c = 0; c < cols; c++) result[r][c] = matrix[r, c];
        }
        return result;
    }

    public static string ToMarkdown(ModelRedundancyReport report)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## V6.5 模型冗余度基线报告");
        sb.AppendLine();
        sb.AppendLine($"样本期数：{report.SampleCount}");
        sb.AppendLine();
        sb.AppendLine("| 模型 | Top3 命中率 | Top6 命中率 | 随机基准 Top3 25% / Top6 50% |");
        sb.AppendLine("|---|---|---|---|");
        foreach (string model in report.Models)
            sb.AppendLine($"| {model} | {report.Top3HitRates[model]:P1} | {report.Top6HitRates[model]:P1} | - |");
        sb.AppendLine();
        sb.AppendLine("### 模型间 Top6 排序重合率");
        sb.AppendLine();
        sb.Append("| | ");
        foreach (string model in report.Models) sb.Append(model).Append(" | ");
        sb.AppendLine();
        sb.Append("|---|");
        for (int i = 0; i < report.Models.Count; i++) sb.Append("---|");
        sb.AppendLine();
        for (int a = 0; a < report.Models.Count; a++)
        {
            sb.Append("| ").Append(report.Models[a]).Append(" | ");
            for (int b = 0; b < report.Models.Count; b++)
                sb.Append(report.ModelRankCorrelation[a, b].ToString("F2")).Append(" | ");
            sb.AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine("### V65 六维评分相关矩阵（频率/走势/遗漏/冷热/周期/连号）");
        string[] dims = { "频率", "走势", "遗漏", "冷热", "周期", "连号" };
        sb.Append("| | ");
        foreach (string d in dims) sb.Append(d).Append(" | ");
        sb.AppendLine();
        sb.Append("|---|");
        for (int i = 0; i < dims.Length; i++) sb.Append("---|");
        sb.AppendLine();
        for (int a = 0; a < dims.Length; a++)
        {
            sb.Append("| ").Append(dims[a]).Append(" | ");
            for (int b = 0; b < dims.Length; b++)
                sb.Append(report.V65DimensionCorrelation[a, b].ToString("F2")).Append(" | ");
            sb.AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine("### 按实际生肖近期热度拆分的 Top3 命中率");
        sb.AppendLine();
        sb.AppendLine("| 模型 | 热样本 | 热Top3 | 冷样本 | 冷Top3 |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (ModelRecencyRow row in report.RecencyBreakdown)
            sb.AppendLine($"| {row.Model} | {row.HotSamples} | {row.HotTop3Rate:P1} | {row.ColdSamples} | {row.ColdTop3Rate:P1} |");
        return sb.ToString();
    }
}
