namespace 六合分析软件;

/// <summary>
/// 将 V7 的生肖概率转换为七个重点号码。只消费预测前可见的开奖记录，
/// 不参与生肖排序，也不改变任何模型权重。
/// </summary>
public sealed record V7RecommendedNumberSelection(
    string Numbers,
    string Details,
    string MappingSnapshotJson);

public static class V7RecommendedNumberService
{
    public const int FocusNumberCount = 7;

    public static V7RecommendedNumberSelection Select(
        string targetIssue,
        IEnumerable<(string Zodiac, double Probability)> ranking,
        IReadOnlyList<DatabaseHelper.HistoryRecord> history)
    {
        DateTime targetDate = ResolveTargetDate(targetIssue, history);
        var top3 = ranking
            .Where(item => !string.IsNullOrWhiteSpace(item.Zodiac) && double.IsFinite(item.Probability) && item.Probability >= 0)
            .OrderByDescending(item => item.Probability).ThenBy(item => item.Zodiac)
            .GroupBy(item => item.Zodiac).Select(group => group.First()).Take(3).ToArray();
        if (top3.Length == 0)
            return new V7RecommendedNumberSelection("", "", V65MappingService.CreateSnapshot(targetIssue, targetDate));

        IReadOnlyDictionary<string, IReadOnlyList<string>> map = V65MappingService.GetZodiacNumberMap(V65MappingService.GetLunarYear(targetDate));
        int[] historicalNumbers = history.Select(item => int.TryParse(item.SpecialNumber, out int number) && number is >= 1 and <= 49 ? number : 0)
            .Where(number => number > 0).ToArray();
        double probabilityTotal = top3.Sum(item => item.Probability);
        var candidates = new List<Candidate>();
        foreach ((string zodiac, double probability) in top3)
        {
            if (!map.TryGetValue(zodiac, out IReadOnlyList<string>? numbers)) continue;
            foreach (string text in numbers)
            {
                if (!int.TryParse(text, out int number)) continue;
                candidates.Add(new Candidate(number, zodiac, Score(number, probability, probabilityTotal, historicalNumbers)));
            }
        }

        var selected = new List<Candidate>();
        foreach ((string zodiac, _) in top3)
        {
            Candidate? best = candidates.Where(candidate => candidate.Zodiac == zodiac)
                .OrderByDescending(candidate => candidate.Score).ThenBy(candidate => candidate.Number).FirstOrDefault();
            if (best is not null) selected.Add(best);
        }
        selected.AddRange(candidates.OrderByDescending(candidate => candidate.Score).ThenBy(candidate => candidate.Number)
            .Where(candidate => selected.All(chosen => chosen.Number != candidate.Number))
            .Take(Math.Max(0, FocusNumberCount - selected.Count)));
        Candidate[] final = selected.OrderByDescending(candidate => candidate.Score).ThenBy(candidate => candidate.Number)
            .Take(FocusNumberCount).ToArray();
        return new V7RecommendedNumberSelection(
            string.Join(',', final.Select(candidate => candidate.Number.ToString("D2"))),
            string.Join(';', final.Select(candidate => $"{candidate.Number:D2}({candidate.Zodiac})={candidate.Score:F3}")),
            V65MappingService.CreateSnapshot(targetIssue, targetDate));
    }

    private static double Score(int number, double probability, double probabilityTotal, IReadOnlyList<int> history)
    {
        double zodiac = probabilityTotal > 0 ? probability / probabilityTotal : 1d / 3d;
        double expected = Math.Max(1d, history.Count / 49d);
        int total = history.Count(value => value == number);
        int recent10 = history.Take(10).Count(value => value == number);
        int missing = history.TakeWhile(value => value != number).Count();
        double frequency = Math.Min(2d, total / expected) / 2d;
        double recent = Math.Min(2d, recent10 / Math.Max(1d, Math.Min(10, history.Count) / 49d)) / 2d;
        double interval = total > 0 ? history.Count / (double)total : 49d;
        double omission = Math.Max(0d, 1d - Math.Abs(missing - interval) / Math.Max(1d, interval));
        return zodiac * .60d + frequency * .15d + recent * .15d + omission * .10d;
    }

    private static DateTime ResolveTargetDate(string targetIssue, IReadOnlyList<DatabaseHelper.HistoryRecord> history)
    {
        if (DateTime.TryParseExact(targetIssue, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out DateTime datedIssue))
            return datedIssue;
        DatabaseHelper.HistoryRecord? latest = history.FirstOrDefault();
        string value = latest?.OpenTime ?? latest?.Date ?? "";
        return DateTime.TryParse(value, out DateTime lastDraw) ? lastDraw.Date.AddDays(1) : DateTime.Today;
    }

    private sealed record Candidate(int Number, string Zodiac, double Score);
}
