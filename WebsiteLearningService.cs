using System.Net;
using System.Data.SQLite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace 六合分析软件.MacroReasoning;

public sealed record WebsiteParsedSignal(string SourceId, int Issue, IReadOnlyList<string> Zodiacs, string RawText, string SourceHash);
public sealed record WebsiteLearningIssueSnapshot(string SourceId, int Issue, IReadOnlyList<string> Zodiacs,
    string RawText, string SourceHash, string? WebsiteResultZodiac);
public sealed record WebsiteLearningSettlement(long CaptureId, string WebsiteResultZodiac, string? LocalResultZodiac,
    string Consistency, bool Top3Hit, bool Top6Hit, DateTimeOffset SettledAt);

public static class WebsiteLearningParser
{
    private static readonly string[] Zodiac = ["鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪"];
    private static readonly Regex IssueMarker = new(@"(?<!\d)(\d{1,4})期", RegexOptions.Compiled);
    public static WebsiteParsedSignal Parse(string text, string sourceId, string sourceHash)
    {
        var first = IssueMarker.Match(text ?? string.Empty);
        if (!first.Success || !int.TryParse(first.Groups[1].Value, out var issue) || issue <= 0)
            throw new InvalidDataException("网站资料缺少有效期号");
        return Parse(text, sourceId, sourceHash, issue);
    }
    public static WebsiteParsedSignal Parse(string text, string sourceId, string sourceHash, int expectedIssue)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("网站资料为空");
        if (expectedIssue <= 0) throw new ArgumentOutOfRangeException(nameof(expectedIssue));
        WebsiteLearningIssueSnapshot? target = ParseAll(text, sourceId, sourceHash)
            .FirstOrDefault(snapshot => snapshot.Issue == expectedIssue);
        if (target is null) throw new InvalidDataException($"网站资料缺少第{expectedIssue}期");
        if (target.WebsiteResultZodiac is not null) throw new InvalidDataException("资料已包含开奖结果");
        if (target.Zodiacs.Count == 0) throw new InvalidDataException("资料未识别到生肖");
        return new(sourceId, expectedIssue, target.Zodiacs, target.RawText, sourceHash);
    }
    public static IReadOnlyList<WebsiteLearningIssueSnapshot> ParseAll(string text, string sourceId, string sourceHash)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("网站资料为空");
        var matches = IssueMarker.Matches(text);
        var snapshots = new List<WebsiteLearningIssueSnapshot>(matches.Count);
        for (int index = 0; index < matches.Count; index++)
        {
            if (!int.TryParse(matches[index].Groups[1].Value, out int issue) || issue <= 0) continue;
            int start = matches[index].Index;
            int end = index + 1 < matches.Count ? matches[index + 1].Index : text.Length;
            string block = text[start..end];
            string[] zodiacs = Zodiac.Where(block.Contains).OrderBy(x => block.IndexOf(x, StringComparison.Ordinal)).ToArray();
            snapshots.Add(new(sourceId, issue, zodiacs, block, sourceHash, ParseWebsiteResultZodiac(block)));
        }
        return snapshots;
    }
    public static bool IsPostResult(string text)
    {
        string visible = WebUtility.HtmlDecode(Regex.Replace(text ?? string.Empty, @"<[^>]+>", " "));
        var result = Regex.Match(visible, @"开\s*[:：]\s*([^\s<】）)]{1,12})");
        return result.Success && !Regex.IsMatch(result.Groups[1].Value, @"^[？?]+0*$");
    }
    private static string? ParseWebsiteResultZodiac(string text)
    {
        if (!IsPostResult(text)) return null;
        string visible = WebUtility.HtmlDecode(Regex.Replace(text, @"<[^>]+>", " "));
        var result = Regex.Match(visible, @"开\s*[:：]\s*([^\s<】）)]{1,12})");
        return Zodiac.FirstOrDefault(zodiac => result.Groups[1].Value.Contains(zodiac, StringComparison.Ordinal));
    }
    public static string NormalizeScript(string script)
    {
        var body = Regex.Replace(script, @"document\.writeln\(\s*(['""])(.*?)\1\s*\)\s*;?", "$2", RegexOptions.Singleline);
        return WebUtility.HtmlDecode(body).Replace("\\'", "'").Replace("\\\"", "\"");
    }
    public static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed class WebsiteLearningArchive
{
    private readonly string connectionString;
    public WebsiteLearningArchive(string databasePath) => connectionString = $"Data Source={databasePath};Version=3;";

    public long SaveCapture(long issue, string sourceId, string sourceHash, string rawText,
        IReadOnlyList<string> zodiacs, DateTimeOffset capturedAt)
    {
        using var connection = Open();
        EnsureSchema(connection);
        using (var insert = new SQLiteCommand(@"INSERT OR IGNORE INTO WebsiteLearningCapture
            (Issue, SourceId, SourceHash, RawText, ZodiacsJson, CapturedAt)
            VALUES (@issue, @sourceId, @sourceHash, @rawText, @zodiacsJson, @capturedAt)", connection))
        {
            insert.Parameters.AddWithValue("@issue", issue);
            insert.Parameters.AddWithValue("@sourceId", sourceId);
            insert.Parameters.AddWithValue("@sourceHash", sourceHash);
            insert.Parameters.AddWithValue("@rawText", rawText);
            insert.Parameters.AddWithValue("@zodiacsJson", JsonSerializer.Serialize(zodiacs));
            insert.Parameters.AddWithValue("@capturedAt", capturedAt.ToString("O"));
            insert.ExecuteNonQuery();
        }
        using var read = new SQLiteCommand("SELECT Id FROM WebsiteLearningCapture WHERE Issue=@issue AND SourceId=@sourceId AND SourceHash=@sourceHash", connection);
        read.Parameters.AddWithValue("@issue", issue);
        read.Parameters.AddWithValue("@sourceId", sourceId);
        read.Parameters.AddWithValue("@sourceHash", sourceHash);
        return Convert.ToInt64(read.ExecuteScalar());
    }

    public WebsiteLearningSettlement Settle(long captureId, string websiteResultZodiac, string? localResultZodiac,
        DateTimeOffset settledAt)
    {
        using var connection = Open();
        EnsureSchema(connection);
        string zodiacsJson;
        using (var read = new SQLiteCommand("SELECT ZodiacsJson FROM WebsiteLearningCapture WHERE Id=@captureId", connection))
        {
            read.Parameters.AddWithValue("@captureId", captureId);
            zodiacsJson = Convert.ToString(read.ExecuteScalar()) ?? throw new InvalidDataException("网站资料档案不存在");
        }
        string[] zodiacs = JsonSerializer.Deserialize<string[]>(zodiacsJson) ?? [];
        string consistency = string.IsNullOrWhiteSpace(localResultZodiac) ? "Unavailable" :
            string.Equals(websiteResultZodiac, localResultZodiac, StringComparison.Ordinal) ? "Consistent" : "Conflict";
        bool top3Hit = zodiacs.Take(3).Contains(websiteResultZodiac, StringComparer.Ordinal);
        bool top6Hit = zodiacs.Take(6).Contains(websiteResultZodiac, StringComparer.Ordinal);
        using (var write = new SQLiteCommand(@"INSERT OR REPLACE INTO WebsiteLearningSettlement
            (CaptureId, WebsiteResultZodiac, LocalResultZodiac, Consistency, Top3Hit, Top6Hit, SettledAt)
            VALUES (@captureId, @websiteResult, @localResult, @consistency, @top3Hit, @top6Hit, @settledAt)", connection))
        {
            write.Parameters.AddWithValue("@captureId", captureId);
            write.Parameters.AddWithValue("@websiteResult", websiteResultZodiac);
            write.Parameters.AddWithValue("@localResult", (object?)localResultZodiac ?? DBNull.Value);
            write.Parameters.AddWithValue("@consistency", consistency);
            write.Parameters.AddWithValue("@top3Hit", top3Hit ? 1 : 0);
            write.Parameters.AddWithValue("@top6Hit", top6Hit ? 1 : 0);
            write.Parameters.AddWithValue("@settledAt", settledAt.ToString("O"));
            write.ExecuteNonQuery();
        }
        return new(captureId, websiteResultZodiac, localResultZodiac, consistency, top3Hit, top6Hit, settledAt);
    }

    private SQLiteConnection Open()
    {
        var connection = new SQLiteConnection(connectionString);
        connection.Open();
        return connection;
    }
    private static void EnsureSchema(SQLiteConnection connection)
    {
        new SQLiteCommand(@"CREATE TABLE IF NOT EXISTS WebsiteLearningCapture (
            Id INTEGER PRIMARY KEY AUTOINCREMENT, Issue INTEGER NOT NULL, SourceId TEXT NOT NULL,
            SourceHash TEXT NOT NULL, RawText TEXT NOT NULL, ZodiacsJson TEXT NOT NULL, CapturedAt TEXT NOT NULL,
            UNIQUE(Issue, SourceId, SourceHash))", connection).ExecuteNonQuery();
        new SQLiteCommand(@"CREATE TABLE IF NOT EXISTS WebsiteLearningSettlement (
            CaptureId INTEGER PRIMARY KEY, WebsiteResultZodiac TEXT NOT NULL, LocalResultZodiac TEXT NULL,
            Consistency TEXT NOT NULL, Top3Hit INTEGER NOT NULL, Top6Hit INTEGER NOT NULL, SettledAt TEXT NOT NULL,
            FOREIGN KEY(CaptureId) REFERENCES WebsiteLearningCapture(Id))", connection).ExecuteNonQuery();
    }
}

public sealed class WebsiteLearningService(HttpClient? client = null)
{
    private readonly HttpClient http = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    public async Task<IReadOnlyList<WebsiteParsedSignal>> FetchAsync(Uri page, int expectedIssue, CancellationToken cancellationToken = default)
    {
        var html = await http.GetStringAsync(page, cancellationToken);
        var sources = Regex.Matches(html, @"<script[^>]+src=['""]([^'""]+)", RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Value).Where(x => x.Contains("/bbs/") &&
                (x.Contains("6x.js") || x.Contains("3bds.js") || x.Contains("x3x6m.js") || x.Contains("6x18mm.js")))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var list = new List<WebsiteParsedSignal>();
        foreach (var source in sources)
        {
            var uri = new Uri(page, source);
            var raw = await http.GetStringAsync(uri, cancellationToken);
            var normalized = WebsiteLearningParser.NormalizeScript(raw);
            try { list.Add(WebsiteLearningParser.Parse(normalized, source, WebsiteLearningParser.Sha256(raw), expectedIssue)); }
            catch (InvalidDataException) { }
        }
        return list;
    }
    public static IReadOnlyList<string> Rank(IEnumerable<WebsiteParsedSignal> signals, int issue)
    {
        var counts = signals.Where(x => x.Issue == issue).SelectMany(x => x.Zodiacs)
            .GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return new[]{"鼠","牛","虎","兔","龙","蛇","马","羊","猴","鸡","狗","猪"}
            .OrderByDescending(x => counts.GetValueOrDefault(x)).ThenBy(x => Array.IndexOf(new[]{"鼠","牛","虎","兔","龙","蛇","马","羊","猴","鸡","狗","猪"}, x)).ToArray();
    }
}

public static class WebsiteLearningIntegration
{
    private static readonly Uri Page = new("https://x17.xn--hdcl2bk2m1bc.xn--gecrj9c:8443/62.html");
    public static bool Publish(long targetIssue)
    {
        if (DatabaseHelper.GetPredictionHistory(int.MaxValue).Any(row => row.Issue == targetIssue.ToString() &&
            row.ModelVersion == "P25-Web" && row.AnalysisPeriods == 25))
            return false;
        int shortIssue = checked((int)(targetIssue % 1000));
        var service = new WebsiteLearningService();
        var signals = service.FetchAsync(Page, shortIssue).GetAwaiter().GetResult();
        var ranking = WebsiteLearningService.Rank(signals, shortIssue);
        var usable = signals.Where(x => x.Issue == shortIssue).ToArray();
        if (usable.Length == 0) throw new InvalidDataException($"网站没有第{shortIssue}期资料");
        string[] top6 = ranking.Take(6).ToArray();
        string[] top3 = top6.Take(3).ToArray();
        string details = System.Text.Json.JsonSerializer.Serialize(new
        {
            source = "62827.com",
            issue = targetIssue,
            source_issue = shortIssue,
            captured_at = DateTimeOffset.Now,
            sources = usable.Select(x => new { x.SourceId, x.SourceHash, zodiacs = x.Zodiacs }).ToArray(),
            ranking = ranking.ToArray()
        });
        DatabaseHelper.SavePrediction(targetIssue.ToString(), string.Join(",", top3), string.Join(",", top6), "",
            "P25-Web", 25, details, "网站资料专家直接写入；开奖后由人工/自动复核", JsonSerializer.Serialize(ranking));
        return true;
    }
}
