using System.Text.Json;
using System.Text.Json.Nodes;

namespace 六合分析软件;

/// <summary>
/// 两条50期候选的正式前瞻旁路发布器。
/// 只允许给“尚未开奖”的期号写入，禁止开奖后补造前瞻记录。
/// </summary>
public static class Forward50PredictionPublisher
{
    public const long ForwardStartIssue = 2026257;
    private const int Window = 50;
    private static readonly string[] ZodiacOrder = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static int EnrichPending(string dailyOutputDirectory)
    {
        DatabaseHelper.InitializeDatabase();
        var history = DatabaseHelper.GetLatestHistory(int.MaxValue)
            .Where(x => long.TryParse(x.Period, out _) && !string.IsNullOrWhiteSpace(x.SpecialZodiac))
            .OrderBy(x => long.Parse(x.Period))
            .ToArray();
        if (history.Length == 0 || !Directory.Exists(dailyOutputDirectory)) return 0;

        long latestDrawIssue = long.Parse(history[^1].Period);
        int updated = 0;
        foreach (string path in Directory.EnumerateFiles(dailyOutputDirectory, "*.json"))
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            if (!long.TryParse(stem, out long issue) || issue < ForwardStartIssue || issue <= latestDrawIssue) continue;

            JsonObject root = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
                ?? throw new InvalidDataException($"第{issue}期预测文件无法解析");
            if (root["status"]?.GetValue<string>() != "success") continue;
            JsonObject ai = root["ai_zodiac"] as JsonObject
                ?? throw new InvalidDataException($"第{issue}期缺少AI预测记录");
            if (ai["regularity50"] is not null && ai["period50_fair"] is not null) continue;

            long prior = history.Select(x => long.Parse(x.Period)).LastOrDefault(x => x < issue);
            if (prior == 0) continue;
            using (DatabaseHelper.UseHistoryThroughIssue(prior))
            {
                string[] seq = DatabaseHelper.GetLatestHistory(int.MaxValue)
                    .Where(x => !string.IsNullOrWhiteSpace(x.SpecialZodiac))
                    .OrderBy(x => long.Parse(x.Period))
                    .Select(x => x.SpecialZodiac)
                    .ToArray();

                Add(ai, "regularity50", BuildRegularityScores(seq), issue, prior,
                    "Regularity50", "冻结公式 · 50期CV稳定度 · Forward前瞻观察");
                Add(ai, "period50_fair", BuildOldPeriodScores(seq), issue, prior,
                    "Period50-Fair", "冻结公式 · 旧50期周期 · 无偏同分裁决 · Forward前瞻观察");
            }

            AtomicWrite(path, root);
            updated++;
        }
        return updated;
    }

    private static void Add(JsonObject ai, string key, Dictionary<string, double> scores,
        long issue, long sourceIssue, string modelName, string description)
    {
        List<string> ranking = RankWithNeutralHash(scores, issue.ToString());
        string[] top3 = ranking.Take(3).ToArray();
        string[] top6 = ranking.Take(6).ToArray();
        int targetYear = checked((int)(issue / 1000));
        string yearPet = DatabaseHelper.GetYearPetPublic(targetYear.ToString());
        Dictionary<string, List<string>> map = DataCrawler.BuildShengXiaoMapPublic(yearPet);
        int[] numbers = top6.Where(map.ContainsKey).SelectMany(z => map[z])
            .Select(x => int.TryParse(x, out int n) ? n : 0)
            .Where(n => n > 0).Distinct().OrderBy(n => n).ToArray();

        ai[key] = new JsonObject
        {
            ["analysis_periods"] = Window,
            ["top3"] = JsonSerializer.SerializeToNode(top3),
            ["top6"] = JsonSerializer.SerializeToNode(top6),
            ["numbers"] = JsonSerializer.SerializeToNode(numbers),
            ["confidence"] = "前瞻观察",
            ["best_model"] = description,
            ["model_name"] = modelName,
            ["forward_status"] = "frozen",
            ["forward_start_issue"] = ForwardStartIssue,
            ["source_issue"] = sourceIssue.ToString(),
            ["full_ranking"] = JsonSerializer.SerializeToNode(ranking)
        };
    }

    private static Dictionary<string, double> BuildRegularityScores(IReadOnlyList<string> seq)
    {
        var slice = seq.Skip(Math.Max(0, seq.Count - Window)).ToList();
        var raw = new Dictionary<string, double>();
        foreach (string zodiac in ZodiacOrder)
        {
            var positions = slice.Select((z, i) => (z, i)).Where(x => x.z == zodiac).Select(x => x.i).ToList();
            var gaps = new List<int>();
            for (int i = 1; i < positions.Count; i++) gaps.Add(positions[i] - positions[i - 1]);
            if (gaps.Count >= 2)
            {
                double avg = gaps.Average();
                double variance = gaps.Select(g => Math.Pow(g - avg, 2)).Average();
                double cv = avg > 0 ? Math.Sqrt(variance) / avg : 1.0;
                raw[zodiac] = 1.0 / (1.0 + cv);
            }
            else raw[zodiac] = 0.5;
        }
        return MidRankPercentile(raw);
    }

    private static Dictionary<string, double> BuildOldPeriodScores(IReadOnlyList<string> seq)
    {
        var newestFirst = seq.Skip(Math.Max(0, seq.Count - Window)).Reverse().ToList();
        var result = new Dictionary<string, double>();
        foreach (string zodiac in ZodiacOrder)
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

    private static Dictionary<string, double> MidRankPercentile(Dictionary<string, double> raw)
    {
        var result = new Dictionary<string, double>();
        int n = raw.Count;
        foreach ((string zodiac, double value) in raw)
        {
            int less = raw.Values.Count(v => v < value - 1e-12);
            int equalOther = raw.Values.Count(v => Math.Abs(v - value) <= 1e-12) - 1;
            result[zodiac] = n <= 1 ? 0.5 : (less + equalOther * 0.5) / (n - 1.0);
        }
        return result;
    }

    private static List<string> RankWithNeutralHash(Dictionary<string, double> scores, string issue) =>
        scores.OrderByDescending(x => x.Value)
            .ThenBy(x => StableHash(issue + "|" + x.Key))
            .Select(x => x.Key).ToList();

    private static uint StableHash(string text)
    {
        uint hash = 2166136261;
        foreach (char c in text) { hash ^= c; hash *= 16777619; }
        return hash;
    }

    private static void AtomicWrite(string path, JsonObject value)
    {
        string temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, value.ToJsonString(JsonOptions));
            using JsonDocument parsed = JsonDocument.Parse(File.ReadAllBytes(temporary));
            if (parsed.RootElement.GetProperty("status").GetString() != "success")
                throw new InvalidDataException("Forward预测记录校验失败");
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
