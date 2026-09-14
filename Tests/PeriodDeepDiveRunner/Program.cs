using System.Text;
using System.Text.Json;
using 六合分析软件;

const int RequestedSamples = 1000;
const int Warmup = 120;
const int OuterBlockSize = 100;
const int InnerLookback = 300;
const int Permutations = 100;
const int BootstrapReplicates = 5000;
const int BootstrapBlockLength = 7;
const double Eps = 1e-12;

int[] windows = { 20, 30, 40, 45, 50, 55, 60, 70, 80, 100, 120 };
int[] primaryWindows = { 30, 40, 50, 60, 80 };
string[] ZodiacOrder =
{
    "鼠", "牛", "虎", "兔", "龙", "蛇",
    "马", "羊", "猴", "鸡", "狗", "猪"
};

string repoRoot = Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
string dataDir = Path.Combine(repoRoot, "data");
Environment.SetEnvironmentVariable("LIUHE_DATA_DIR", dataDir);
DatabaseHelper.InitializeDatabase();

var history = DatabaseHelper.GetHistory()
    .Where(x => !string.IsNullOrWhiteSpace(x.SpecialZodiac))
    .OrderBy(x => long.TryParse(x.Period, out long p) ? p : long.MaxValue)
    .ToList();

if (history.Count < Warmup + 100)
    throw new InvalidOperationException($"历史数据不足：{history.Count}");

int firstTarget = Math.Max(Warmup, history.Count - RequestedSamples);
int actualSamples = history.Count - firstTarget;
var series = history.Select(x => x.SpecialZodiac).ToList();

// target -> window -> snapshot. Every snapshot uses only [0,target).
var snapshots = new Dictionary<int, Dictionary<int, PeriodSnapshot>>();
foreach (int target in Enumerable.Range(Warmup, history.Count - Warmup))
{
    var byWindow = new Dictionary<int, PeriodSnapshot>();
    foreach (int window in windows)
        byWindow[window] = BuildSnapshot(series, target, window);
    snapshots[target] = byWindow;
}

var windowRows = new List<WindowRow>();
foreach (int window in windows)
{
    var fixedMetric = new Metric();
    var reverseTieMetric = new Metric();
    var inverseMetric = new Metric();
    double expectedTop3 = 0;
    double expectedTop6 = 0;
    int tie3 = 0;
    int tie6 = 0;
    int valid = 0;
    var blockMetrics = new List<Metric>();
    var yearMetrics = new Dictionary<string, Metric>(StringComparer.Ordinal);

    for (int target = firstTarget; target < history.Count; target++)
    {
        var s = snapshots[target][window];
        string actual = history[target].SpecialZodiac;
        fixedMetric.Observe(s.FixedRanking, actual);
        reverseTieMetric.Observe(s.ReverseTieRanking, actual);
        inverseMetric.Observe(s.InverseRanking, actual);
        expectedTop3 += ExpectedHit(s.Scores, actual, 3);
        expectedTop6 += ExpectedHit(s.Scores, actual, 6);
        if (s.Top3BoundaryTie) tie3++;
        if (s.Top6BoundaryTie) tie6++;
        valid++;

        int block = (target - firstTarget) / 100;
        while (blockMetrics.Count <= block) blockMetrics.Add(new Metric());
        blockMetrics[block].Observe(s.FixedRanking, actual);

        string year = history[target].Period.Length >= 4 ? history[target].Period[..4] : "unknown";
        if (!yearMetrics.TryGetValue(year, out var ym))
        {
            ym = new Metric();
            yearMetrics[year] = ym;
        }
        ym.Observe(s.FixedRanking, actual);
    }

    windowRows.Add(new WindowRow(
        window,
        fixedMetric.ToResult(),
        reverseTieMetric.ToResult(),
        inverseMetric.ToResult(),
        valid == 0 ? 0 : expectedTop3 / valid,
        valid == 0 ? 0 : expectedTop6 / valid,
        valid == 0 ? 0 : tie3 / (double)valid,
        valid == 0 ? 0 : tie6 / (double)valid,
        blockMetrics.Select((m, i) => new BlockRow(i + 1, m.ToResult())).ToList(),
        yearMetrics.OrderBy(x => x.Key).Select(x => new YearRow(x.Key, x.Value.ToResult())).ToList()));
}

// Explain observation sufficiency and ties specifically for the current 50-period signal.
var allIntervalBuckets = NewBuckets();
var winnerIntervalBuckets = NewBuckets();
var selectedTop6IntervalBuckets = NewBuckets();
int top6Selections = 0;
for (int target = firstTarget; target < history.Count; target++)
{
    var s = snapshots[target][50];
    string actual = history[target].SpecialZodiac;
    foreach (var cell in s.Cells.Values)
        AddBucket(allIntervalBuckets, cell.IntervalCount);

    AddBucket(winnerIntervalBuckets, s.Cells[actual].IntervalCount);
    foreach (string z in s.FixedRanking.Take(6))
    {
        AddBucket(selectedTop6IntervalBuckets, s.Cells[z].IntervalCount);
        top6Selections++;
    }
}

// Diagnostic alternatives: current regularity, phase-only, regularity x phase.
var phaseMetric = new Metric();
var regularityPhaseMetric = new Metric();
for (int target = firstTarget; target < history.Count; target++)
{
    var s = snapshots[target][50];
    string actual = history[target].SpecialZodiac;
    var phaseScores = s.Cells.ToDictionary(x => x.Key, x => x.Value.PhaseScore, StringComparer.Ordinal);
    var rpScores = s.Cells.ToDictionary(x => x.Key, x => x.Value.RegularityPhaseScore, StringComparer.Ordinal);
    phaseMetric.Observe(Rank(phaseScores, reverseTie: false, inverse: false), actual);
    regularityPhaseMetric.Observe(Rank(rpScores, reverseTie: false, inverse: false), actual);
}

// Nested adaptive-window test: choose among 30/40/50/60/80 only from previous <=300 targets.
var nestedMetric = new Metric();
var nestedFolds = new List<NestedWindowFold>();
for (int outerStart = firstTarget; outerStart < history.Count; outerStart += OuterBlockSize)
{
    int outerEnd = Math.Min(history.Count, outerStart + OuterBlockSize);
    int innerStart = Math.Max(Warmup, outerStart - InnerLookback);
    if (outerStart <= innerStart) continue;

    var candidates = new List<WindowCandidate>();
    foreach (int window in primaryWindows)
    {
        var m = EvaluateWindow(window, innerStart, outerStart);
        candidates.Add(new WindowCandidate(window, m.Top3Rate, m.Top6Rate, WilsonLower(m.Top6Hits, m.Samples), m.MaxTop6Miss));
    }

    var selected = candidates
        .OrderByDescending(x => x.Top6WilsonLower95)
        .ThenByDescending(x => x.Top6Rate)
        .ThenByDescending(x => x.Top3Rate)
        .ThenBy(x => x.MaxTop6Miss)
        .ThenBy(x => x.Window)
        .First();

    var outerMetric = new Metric();
    for (int target = outerStart; target < outerEnd; target++)
    {
        string actual = history[target].SpecialZodiac;
        var ranking = snapshots[target][selected.Window].FixedRanking;
        outerMetric.Observe(ranking, actual);
        nestedMetric.Observe(ranking, actual);
    }

    nestedFolds.Add(new NestedWindowFold(
        history[outerStart].Period,
        history[outerEnd - 1].Period,
        selected.Window,
        selected.Top6Rate,
        selected.Top6WilsonLower95,
        outerMetric.ToResult(),
        candidates));
}

// Block-bootstrap CI for current 50-period exact hit stream.
var hitSeries50 = new List<int>();
for (int target = firstTarget; target < history.Count; target++)
{
    var s = snapshots[target][50];
    hitSeries50.Add(s.FixedRanking.Take(6).Contains(history[target].SpecialZodiac) ? 1 : 0);
}
var bootstrap = BlockBootstrap(hitSeries50, BootstrapBlockLength, BootstrapReplicates, 20260914);

// Permutation null: shuffle zodiac outcomes within each calendar year, preserving yearly marginals.
// For each shuffled sequence evaluate the five predeclared primary windows and their maximum.
var observedPrimary = primaryWindows.ToDictionary(
    w => w,
    w => windowRows.First(x => x.Window == w).Fixed.Top6Rate);
double observedPrimaryMax = observedPrimary.Values.Max();
var nullPerWindow = primaryWindows.ToDictionary(w => w, _ => new List<double>());
var nullMax = new List<double>();
for (int p = 0; p < Permutations; p++)
{
    var shuffled = ShuffleWithinYear(history, series, 20260914 + p * 7919);
    double maxRate = 0;
    foreach (int window in primaryWindows)
    {
        double rate = EvaluateSequenceTop6(shuffled, firstTarget, window);
        nullPerWindow[window].Add(rate);
        if (rate > maxRate) maxRate = rate;
    }
    nullMax.Add(maxRate);
}

var permutationRows = primaryWindows.Select(w => new PermutationRow(
    w,
    observedPrimary[w],
    nullPerWindow[w].Average(),
    Quantile(nullPerWindow[w], 0.95),
    (1.0 + nullPerWindow[w].Count(x => x >= observedPrimary[w] - Eps)) / (Permutations + 1.0)))
    .ToList();

double familyAdjustedP = (1.0 + nullMax.Count(x => x >= observedPrimaryMax - Eps)) / (Permutations + 1.0);

var selectedWindowCounts = nestedFolds
    .GroupBy(x => x.SelectedWindow)
    .ToDictionary(x => x.Key.ToString(), x => x.Count(), StringComparer.Ordinal);

var report = new
{
    GeneratedAtUtc = DateTime.UtcNow,
    Protocol = new
    {
        HistoryCount = history.Count,
        RequestedSamples,
        ActualSamples = actualSamples,
        FirstIssue = history[firstTarget].Period,
        LastIssue = history[^1].Period,
        Formula = "intervalCount>=3: score=max(0,(1-CV)*100); intervalCount 1-2 => 40; 0 => 20",
        CurrentTieBreaker = "固定生肖顺序：鼠牛虎兔龙蛇马羊猴鸡狗猪",
        Windows = windows,
        PrimaryWindowFamily = primaryWindows,
        NestedWindowRule = "每100期外层块开始前，只用此前最多300期，在30/40/50/60/80中按Top6 Wilson下界选择窗口并锁死100期。",
        PermutationNull = "100次；只在同一年份内部打乱生肖，保持每年生肖边际分布；家族校正取5个主窗口中的最大Top6。",
        Bootstrap = $"Top6命中序列，循环块Bootstrap，块长{BootstrapBlockLength}，{BootstrapReplicates}次。",
        Warning = "窗口扫描、phase诊断均为探索性分析；本报告不能把同一1000期重新包装成独立证明。"
    },
    WindowSensitivity = windowRows,
    Window50ObservationSufficiency = new
    {
        AllZodiacCells = BucketReport(allIntervalBuckets),
        ActualWinnerCells = BucketReport(winnerIntervalBuckets),
        SelectedTop6Cells = BucketReport(selectedTop6IntervalBuckets),
        SelectedTop6CellsTotal = top6Selections
    },
    Window50AlternativeTimingDiagnostics = new
    {
        PhaseOnly = phaseMetric.ToResult(),
        RegularityTimesPhase = regularityPhaseMetric.ToResult(),
        Note = "探索性解释：用于判断54.9%来自纯规律性还是接近历史平均间隔的时点信息，不作为已验证模型。"
    },
    NestedAdaptiveWindow = new
    {
        Overall = nestedMetric.ToResult(),
        SelectedWindowCounts = selectedWindowCounts,
        Folds = nestedFolds
    },
    Window50BlockBootstrapTop6 = bootstrap,
    Permutation = new
    {
        Replicates = Permutations,
        PerWindow = permutationRows,
        ObservedPrimaryMax = observedPrimaryMax,
        NullMaxMean = nullMax.Average(),
        NullMax95th = Quantile(nullMax, 0.95),
        FamilyAdjustedP = familyAdjustedP
    }
};

string docs = Path.Combine(repoRoot, "docs");
Directory.CreateDirectory(docs);
string jsonPath = Path.Combine(docs, "period-deep-dive-report.json");
string mdPath = Path.Combine(docs, "period-deep-dive-report.md");
File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
File.WriteAllText(mdPath, BuildMarkdown(windowRows, nestedMetric.ToResult(), bootstrap, permutationRows, observedPrimaryMax, familyAdjustedP,
    allIntervalBuckets, winnerIntervalBuckets, selectedTop6IntervalBuckets, phaseMetric.ToResult(), regularityPhaseMetric.ToResult()));

Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"REPORT_JSON={jsonPath}");
Console.WriteLine($"REPORT_MD={mdPath}");

MetricResult EvaluateWindow(int window, int start, int end)
{
    var m = new Metric();
    for (int target = start; target < end; target++)
        m.Observe(snapshots[target][window].FixedRanking, history[target].SpecialZodiac);
    return m.ToResult();
}

PeriodSnapshot BuildSnapshot(IReadOnlyList<string> source, int target, int window)
{
    int start = Math.Max(0, target - window);
    var newestFirst = source.Skip(start).Take(target - start).Reverse().ToList();
    var cells = ZodiacOrder.ToDictionary(z => z, z => CalculateCell(z, newestFirst), StringComparer.Ordinal);
    var scores = cells.ToDictionary(x => x.Key, x => x.Value.PeriodScore, StringComparer.Ordinal);
    var fixedRanking = Rank(scores, reverseTie: false, inverse: false);
    var reverseRanking = Rank(scores, reverseTie: true, inverse: false);
    var inverseRanking = Rank(scores, reverseTie: false, inverse: true);
    var sortedValues = scores.Values.OrderByDescending(x => x).ToArray();
    bool tie3 = sortedValues.Length > 3 && Math.Abs(sortedValues[2] - sortedValues[3]) <= Eps;
    bool tie6 = sortedValues.Length > 6 && Math.Abs(sortedValues[5] - sortedValues[6]) <= Eps;
    return new PeriodSnapshot(cells, scores, fixedRanking, reverseRanking, inverseRanking, tie3, tie6);
}

PeriodCell CalculateCell(string zodiac, IReadOnlyList<string> newestFirst)
{
    var positions = new List<int>();
    for (int i = 0; i < newestFirst.Count; i++)
        if (newestFirst[i] == zodiac) positions.Add(i);

    var intervals = new List<int>();
    for (int i = 1; i < positions.Count; i++)
        intervals.Add(positions[i] - positions[i - 1]);

    double avg = intervals.Count > 0 ? intervals.Average() : 0;
    double cv = 1;
    double regularity = 0;
    double periodScore;
    if (intervals.Count >= 3)
    {
        double variance = intervals.Select(x => Math.Pow(x - avg, 2)).Average();
        cv = avg > 0 ? Math.Sqrt(variance) / avg : 1;
        regularity = Math.Max(0, 1 - cv);
        periodScore = regularity * 100;
    }
    else if (intervals.Count >= 1)
    {
        periodScore = 40;
    }
    else
    {
        periodScore = 20;
    }

    int currentOmission = positions.Count > 0 ? positions[0] : -1;
    double phase = intervals.Count >= 3 && avg > 0 && currentOmission >= 0
        ? Math.Max(0, 1 - Math.Abs((currentOmission + 1) - avg) / avg)
        : 0;
    return new PeriodCell(intervals.Count, currentOmission, avg, cv, periodScore, phase, regularity * phase);
}

List<string> Rank(Dictionary<string, double> scores, bool reverseTie, bool inverse)
{
    var q = ZodiacOrder.Select((z, i) => new { Zodiac = z, Index = i, Score = scores[z] });
    if (inverse)
        return q.OrderBy(x => x.Score).ThenBy(x => x.Index).Select(x => x.Zodiac).ToList();
    if (reverseTie)
        return q.OrderByDescending(x => x.Score).ThenByDescending(x => x.Index).Select(x => x.Zodiac).ToList();
    return q.OrderByDescending(x => x.Score).ThenBy(x => x.Index).Select(x => x.Zodiac).ToList();
}

double ExpectedHit(Dictionary<string, double> scores, string actual, int k)
{
    double actualScore = scores[actual];
    int greater = scores.Values.Count(x => x > actualScore + Eps);
    int equal = scores.Values.Count(x => Math.Abs(x - actualScore) <= Eps);
    if (greater >= k) return 0;
    if (greater + equal <= k) return 1;
    return Math.Clamp((k - greater) / (double)equal, 0, 1);
}

List<string> ShuffleWithinYear(IReadOnlyList<DatabaseHelper.HistoryRecord> rows, IReadOnlyList<string> values, int seed)
{
    var output = values.ToList();
    var rnd = new Random(seed);
    var groups = rows.Select((r, i) => new { r.Period, Index = i })
        .GroupBy(x => x.Period.Length >= 4 ? x.Period[..4] : "unknown");
    foreach (var g in groups)
    {
        var indices = g.Select(x => x.Index).ToList();
        var bucket = indices.Select(i => output[i]).ToList();
        for (int i = bucket.Count - 1; i > 0; i--)
        {
            int j = rnd.Next(i + 1);
            (bucket[i], bucket[j]) = (bucket[j], bucket[i]);
        }
        for (int i = 0; i < indices.Count; i++) output[indices[i]] = bucket[i];
    }
    return output;
}

double EvaluateSequenceTop6(IReadOnlyList<string> sequence, int startTarget, int window)
{
    int hits = 0;
    int n = 0;
    for (int target = startTarget; target < sequence.Count; target++)
    {
        var s = BuildSnapshot(sequence, target, window);
        if (s.FixedRanking.Take(6).Contains(sequence[target])) hits++;
        n++;
    }
    return n == 0 ? 0 : hits / (double)n;
}

BootstrapResult BlockBootstrap(IReadOnlyList<int> hits, int blockLength, int reps, int seed)
{
    if (hits.Count == 0) return new BootstrapResult(0, 0, 0, 0, blockLength, reps);
    var rnd = new Random(seed);
    var rates = new List<double>(reps);
    for (int r = 0; r < reps; r++)
    {
        int total = 0;
        int drawn = 0;
        while (drawn < hits.Count)
        {
            int start = rnd.Next(hits.Count);
            for (int b = 0; b < blockLength && drawn < hits.Count; b++)
            {
                total += hits[(start + b) % hits.Count];
                drawn++;
            }
        }
        rates.Add(total / (double)hits.Count);
    }
    double observed = hits.Average();
    return new BootstrapResult(observed, rates.Average(), Quantile(rates, 0.025), Quantile(rates, 0.975), blockLength, reps);
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

double WilsonLower(int hits, int n)
{
    if (n <= 0) return 0;
    const double z = 1.959963984540054;
    double p = hits / (double)n;
    double z2 = z * z;
    double denom = 1 + z2 / n;
    double center = p + z2 / (2 * n);
    double margin = z * Math.Sqrt((p * (1 - p) + z2 / (4 * n)) / n);
    return (center - margin) / denom;
}

Dictionary<string, int> NewBuckets() => new(StringComparer.Ordinal)
{
    ["0"] = 0, ["1"] = 0, ["2"] = 0, [">=3"] = 0
};

void AddBucket(Dictionary<string, int> buckets, int intervalCount)
{
    string key = intervalCount >= 3 ? ">=3" : intervalCount.ToString();
    buckets[key]++;
}

object BucketReport(Dictionary<string, int> buckets)
{
    int total = buckets.Values.Sum();
    return new
    {
        Total = total,
        Counts = buckets,
        Rates = buckets.ToDictionary(x => x.Key, x => total == 0 ? 0 : x.Value / (double)total, StringComparer.Ordinal)
    };
}

string BuildMarkdown(
    IReadOnlyList<WindowRow> rows,
    MetricResult nested,
    BootstrapResult bootstrap,
    IReadOnlyList<PermutationRow> permutation,
    double observedMax,
    double familyP,
    Dictionary<string, int> allBuckets,
    Dictionary<string, int> winnerBuckets,
    Dictionary<string, int> selectedBuckets,
    MetricResult phase,
    MetricResult regPhase)
{
    var sb = new StringBuilder();
    sb.AppendLine("# 50期周期信号深度审计");
    sb.AppendLine();
    sb.AppendLine("严格 Walk-Forward：每个目标期只使用此前历史。周期公式为 CV 规律性；八肖、趋势、遗漏等均不参与。");
    sb.AppendLine();
    sb.AppendLine("## 窗口敏感性与并列偏差");
    sb.AppendLine();
    sb.AppendLine("|窗口|Top3|Top6|反向并列Top6|随机并列期望Top6|反周期Top6|Top6边界并列率|最长Top6连错|");
    sb.AppendLine("|---:|---:|---:|---:|---:|---:|---:|---:|");
    foreach (var r in rows)
        sb.AppendLine($"|{r.Window}|{r.Fixed.Top3Rate:P1}|{r.Fixed.Top6Rate:P1}|{r.ReverseTie.Top6Rate:P1}|{r.ExpectedTop6:P1}|{r.Inverse.Top6Rate:P1}|{r.Top6BoundaryTieRate:P1}|{r.Fixed.MaxTop6Miss}|");

    var w50 = rows.First(x => x.Window == 50);
    sb.AppendLine();
    sb.AppendLine("## 50期重点");
    sb.AppendLine();
    sb.AppendLine($"- 固定顺序：Top3 {w50.Fixed.Top3Rate:P2}，Top6 {w50.Fixed.Top6Rate:P2}；反向并列 Top6 {w50.ReverseTie.Top6Rate:P2}；均匀随机打破并列的期望 Top6 {w50.ExpectedTop6:P2}。");
    sb.AppendLine($"- Top6 第6/7名边界并列率：{w50.Top6BoundaryTieRate:P2}；Top3 第3/4名边界并列率：{w50.Top3BoundaryTieRate:P2}。");
    sb.AppendLine($"- 反周期排序 Top6：{w50.Inverse.Top6Rate:P2}。Phase-only Top6：{phase.Top6Rate:P2}；规律性×Phase Top6：{regPhase.Top6Rate:P2}。");
    sb.AppendLine($"- Nested动态窗口 Top6：{nested.Top6Rate:P2}，最长连错 {nested.MaxTop6Miss}。");
    sb.AppendLine($"- 块Bootstrap 95%区间：{bootstrap.Lower95:P2} – {bootstrap.Upper95:P2}。");
    sb.AppendLine();
    sb.AppendLine("## 间隔样本充足度（50期）");
    sb.AppendLine();
    sb.AppendLine($"- 全部生肖单元：{JsonSerializer.Serialize(allBuckets)}");
    sb.AppendLine($"- 实际开奖生肖：{JsonSerializer.Serialize(winnerBuckets)}");
    sb.AppendLine($"- 被选Top6生肖：{JsonSerializer.Serialize(selectedBuckets)}");
    sb.AppendLine();
    sb.AppendLine("## 年内置换检验");
    sb.AppendLine();
    sb.AppendLine("|窗口|观察Top6|置换均值|置换95分位|名义p|");
    sb.AppendLine("|---:|---:|---:|---:|---:|");
    foreach (var p in permutation)
        sb.AppendLine($"|{p.Window}|{p.ObservedTop6:P2}|{p.NullMean:P2}|{p.Null95th:P2}|{p.NominalP:F4}|");
    sb.AppendLine();
    sb.AppendLine($"主窗口家族观察最大Top6={observedMax:P2}；针对30/40/50/60/80选择后的置换校正 p={familyP:F4}。");
    sb.AppendLine();
    sb.AppendLine("> 注意：本报告仍使用已经被我们观察过的1000期，因此是机制审计和稳健性检查，不是新的独立盲测证明。");
    return sb.ToString();
}

sealed record PeriodCell(int IntervalCount, int CurrentOmission, double AverageInterval, double Cv,
    double PeriodScore, double PhaseScore, double RegularityPhaseScore);
sealed record PeriodSnapshot(Dictionary<string, PeriodCell> Cells, Dictionary<string, double> Scores,
    List<string> FixedRanking, List<string> ReverseTieRanking, List<string> InverseRanking,
    bool Top3BoundaryTie, bool Top6BoundaryTie);
sealed record MetricResult(int Samples, int Top3Hits, int Top6Hits, double Top3Rate, double Top6Rate,
    int MaxTop3Miss, int MaxTop6Miss);
sealed record BlockRow(int Block, MetricResult Metric);
sealed record YearRow(string Year, MetricResult Metric);
sealed record WindowRow(int Window, MetricResult Fixed, MetricResult ReverseTie, MetricResult Inverse,
    double ExpectedTop3, double ExpectedTop6, double Top3BoundaryTieRate, double Top6BoundaryTieRate,
    List<BlockRow> Blocks, List<YearRow> Years);
sealed record WindowCandidate(int Window, double Top3Rate, double Top6Rate, double Top6WilsonLower95, int MaxTop6Miss);
sealed record NestedWindowFold(string StartIssue, string EndIssue, int SelectedWindow,
    double InnerTop6, double InnerTop6WilsonLower95, MetricResult Outer, List<WindowCandidate> Candidates);
sealed record BootstrapResult(double Observed, double BootstrapMean, double Lower95, double Upper95, int BlockLength, int Replicates);
sealed record PermutationRow(int Window, double ObservedTop6, double NullMean, double Null95th, double NominalP);

sealed class Metric
{
    private int samples;
    private int top3Hits;
    private int top6Hits;
    private int current3Miss;
    private int current6Miss;
    private int max3Miss;
    private int max6Miss;

    public void Observe(IReadOnlyList<string> ranking, string actual)
    {
        samples++;
        bool h3 = ranking.Take(3).Contains(actual);
        bool h6 = ranking.Take(6).Contains(actual);
        if (h3) { top3Hits++; current3Miss = 0; }
        else { current3Miss++; max3Miss = Math.Max(max3Miss, current3Miss); }
        if (h6) { top6Hits++; current6Miss = 0; }
        else { current6Miss++; max6Miss = Math.Max(max6Miss, current6Miss); }
    }

    public MetricResult ToResult() => new(
        samples,
        top3Hits,
        top6Hits,
        samples == 0 ? 0 : top3Hits / (double)samples,
        samples == 0 ? 0 : top6Hits / (double)samples,
        max3Miss,
        max6Miss);
}
