using System.Text;
using System.Text.Json;
using 六合分析软件;

string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
string dataDir = Path.Combine(repoRoot, "data");
Environment.SetEnvironmentVariable("LIUHE_DATA_DIR", dataDir);
DatabaseHelper.InitializeDatabase();

string[] zodiacOrder = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
int[] mainWindows = { 45, 50, 55 };
int[] stressWindows = { 40, 50, 60 };
const int targetSamples = 1000;
const int warmup = 120;

var history = DatabaseHelper.GetHistory()
    .Where(x => !string.IsNullOrWhiteSpace(x.SpecialZodiac))
    .OrderBy(x => long.TryParse(x.Period, out var p) ? p : long.MaxValue)
    .Select(x => new Entry(x.Period, x.SpecialZodiac))
    .ToList();

if (history.Count <= warmup)
    throw new InvalidOperationException($"历史数据不足：{history.Count}");

int firstTarget = Math.Max(warmup, history.Count - targetSamples);
var zodiacs = history.Select(x => x.Zodiac).ToList();
var periods = history.Select(x => x.Period).ToList();

var main = EvaluateRange(zodiacs, periods, firstTarget, history.Count);
var earlier = firstTarget > warmup
    ? EvaluateRange(zodiacs, periods, warmup, firstTarget)
    : new RangeResult(0, "", "", new Dictionary<string, MetricRow>(), new List<BlockRow>(), new List<YearRow>());

var bootstrap = BlockBootstrap(main.Observations["clean-full"].Select(x => x.HardHit6).ToArray(), 7, 5000, 20260914);
var permutation = RunYearPermutation(zodiacs, periods, firstTarget, history.Count, 200, 20260915);

var report = new
{
    GeneratedAtUtc = DateTime.UtcNow,
    Protocol = new
    {
        HistoryCount = history.Count,
        RequestedSamples = targetSamples,
        ActualSamples = history.Count - firstTarget,
        FirstIssue = periods[firstTarget],
        LastIssue = periods[^1],
        MainWindows = mainWindows,
        Formula = "每个窗口分别计算三项：scarcity=出现次数越少越高；regularity=至少2个间隔时1/(1+CV)，否则中性0.5；balance=前后半窗口出现次数越均衡越高。三项都先在当期12生肖内做中位秩百分位，再三项等权、45/50/55三窗口等权。",
        TiePolicy = "主评价使用TopK边界同分的均匀随机期望；最长连错另用target期号+生肖的固定FNV哈希打破同分，避免长期偏向某个生肖。",
        NoChasing = "不使用当前遗漏值、不使用距离上次出现多少期、不使用phase/due概念。",
        NoTuning = "本轮公式与45/50/55窗口在运行前锁死；不根据本轮结果搜索权重或阈值。",
        Permutation = "200次按年份内部打乱生肖序列并重新生成全部历史特征；检验clean-full的Top6中性期望。",
        Warning = "45/50/55区域来自前一轮同一历史数据的探索，因此本轮仍是机制验证，不是独立未来证明。"
    },
    Latest1000 = new
    {
        main.Samples,
        main.FirstIssue,
        main.LastIssue,
        Metrics = main.Metrics,
        Blocks = main.Blocks,
        Years = main.Years
    },
    EarlierSegment = new
    {
        earlier.Samples,
        earlier.FirstIssue,
        earlier.LastIssue,
        Metrics = earlier.Metrics
    },
    BlockBootstrapTop6 = bootstrap,
    YearPermutation = permutation
};

string outDir = Path.Combine(repoRoot, "docs");
Directory.CreateDirectory(outDir);
string jsonPath = Path.Combine(outDir, "local-structure-report.json");
string mdPath = Path.Combine(outDir, "local-structure-report.md");
File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
File.WriteAllText(mdPath, BuildMarkdown(main, earlier, bootstrap, permutation));

Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"REPORT_JSON={jsonPath}");
Console.WriteLine($"REPORT_MD={mdPath}");

RangeResult EvaluateRange(IReadOnlyList<string> sequence, IReadOnlyList<string> issues, int start, int end)
{
    var observations = VariantNames().ToDictionary(x => x, _ => new List<Observation>());

    for (int target = start; target < end; target++)
    {
        string actual = sequence[target];
        string issue = issues[target];

        var mainScores = BuildCleanScores(sequence, target, mainWindows, true, true, true);
        var scarcity = BuildCleanScores(sequence, target, mainWindows, true, false, false);
        var regularity = BuildCleanScores(sequence, target, mainWindows, false, true, false);
        var balance = BuildCleanScores(sequence, target, mainWindows, false, false, true);
        var scarcityRegularity = BuildCleanScores(sequence, target, mainWindows, true, true, false);
        var scarcityBalance = BuildCleanScores(sequence, target, mainWindows, true, false, true);
        var regularityBalance = BuildCleanScores(sequence, target, mainWindows, false, true, true);
        var stress = BuildCleanScores(sequence, target, stressWindows, true, true, true);
        var window50 = BuildCleanScores(sequence, target, new[] { 50 }, true, true, true);
        var old50 = BuildOldPeriodScores(sequence, target, 50);

        Add("clean-full", mainScores);
        Add("scarcity-only", scarcity);
        Add("regularity-only", regularity);
        Add("balance-only", balance);
        Add("scarcity+regularity", scarcityRegularity);
        Add("scarcity+balance", scarcityBalance);
        Add("regularity+balance", regularityBalance);
        Add("stress-40-50-60", stress);
        Add("clean-50-only", window50);
        Add("old-period50", old50);

        void Add(string name, Dictionary<string, double> scores)
        {
            var hardRanking = RankWithNeutralHash(scores, issue);
            bool hardHit3 = hardRanking.Take(3).Contains(actual);
            bool hardHit6 = hardRanking.Take(6).Contains(actual);
            double expected3 = BoundaryTieProbability(scores, actual, 3);
            double expected6 = BoundaryTieProbability(scores, actual, 6);
            observations[name].Add(new Observation(target, issue, actual, hardHit3, hardHit6, expected3, expected6));
        }
    }

    var metrics = observations.ToDictionary(x => x.Key, x => ToMetric(x.Value));
    var blocks = BuildBlocks(observations["clean-full"], 100);
    var years = BuildYears(observations["clean-full"]);

    return new RangeResult(
        end - start,
        start < end ? issues[start] : "",
        start < end ? issues[end - 1] : "",
        metrics,
        blocks,
        years,
        observations);
}

Dictionary<string, double> BuildCleanScores(IReadOnlyList<string> sequence, int target, IReadOnlyList<int> windows,
    bool useScarcity, bool useRegularity, bool useBalance)
{
    var aggregate = zodiacOrder.ToDictionary(z => z, _ => 0.0);

    foreach (int window in windows)
    {
        int start = Math.Max(0, target - window);
        var slice = sequence.Skip(start).Take(target - start).ToList();

        var scarcityRaw = new Dictionary<string, double>();
        var regularityRaw = new Dictionary<string, double>();
        var balanceRaw = new Dictionary<string, double>();

        foreach (string zodiac in zodiacOrder)
        {
            var positions = slice.Select((z, i) => new { z, i })
                .Where(x => x.z == zodiac)
                .Select(x => x.i)
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

        int components = (useScarcity ? 1 : 0) + (useRegularity ? 1 : 0) + (useBalance ? 1 : 0);
        if (components == 0) throw new InvalidOperationException("至少启用一个组件");

        foreach (string zodiac in zodiacOrder)
        {
            double sum = 0;
            if (useScarcity) sum += scarcityRank[zodiac];
            if (useRegularity) sum += regularityRank[zodiac];
            if (useBalance) sum += balanceRank[zodiac];
            aggregate[zodiac] += sum / components;
        }
    }

    foreach (string zodiac in zodiacOrder)
        aggregate[zodiac] /= windows.Count;

    return aggregate;
}

Dictionary<string, double> BuildOldPeriodScores(IReadOnlyList<string> sequence, int target, int window)
{
    int start = Math.Max(0, target - window);
    var sliceNewestFirst = sequence.Skip(start).Take(target - start).Reverse().ToList();
    var scores = new Dictionary<string, double>();

    foreach (string zodiac in zodiacOrder)
    {
        var intervals = new List<int>();
        int last = -1;
        for (int i = 0; i < sliceNewestFirst.Count; i++)
        {
            if (sliceNewestFirst[i] != zodiac) continue;
            if (last >= 0) intervals.Add(i - last);
            last = i;
        }

        if (intervals.Count >= 3)
        {
            double avg = intervals.Average();
            double variance = intervals.Select(x => Math.Pow(x - avg, 2)).Average();
            double cv = avg > 0 ? Math.Sqrt(variance) / avg : 1;
            scores[zodiac] = Math.Max(0, (1 - cv) * 100);
        }
        else if (intervals.Count >= 1)
        {
            scores[zodiac] = 40;
        }
        else
        {
            scores[zodiac] = 20;
        }
    }

    return scores;
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
    return scores
        .OrderByDescending(x => x.Value)
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
    double actualScore = scores[actual];
    int above = scores.Values.Count(v => v > actualScore + 1e-12);
    int tied = scores.Values.Count(v => Math.Abs(v - actualScore) <= 1e-12);
    if (above >= k) return 0;
    if (above + tied <= k) return 1;
    return Math.Clamp((k - above) / (double)tied, 0, 1);
}

MetricRow ToMetric(IReadOnlyList<Observation> rows)
{
    int samples = rows.Count;
    int hard3 = rows.Count(x => x.HardHit3);
    int hard6 = rows.Count(x => x.HardHit6);
    int maxMiss3 = MaxMiss(rows.Select(x => x.HardHit3));
    int maxMiss6 = MaxMiss(rows.Select(x => x.HardHit6));
    return new MetricRow(
        samples,
        samples == 0 ? 0 : hard3 / (double)samples,
        samples == 0 ? 0 : hard6 / (double)samples,
        samples == 0 ? 0 : rows.Sum(x => x.Expected3) / samples,
        samples == 0 ? 0 : rows.Sum(x => x.Expected6) / samples,
        maxMiss3,
        maxMiss6);
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
        var block = rows.Skip(i).Take(size).ToList();
        result.Add(new BlockRow(i / size + 1, block.First().Issue, block.Last().Issue, ToMetric(block)));
    }
    return result;
}

List<YearRow> BuildYears(IReadOnlyList<Observation> rows)
{
    return rows.GroupBy(x => x.Issue.Length >= 4 ? x.Issue[..4] : "unknown")
        .Select(g => new YearRow(g.Key, ToMetric(g.ToList())))
        .ToList();
}

BootstrapResult BlockBootstrap(bool[] hits, int blockLength, int replicates, int seed)
{
    var rng = new Random(seed);
    var values = new double[replicates];
    int n = hits.Length;
    for (int r = 0; r < replicates; r++)
    {
        int total = 0;
        int filled = 0;
        while (filled < n)
        {
            int start = rng.Next(n);
            for (int j = 0; j < blockLength && filled < n; j++)
            {
                if (hits[(start + j) % n]) total++;
                filled++;
            }
        }
        values[r] = total / (double)n;
    }
    Array.Sort(values);
    return new BootstrapResult(
        hits.Count(x => x) / (double)n,
        values.Average(),
        values[(int)Math.Floor((replicates - 1) * 0.025)],
        values[(int)Math.Floor((replicates - 1) * 0.975)],
        blockLength,
        replicates);
}

PermutationResult RunYearPermutation(IReadOnlyList<string> original, IReadOnlyList<string> issues, int start, int end, int replicates, int seed)
{
    var observedRows = EvaluateCleanOnly(original, issues, start, end);
    double observed = observedRows.Average(x => x.Expected6);
    var nullValues = new List<double>();
    var rng = new Random(seed);

    var yearGroups = Enumerable.Range(0, original.Count)
        .GroupBy(i => issues[i].Length >= 4 ? issues[i][..4] : "unknown")
        .Select(g => g.ToList())
        .ToList();

    for (int r = 0; r < replicates; r++)
    {
        var perm = original.ToList();
        foreach (var indices in yearGroups)
        {
            var vals = indices.Select(i => perm[i]).ToList();
            for (int i = vals.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (vals[i], vals[j]) = (vals[j], vals[i]);
            }
            for (int i = 0; i < indices.Count; i++)
                perm[indices[i]] = vals[i];
        }

        var rows = EvaluateCleanOnly(perm, issues, start, end);
        nullValues.Add(rows.Average(x => x.Expected6));
    }

    var sorted = nullValues.OrderBy(x => x).ToList();
    int ge = nullValues.Count(x => x >= observed - 1e-12);
    return new PermutationResult(
        replicates,
        observed,
        nullValues.Average(),
        sorted[(int)Math.Floor((replicates - 1) * 0.95)],
        (ge + 1.0) / (replicates + 1.0));
}

List<Observation> EvaluateCleanOnly(IReadOnlyList<string> sequence, IReadOnlyList<string> issues, int start, int end)
{
    var rows = new List<Observation>();
    for (int target = start; target < end; target++)
    {
        string actual = sequence[target];
        string issue = issues[target];
        var scores = BuildCleanScores(sequence, target, mainWindows, true, true, true);
        var ranking = RankWithNeutralHash(scores, issue);
        rows.Add(new Observation(
            target, issue, actual,
            ranking.Take(3).Contains(actual),
            ranking.Take(6).Contains(actual),
            BoundaryTieProbability(scores, actual, 3),
            BoundaryTieProbability(scores, actual, 6)));
    }
    return rows;
}

string BuildMarkdown(RangeResult latest, RangeResult early, BootstrapResult bootstrapResult, PermutationResult permutationResult)
{
    var sb = new StringBuilder();
    sb.AppendLine("# 45/50/55 局部分布结构旁路实验");
    sb.AppendLine();
    sb.AppendLine("公式在运行前锁死：不使用当前遗漏/到期概念，不使用20/40硬兜底，不用固定生肖顺序。同分主评价按均匀随机边界期望计算。");
    sb.AppendLine();
    sb.AppendLine("## 最新1000期");
    sb.AppendLine();
    sb.AppendLine("|变体|硬Top3|硬Top6|中性Top3期望|中性Top6期望|Top6最长连错|");
    sb.AppendLine("|---|---:|---:|---:|---:|---:|");
    foreach (var kv in latest.Metrics)
    {
        var m = kv.Value;
        sb.AppendLine($"|{kv.Key}|{m.HardTop3:P2}|{m.HardTop6:P2}|{m.ExpectedTop3:P2}|{m.ExpectedTop6:P2}|{m.MaxTop6Miss}|");
    }
    sb.AppendLine();
    sb.AppendLine("### clean-full 每100期");
    sb.AppendLine();
    sb.AppendLine("|块|期号|中性Top6期望|硬Top6|最长连错|");
    sb.AppendLine("|---:|---|---:|---:|---:|");
    foreach (var b in latest.Blocks)
        sb.AppendLine($"|{b.Block}|{b.FirstIssue}-{b.LastIssue}|{b.Metric.ExpectedTop6:P2}|{b.Metric.HardTop6:P2}|{b.Metric.MaxTop6Miss}|");
    sb.AppendLine();
    sb.AppendLine("### clean-full 分年度");
    sb.AppendLine();
    foreach (var y in latest.Years)
        sb.AppendLine($"- {y.Year}: 中性Top6 {y.Metric.ExpectedTop6:P2}，硬Top6 {y.Metric.HardTop6:P2}");
    sb.AppendLine();
    sb.AppendLine("## 更早时间段");
    sb.AppendLine();
    if (early.Samples > 0)
    {
        var m = early.Metrics["clean-full"];
        sb.AppendLine($"{early.FirstIssue}-{early.LastIssue}，N={early.Samples}：clean-full 中性Top6 {m.ExpectedTop6:P2}，硬Top6 {m.HardTop6:P2}。");
    }
    sb.AppendLine();
    sb.AppendLine("## 稳健性");
    sb.AppendLine();
    sb.AppendLine($"- clean-full硬Top6块Bootstrap 95%区间：{bootstrapResult.Lower95:P2} – {bootstrapResult.Upper95:P2}。");
    sb.AppendLine($"- 年内置换：观察中性Top6 {permutationResult.Observed:P2}，空模型均值 {permutationResult.NullMean:P2}，95分位 {permutationResult.Null95th:P2}，p={permutationResult.P:F4}。");
    sb.AppendLine();
    sb.AppendLine("> 注意：45/50/55窗口来自上一轮同一数据的探索，因此仍不能当作独立未来证明；本轮主要判断去掉硬常数和固定生肖顺序后，异常是否还能存在。 ");
    return sb.ToString();
}

string[] VariantNames() =>
{
    "clean-full",
    "scarcity-only",
    "regularity-only",
    "balance-only",
    "scarcity+regularity",
    "scarcity+balance",
    "regularity+balance",
    "stress-40-50-60",
    "clean-50-only",
    "old-period50"
};

sealed record Entry(string Period, string Zodiac);
sealed record Observation(int TargetIndex, string Issue, string Actual, bool HardHit3, bool HardHit6, double Expected3, double Expected6);
sealed record MetricRow(int Samples, double HardTop3, double HardTop6, double ExpectedTop3, double ExpectedTop6, int MaxTop3Miss, int MaxTop6Miss);
sealed record BlockRow(int Block, string FirstIssue, string LastIssue, MetricRow Metric);
sealed record YearRow(string Year, MetricRow Metric);
sealed record BootstrapResult(double Observed, double Mean, double Lower95, double Upper95, int BlockLength, int Replicates);
sealed record PermutationResult(int Replicates, double Observed, double NullMean, double Null95th, double P);
sealed record RangeResult(
    int Samples,
    string FirstIssue,
    string LastIssue,
    Dictionary<string, MetricRow> Metrics,
    List<BlockRow> Blocks,
    List<YearRow> Years,
    Dictionary<string, List<Observation>> Observations)
{
    public RangeResult(int Samples, string FirstIssue, string LastIssue, Dictionary<string, MetricRow> Metrics,
        List<BlockRow> Blocks, List<YearRow> Years)
        : this(Samples, FirstIssue, LastIssue, Metrics, Blocks, Years, new Dictionary<string, List<Observation>>()) { }
}
