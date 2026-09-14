using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using 六合分析软件;

string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
string historyPath = Path.Combine(repoRoot, "site", "data", "history.json");
string[] zodiacOrder = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
const int window = 50;
const int warmup = 120;
const int targetSamples = 1000;

var payload = JsonSerializer.Deserialize<HistoryPayload>(File.ReadAllText(historyPath), new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true
}) ?? throw new InvalidOperationException("无法读取site/data/history.json");

var history = payload.Records
    .Where(x => !string.IsNullOrWhiteSpace(x.SpecialZodiac) && DateTime.TryParse(x.Date, out _))
    .OrderBy(x => long.TryParse(x.Issue, out long p) ? p : long.MaxValue)
    .ToList();

if (history.Count <= warmup) throw new InvalidOperationException($"历史不足：{history.Count}");
int firstTarget = Math.Max(warmup, history.Count - targetSamples);

var latest = Evaluate(firstTarget, history.Count);
var earlier = firstTarget > warmup ? Evaluate(warmup, firstTarget) : null;

var report = new
{
    GeneratedAtUtc = DateTime.UtcNow,
    Protocol = new
    {
        HistoryCount = history.Count,
        Window = window,
        Rule = "逐期按开奖日期计算农历年生肖号码表。本命生肖5个号码，其余11肖4个号码。Top6理论随机覆盖率按模型当期各生肖进入Top6的概率加权计算，不再固定用50%。",
        NeutralTie = "同分边界按均匀随机分配：每个生肖计算进入Top6概率，再同时用于观察命中期望和理论号码覆盖率。",
        Candidates = new[] { "regularity50", "clean50", "old-period50" },
        Warning = "这是对既有候选的基准校正，不是新独立样本。"
    },
    Latest1000 = latest,
    EarlierSegment = earlier
};

string outDir = Path.Combine(repoRoot, "docs");
Directory.CreateDirectory(outDir);
string jsonPath = Path.Combine(outDir, "theoretical-baseline-audit.json");
string mdPath = Path.Combine(outDir, "theoretical-baseline-audit.md");
File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
File.WriteAllText(mdPath, BuildMarkdown(latest, earlier));
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"REPORT_JSON={jsonPath}");
Console.WriteLine($"REPORT_MD={mdPath}");

RangeAudit Evaluate(int start, int end)
{
    string[] names = { "regularity50", "clean50", "old-period50" };
    var stats = names.ToDictionary(x => x, _ => new Accumulator());
    var yearStats = names.ToDictionary(x => x, _ => new Dictionary<string, Accumulator>());

    for (int target = start; target < end; target++)
    {
        string actual = history[target].SpecialZodiac;
        DateTime date = DateTime.Parse(history[target].Date);
        int lunarYear = V65MappingService.GetLunarYear(date);
        string yearZodiac = V65MappingService.GetYearZodiac(lunarYear);
        var numberMap = V65MappingService.GetZodiacNumberMap(lunarYear);

        var scoreSets = new Dictionary<string, Dictionary<string, double>>
        {
            ["regularity50"] = BuildCleanScores(target, false, true, false),
            ["clean50"] = BuildCleanScores(target, true, true, true),
            ["old-period50"] = BuildOldPeriodScores(target)
        };

        foreach (var kv in scoreSets)
        {
            var probs = SelectionProbabilities(kv.Value, 6);
            double observed = probs[actual];
            double theoretical = zodiacOrder.Sum(z => probs[z] * numberMap[z].Count / 49.0);
            bool includesYearZodiacHard = RankWithNeutralHash(kv.Value, history[target].Issue).Take(6).Contains(yearZodiac);
            stats[kv.Key].Add(observed, theoretical, includesYearZodiacHard);

            string year = date.Year.ToString();
            if (!yearStats[kv.Key].TryGetValue(year, out var acc))
            {
                acc = new Accumulator();
                yearStats[kv.Key][year] = acc;
            }
            acc.Add(observed, theoretical, includesYearZodiacHard);
        }
    }

    return new RangeAudit(
        end - start,
        history[start].Issue,
        history[end - 1].Issue,
        stats.ToDictionary(x => x.Key, x => x.Value.ToRow()),
        yearStats.ToDictionary(
            x => x.Key,
            x => x.Value.ToDictionary(y => y.Key, y => y.Value.ToRow())));
}

Dictionary<string, double> BuildCleanScores(int target, bool useScarcity, bool useRegularity, bool useBalance)
{
    int from = Math.Max(0, target - window);
    var slice = history.Skip(from).Take(target - from).Select(x => x.SpecialZodiac).ToList();
    var scarcityRaw = new Dictionary<string, double>();
    var regularityRaw = new Dictionary<string, double>();
    var balanceRaw = new Dictionary<string, double>();

    foreach (string zodiac in zodiacOrder)
    {
        var positions = slice.Select((z, i) => (Zodiac: z, Index: i))
            .Where(x => x.Zodiac == zodiac).Select(x => x.Index).ToList();
        int count = positions.Count;
        scarcityRaw[zodiac] = -count;

        var gaps = new List<int>();
        for (int i = 1; i < positions.Count; i++) gaps.Add(positions[i] - positions[i - 1]);
        if (gaps.Count >= 2)
        {
            double avg = gaps.Average();
            double variance = gaps.Select(g => Math.Pow(g - avg, 2)).Average();
            double cv = avg > 0 ? Math.Sqrt(variance) / avg : 1.0;
            regularityRaw[zodiac] = 1.0 / (1.0 + cv);
        }
        else regularityRaw[zodiac] = 0.5;

        int split = slice.Count / 2;
        int older = positions.Count(p => p < split);
        int newer = count - older;
        balanceRaw[zodiac] = count == 0 ? 0.5 : 1.0 - Math.Abs(newer - older) / (double)count;
    }

    var sr = MidRankPercentile(scarcityRaw);
    var rr = MidRankPercentile(regularityRaw);
    var br = MidRankPercentile(balanceRaw);
    int enabled = (useScarcity ? 1 : 0) + (useRegularity ? 1 : 0) + (useBalance ? 1 : 0);
    var result = new Dictionary<string, double>();
    foreach (string zodiac in zodiacOrder)
    {
        double sum = 0;
        if (useScarcity) sum += sr[zodiac];
        if (useRegularity) sum += rr[zodiac];
        if (useBalance) sum += br[zodiac];
        result[zodiac] = sum / enabled;
    }
    return result;
}

Dictionary<string, double> BuildOldPeriodScores(int target)
{
    int from = Math.Max(0, target - window);
    var newestFirst = history.Skip(from).Take(target - from).Select(x => x.SpecialZodiac).Reverse().ToList();
    var result = new Dictionary<string, double>();
    foreach (string zodiac in zodiacOrder)
    {
        var gaps = new List<int>();
        int last = -1;
        for (int i = 0; i < newestFirst.Count; i++)
        {
            if (newestFirst[i] != zodiac) continue;
            if (last >= 0) gaps.Add(i - last);
            last = i;
        }
        if (gaps.Count >= 3)
        {
            double avg = gaps.Average();
            double variance = gaps.Select(g => Math.Pow(g - avg, 2)).Average();
            double cv = avg > 0 ? Math.Sqrt(variance) / avg : 1.0;
            result[zodiac] = Math.Max(0, (1 - cv) * 100);
        }
        else if (gaps.Count >= 1) result[zodiac] = 40;
        else result[zodiac] = 20;
    }
    return result;
}

Dictionary<string, double> MidRankPercentile(Dictionary<string, double> raw)
{
    int n = raw.Count;
    return raw.ToDictionary(kv => kv.Key, kv =>
    {
        int less = raw.Values.Count(v => v < kv.Value - 1e-12);
        int equalOther = raw.Values.Count(v => Math.Abs(v - kv.Value) <= 1e-12) - 1;
        return n <= 1 ? 0.5 : (less + equalOther * 0.5) / (n - 1.0);
    });
}

Dictionary<string, double> SelectionProbabilities(Dictionary<string, double> scores, int k)
{
    var probs = new Dictionary<string, double>();
    foreach (var kv in scores)
    {
        int above = scores.Values.Count(v => v > kv.Value + 1e-12);
        int tied = scores.Values.Count(v => Math.Abs(v - kv.Value) <= 1e-12);
        probs[kv.Key] = above >= k ? 0 : above + tied <= k ? 1 : Math.Clamp((k - above) / (double)tied, 0, 1);
    }
    return probs;
}

List<string> RankWithNeutralHash(Dictionary<string, double> scores, string issue) => scores
    .OrderByDescending(x => x.Value)
    .ThenBy(x => StableHash(issue + "|" + x.Key))
    .Select(x => x.Key).ToList();

uint StableHash(string text)
{
    uint hash = 2166136261;
    foreach (char c in text) { hash ^= c; hash *= 16777619; }
    return hash;
}

string BuildMarkdown(RangeAudit latestResult, RangeAudit? early)
{
    var sb = new StringBuilder();
    sb.AppendLine("# 50期信号：生肖号码理论基准校正");
    sb.AppendLine();
    sb.AppendLine("不再把Top6随机基准固定写成50%。逐期开奖按农历年号码映射计算：本命生肖5个号码，其余生肖4个号码；同分边界按均匀概率分配。");
    sb.AppendLine();
    sb.AppendLine("## 最新1000期");
    sb.AppendLine();
    sb.AppendLine("|候选|观察Top6|理论随机覆盖|净优势|本命生肖硬入Top6率|");
    sb.AppendLine("|---|---:|---:|---:|---:|");
    foreach (var kv in latestResult.Metrics)
    {
        var m = kv.Value;
        sb.AppendLine($"|{kv.Key}|{m.ObservedTop6:P2}|{m.TheoreticalBaseline:P2}|{m.Edge:+0.00%;-0.00%;0.00%}|{m.YearZodiacIncludedRate:P2}|");
    }
    sb.AppendLine();
    sb.AppendLine("### 分年度净优势");
    sb.AppendLine();
    foreach (var kv in latestResult.ByYear)
        sb.AppendLine($"- {kv.Key}: " + string.Join("，", kv.Value.OrderBy(x => x.Key).Select(x => $"{x.Key} {x.Value.Edge:+0.00%;-0.00%;0.00%}")));

    if (early != null)
    {
        sb.AppendLine();
        sb.AppendLine("## 更早时间段");
        sb.AppendLine();
        sb.AppendLine($"{early.FirstIssue}-{early.LastIssue}，N={early.Samples}");
        foreach (var kv in early.Metrics)
            sb.AppendLine($"- {kv.Key}: 观察 {kv.Value.ObservedTop6:P2}，理论 {kv.Value.TheoreticalBaseline:P2}，净优势 {kv.Value.Edge:+0.00%;-0.00%;0.00%}");
    }
    return sb.ToString();
}

sealed class Accumulator
{
    private int n;
    private double observed;
    private double baseline;
    private int yearIncluded;
    public void Add(double obs, double theory, bool includesYear)
    {
        n++; observed += obs; baseline += theory; if (includesYear) yearIncluded++;
    }
    public BaselineRow ToRow()
    {
        double o = n == 0 ? 0 : observed / n;
        double b = n == 0 ? 0 : baseline / n;
        return new BaselineRow(n, o, b, o - b, n == 0 ? 0 : yearIncluded / (double)n);
    }
}

sealed record BaselineRow(int Samples, double ObservedTop6, double TheoreticalBaseline, double Edge, double YearZodiacIncludedRate);
sealed record RangeAudit(int Samples, string FirstIssue, string LastIssue, Dictionary<string, BaselineRow> Metrics, Dictionary<string, Dictionary<string, BaselineRow>> ByYear);
sealed class HistoryPayload
{
    [JsonPropertyName("records")] public List<HistoryRecordJson> Records { get; set; } = new();
}
sealed class HistoryRecordJson
{
    [JsonPropertyName("issue")] public string Issue { get; set; } = "";
    [JsonPropertyName("special_zodiac")] public string SpecialZodiac { get; set; } = "";
    [JsonPropertyName("date")] public string Date { get; set; } = "";
}
