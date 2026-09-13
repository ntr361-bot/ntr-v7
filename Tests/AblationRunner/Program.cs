using System.Text;
using System.Text.Json;
using 六合分析软件;

string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
string dataDir = Path.Combine(repoRoot, "data");
Environment.SetEnvironmentVariable("LIUHE_DATA_DIR", dataDir);
DatabaseHelper.InitializeDatabase();

var history = DatabaseHelper.GetHistory()
    .Where(x => !string.IsNullOrWhiteSpace(x.SpecialZodiac))
    .OrderBy(x => long.TryParse(x.Period, out var p) ? p : long.MaxValue)
    .ToList();

const int targetSamples = 1000;
const int warmup = 100;
if (history.Count <= warmup)
    throw new InvalidOperationException($"历史数据不足：{history.Count}");

int firstTarget = Math.Max(warmup, history.Count - targetSamples);
var metrics = new Dictionary<string, Metric>(StringComparer.Ordinal);
var v65 = new V65RuleScoringEngine();

for (int target = firstTarget; target < history.Count; target++)
{
    var prefix = history.Take(target).ToList();
    string actual = history[target].SpecialZodiac;
    if (string.IsNullOrWhiteSpace(actual)) continue;

    EvaluateV65(prefix, actual, 50, "V65-50");
    EvaluateV65(prefix, actual, 100, "V65-100");
    EvaluateV65(prefix, actual, AISettings.AllHistoryModeValue, "V65-All");
    EvaluateV7(prefix, actual);
}

var rows = metrics.Values
    .Select(m => m.ToRow())
    .OrderBy(r => r.Model, StringComparer.Ordinal)
    .ThenBy(r => VariantOrder(r.Variant))
    .ThenBy(r => r.Variant, StringComparer.Ordinal)
    .ToList();

var report = new
{
    GeneratedAtUtc = DateTime.UtcNow,
    HistoryCount = history.Count,
    Warmup = warmup,
    RequestedTargetSamples = targetSamples,
    ActualTargetSamples = rows.Count == 0 ? 0 : rows.Max(x => x.Samples),
    Warning = "V6.5 only-eight 与生产 baseline 使用硬编码 EightZodiacRules/EightZodiacHitRates，可能含评估区间未来信息；判断规则贡献应以 no-eight / clean-remove-* 为主。",
    Rows = rows
};

string outDir = Path.Combine(repoRoot, "docs");
Directory.CreateDirectory(outDir);
string jsonPath = Path.Combine(outDir, "1000-ablation-report.json");
string mdPath = Path.Combine(outDir, "1000-ablation-report.md");
File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
File.WriteAllText(mdPath, BuildMarkdown(rows));
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"REPORT_JSON={jsonPath}");
Console.WriteLine($"REPORT_MD={mdPath}");

void EvaluateV65(IReadOnlyList<DatabaseHelper.HistoryRecord> prefix, string actual, int periods, string model)
{
    var weights = V65ExperimentPipeline.GetWeightsForPeriods(periods);
    var result = v65.Predict(prefix, periods, weights);
    var scores = result.AllScores;

    Observe(model, "baseline", Rank(scores, s => s.TotalScore), actual);
    Observe(model, "no-eight", Rank(scores, s => Weighted(s, weights, null, includeEight: false)), actual);

    var dimensions = new (string Name, Func<V65RuleScoringEngine.ZodiacScoreV2, double> Value)[]
    {
        ("frequency", s => s.FrequencyScore),
        ("trend", s => s.RecentTrendScore),
        ("omission", s => s.OmissionScore),
        ("hotcold", s => s.HotColdScore),
        ("period", s => s.PeriodPatternScore),
        ("consecutive", s => s.ConsecutiveScore),
        ("eight", s => s.EightZodiacScore)
    };

    foreach (var dimension in dimensions)
        Observe(model, $"only-{dimension.Name}", Rank(scores, dimension.Value), actual);

    foreach (string removed in new[] { "frequency", "trend", "omission", "hotcold", "period", "consecutive" })
    {
        Observe(model, $"remove-{removed}", Rank(scores, s => Weighted(s, weights, removed, includeEight: true)), actual);
        Observe(model, $"clean-remove-{removed}", Rank(scores, s => Weighted(s, weights, removed, includeEight: false)), actual);
    }
}

void EvaluateV7(IReadOnlyList<DatabaseHelper.HistoryRecord> prefix, string actual)
{
    var features = FeatureEngine.BuildFeatures(prefix, 0).ToList();
    Observe("V7", "baseline", RankV7(features, true, .55, .45, .10), actual);
    Observe("V7", "no-filter", RankV7(features, false, .55, .45, .10), actual);
    Observe("V7", "only-frequency", RankV7(features, true, 1, 0, 0), actual);
    Observe("V7", "only-omission", RankV7(features, true, 0, 1, 0), actual);
    Observe("V7", "only-cycle", RankV7(features, true, 0, 0, 1), actual);
    Observe("V7", "remove-frequency", RankV7(features, true, 0, .45, .10), actual);
    Observe("V7", "remove-omission", RankV7(features, true, .55, 0, .10), actual);
    Observe("V7", "remove-cycle", RankV7(features, true, .55, .45, 0), actual);
}

void Observe(string model, string variant, IReadOnlyList<string> ranking, string actual)
{
    string key = model + "|" + variant;
    if (!metrics.TryGetValue(key, out var metric))
    {
        metric = new Metric(model, variant);
        metrics[key] = metric;
    }
    metric.Observe(ranking, actual);
}

static List<string> Rank(IReadOnlyList<V65RuleScoringEngine.ZodiacScoreV2> scores,
    Func<V65RuleScoringEngine.ZodiacScoreV2, double> selector)
{
    return scores.Select((s, i) => new { Score = s, Index = i, Value = selector(s) })
        .OrderByDescending(x => x.Value)
        .ThenBy(x => x.Index)
        .Select(x => x.Score.Zodiac)
        .ToList();
}

static double Weighted(V65RuleScoringEngine.ZodiacScoreV2 s,
    V65RuleScoringEngine.WeightConfig w, string? removed, bool includeEight)
{
    double score = 0;
    if (removed != "frequency") score += s.FrequencyScore * w.FrequencyWeight;
    if (removed != "trend") score += s.RecentTrendScore * w.RecentTrendWeight;
    if (removed != "omission") score += s.OmissionScore * w.OmissionWeight;
    if (removed != "hotcold") score += s.HotColdScore * w.HotColdWeight;
    if (removed != "period") score += s.PeriodPatternScore * w.PeriodPatternWeight;
    if (removed != "consecutive") score += s.ConsecutiveScore * w.ConsecutiveWeight;
    if (includeEight) score += s.EightZodiacScore;
    return score;
}

static List<string> RankV7(IReadOnlyList<ZodiacFeature> features, bool useFilter,
    double frequencyWeight, double omissionWeight, double cycleWeight)
{
    var candidates = useFilter ? features.Where(x => !x.ShortForbidden).ToList() : features.ToList();
    return candidates.Select((x, i) => new
        {
            Feature = x,
            Index = i,
            Score = frequencyWeight * (x.Recent10Count + x.Recent20Count * .5 + x.Recent50Count * .2) +
                    omissionWeight * Math.Min(x.CurrentOmission, x.AverageOmission * 2 + 1) +
                    cycleWeight * x.ShortCycleRepeatCount
        })
        .OrderByDescending(x => x.Score)
        .ThenBy(x => x.Index)
        .Select(x => x.Feature.Zodiac)
        .ToList();
}

static int VariantOrder(string variant) => variant switch
{
    "baseline" => 0,
    "no-eight" => 1,
    "no-filter" => 1,
    _ when variant.StartsWith("clean-remove-", StringComparison.Ordinal) => 2,
    _ when variant.StartsWith("remove-", StringComparison.Ordinal) => 3,
    _ when variant.StartsWith("only-", StringComparison.Ordinal) => 4,
    _ => 9
};

static string BuildMarkdown(IReadOnlyList<MetricRow> rows)
{
    var sb = new StringBuilder();
    sb.AppendLine("# 1000期模型拆解 / 消融实验");
    sb.AppendLine();
    sb.AppendLine("严格 Walk-Forward：每一期只使用此前历史。V6.5 的八肖规则/命中率为硬编码常量，可能含未来信息，因此判断 V6.5 规则贡献时以 no-eight 和 clean-remove-* 为主。");
    sb.AppendLine();
    foreach (var group in rows.GroupBy(x => x.Model))
    {
        var baseline = group.First(x => x.Variant == "baseline");
        sb.AppendLine($"## {group.Key}");
        sb.AppendLine();
        sb.AppendLine("| 变体 | 样本 | Top3 | ΔTop3 | Top6 | ΔTop6 | 前500 Top6 | 后500 Top6 | Top3最长连错 | Top6最长连错 |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var row in group)
        {
            sb.AppendLine($"| {row.Variant} | {row.Samples} | {row.Top3Rate:P1} | {(row.Top3Rate - baseline.Top3Rate):+0.0%;-0.0%;0.0%} | {row.Top6Rate:P1} | {(row.Top6Rate - baseline.Top6Rate):+0.0%;-0.0%;0.0%} | {row.FirstHalfTop6Rate:P1} | {row.SecondHalfTop6Rate:P1} | {row.MaxTop3MissStreak} | {row.MaxTop6MissStreak} |");
        }
        sb.AppendLine();
    }
    return sb.ToString();
}

sealed class Metric(string model, string variant)
{
    private int samples;
    private int top3Hits;
    private int top6Hits;
    private int firstSamples;
    private int secondSamples;
    private int firstTop3Hits;
    private int firstTop6Hits;
    private int secondTop3Hits;
    private int secondTop6Hits;
    private int currentTop3Miss;
    private int currentTop6Miss;
    private int maxTop3Miss;
    private int maxTop6Miss;

    public void Observe(IReadOnlyList<string> ranking, string actual)
    {
        samples++;
        bool hit3 = ranking.Take(3).Contains(actual);
        bool hit6 = ranking.Take(6).Contains(actual);
        if (samples <= 500)
        {
            firstSamples++;
            if (hit3) firstTop3Hits++;
            if (hit6) firstTop6Hits++;
        }
        else
        {
            secondSamples++;
            if (hit3) secondTop3Hits++;
            if (hit6) secondTop6Hits++;
        }

        if (hit3)
        {
            top3Hits++;
            currentTop3Miss = 0;
        }
        else
        {
            currentTop3Miss++;
            maxTop3Miss = Math.Max(maxTop3Miss, currentTop3Miss);
        }
        if (hit6)
        {
            top6Hits++;
            currentTop6Miss = 0;
        }
        else
        {
            currentTop6Miss++;
            maxTop6Miss = Math.Max(maxTop6Miss, currentTop6Miss);
        }
    }

    public MetricRow ToRow() => new(model, variant, samples,
        samples == 0 ? 0 : top3Hits / (double)samples,
        samples == 0 ? 0 : top6Hits / (double)samples,
        firstSamples == 0 ? 0 : firstTop3Hits / (double)firstSamples,
        firstSamples == 0 ? 0 : firstTop6Hits / (double)firstSamples,
        secondSamples == 0 ? 0 : secondTop3Hits / (double)secondSamples,
        secondSamples == 0 ? 0 : secondTop6Hits / (double)secondSamples,
        maxTop3Miss, maxTop6Miss);
}

sealed record MetricRow(string Model, string Variant, int Samples,
    double Top3Rate, double Top6Rate,
    double FirstHalfTop3Rate, double FirstHalfTop6Rate,
    double SecondHalfTop3Rate, double SecondHalfTop6Rate,
    int MaxTop3MissStreak, int MaxTop6MissStreak);
