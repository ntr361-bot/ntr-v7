using System.Text.Json;
using 六合分析软件.MacroReasoning;

namespace 六合分析软件;

public sealed record P25ScheduledCheckResult(
    long TargetIssue,
    bool Complete,
    bool Published,
    bool AlreadyExists,
    string? EntryUrl,
    string[] PresentSources,
    string[] MissingSources,
    string Message,
    DateTimeOffset CheckedAt);

public static class P25ScheduledWebCheck
{
    private static readonly string[] RequiredSources = ["6x.js", "3bds.js", "x3x6m.js", "6x18mm.js"];
    private static readonly Uri[] CandidatePages =
    [
        new("https://62827.com/62.html"),
        new("https://1.62827.com/62.html"),
        new("https://a.62827.com/62.html"),
        new("https://12.62827.com/62.html"),
        new("https://ab.62827.com/62.html"),
        new("https://xn--hdcl2bk2m1bc.xn--gecrj9c/62.html"),
        new("https://x17.xn--hdcl2bk2m1bc.xn--gecrj9c:8443/62.html")
    ];

    public static P25ScheduledCheckResult Run(long targetIssue, string checkStatePath)
    {
        DateTimeOffset checkedAt = DateTimeOffset.Now;
        bool exists = DatabaseHelper.GetPredictionHistory(int.MaxValue).Any(row =>
            row.Issue == targetIssue.ToString() && row.ModelVersion == "P25-Web" && row.AnalysisPeriods == 25);
        if (exists)
        {
            return new P25ScheduledCheckResult(targetIssue, true, false, true, null,
                RequiredSources, [], "本期P25已存在，跳过网页检查", checkedAt);
        }

        var service = new WebsiteLearningService();
        WebsiteLearningFetchResult fetch = service.FetchFirstAvailableAsync(CandidatePages).GetAwaiter().GetResult();
        int shortIssue = checked((int)(targetIssue % 1000));
        if (!fetch.Success)
        {
            var unavailable = new P25ScheduledCheckResult(targetIssue, false, false, false, null,
                [], RequiredSources, WebsiteLearningService.BuildFailureSummary(fetch.Attempts), checkedAt);
            Save(checkStatePath, unavailable);
            return unavailable;
        }

        string[] present = fetch.Snapshots
            .Where(snapshot => snapshot.Issue == shortIssue && snapshot.WebsiteResultZodiac is null && snapshot.Zodiacs.Count > 0)
            .Select(snapshot => Path.GetFileName(snapshot.SourceId).ToLowerInvariant())
            .Where(source => RequiredSources.Contains(source, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(source => Array.FindIndex(RequiredSources, required => required.Equals(source, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        string[] missing = RequiredSources.Except(present, StringComparer.OrdinalIgnoreCase).ToArray();

        if (missing.Length > 0)
        {
            var incomplete = new P25ScheduledCheckResult(targetIssue, false, false, false, fetch.Page?.AbsoluteUri,
                present, missing, $"第{shortIssue}期资料未齐：已到 {present.Length}/4，缺少 {string.Join("、", missing)}", checkedAt);
            Save(checkStatePath, incomplete);
            return incomplete;
        }

        bool published = WebsiteLearningIntegration.Publish(targetIssue);
        var completed = new P25ScheduledCheckResult(targetIssue, true, published, !published, fetch.Page?.AbsoluteUri,
            present, [], published ? "四类网页资料齐全，已生成P25" : "四类网页资料齐全，本期P25已存在", checkedAt);
        Save(checkStatePath, completed);
        return completed;
    }

    private static void Save(string path, P25ScheduledCheckResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = true
            }));
            using JsonDocument _ = JsonDocument.Parse(File.ReadAllBytes(temp));
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
