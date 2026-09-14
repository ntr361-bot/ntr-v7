using System.Text.Json;
using 六合分析软件;

const int Warmup = 100;
const int RequestedOuterSamples = 1000;
const int InnerLookback = 300;
const int OuterBlockSize = 100;

string[] ZodiacOrder =
{
    "鼠", "牛", "虎", "兔", "龙", "蛇",
    "马", "羊", "猴", "鸡", "狗", "猪"
};

var candidates = new Dictionary<string, (double Period, double Cycle, double Omission)>
{
    ["period50"] = (1, 0, 0),
    ["cycle20"] = (0, 1, 0),
    ["omission-all"] = (0, 0, 1),
    ["period+cycle"] = (0.5, 0.5, 0),
    ["period+omission"] = (0.5, 0, 0.5),
    ["cycle+omission"] = (0, 0.5, 0.5),
    ["period+cycle+omission"] = (1.0 / 3, 1.0 / 3, 1.0 / 3)
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
var snapshots = new Dictionary<int, Snapshot>();

// 每个 target 的所有特征都只能使用 history[0..target)，target 本身只作为开奖答案。
for (int target = Warmup; target < history.Count; target++)
{
    var prefix = history.Take(target).ToList();

    var period50Result = engine.Predict(
        prefix,
        50,
        V65ExperimentPipeline.GetWeightsForPeriods(50));

    var allResult = engine.Predict(
        prefix,
        AISettings.AllHistoryModeValue,
        V65ExperimentPipeline.GetWeightsForPeriods(AISettings.AllHistoryModeValue));

    var features = FeatureEngine.BuildFeatures(prefix, 0);

    // 只读取原始维度，不使用 TotalScore，因此八肖加分不会进入本研究。
    var periodRaw = period50Result.AllScores
        .ToDictionary(x => x.Zodiac, x => x.PeriodPatternScore);
    var omissionRaw = allResult.AllScores
        .ToDictionary(x => x.Zodiac, x => x.OmissionScore);
    var cycleRaw = features
        .ToDictionary(x => x.Zodiac, x => (double)x.ShortCycleRepeatCount);

    // 三类信号量纲不同；按当期12生肖内部做 Min-Max，避免数值尺度冒充权重。
    var period = MinMax(periodRaw);
    var cycle = MinMax(cycleRaw);
    var omission = MinMax(omissionRaw);

    var rankings = new Dictionary<string, List<string>>(StringComparer.Ordinal);

    foreach (var candidate in candidates)
    {
        var weights = candidate.Value;
        rankings[candidate.Key] = ZodiacOrder
            .Select((z, index) => new
            {
                Zodiac = z,
                Index = index,
                Score = period[z] * weights.Period +
                        cycle[z] * weights.Cycle +
                        omission[z] * weights.Omission
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Index)
            .Select(x => x.Zodiac)
            .ToList();
    }

    snapshots[target] = new Snapshot(
        target,
        history[target].Period,
        history[target].SpecialZodiac,
        rankings);
}

var folds = new List<FoldResult>();
var finalMetric = new Metric();

// 外层每100期一个完全未知测试块；候选只允许在它之前最多300期内竞争。
for (int outerStart = firstOuterTarget;
     outerStart < history.Count;
     outerStart += OuterBlockSize)
{
    int outerEnd = Math.Min(history.Count, outerStart + OuterBlockSize);
    int innerStart = Math.Max(Warmup, outerStart - InnerLookback);
    if (outerStart <= innerStart) continue;

    var innerScores = new List<CandidateResult>();
    foreach (string candidate in candidates.Keys)
    {
        Metric metric = Evaluate(candidate, innerStart, outerStart);
        innerScores.Add(new CandidateResult(
            candidate,
            metric.Samples,
            metric.Top3Rate,
            metric.Top6Rate,
            metric.MaxTop3Miss,
            metric.MaxTop6Miss));
    }

    // 选择标准固定：Top6 > Top3 > Top6最长连错。外层结果绝不参与选择。
    CandidateResult selected = innerScores
        .OrderByDescending(x => x.Top6Rate)
        .ThenByDescending(x => x.Top3Rate)
        .ThenBy(x => x.MaxTop6Miss)
        .ThenBy(x => x.Name, StringComparer.Ordinal)
        .First();

    Metric outerMetric = Evaluate(selected.Name, outerStart, outerEnd);

    for (int target = outerStart; target < outerEnd; target++)
    {
        Snapshot shot = snapshots[target];
        finalMetric.Observe(shot.Rankings[selected.Name], shot.Actual);
    }

    folds.Add(new FoldResult(
        history[outerStart].Period,
        history[outerEnd - 1].Period,
        innerStart,
        outerStart - 1,
        selected.Name,
        selected.Top3Rate,
        selected.Top6Rate,
        outerMetric.Top3Rate,
        outerMetric.Top6Rate,
        outerMetric.MaxTop3Miss,
        outerMetric.MaxTop6Miss,
        innerScores));
}

// 固定候选的1000期结果只作诊断；真正需要看的主结果是 NestedSelected。
var fixedDiagnostics = candidates.Keys.ToDictionary(
    name => name,
    name =>
    {
        Metric m = Evaluate(name, firstOuterTarget, history.Count);
        return new
        {
            m.Samples,
            m.Top3Rate,
            m.Top6Rate,
            m.MaxTop3Miss,
            m.MaxTop6Miss
        };
    },
    StringComparer.Ordinal);

var selectedCounts = folds
    .GroupBy(x => x.SelectedModel)
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
        Rule = "每个外层测试块开始前，只使用此前历史选择候选；选择后锁死，禁止查看外层结果重新调参。",
        Selection = "Top6优先，其次Top3，其次最大Top6连错。",
        Signals = new[] { "50期周期", "20期短周期重复", "全部历史遗漏" },
        Disabled = new[]
        {
            "八肖",
            "Trend近期走势",
            "ShortForbidden五期硬过滤",
            "Consecutive关联",
            "自动权重搜索"
        }
    },
    HistoryCount = history.Count,
    FirstOuterIssue = history[firstOuterTarget].Period,
    LastOuterIssue = history[^1].Period,
    OuterSamples = finalMetric.Samples,
    NestedSelected = new
    {
        finalMetric.Top3Rate,
        finalMetric.Top6Rate,
        finalMetric.MaxTop3Miss,
        finalMetric.MaxTop6Miss
    },
    SelectedCounts = selectedCounts,
    Folds = folds,
    FixedCandidateDiagnostics = fixedDiagnostics
};

string docs = Path.Combine(repoRoot, "docs");
Directory.CreateDirectory(docs);
string jsonPath = Path.Combine(docs, "nested-walkforward-report.json");
File.WriteAllText(jsonPath, JsonSerializer.Serialize(report,
    new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine(JsonSerializer.Serialize(report,
    new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine();
Console.WriteLine($"Nested Top3 = {finalMetric.Top3Rate:P2}");
Console.WriteLine($"Nested Top6 = {finalMetric.Top6Rate:P2}");
Console.WriteLine($"Top6最大连错 = {finalMetric.MaxTop6Miss}");
Console.WriteLine($"REPORT={jsonPath}");

Metric Evaluate(string candidate, int start, int end)
{
    var metric = new Metric();
    for (int target = start; target < end; target++)
    {
        if (!snapshots.TryGetValue(target, out Snapshot? shot))
            continue;
        metric.Observe(shot.Rankings[candidate], shot.Actual);
    }
    return metric;
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

sealed record Snapshot(
    int Index,
    string Issue,
    string Actual,
    Dictionary<string, List<string>> Rankings);

sealed record CandidateResult(
    string Name,
    int Samples,
    double Top3Rate,
    double Top6Rate,
    int MaxTop3Miss,
    int MaxTop6Miss);

sealed record FoldResult(
    string StartIssue,
    string EndIssue,
    int InnerStartIndex,
    int InnerEndIndex,
    string SelectedModel,
    double InnerTop3,
    double InnerTop6,
    double OuterTop3,
    double OuterTop6,
    int OuterMaxTop3Miss,
    int OuterMaxTop6Miss,
    List<CandidateResult> InnerCandidates);

sealed class Metric
{
    private int top3Hits;
    private int top6Hits;
    private int currentTop3Miss;
    private int currentTop6Miss;

    public int Samples { get; private set; }
    public int MaxTop3Miss { get; private set; }
    public int MaxTop6Miss { get; private set; }

    public double Top3Rate => Samples == 0 ? 0 : top3Hits / (double)Samples;
    public double Top6Rate => Samples == 0 ? 0 : top6Hits / (double)Samples;

    public void Observe(IReadOnlyList<string> ranking, string actual)
    {
        Samples++;

        bool hit3 = ranking.Take(3).Contains(actual);
        bool hit6 = ranking.Take(6).Contains(actual);

        if (hit3)
        {
            top3Hits++;
            currentTop3Miss = 0;
        }
        else
        {
            currentTop3Miss++;
            MaxTop3Miss = Math.Max(MaxTop3Miss, currentTop3Miss);
        }

        if (hit6)
        {
            top6Hits++;
            currentTop6Miss = 0;
        }
        else
        {
            currentTop6Miss++;
            MaxTop6Miss = Math.Max(MaxTop6Miss, currentTop6Miss);
        }
    }
}
