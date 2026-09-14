using System.Text.Json;
using 六合分析软件;

const int Warmup = 100;
const int RequestedOuterSamples = 1000;
const int InnerLookback = 300;
const int OuterBlockSize = 100;
const double MinimumInnerCoverage = 0.30;

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

int firstOuterTarget = Math.Max(Warmup, history.Count - RequestedOuterSamples);
var engine = new V65RuleScoringEngine();
var states = new Dictionary<int, GateState>();

// Gate 只允许使用开奖前可见状态；底层排名固定为 50期周期单信号。
for (int target = Warmup; target < history.Count; target++)
{
    var prefix = history.Take(target).ToList();

    var periodResult = engine.Predict(
        prefix,
        50,
        V65ExperimentPipeline.GetWeightsForPeriods(50));

    var omissionResult = engine.Predict(
        prefix,
        AISettings.AllHistoryModeValue,
        V65ExperimentPipeline.GetWeightsForPeriods(AISettings.AllHistoryModeValue));

    var features = FeatureEngine.BuildFeatures(prefix, 0).ToList();

    var periodRaw = periodResult.AllScores
        .ToDictionary(x => x.Zodiac, x => x.PeriodPatternScore, StringComparer.Ordinal);
    var omissionRaw = omissionResult.AllScores
        .ToDictionary(x => x.Zodiac, x => x.OmissionScore, StringComparer.Ordinal);
    var cycleRaw = features
        .ToDictionary(x => x.Zodiac, x => (double)x.ShortCycleRepeatCount, StringComparer.Ordinal);

    var period = MinMax(periodRaw);
    var omission = MinMax(omissionRaw);
    var cycle = MinMax(cycleRaw);

    var periodRanking = Rank(period);
    var cycleRanking = Rank(cycle);
    var omissionRanking = Rank(omission);

    var combinedScores = ZodiacOrder.ToDictionary(
        z => z,
        z => (period[z] + cycle[z] + omission[z]) / 3.0,
        StringComparer.Ordinal);

    double consensus = AverageTop6Jaccard(periodRanking, cycleRanking, omissionRanking);
    double periodMargin6 = BoundaryMargin(periodRanking, period);
    double combinedMargin6 = BoundaryMargin(Rank(combinedScores), combinedScores);
    int maxCycleRaw = cycleRaw.Values.Count == 0 ? 0 : (int)cycleRaw.Values.Max();

    states[target] = new GateState(
        target,
        history[target].Period,
        history[target].SpecialZodiac,
        periodRanking,
        consensus,
        periodMargin6,
        combinedMargin6,
        maxCycleRaw);
}

var gates = new List<GateDef>
{
    new("always", _ => true),
    new("consensus>=0.55", s => s.Consensus >= 0.55),
    new("consensus>=0.65", s => s.Consensus >= 0.65),
    new("period-margin>=0.05", s => s.PeriodMargin6 >= 0.05),
    new("period-margin>=0.10", s => s.PeriodMargin6 >= 0.10),
    new("combined-margin>=0.05", s => s.CombinedMargin6 >= 0.05),
    new("combined-margin>=0.10", s => s.CombinedMargin6 >= 0.10),
    new("consensus55+period05", s => s.Consensus >= 0.55 && s.PeriodMargin6 >= 0.05),
    new("consensus55+combined05", s => s.Consensus >= 0.55 && s.CombinedMargin6 >= 0.05),
    new("cycle-active>=2", s => s.MaxCycleRaw >= 2),
    new("cycle-active>=3", s => s.MaxCycleRaw >= 3)
};

var finalNested = new GateMetric();
var folds = new List<FoldResult>();

for (int outerStart = firstOuterTarget;
     outerStart < history.Count;
     outerStart += OuterBlockSize)
{
    int outerEnd = Math.Min(history.Count, outerStart + OuterBlockSize);
    int innerStart = Math.Max(Warmup, outerStart - InnerLookback);
    if (outerStart <= innerStart) continue;

    var innerResults = gates
        .Select(g => Evaluate(g, innerStart, outerStart))
        .ToList();

    // Gate 只能从过去数据中选择。最低覆盖30%，再按Top6的Wilson 95%下界排序，避免少量样本虚高。
    var eligible = innerResults
        .Where(x => x.Coverage >= MinimumInnerCoverage)
        .ToList();

    GateResult selected = (eligible.Count > 0 ? eligible : innerResults.Where(x => x.Name == "always").ToList())
        .OrderByDescending(x => x.Top6WilsonLower95)
        .ThenByDescending(x => x.Top6Rate)
        .ThenByDescending(x => x.Coverage)
        .ThenBy(x => x.Name, StringComparer.Ordinal)
        .First();

    GateDef selectedGate = gates.First(x => x.Name == selected.Name);
    GateResult outerResult = Evaluate(selectedGate, outerStart, outerEnd);

    for (int target = outerStart; target < outerEnd; target++)
    {
        GateState state = states[target];
        finalNested.Observe(state, selectedGate.Predicate(state));
    }

    folds.Add(new FoldResult(
        history[outerStart].Period,
        history[outerEnd - 1].Period,
        selected.Name,
        selected.Coverage,
        selected.Top6Rate,
        selected.Top6WilsonLower95,
        outerResult.Coverage,
        outerResult.Top3Rate,
        outerResult.Top6Rate,
        outerResult.SkippedTop6Rate,
        outerResult.MaxBetTop6Miss,
        innerResults));
}

var fixedDiagnostics = gates.ToDictionary(
    g => g.Name,
    g => Evaluate(g, firstOuterTarget, history.Count),
    StringComparer.Ordinal);

var nestedResult = finalNested.ToResult("nested-selected-gate");
var alwaysResult = fixedDiagnostics["always"];

var selectedCounts = folds
    .GroupBy(x => x.SelectedGate)
    .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);

var report = new
{
    GeneratedAtUtc = DateTime.UtcNow,
    Protocol = new
    {
        Warmup,
        RequestedOuterSamples,
        InnerLookback,
        OuterBlockSize,
        MinimumInnerCoverage,
        BaseModel = "50期周期单信号，固定不调权重",
        Rule = "每个100期外层块之前，只用此前最多300期选择Gate；Gate选定后锁死到该外层块结束。",
        Selection = "覆盖率至少30%，按过去Top6命中率的Wilson 95%下界优先，避免低样本虚高。",
        GateInputs = new[]
        {
            "三信号Top6平均Jaccard一致度",
            "50期周期第6名-第7名归一化分差",
            "三信号等权第6名-第7名归一化分差",
            "短周期重复活跃度"
        }
    },
    HistoryCount = history.Count,
    FirstOuterIssue = history[firstOuterTarget].Period,
    LastOuterIssue = history[^1].Period,
    BaselineAlways = alwaysResult,
    NestedGate = nestedResult,
    LiftVsAlwaysTop6 = nestedResult.Top6Rate - alwaysResult.Top6Rate,
    CoverageChange = nestedResult.Coverage - alwaysResult.Coverage,
    SelectedCounts = selectedCounts,
    Folds = folds,
    FixedGateDiagnostics = fixedDiagnostics
};

string docs = Path.Combine(repoRoot, "docs");
Directory.CreateDirectory(docs);
string jsonPath = Path.Combine(docs, "regime-gate-report.json");
File.WriteAllText(jsonPath, JsonSerializer.Serialize(report,
    new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine(JsonSerializer.Serialize(report,
    new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine();
Console.WriteLine($"Always Top6 = {alwaysResult.Top6Rate:P2}");
Console.WriteLine($"Nested Gate coverage = {nestedResult.Coverage:P2}");
Console.WriteLine($"Nested Gate Top6 = {nestedResult.Top6Rate:P2}");
Console.WriteLine($"Skipped-period base Top6 = {nestedResult.SkippedTop6Rate:P2}");
Console.WriteLine($"Nested Gate Top6 max bet miss = {nestedResult.MaxBetTop6Miss}");
Console.WriteLine($"REPORT={jsonPath}");

GateResult Evaluate(GateDef gate, int start, int end)
{
    var metric = new GateMetric();
    for (int target = start; target < end; target++)
    {
        if (!states.TryGetValue(target, out GateState? state)) continue;
        metric.Observe(state, gate.Predicate(state));
    }
    return metric.ToResult(gate.Name);
}

List<string> Rank(Dictionary<string, double> scores)
{
    return ZodiacOrder
        .Select((z, i) => new { Zodiac = z, Index = i, Score = scores[z] })
        .OrderByDescending(x => x.Score)
        .ThenBy(x => x.Index)
        .Select(x => x.Zodiac)
        .ToList();
}

static Dictionary<string, double> MinMax(Dictionary<string, double> source)
{
    double min = source.Values.Min();
    double max = source.Values.Max();
    if (Math.Abs(max - min) < 1e-12)
        return source.Keys.ToDictionary(x => x, _ => 0.5, StringComparer.Ordinal);

    return source.ToDictionary(
        x => x.Key,
        x => (x.Value - min) / (max - min),
        StringComparer.Ordinal);
}

static double BoundaryMargin(IReadOnlyList<string> ranking, Dictionary<string, double> scores)
{
    if (ranking.Count < 7) return 0;
    return Math.Max(0, scores[ranking[5]] - scores[ranking[6]]);
}

static double AverageTop6Jaccard(params IReadOnlyList<string>[] rankings)
{
    var sets = rankings.Select(r => r.Take(6).ToHashSet(StringComparer.Ordinal)).ToArray();
    double total = 0;
    int pairs = 0;
    for (int i = 0; i < sets.Length; i++)
    {
        for (int j = i + 1; j < sets.Length; j++)
        {
            int intersection = sets[i].Intersect(sets[j]).Count();
            int union = sets[i].Union(sets[j]).Count();
            total += union == 0 ? 0 : intersection / (double)union;
            pairs++;
        }
    }
    return pairs == 0 ? 0 : total / pairs;
}

static double WilsonLower95(int hits, int n)
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

sealed record GateState(
    int Index,
    string Issue,
    string Actual,
    List<string> BaseRanking,
    double Consensus,
    double PeriodMargin6,
    double CombinedMargin6,
    int MaxCycleRaw);

sealed record GateDef(string Name, Func<GateState, bool> Predicate);

sealed record GateResult(
    string Name,
    int Periods,
    int Bets,
    double Coverage,
    double Top3Rate,
    double Top6Rate,
    double Top6WilsonLower95,
    int SkippedPeriods,
    double SkippedTop6Rate,
    int MaxBetTop3Miss,
    int MaxBetTop6Miss);

sealed record FoldResult(
    string StartIssue,
    string EndIssue,
    string SelectedGate,
    double InnerCoverage,
    double InnerTop6,
    double InnerWilsonLower95,
    double OuterCoverage,
    double OuterTop3,
    double OuterTop6,
    double OuterSkippedTop6,
    int OuterMaxBetTop6Miss,
    List<GateResult> InnerGates);

sealed class GateMetric
{
    private int periods;
    private int bets;
    private int top3Hits;
    private int top6Hits;
    private int skipped;
    private int skippedTop6Hits;
    private int currentBetTop3Miss;
    private int currentBetTop6Miss;
    private int maxBetTop3Miss;
    private int maxBetTop6Miss;

    public void Observe(GateState state, bool bet)
    {
        periods++;
        bool hit3 = state.BaseRanking.Take(3).Contains(state.Actual);
        bool hit6 = state.BaseRanking.Take(6).Contains(state.Actual);

        if (!bet)
        {
            skipped++;
            if (hit6) skippedTop6Hits++;
            return;
        }

        bets++;
        if (hit3)
        {
            top3Hits++;
            currentBetTop3Miss = 0;
        }
        else
        {
            currentBetTop3Miss++;
            maxBetTop3Miss = Math.Max(maxBetTop3Miss, currentBetTop3Miss);
        }

        if (hit6)
        {
            top6Hits++;
            currentBetTop6Miss = 0;
        }
        else
        {
            currentBetTop6Miss++;
            maxBetTop6Miss = Math.Max(maxBetTop6Miss, currentBetTop6Miss);
        }
    }

    public GateResult ToResult(string name)
    {
        double top3 = bets == 0 ? 0 : top3Hits / (double)bets;
        double top6 = bets == 0 ? 0 : top6Hits / (double)bets;
        return new GateResult(
            name,
            periods,
            bets,
            periods == 0 ? 0 : bets / (double)periods,
            top3,
            top6,
            WilsonLower95(top6Hits, bets),
            skipped,
            skipped == 0 ? 0 : skippedTop6Hits / (double)skipped,
            maxBetTop3Miss,
            maxBetTop6Miss);
    }
}
