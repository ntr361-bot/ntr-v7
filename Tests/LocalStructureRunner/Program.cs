using System.Text;
using System.Text.Json;
using 六合分析软件;

string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
Environment.SetEnvironmentVariable("LIUHE_DATA_DIR", Path.Combine(repoRoot, "data"));
DatabaseHelper.InitializeDatabase();

string[] zodiacOrder = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
int[] mainWindows = { 45, 50, 55 };
int[] stressWindows = { 40, 50, 60 };
const int warmup = 120;
const int targetSamples = 1000;

var history = DatabaseHelper.GetHistory()
    .Where(x => !string.IsNullOrWhiteSpace(x.SpecialZodiac))
    .OrderBy(x => long.TryParse(x.Period, out long p) ? p : long.MaxValue)
    .Select(x => new Entry(x.Period, x.SpecialZodiac))
    .ToList();

if (history.Count <= warmup)
    throw new InvalidOperationException($"历史数据不足：{history.Count}");

var sequence = history.Select(x => x.Zodiac).ToList();
var issues = history.Select(x => x.Period).ToList();
int firstTarget = Math.Max(warmup, history.Count - targetSamples);

EvalResult latest = EvaluateRange(sequence, issues, firstTarget, history.Count);
EvalResult? earlier = firstTarget > warmup ? EvaluateRange(sequence, issues, warmup, firstTarget) : null;
BootstrapResult bootstrap = BlockBootstrap(latest.Observations["clean-full"].Select(x => x.HardHit6).ToArray(), 7, 5000, 20260914);
PermutationResult permutation = RunYearPermutation(sequence, issues, firstTarget, history.Count, 200, 20260915);

var report = new
{
    GeneratedAtUtc = DateTime.UtcNow,
    Protocol = new
    {
        HistoryCount = history.Count,
        RequestedSamples = targetSamples,
        ActualSamples = latest.Samples,
        latest.FirstIssue,
        latest.LastIssue,
        MainWindows = mainWindows,
        Formula = "每个窗口计算scarcity、regularity、balance三项；各自在当期12生肖内转中位秩百分位；三项等权，45/50/55三窗口等权。",
        Scarcity = "窗口内出现次数越少分越高；不读取当前遗漏。",
        Regularity = "至少2个历史间隔时使用1/(1+CV)，不足时设为中性0.5，不再给20/40奖励。",
        Balance = "窗口前后两半出现次数越均衡分越高；只看分布平衡，不看是否‘到期’。",
        TiePolicy = "主指标使用TopK边界同分的均匀分配期望；最长连错使用期号+生肖FNV哈希打破同分，避免固定生肖顺序偏差。",
        NoTuning = "公式和45/50/55窗口在运行前锁死；本轮不搜索权重、阈值或最佳窗口。",
        Permutation = "200次按年份内部打乱整个生肖序列并重新生成特征，保持每年生肖边际分布。",
        Warning = "45/50/55来自上一轮同一历史数据探索，本轮属于机制复核，不是独立未来样本。"
    },
    Latest1000 = new
    {
        latest.Samples,
        latest.FirstIssue,
        latest.LastIssue,
        latest.Metrics,
        latest.Blocks,
        latest.Years
    },
    EarlierSegment = earlier == null ? null : new
    {
        earlier.Samples,
        earlier.FirstIssue,
        earlier.LastIssue,
        earlier.Metrics
    },
    BlockBootstrapTop6 = bootstrap,
    YearPermutation = permutation
};

string outDir = Path.Combine(repoRoot, "docs");
Directory.CreateDirectory(outDir);
string jsonPath = Path.Combine(outDir, "local-structure-report.json");
string mdPath = Path.Combine(outDir, "local-structure-report.md");
File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
File.WriteAllText(mdPath, BuildMarkdown(latest, earlier, bootstrap, permutation));
Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"REPORT_JSON={jsonPath}");
Console.WriteLine($"REPORT_MD={mdPath}");

EvalResult EvaluateRange(IReadOnlyList<string> seq, IReadOnlyList<string> periodList, int start, int end)
{
    string[] variants =
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

    var observations = variants.ToDictionary(x => x, _ => new List<Observation>());

    for (int target = start; target < end; target++)
    {
        string actual = seq[target];
        string issue = periodList[target];

        Add("clean-full", BuildCleanScores(seq, target, mainWindows, true, true, true));
        Add("scarcity-only", BuildCleanScores(seq, target, mainWindows, true, false, false));
        Add("regularity-only", BuildCleanScores(seq, target, mainWindows, false, true, false));
        Add("balance-only", BuildCleanScores(seq, target, mainWindows, false, false, true));
        Add("scarcity+regularity", BuildCleanScores(seq, target, mainWindows, true, true, false));
        Add("scarcity+balance", BuildCleanScores(seq, target, mainWindows, true, false, true));
        Add("regularity+balance", BuildCleanScores(seq, target, mainWindows, false, true, true));
        Add("stress-40-50-60", BuildCleanScores(seq, target, stressWindows, true, true, true));
        Add("clean-50-only", BuildCleanScores(seq, target, new[] { 50 }, true, true, true));
        Add("old-period50", BuildOldPeriodScores(seq, target, 50));

        void Add(string name, Dictionary<string, double> scores)
        {
            List<string> hardRanking = RankWithNeutralHash(scores, issue);
            observations[name].Add(new Observation(
                issue,
                hardRanking.Take(3).Contains(actual),
                hardRanking.Take(6).Contains(actual),
                BoundaryTieProbability(scores, actual, 3),
                BoundaryTieProbability(scores, actual, 6)));
        }
    }

    var metrics = observations.ToDictionary(kv => kv.Key, kv => ToMetric(kv.Value));
    var blocks = BuildBlocks(observations["clean-full"], 100);
    var years = BuildYears(observations["clean-full"]);
    return new EvalResult(
        end - start,
        start < end ? periodList[start] : "",
        start < end ? periodList[end - 1] : "",
        metrics,
        blocks,
        years,
        observations);
}

Dictionary<string, double> BuildCleanScores(
    IReadOnlyList<string> seq,
    int target,
    IReadOnlyList<int> windows,
    bool useScarcity,
    bool useRegularity,
    bool useBalance)
{
    var aggregate = zodiacOrder.ToDictionary(z => z, _ => 0.0);
    int enabled = (useScarcity ? 1 : 0) + (useRegularity ? 1 : 0) + (useBalance ? 1 : 0);
    if (enabled == 0) throw new InvalidOperationException("至少启用一个组件");

    foreach (int window in windows)
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

        Dictionary<string, double> scarcityRank = MidRankPercentile(scarcityRaw);
        Dictionary<string, double> regularityRank = MidRankPercentile(regularityRaw);
        Dictionary<string, double> balanceRank = MidRankPercentile(balanceRaw);

        foreach (string zodiac in zodiacOrder)
        {
            double score = 0;
            if (useScarcity) score += scarcityRank[zodiac];
            if (useRegularity) score += regularityRank[zodiac];
            if (useBalance) score += balanceRank[zodiac];
            aggregate[zodiac] += score / enabled;
        }
    }

    foreach (string zodiac in zodiacOrder)
        aggregate[zodiac] /= windows.Count;

    return aggregate;
}

Dictionary<string, double> BuildOldPeriodScores(IReadOnlyList<string> seq, int target, int window)
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
        else if (gaps.Count >= 1)
        {
            result[zodiac] = 40;
        }
        else
        {
            result[zodiac] = 20;
        }
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
        int equalOthers = raw.Values.Count(v => Math.Abs(v - kv.Value) <= 1e-12) - 1;
        result[kv.Key] = n <= 1 ? 0.5 : (less + equalOthers * 0.5) / (n - 1.0);
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
        MaxMiss(rows.Select(x => x.HardHit3)),
        MaxMiss(rows.Select(x => x.HardHit6)));
}

int MaxMiss(IEnumerable<bool> hits)
{
    int current = 0;
    int max = 0;
    foreach (bool hit in hits)
    {
        if (hit) current = 0;
        else
        {
            current++;
            max = Math.Max(max, current);
        }
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

List<YearRow> BuildYears(IReadOnlyList<Observation> rows)
{
    return rows
        .GroupBy(x => x.Issue.Length >= 4 ? x.Issue[..4] : "unknown")
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

PermutationResult RunYearPermutation(
    IReadOnlyList<string> original,
    IReadOnlyList<string> periodList,
    int start,
    int end,
    int replicates,
    int seed)
{
    double observed = EvaluateCleanExpectedTop6(original, periodList, start, end);
    var nullValues = new List<double>();
    var rng = new Random(seed);

    var yearGroups = Enumerable.Range(0, original.Count)
        .GroupBy(i => periodList[i].Length >= 4 ? periodList[i][..4] : "unknown")
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

        nullValues.Add(EvaluateCleanExpectedTop6(perm, periodList, start, end));
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

double EvaluateCleanExpectedTop6(IReadOnlyList<string> seq, IReadOnlyList<string> periodList, int start, int end)
{
    double sum = 0;
    int n = 0;
    for (int target = start; target < end; target++)
    {
        var scores = BuildCleanScores(seq, target, mainWindows, true, true, true);
        sum += BoundaryTieProbability(scores, seq[target], 6);
        n++;
    }
    return n == 0 ? 0 : sum / n;
}

string BuildMarkdown(EvalResult latestResult, EvalResult? earlierResult, BootstrapResult boot, PermutationResult perm)
{
    var sb = new StringBuilder();
    sb.AppendLine("# 45/50/55 局部分布结构旁路实验");
    sb.AppendLine();
    sb.AppendLine("公式在运行前锁死：不使用当前遗漏/到期概念，不使用20/40硬兜底，不使用固定生肖顺序。主指标按边界同分的中性期望计算。");
    sb.AppendLine();
    sb.AppendLine("## 最新1000期");
    sb.AppendLine();
    sb.AppendLine("|变体|硬Top3|硬Top6|中性Top3|中性Top6|Top6最长连错|");
    sb.AppendLine("|---|---:|---:|---:|---:|---:|");
    foreach (var kv in latestResult.Metrics)
    {
        MetricRow m = kv.Value;
        sb.AppendLine($"|{kv.Key}|{m.HardTop3:P2}|{m.HardTop6:P2}|{m.ExpectedTop3:P2}|{m.ExpectedTop6:P2}|{m.MaxTop6Miss}|");
    }

    sb.AppendLine();
    sb.AppendLine("### clean-full 每100期");
    sb.AppendLine();
    sb.AppendLine("|块|期号|中性Top6|硬Top6|最长连错|");
    sb.AppendLine("|---:|---|---:|---:|---:|");
    foreach (BlockRow b in latestResult.Blocks)
        sb.AppendLine($"|{b.Block}|{b.FirstIssue}-{b.LastIssue}|{b.Metric.ExpectedTop6:P2}|{b.Metric.HardTop6:P2}|{b.Metric.MaxTop6Miss}|");

    sb.AppendLine();
    sb.AppendLine("### clean-full 分年度");
    sb.AppendLine();
    foreach (YearRow y in latestResult.Years)
        sb.AppendLine($"- {y.Year}: 中性Top6 {y.Metric.ExpectedTop6:P2}，硬Top6 {y.Metric.HardTop6:P2}");

    if (earlierResult != null)
    {
        MetricRow e = earlierResult.Metrics["clean-full"];
        sb.AppendLine();
        sb.AppendLine("## 更早时间段");
        sb.AppendLine();
        sb.AppendLine($"{earlierResult.FirstIssue}-{earlierResult.LastIssue}，N={earlierResult.Samples}：中性Top6 {e.ExpectedTop6:P2}，硬Top6 {e.HardTop6:P2}。");
    }

    sb.AppendLine();
    sb.AppendLine("## 稳健性");
    sb.AppendLine();
    sb.AppendLine($"- clean-full硬Top6块Bootstrap 95%区间：{boot.Lower95:P2} – {boot.Upper95:P2}。");
    sb.AppendLine($"- 年内置换：观察中性Top6 {perm.Observed:P2}，空模型均值 {perm.NullMean:P2}，95分位 {perm.Null95th:P2}，p={perm.P:F4}。");
    sb.AppendLine();
    sb.AppendLine("> 注意：45/50/55来自上一轮同一历史探索，因此仍不能当成独立未来证明。 ");
    return sb.ToString();
}

sealed record Entry(string Period, string Zodiac);
sealed record Observation(string Issue, bool HardHit3, bool HardHit6, double Expected3, double Expected6);
sealed record MetricRow(int Samples, double HardTop3, double HardTop6, double ExpectedTop3, double ExpectedTop6, int MaxTop3Miss, int MaxTop6Miss);
sealed record BlockRow(int Block, string FirstIssue, string LastIssue, MetricRow Metric);
sealed record YearRow(string Year, MetricRow Metric);
sealed record BootstrapResult(double Observed, double Mean, double Lower95, double Upper95, int BlockLength, int Replicates);
sealed record PermutationResult(int Replicates, double Observed, double NullMean, double Null95th, double P);
sealed record EvalResult(
    int Samples,
    string FirstIssue,
    string LastIssue,
    Dictionary<string, MetricRow> Metrics,
    List<BlockRow> Blocks,
    List<YearRow> Years,
    Dictionary<string, List<Observation>> Observations);
