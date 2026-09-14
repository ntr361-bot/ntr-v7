using System.Text.Json;
using 六合分析软件;

const int Warmup = 120;
const int RequestedSamples = 1000;
const int Permutations = 200;
const double Eps = 1e-12;

int[] Windows = { 20, 30, 40, 45, 50, 55, 60, 70, 80, 100, 120 };
string[] ZodiacOrder = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };

string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
string dataDir = Path.Combine(repoRoot, "data");
Environment.SetEnvironmentVariable("LIUHE_DATA_DIR", dataDir);
DatabaseHelper.InitializeDatabase();

var history = DatabaseHelper.GetHistory()
    .Where(x => !string.IsNullOrWhiteSpace(x.SpecialZodiac))
    .OrderBy(x => long.TryParse(x.Period, out long p) ? p : long.MaxValue)
    .ToList();

var zodiacIndex = ZodiacOrder.Select((z, i) => (z, i)).ToDictionary(x => x.z, x => x.i, StringComparer.Ordinal);
var sequence = history.Select(x => zodiacIndex[x.SpecialZodiac]).ToArray();
int firstTarget = Math.Max(Warmup, history.Count - RequestedSamples);

var mechanisms = new Dictionary<string, Func<Cell, double>>(StringComparer.Ordinal)
{
    ["current"] = c => c.IntervalCount >= 3 ? c.Regularity * 100 : c.IntervalCount >= 1 ? 40 : 20,
    ["strict-cv-min2"] = c => c.IntervalCount >= 2 ? c.Regularity * 100 : -1,
    ["strict-cv-min3"] = c => c.IntervalCount >= 3 ? c.Regularity * 100 : -1,
    ["strict-cv-min4"] = c => c.IntervalCount >= 4 ? c.Regularity * 100 : -1,
    ["fallback-only"] = c => c.IntervalCount is 1 or 2 ? 40 : c.IntervalCount == 0 ? 20 : 0,
    ["enough-intervals-only"] = c => c.IntervalCount >= 3 ? 1 : 0,
    ["favor-fewer-intervals"] = c => -c.IntervalCount,
    ["favor-more-intervals"] = c => c.IntervalCount
};

var mechanismMetrics = mechanisms.Keys.ToDictionary(x => x, _ => new Metric(), StringComparer.Ordinal);
var mechanismExpectedTop6 = mechanisms.Keys.ToDictionary(x => x, _ => 0.0, StringComparer.Ordinal);
var fallbackMetrics = new Dictionary<int, Metric>();
foreach (int sparseScore in new[] { 20, 30, 40, 50, 60 }) fallbackMetrics[sparseScore] = new Metric();

var zodiacRows = ZodiacOrder.Select(z => new ZodiacAudit(z)).ToArray();
var yearCurrent = new Dictionary<string, Metric>(StringComparer.Ordinal);
var yearStrict3 = new Dictionary<string, Metric>(StringComparer.Ordinal);

for (int target = firstTarget; target < history.Count; target++)
{
    var cells = ComputeCells(sequence, target, 50);
    int actual = sequence[target];

    foreach (var kv in mechanisms)
    {
        double[] scores = cells.Select(kv.Value).ToArray();
        int[] ranking = Rank(scores);
        mechanismMetrics[kv.Key].Observe(ranking, actual);
        mechanismExpectedTop6[kv.Key] += ExpectedHit(scores, actual, 6);
    }

    foreach (int sparseScore in fallbackMetrics.Keys.ToArray())
    {
        double[] scores = cells.Select(c => c.IntervalCount >= 3 ? c.Regularity * 100 : c.IntervalCount >= 1 ? sparseScore : 20).ToArray();
        fallbackMetrics[sparseScore].Observe(Rank(scores), actual);
    }

    double[] currentScores = cells.Select(mechanisms["current"]).ToArray();
    int[] currentRank = Rank(currentScores);
    var top6 = currentRank.Take(6).ToHashSet();
    for (int z = 0; z < 12; z++)
    {
        zodiacRows[z].Periods++;
        if (actual == z) zodiacRows[z].ActualCount++;
        if (top6.Contains(z)) zodiacRows[z].SelectedTop6Count++;
        if (actual == z && top6.Contains(z)) zodiacRows[z].HitContribution++;
    }

    string year = history[target].Period.Length >= 4 ? history[target].Period[..4] : "unknown";
    if (!yearCurrent.TryGetValue(year, out var cy)) { cy = new Metric(); yearCurrent[year] = cy; }
    if (!yearStrict3.TryGetValue(year, out var sy)) { sy = new Metric(); yearStrict3[year] = sy; }
    cy.Observe(currentRank, actual);
    sy.Observe(Rank(cells.Select(mechanisms["strict-cv-min3"]).ToArray()), actual);
}

// Earlier segment, outside the latest-1000 audit window. Diagnostic only: the project/model may have been developed with these data.
var pre1000 = new Metric();
double pre1000Expected = 0;
for (int target = Warmup; target < firstTarget; target++)
{
    var cells = ComputeCells(sequence, target, 50);
    double[] scores = cells.Select(mechanisms["current"]).ToArray();
    pre1000.Observe(Rank(scores), sequence[target]);
    pre1000Expected += ExpectedHit(scores, sequence[target], 6);
}
int pre1000Samples = Math.Max(0, firstTarget - Warmup);

// Fixed-window observed rates across every window we actually inspected.
var observedWindow = new Dictionary<int, double>();
foreach (int window in Windows)
    observedWindow[window] = EvaluateCurrentTop6(sequence, firstTarget, window);
double observedGlobalMax = observedWindow.Values.Max();
int observedBestWindow = observedWindow.OrderByDescending(x => x.Value).ThenBy(x => x.Key).First().Key;

// Full multiplicity correction across all 11 inspected windows. Also test neutral tie expectation for W50.
var nullGlobalMax = new List<double>(Permutations);
var nullW50Fixed = new List<double>(Permutations);
var nullW50Neutral = new List<double>(Permutations);
for (int p = 0; p < Permutations; p++)
{
    int[] shuffled = ShuffleWithinYear(history, sequence, 20260914 + p * 104729);
    double max = double.MinValue;
    foreach (int window in Windows)
    {
        double r = EvaluateCurrentTop6(shuffled, firstTarget, window);
        if (r > max) max = r;
    }
    nullGlobalMax.Add(max);
    nullW50Fixed.Add(EvaluateCurrentTop6(shuffled, firstTarget, 50));
    nullW50Neutral.Add(EvaluateCurrentExpectedTop6(shuffled, firstTarget, 50));
}

double observedW50Fixed = observedWindow[50];
double observedW50Neutral = EvaluateCurrentExpectedTop6(sequence, firstTarget, 50);
var permutation = new
{
    Replicates = Permutations,
    GlobalAcrossAll11 = new
    {
        ObservedBestWindow = observedBestWindow,
        ObservedMaxTop6 = observedGlobalMax,
        NullMeanMax = nullGlobalMax.Average(),
        Null95thMax = Quantile(nullGlobalMax, .95),
        AdjustedP = MonteCarloP(nullGlobalMax, observedGlobalMax)
    },
    Window50FixedTie = new
    {
        Observed = observedW50Fixed,
        NullMean = nullW50Fixed.Average(),
        Null95th = Quantile(nullW50Fixed, .95),
        P = MonteCarloP(nullW50Fixed, observedW50Fixed)
    },
    Window50NeutralTieExpectation = new
    {
        Observed = observedW50Neutral,
        NullMean = nullW50Neutral.Average(),
        Null95th = Quantile(nullW50Neutral, .95),
        P = MonteCarloP(nullW50Neutral, observedW50Neutral)
    }
};

var report = new
{
    GeneratedAtUtc = DateTime.UtcNow,
    Protocol = new
    {
        HistoryCount = history.Count,
        LatestAuditSamples = history.Count - firstTarget,
        FirstAuditIssue = history[firstTarget].Period,
        LastAuditIssue = history[^1].Period,
        Windows,
        Permutations,
        Null = "同一年份内部洗牌，保持年度生肖边际分布；全11窗口取每次置换的最大Top6用于多重比较校正。",
        Warning = "仍属于同一历史数据上的机制审计，不是新的独立未来样本。"
    },
    MechanismDecomposition = mechanismMetrics.Select(kv => new
    {
        Variant = kv.Key,
        Metric = kv.Value.ToResult(),
        NeutralTieExpectedTop6 = mechanismExpectedTop6[kv.Key] / Math.Max(1, history.Count - firstTarget)
    }).ToList(),
    SparseFallbackScoreSensitivity = fallbackMetrics.Select(kv => new
    {
        SparseScore = kv.Key,
        Metric = kv.Value.ToResult()
    }).OrderBy(x => x.SparseScore).ToList(),
    EarlierPre1000Segment = new
    {
        Samples = pre1000Samples,
        FirstIssue = pre1000Samples > 0 ? history[Warmup].Period : "",
        LastIssue = pre1000Samples > 0 ? history[firstTarget - 1].Period : "",
        CurrentFixedTie = pre1000.ToResult(),
        NeutralTieExpectedTop6 = pre1000Samples == 0 ? 0 : pre1000Expected / pre1000Samples,
        Caveat = "仅作时间稳定性诊断；不能视为真正独立，因为模型开发历史可能已接触这些数据。"
    },
    ByYear = yearCurrent.OrderBy(x => x.Key).Select(x => new
    {
        Year = x.Key,
        Current = x.Value.ToResult(),
        StrictCvMin3 = yearStrict3[x.Key].ToResult()
    }).ToList(),
    ZodiacSelectionBias = zodiacRows.Select(x => new
    {
        x.Zodiac,
        ActualRate = x.Periods == 0 ? 0 : x.ActualCount / (double)x.Periods,
        SelectedTop6Rate = x.Periods == 0 ? 0 : x.SelectedTop6Count / (double)x.Periods,
        x.HitContribution
    }).ToList(),
    ObservedWindowTop6 = observedWindow,
    Permutation = permutation
};

string docs = Path.Combine(repoRoot, "docs");
Directory.CreateDirectory(docs);
string jsonPath = Path.Combine(docs, "period-mechanism-report.json");
File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"REPORT={jsonPath}");

Cell[] ComputeCells(IReadOnlyList<int> seq, int target, int window)
{
    int start = Math.Max(0, target - window);
    int[] last = Enumerable.Repeat(-1, 12).ToArray();
    int[] n = new int[12];
    double[] sum = new double[12];
    double[] sumSq = new double[12];
    int local = 0;
    for (int i = start; i < target; i++, local++)
    {
        int z = seq[i];
        if (last[z] >= 0)
        {
            int d = local - last[z];
            n[z]++;
            sum[z] += d;
            sumSq[z] += d * d;
        }
        last[z] = local;
    }

    var cells = new Cell[12];
    for (int z = 0; z < 12; z++)
    {
        double avg = n[z] == 0 ? 0 : sum[z] / n[z];
        double variance = n[z] == 0 ? 0 : Math.Max(0, sumSq[z] / n[z] - avg * avg);
        double cv = avg > 0 ? Math.Sqrt(variance) / avg : 1;
        double regularity = Math.Max(0, 1 - cv);
        cells[z] = new Cell(n[z], avg, cv, regularity);
    }
    return cells;
}

int[] Rank(double[] scores)
{
    return Enumerable.Range(0, 12)
        .OrderByDescending(z => scores[z])
        .ThenBy(z => z)
        .ToArray();
}

double ExpectedHit(double[] scores, int actual, int k)
{
    double s = scores[actual];
    int greater = scores.Count(x => x > s + Eps);
    int equal = scores.Count(x => Math.Abs(x - s) <= Eps);
    if (greater >= k) return 0;
    if (greater + equal <= k) return 1;
    return Math.Clamp((k - greater) / (double)equal, 0, 1);
}

double EvaluateCurrentTop6(IReadOnlyList<int> seq, int startTarget, int window)
{
    int hits = 0;
    int n = 0;
    for (int target = startTarget; target < seq.Count; target++)
    {
        var cells = ComputeCells(seq, target, window);
        double[] scores = cells.Select(mechanisms["current"]).ToArray();
        if (Rank(scores).Take(6).Contains(seq[target])) hits++;
        n++;
    }
    return n == 0 ? 0 : hits / (double)n;
}

double EvaluateCurrentExpectedTop6(IReadOnlyList<int> seq, int startTarget, int window)
{
    double total = 0;
    int n = 0;
    for (int target = startTarget; target < seq.Count; target++)
    {
        var cells = ComputeCells(seq, target, window);
        double[] scores = cells.Select(mechanisms["current"]).ToArray();
        total += ExpectedHit(scores, seq[target], 6);
        n++;
    }
    return n == 0 ? 0 : total / n;
}

int[] ShuffleWithinYear(IReadOnlyList<DatabaseHelper.HistoryRecord> rows, IReadOnlyList<int> values, int seed)
{
    int[] output = values.ToArray();
    var rnd = new Random(seed);
    foreach (var group in rows.Select((r, i) => new { r.Period, Index = i })
        .GroupBy(x => x.Period.Length >= 4 ? x.Period[..4] : "unknown"))
    {
        int[] indices = group.Select(x => x.Index).ToArray();
        int[] bucket = indices.Select(i => output[i]).ToArray();
        for (int i = bucket.Length - 1; i > 0; i--)
        {
            int j = rnd.Next(i + 1);
            (bucket[i], bucket[j]) = (bucket[j], bucket[i]);
        }
        for (int i = 0; i < indices.Length; i++) output[indices[i]] = bucket[i];
    }
    return output;
}

double MonteCarloP(IEnumerable<double> nullValues, double observed)
{
    var a = nullValues.ToArray();
    return (1.0 + a.Count(x => x >= observed - Eps)) / (a.Length + 1.0);
}

double Quantile(IEnumerable<double> source, double q)
{
    var a = source.OrderBy(x => x).ToArray();
    if (a.Length == 0) return 0;
    double pos = Math.Clamp(q, 0, 1) * (a.Length - 1);
    int lo = (int)Math.Floor(pos);
    int hi = (int)Math.Ceiling(pos);
    if (lo == hi) return a[lo];
    double t = pos - lo;
    return a[lo] * (1 - t) + a[hi] * t;
}

sealed record Cell(int IntervalCount, double AverageInterval, double Cv, double Regularity);
sealed record MetricResult(int Samples, int Top3Hits, int Top6Hits, double Top3Rate, double Top6Rate, int MaxTop3Miss, int MaxTop6Miss);

sealed class Metric
{
    private int n, h3, h6, c3, c6, m3, m6;
    public void Observe(IReadOnlyList<int> ranking, int actual)
    {
        n++;
        bool x3 = ranking.Take(3).Contains(actual);
        bool x6 = ranking.Take(6).Contains(actual);
        if (x3) { h3++; c3 = 0; } else { c3++; m3 = Math.Max(m3, c3); }
        if (x6) { h6++; c6 = 0; } else { c6++; m6 = Math.Max(m6, c6); }
    }
    public MetricResult ToResult() => new(n, h3, h6, n == 0 ? 0 : h3 / (double)n, n == 0 ? 0 : h6 / (double)n, m3, m6);
}

sealed class ZodiacAudit(string zodiac)
{
    public string Zodiac { get; } = zodiac;
    public int Periods { get; set; }
    public int ActualCount { get; set; }
    public int SelectedTop6Count { get; set; }
    public int HitContribution { get; set; }
}
