using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace 六合分析软件.MacroReasoning;

public sealed record WebsiteParsedSignal(string SourceId, int Issue, IReadOnlyList<string> Zodiacs, string RawText, string SourceHash);

public static class WebsiteLearningParser
{
    private static readonly string[] Zodiac = ["鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪"];
    public static WebsiteParsedSignal Parse(string text, string sourceId, string sourceHash)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("网站资料为空");
        var issue = Regex.Match(text, @"(?<!\d)(\d{1,4})期").Groups[1].Value;
        if (!int.TryParse(issue, out var n) || n <= 0) throw new InvalidDataException("网站资料缺少有效期号");
        if (IsPostResult(text)) throw new InvalidDataException("资料已包含开奖结果");
        var z = Zodiac.Where(text.Contains).OrderBy(x => text.IndexOf(x, StringComparison.Ordinal)).ToArray();
        if (z.Length == 0) throw new InvalidDataException("资料未识别到生肖");
        return new(sourceId, n, z, text, sourceHash);
    }
    public static bool IsPostResult(string text) => Regex.IsMatch(text, @"开\s*[:：]\s*(?![？?])[^\s<】）)]{1,12}");
    public static string NormalizeScript(string script)
    {
        var body = Regex.Replace(script, @"document\.writeln\(\s*(['""])(.*?)\1\s*\)\s*;?", "$2", RegexOptions.Singleline);
        return WebUtility.HtmlDecode(body).Replace("\\'", "'").Replace("\\\"", "\"");
    }
    public static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed class WebsiteLearningService(HttpClient? client = null)
{
    private readonly HttpClient http = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    public async Task<IReadOnlyList<WebsiteParsedSignal>> FetchAsync(Uri page, CancellationToken cancellationToken = default)
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
            try { list.Add(WebsiteLearningParser.Parse(normalized, source, WebsiteLearningParser.Sha256(raw))); }
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
    public static void Publish(long targetIssue)
    {
        var service = new WebsiteLearningService();
        var signals = service.FetchAsync(Page).GetAwaiter().GetResult();
        int shortIssue = checked((int)(targetIssue % 1000));
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
    }
}
