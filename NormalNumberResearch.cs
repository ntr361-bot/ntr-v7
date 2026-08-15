using System.Data.SQLite;
using System.Text.Json;

namespace 六合分析软件;

public sealed record NormalResearchRow(string Issue, string OpenTime, int[] NormalNumbers, int SpecialNumber, string[] NormalZodiacs, string SpecialZodiac);
public sealed record NormalResearchMetric(string Feature, string Condition, int N, int Hits, double Rate, double Baseline, double Lift, double Lower95, double Upper95, bool DirectionStable);
public sealed record NormalCandidateMetric(string Model, int N, double Top1, double Top3, double Top6, double Mrr, double MeanRank, int MaxTop3Miss, int MaxTop6Miss);
public sealed class NormalNumberResearchReport
{
    public string ReportTitle { get; init; } = "平码 -> 下一期特肖 Signal Research";
    public string SourceCopy { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public int N { get; init; }
    public string EarliestIssue { get; init; } = "";
    public string LatestIssue { get; init; } = "";
    public int MissingIssueCount { get; init; }
    public int DuplicateIssueCount { get; init; }
    public int IncompleteNormalCount { get; init; }
    public int MissingSpecialCount { get; init; }
    public int MissingZodiacCount { get; init; }
    public int NumberAnomalyCount { get; init; }
    public int MappingAnomalyCount { get; init; }
    public int MappingSampleCount { get; init; }
    public int MappingSampleMismatch { get; init; }
    public IReadOnlyList<NormalResearchMetric> Univariate { get; init; } = Array.Empty<NormalResearchMetric>();
    public IReadOnlyList<NormalCandidateMetric> Candidate { get; init; } = Array.Empty<NormalCandidateMetric>();
    public IReadOnlyList<object> Transition { get; init; } = Array.Empty<object>();
    public IReadOnlyList<object> Yearly { get; init; } = Array.Empty<object>();
    public IReadOnlyList<object> Rolling { get; init; } = Array.Empty<object>();
    public IReadOnlyList<object> V65Comparison { get; init; } = Array.Empty<object>();
    public string CandidateDecision { get; init; } = "不构建 Candidate";
    public bool FutureDataLeakageDetected { get; init; }
}

public static class NormalNumberResearch
{
    private static readonly string[] Z = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };

    public static NormalNumberResearchReport Run(IReadOnlyList<DatabaseHelper.HistoryRecord> source, string sourceCopy, int monteCarloIterations = 10000)
    {
        var rows = source.Select(ToRow).Where(x => x is not null).Cast<NormalResearchRow>().OrderBy(x => long.Parse(x.Issue)).ToArray();
        var metrics = new List<NormalResearchMetric>();
        foreach (string z in Z)
        {
            var baseRows = rows.Skip(1).ToArray();
            Add(metrics, $"PreviousNormalContains:{z}", "contains", baseRows, x => x.NormalZodiacs.Contains(z), z);
            Add(metrics, $"PreviousNormalCount:{z}", "count>=2", baseRows, x => x.NormalZodiacs.Count(y => y == z) >= 2, z);
            Add(metrics, $"PreviousNormalCount:{z}", "count=0", baseRows, x => !x.NormalZodiacs.Contains(z), z);
            foreach (int w in new[] { 5, 10, 20, 50 })
                Add(metrics, $"NormalCount{w}:{z}", "topQuartile(training-free descriptive)", baseRows, x => WindowCount(rows, x.Issue, w, z) >= WindowCount(rows, x.Issue, w, z), z);
        }
        var transitions = new List<object>();
        foreach (string a in Z) foreach (string b in Z)
        {
            var subset = rows.Skip(1).Where(x => x.NormalZodiacs.Contains(a)).ToArray();
            int hit = subset.Count(x => x.SpecialZodiac == b); double rate = subset.Length == 0 ? 0 : (double)hit / subset.Length;
            double baseline = rows.Length == 0 ? 0 : (double)rows.Count(x => x.SpecialZodiac == b) / rows.Length;
            transitions.Add(new { From = a, To = b, N = subset.Length, Count = hit, ConditionalProbability = rate, BaseProbability = baseline, Lift = baseline == 0 ? 0 : rate / baseline });
        }
        var candidate = BuildCandidate(rows);
        var yearly = rows.Skip(1).GroupBy(x => Year(x.OpenTime)).Select(g => new { Year = g.Key, N = g.Count(), Top1 = candidate.Count(x => x.IssueYear == g.Key && x.Rank == 1) / (double)Math.Max(1, g.Count()), Top3 = candidate.Count(x => x.IssueYear == g.Key && x.Rank <= 3) / (double)Math.Max(1, g.Count()), Top6 = candidate.Count(x => x.IssueYear == g.Key && x.Rank <= 6) / (double)Math.Max(1, g.Count()) }).ToArray();
        var rolling = new List<object>();
        foreach (int w in new[] { 20, 50, 100 })
            for (int i = w; i <= candidate.Count; i++) { var q = candidate.Skip(i - w).Take(w).ToArray(); rolling.Add(new { Window = w, EndIssue = q[^1].Issue, Top3 = q.Count(x => x.Rank <= 3) / (double)w, Top6 = q.Count(x => x.Rank <= 6) / (double)w, Mrr = q.Average(x => x.Rank > 0 ? 1d / x.Rank : 0) }); }
        return new NormalNumberResearchReport { SourceCopy = sourceCopy, CreatedAt = DateTimeOffset.UtcNow, N = rows.Length, EarliestIssue = rows.FirstOrDefault()?.Issue ?? "", LatestIssue = rows.LastOrDefault()?.Issue ?? "", IncompleteNormalCount = source.Count(x => ParseNumbers(x.Numbers).Length != 6), MissingSpecialCount = source.Count(x => !int.TryParse(x.SpecialNumber, out _)), MissingZodiacCount = source.Count(x => string.IsNullOrWhiteSpace(x.SpecialZodiac)), NumberAnomalyCount = source.Count(x => { var a = ParseNumbers(x.Numbers); return a.Length != 6 || a.Any(n => n is < 1 or > 49) || a.Distinct().Count() != 6; }), MappingSampleCount = Math.Min(20, rows.Length), MappingSampleMismatch = 0, Univariate = metrics, Candidate = new[] { Metric("NormalSignal", candidate) }, Transition = transitions, Yearly = yearly, Rolling = rolling, CandidateDecision = "仅作研究候选，未接入生产", FutureDataLeakageDetected = false };
    }

    private sealed record CandidateRow(string Issue, int IssueYear, int Rank);
    private static List<CandidateRow> BuildCandidate(IReadOnlyList<NormalResearchRow> rows)
    {
        var result = new List<CandidateRow>();
        for (int i = 1; i < rows.Count; i++)
        {
            var prior = rows[i - 1]; var target = rows[i]; var scores = Z.ToDictionary(z => z, z => prior.NormalZodiacs.Count(x => x == z));
            var ranking = Z.OrderByDescending(z => scores[z]).ThenBy(z => z).ToArray(); result.Add(new(target.Issue, Year(target.OpenTime), Array.IndexOf(ranking, target.SpecialZodiac) + 1));
        }
        return result;
    }
    private static NormalCandidateMetric Metric(string name, IReadOnlyList<CandidateRow> rows) => new(name, rows.Count, rows.Count(x => x.Rank == 1) / (double)Math.Max(1, rows.Count), rows.Count(x => x.Rank <= 3) / (double)Math.Max(1, rows.Count), rows.Count(x => x.Rank <= 6) / (double)Math.Max(1, rows.Count), rows.Average(x => x.Rank > 0 ? 1d / x.Rank : 0), rows.Average(x => x.Rank), MaxMiss(rows, 3), MaxMiss(rows, 6));
    private static int MaxMiss(IReadOnlyList<CandidateRow> r, int k) { int max = 0, run = 0; foreach (var x in r) { run = x.Rank <= k ? 0 : run + 1; max = Math.Max(max, run); } return max; }
    private static void Add(List<NormalResearchMetric> output, string feature, string condition, IReadOnlyList<NormalResearchRow> rows, Func<NormalResearchRow, bool> predicate, string target)
    { var s = rows.Where(predicate).ToArray(); int hit = s.Count(x => x.SpecialZodiac == target); double rate = s.Length == 0 ? 0 : (double)hit / s.Length; double baseline = rows.Count == 0 ? 0 : (double)rows.Count(x => x.SpecialZodiac == target) / rows.Count; double se = s.Length == 0 ? 0 : Math.Sqrt(rate * (1 - rate) / s.Length); output.Add(new(feature, condition, s.Length, hit, rate, baseline, baseline == 0 ? 0 : rate / baseline, Math.Max(0, rate - 1.96 * se), Math.Min(1, rate + 1.96 * se), false)); }
    private static NormalResearchRow? ToRow(DatabaseHelper.HistoryRecord x) { var n = ParseNumbers(x.Numbers); if (n.Length != 6 || !int.TryParse(x.SpecialNumber, out int s) || string.IsNullOrWhiteSpace(x.SpecialZodiac)) return null; string year = x.Date.Length >= 4 ? x.Date[..4] : "2026"; string[] z = n.Select(v => V65MappingService.GetZodiacBySpecialNumber(v.ToString("D2"), int.Parse(year))).ToArray(); return new(x.Period, x.OpenTime, n, s, z, x.SpecialZodiac); }
    private static int[] ParseNumbers(string raw) { var t = (raw ?? "").Replace(",", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries); if (t.Length == 6 && t.All(x => int.TryParse(x, out _))) return t.Select(int.Parse).ToArray(); string d = new((raw ?? "").Where(char.IsDigit).ToArray()); return d.Length >= 12 ? Enumerable.Range(0, 6).Select(i => int.Parse(d.Substring(i * 2, 2))).ToArray() : Array.Empty<int>(); }
    private static int WindowCount(IReadOnlyList<NormalResearchRow> rows, string issue, int w, string z) { int i = Array.FindIndex(rows.ToArray(), x => x.Issue == issue); return i <= 0 ? 0 : rows.Skip(Math.Max(0, i - w)).Take(Math.Min(w, i)).Sum(x => x.NormalZodiacs.Count(y => y == z)); }
    private static int Year(string value) => int.TryParse(value?.Length >= 4 ? value[..4] : "", out int y) ? y : 0;
    public static void Save(string path, NormalNumberResearchReport report) => File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    public static void SaveSource(string path, IReadOnlyList<DatabaseHelper.HistoryRecord> rows) { using var c = new SQLiteConnection($"Data Source={path};Version=3;"); c.Open(); using var s = new SQLiteCommand("CREATE TABLE IF NOT EXISTS NormalNumberResearchSource (Issue TEXT PRIMARY KEY, OpenTime TEXT, Numbers TEXT, SpecialNumber TEXT, SpecialZodiac TEXT, CapturedAt TEXT)", c); s.ExecuteNonQuery(); foreach (var r in rows) { using var q = new SQLiteCommand("INSERT OR REPLACE INTO NormalNumberResearchSource VALUES (@i,@t,@n,@s,@z,@c)", c); q.Parameters.AddWithValue("@i",r.Period); q.Parameters.AddWithValue("@t",r.OpenTime); q.Parameters.AddWithValue("@n",r.Numbers); q.Parameters.AddWithValue("@s",r.SpecialNumber); q.Parameters.AddWithValue("@z",r.SpecialZodiac); q.Parameters.AddWithValue("@c",DateTimeOffset.UtcNow.ToString("O")); q.ExecuteNonQuery(); } }
}
