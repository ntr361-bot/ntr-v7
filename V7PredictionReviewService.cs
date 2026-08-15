using System.Globalization;

namespace 六合分析软件;

/// <summary>Explains V7 probability snapshots without applying the V6.5 score parser.</summary>
public static class V7PredictionReviewService
{
    public static string BuildReview(string scoreDetails, string predictedTop3, string actualZodiac)
    {
        var scores = ParseScores(scoreDetails);
        if (scores.Count != 12 || !scores.TryGetValue(actualZodiac, out double actual))
            return "V7复盘：缺少完整V7概率快照，无法归因";

        var ranking = scores.OrderByDescending(x => x.Value).ThenBy(x => x.Key).ToList();
        int rank = ranking.FindIndex(x => x.Key == actualZodiac) + 1;
        bool top3 = predictedTop3.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(actualZodiac, StringComparer.Ordinal);
        if (top3)
            return $"V7复盘：实际{actualZodiac}排名第{rank}，Top3命中；保留概率快照作为学习样本";

        double cutoff = ranking[2].Value;
        double gap = Math.Max(0, cutoff - actual);
        return $"V7复盘：实际{actualZodiac}排名第{rank}，Top3未命中；距Top3概率差{gap:P2}；仅加入V7独立学习样本";
    }

    private static Dictionary<string, double> ParseScores(string details)
    {
        string scorePart = (details ?? "").Split('|')[0];
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (string item in scorePart.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = item.Split(':', 2);
            if (parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                result[parts[0].Trim()] = value;
        }
        return result;
    }
}
