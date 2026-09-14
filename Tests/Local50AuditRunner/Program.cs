using System.Text;
using System.Text.Json;
using 六合分析软件;

string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
Environment.SetEnvironmentVariable("LIUHE_DATA_DIR", Path.Combine(repoRoot, "data"));
DatabaseHelper.InitializeDatabase();

string[] zodiacOrder = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
const int warmup = 120;
const int targetSamples = 1000;
const int window = 50;
const int replicates = 200;

var history = DatabaseHelper.GetHistory()
    .Where(x => !string.IsNullOrWhiteSpace(x.SpecialZodiac))
    .OrderBy(x => long.TryParse(x.Period, out long p) ? p : long.MaxValue)
    .Select(x => new Entry(x.Period, x.SpecialZodiac))
    .ToList();

var sequence = history.Select(x => x.Zodiac).ToList();
var issues = history.Select(x => x.Period).ToList();
int firstTarget = Math.Max(warmup, history.Count - targetSamples);

AuditResult observed = Evaluate(sequence, issues, firstTarget, history.Count);
AuditResult? earlier = firstTarget > warmup ? Evaluate(sequence, issues, warmup, firstTarget) : null;
NullAudit yearShuffle = RunNull(sequence, issues, firstTarget, history.Count, replicates, 20260916, blockShuffle: false);
NullAudit block7Shuffle = RunNull(sequence, issues, firstTarget, history.Count, replicates, 20260917, blockShuffle: true);

var report = new
{
    GeneratedAtUtc = DateTime.UtcNow,
    Protocol = new
    {
        HistoryCount = history.Count,
        Samples = observed.Samples,
        observed.FirstIssue,
        observed.LastIssue,
        Window = window,
        CandidateFamily = new[] { "clean50", "regularity50", "scarcity50", "balance50", "old-period50" },
        MainMetric = "Top6边界同分中性期望",
        YearShuffle = "按年份内部完全洗牌，保留每年生肖边际分布；200次。",
        Block7Shuffle = "按年份把序列切成7期块，只洗牌块顺序、保留块内顺序；200次。用于保留短期重复/聚集后再破坏约50期结构。",
        FamilyCorrection = "每个空模型重复都取5个候选中的最高Top6，再与真实候选家族最高值比较。",
        Warning = "这5个候选是前面同一1000期研究后留下的残余候选，因此仍属于后验机制审计，不是独立未来证明。"
    },
    Latest1000 = observed,
    EarlierSegment = earlier,
    YearShuffleNull = yearShuffle,
    Block7ShuffleNull = block7Shuffle
};

string outDir = Path.Combine(repoRoot, "docs");
Directory.CreateDirectory(outDir);
string jsonPath = Path.Combine(outDir, "local50-audit-report.json");
string mdPath = Path.Combine(outDir, "local50-audit-report.md");
File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
File.WriteAllText(mdPath, BuildMarkdown(observed, earlier, yearShuffle, block7Shuffle));
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"REPORT_JSON={jsonPath}");
Console.WriteLine($"REPORT_MD={mdPath}");

AuditResult Evaluate(IReadOnlyList<string> seq, IReadOnlyList<string> periodList, int start, int end)
{
    string[] variants = { "clean50", "regularity50", "scarcity50", "balance50", "old-period50" };
    var rows = variants.ToDictionary(x => x, _ => new List<Observation>());

    for (int target = start; target < end; target++)
    {
        string actual = seq[target];
        string issue = periodList[target];
        Add("clean50", BuildCleanScores(seq, target, true, true, true));
        Add("regularity50", BuildCleanScores(seq, target, false, true, false));
        Add("scarcity50", BuildCleanScores(seq, target, true, false, false));
        Add("balance50", BuildCleanScores(seq, target, false, false, true));
        Add("old-period50", BuildOldPeriodScores(seq, target));

        void Add(string name, Dictionary<string, double> scores)
        {
            var ranking = RankWithNeutralHash(scores, issue);
            rows[name].Add(new Observation(
                issue,
                ranking.Take(3).Contains(actual),
                ranking.Take(6).Contains(actual),
                BoundaryTieProbability(scores, actual, 3),
                BoundaryTieProbability(scores, actual, 6)));
        }
    }

    var metrics = rows.ToDictionary(kv => kv.Key, kv => ToMetric(kv.Value));
    var byYear = rows.ToDictionary(
        kv => kv.Key,
        kv => kv.Value
            .GroupBy(x => x.Issue.Length >= 4 ? x.Issue[..4] : "unknown")
            .ToDictionary(g => g.Key, g => ToMetric(g.ToList())));
    var blocks = rows.ToDictionary(kv => kv.Key, kv => BuildBlocks(kv.Value, 100));

    return new AuditResult(
        end - start,
        start < end ? periodList[start] : "",
        start < end ? periodList[end - 1] : "",
        metrics,
        byYear,
        blocks);
}

Dictionary<string, double> BuildCleanScores(IReadOnlyList<string> seq, int target, bool useScarcity, bool useRegularity, bool useBalance)
{
    int from = Math.Max(0, target - window);
    var slice = seq.Skip(from).Take(target - from).ToList();
    var scarcityRaw = new Dictionary<string, double>();
    var regularityRaw = new Dictionary<string, double>();
    var balanceRaw = new Dictionary<string, double>();

    foreach (string zodiac in zodiacOrder)
    {
        var positions = slice.Select((z, i) => (Zodiac: z, Index: i))
            .Where(x => x.Zodiac == zodiac)
            .Select(x => x.Index)
            .ToList();

        int count = positions.Count;
        scarcityRaw[zodiac] = -count;

        var gaps = new List<int>();
        for (int i = 1; i < positions.Count; i++)
            gaps.Add(positions[i] - positions[i - 1]);

        if (gaps.Count >= 2)
        {
            double avg = gaps.Average();
            double variance = gaps.Select(g => Math.Pow(g - avg, 2)).Average();
            double cv = avg > 0 ? Math.Sqrt(variance) / avg : 1.0;
            regularityRaw[zodiac] = 1.0 / (1.0 + cv);
        }
        else
        {
            regularityRaw[zodiac] = 0.5;
        }

        int split = slice.Count / 2;
        int older = positions.Count(p => p < split);
        int newer = count - older;
        balanceRaw[zodiac] = count == 0 ? 0.5 : 1.0 - Math.Abs(newer - older) / (double)count;
    }

    var scarcityRank = MidRankPercentile(scarcityRaw);
    var regularityRank = MidRankPercentile(regularityRaw);
    var balanceRank = MidRankPercentile(balanceRaw);
    int enabled = (useScarcity ? 1 : 0) + (useRegularity ? 1 : 0) + (useBalance ? 1 : 0);
    var result = new Dictionary<string, double>();

    foreach (string zodiac in zodiacOrder)
    {
        double sum = 0;
        if (useScarcity) sum += scarcityRank[zodiac];
        if (useRegularity) sum += regularityRank[zodiac];
        if (useBalance) sum += balanceRank[zodiac];
        result[zodiac] = sum / enabled;
    }

    return result;
}

Dictionary<string, double> BuildOldPeriodScores(IReadOnlyList<string> seq, int target)
{
    int from = Math.Max(0, target - window);
    var newestFirst = seq.Skip(from).Take(target - from).Reverse().ToList();
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
    var result = new Dictionary<string, double>();
    int n = raw.Count;
    foreach (var kv in raw)
    {
        int less = raw.Values.Count(v => v < kv.Value - 1e-12);
        int equalOther = raw.Values.Count(v => Math.Abs(v - kv.Value) <= 1e-12) - 1;
        result[kv.Key] = n <= 1 ? 0.5 : (less + equalOther * 0.5) / (n - 1.0);
    }
    return result;
}

List<string> RankWithNeutralHash(Dictionary<string, double> scores, string issue)
{
    return scores.OrderByDescending(x => x.Value)
        .ThenBy(x => StableHash(issue + "|" + x.Key))
        .Select(x => x.Key)
        .ToList();
}

uint StableHash(string text)
{
    uint hash = 2166136261;
    foreach (char c in text)
    {
        hash ^= c;
        hash *= 16777619;
    }
    return hash;
}

double BoundaryTieProbability(Dictionary<string, double> scores, string actual, int k)
{
    double s = scores[actual];
    int above = scores.Values.Count(v => v > s + 1e-12);
    int tied = scores.Values.Count(v => Math.Abs(v - s) <= 1e-12);
    if (above >= k) return 0;
    if (above + tied <= k) return 1;
    return Math.Clamp((k - above) / (double)tied, 0, 1);
}

MetricRow ToMetric(IReadOnlyList<Observation> rows)
{
    int n = rows.Count;
    return new MetricRow(
        n,
        n == 0 ? 0 : rows.Count(x => x.HardHit3) / (double)n,
        n == 0 ? 0 : rows.Count(x => x.HardHit6) / (double)n,
        n == 0 ? 0 : rows.Sum(x => x.Expected3) / n,
        n == 0 ? 0 : rows.Sum(x => x.Expected6) / n,
        MaxMiss(rows.Select(x => x.HardHit6)));
}

int MaxMiss(IEnumerable<bool> hits)
{
    int current = 0, max = 0;
    foreach (bool hit in hits)
    {
        if (hit) current = 0;
        else { current++; max = Math.Max(max, current); }
    }
    return max;
}

List<BlockRow> BuildBlocks(IReadOnlyList<Observation> rows, int size)
{
    var result = new List<BlockRow>();
    for (int i = 0; i < rows.Count; i += size)
    {
        var part = rows.Skip(i).Take(size).ToList();
        result.Add(new BlockRow(i / size + 1, part.First().Issue, part.Last().Issue, ToMetric(part)));
    }
    return result;
}

NullAudit RunNull(IReadOnlyList<string> original, IReadOnlyList<string> periodList, int start, int end, int reps, int seed, bool blockShuffle)
{
    string[] variants = { "clean50", "regularity50", "scarcity50", "balance50", "old-period50" };
    AuditResult observedResult = Evaluate(original, periodList, start, end);
    var observed = variants.ToDictionary(v => v, v => observedResult.Metrics[v].ExpectedTop6);
    double observedMax = observed.Values.Max();

    var nullByVariant = variants.ToDictionary(v => v, _ => new List<double>());
    var nullFamilyMax = new List<double>();
    var rng = new Random(seed);
    var yearGroups = Enumerable.Range(0, original.Count)
        .GroupBy(i => periodList[i].Length >= 4 ? periodList[i][..4] : "unknown")
        .Select(g => g.ToList())
        .ToList();

    for (int r = 0; r < reps; r++)
    {
        var perm = original.ToList();
        foreach (var indices in yearGroups)
        {
            List<string> vals = indices.Select(i => perm[i]).ToList();
            List<string> shuffled;
            if (!blockShuffle)
            {
                shuffled = vals.ToList();
                Shuffle(shuffled, rng);
            }
            else
            {
                var blocks = new List<List<string>>();
                for (int i = 0; i < vals.Count; i += 7)
                    blocks.Add(vals.Skip(i).Take(7).ToList());
                Shuffle(blocks, rng);
                shuffled = blocks.SelectMany(x => x).ToList();
            }

            for (int i = 0; i < indices.Count; i++)
                perm[indices[i]] = shuffled[i];
        }

        AuditResult a = Evaluate(perm, periodList, start, end);
        double familyMax = 0;
        foreach (string v in variants)
        {
            double value = a.Metrics[v].ExpectedTop6;
            nullByVariant[v].Add(value);
            familyMax = Math.Max(familyMax, value);
        }
        nullFamilyMax.Add(familyMax);
    }

    var variantStats = new Dictionary<string, NullMetric>();
    foreach (string v in variants)
    {
        var vals = nullByVariant[v].OrderBy(x => x).ToList();
        int ge = vals.Count(x => x >= observed[v] - 1e-12);
        variantStats[v] = new NullMetric(
            observed[v],
            vals.Average(),
            vals[(int)Math.Floor((reps - 1) * 0.95)],
            (ge + 1.0) / (reps + 1.0));
    }

    var familySorted = nullFamilyMax.OrderBy(x => x).ToList();
    int familyGe = familySorted.Count(x => x >= observedMax - 1e-12);
    var family = new NullMetric(
        observedMax,
        familySorted.Average(),
        familySorted[(int)Math.Floor((reps - 1) * 0.95)],
        (familyGe + 1.0) / (reps + 1.0));

    return new NullAudit(reps, blockShuffle ? "year-block7-shuffle" : "year-full-shuffle", variantStats, family);
}

void Shuffle<T>(IList<T> list, Random rng)
{
    for (int i = list.Count - 1; i > 0; i--)
    {
        int j = rng.Next(i + 1);
        (list[i], list[j]) = (list[j], list[i]);
    }
}

string BuildMarkdown(AuditResult current, AuditResult? oldSegment, NullAudit fullNull, NullAudit blockNull)
{
    var sb = new StringBuilder();
    sb.AppendLine("# 50期残余信号审计");
    sb.AppendLine();
    sb.AppendLine("目标：在去掉固定生肖顺序后，判断50期剩余约3个百分点到底是否仍超出随机波动。主指标为Top6边界同分中性期望。");
    sb.AppendLine();
    sb.AppendLine("## 最新1000期");
    sb.AppendLine();
    sb.AppendLine("|候选|中性Top3|中性Top6|硬Top6|最长连错|");
    sb.AppendLine("|---|---:|---:|---:|---:|");
    foreach (var kv in current.Metrics)
    {
        MetricRow m = kv.Value;
        sb.AppendLine($"|{kv.Key}|{m.ExpectedTop3:P2}|{m.ExpectedTop6:P2}|{m.HardTop6:P2}|{m.MaxTop6Miss}|");
    }

    sb.AppendLine();
    sb.AppendLine("## 分年度 Top6 中性期望");
    sb.AppendLine();
    foreach (var kv in current.ByYear)
    {
        sb.Append($"- {kv.Key}: ");
        sb.AppendLine(string.Join("，", kv.Value.OrderBy(x => x.Key).Select(x => $"{x.Key} {x.Value.ExpectedTop6:P2}")));
    }

    if (oldSegment != null)
    {
        sb.AppendLine();
        sb.AppendLine("## 更早时间段");
        sb.AppendLine();
        sb.AppendLine($"{oldSegment.FirstIssue}-{oldSegment.LastIssue}，N={oldSegment.Samples}");
        foreach (var kv in oldSegment.Metrics)
            sb.AppendLine($"- {kv.Key}: Top6中性 {kv.Value.ExpectedTop6:P2}");
    }

    sb.AppendLine();
    sb.AppendLine("## 年内完全洗牌");
    sb.AppendLine();
    foreach (var kv in fullNull.Variants)
        sb.AppendLine($"- {kv.Key}: 观察 {kv.Value.Observed:P2}，空均值 {kv.Value.NullMean:P2}，95分位 {kv.Value.Null95th:P2}，p={kv.Value.P:F4}");
    sb.AppendLine($"- 5候选家族校正：观察最大 {fullNull.FamilyMax.Observed:P2}，空均值 {fullNull.FamilyMax.NullMean:P2}，95分位 {fullNull.FamilyMax.Null95th:P2}，p={fullNull.FamilyMax.P:F4}");

    sb.AppendLine();
    sb.AppendLine("## 年内7期块洗牌");
    sb.AppendLine();
    foreach (var kv in blockNull.Variants)
        sb.AppendLine($"- {kv.Key}: 观察 {kv.Value.Observed:P2}，空均值 {kv.Value.NullMean:P2}，95分位 {kv.Value.Null95th:P2}，p={kv.Value.P:F4}");
    sb.AppendLine($"- 5候选家族校正：观察最大 {blockNull.FamilyMax.Observed:P2}，空均值 {blockNull.FamilyMax.NullMean:P2}，95分位 {blockNull.FamilyMax.Null95th:P2}，p={blockNull.FamilyMax.P:F4}");
    sb.AppendLine();
    sb.AppendLine("> 7期块洗牌更严格：它保留短期重复和局部聚集，只破坏更长尺度的排列。若在该空模型下不显著，50期优势可能只是短期结构累积而非独立的50期规律。 ");
    return sb.ToString();
}

sealed record Entry(string Period, string Zodiac);
sealed record Observation(string Issue, bool HardHit3, bool HardHit6, double Expected3, double Expected6);
sealed record MetricRow(int Samples, double HardTop3, double HardTop6, double ExpectedTop3, double ExpectedTop6, int MaxTop6Miss);
sealed record BlockRow(int Block, string FirstIssue, string LastIssue, MetricRow Metric);
sealed record AuditResult(
    int Samples,
    string FirstIssue,
    string LastIssue,
    Dictionary<string, MetricRow> Metrics,
    Dictionary<string, Dictionary<string, MetricRow>> ByYear,
    Dictionary<string, List<BlockRow>> Blocks);
sealed record NullMetric(double Observed, double NullMean, double Null95th, double P);
sealed record NullAudit(int Replicates, string NullType, Dictionary<string, NullMetric> Variants, NullMetric FamilyMax);
