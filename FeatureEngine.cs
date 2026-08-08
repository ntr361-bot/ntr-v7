using System.Text.Json.Serialization;

namespace 六合分析软件;

public sealed class ZodiacFeature
{
    public string Zodiac { get; init; } = "";
    public int Recent3Count { get; init; }
    public int Recent5Count { get; init; }
    public int Recent7Count { get; init; }
    public int Recent10Count { get; init; }
    public int Recent15Count { get; init; }
    public int Recent20Count { get; init; }
    public int Recent30Count { get; init; }
    public int Recent50Count { get; init; }
    public int Recent100Count { get; init; }
    public double Recent3Rate { get; init; }
    public double Recent5Rate { get; init; }
    public double Recent10Rate { get; init; }
    public double Recent20Rate { get; init; }
    public double Recent50Rate { get; init; }
    public double Recent100Rate { get; init; }
    public int Gap1RepeatCount { get; init; }
    public int Gap2RepeatCount { get; init; }
    public int Gap3RepeatCount { get; init; }
    public int Gap4RepeatCount { get; init; }
    public int Gap5RepeatCount { get; init; }
    public int Recent10Gap1RepeatCount { get; init; }
    public int Recent10Gap2RepeatCount { get; init; }
    public int Recent20Gap1RepeatCount { get; init; }
    public int Recent20Gap2RepeatCount { get; init; }
    public int ShortCycleRepeatCount { get; init; }
    public double RepeatFrequencyTrend { get; init; }
    public bool ShortForbidden { get; init; }
    public int CurrentOmission { get; init; }
    public double AverageOmission { get; init; }
    public int MaximumOmission { get; init; }
    public double OmissionDeviation { get; init; }
    public double OmissionRatio { get; init; }
    public int PreviousOmission { get; init; }
    public double OmissionStdDev { get; init; }
    public double Momentum5Vs20 { get; init; }
    public double Momentum10Vs50 { get; init; }
    public double Momentum20Vs100 { get; init; }
    public int CurrentStreak { get; init; }
    public bool LastDrawHit { get; init; }
    public double HistoricalRate { get; init; }
    public double ColorTrend { get; init; }
    public double ZodiacColorAffinity { get; init; }
    public double OmissionXMomentum5Vs20 { get; init; }
    public double OmissionXMomentum10Vs50 { get; init; }
    public double Recent7XShortForbidden { get; init; }
    public double RepeatXOmission { get; init; }
    public double LongXShortTrend { get; init; }
    public double OmissionRatioXRepeatTrend { get; init; }
    public double Recent10RateXHistoricalRate { get; init; }
    public double ColorAffinityXColorTrend { get; init; }

    public double[] ToVector() => new double[]
    {
        Recent3Count, Recent5Count, Recent7Count, Recent10Count, Recent15Count, Recent20Count,
        Recent30Count, Recent50Count, Recent100Count, Recent3Rate, Recent5Rate, Recent10Rate,
        Recent20Rate, Recent50Rate, Recent100Rate, Gap1RepeatCount, Gap2RepeatCount, Gap3RepeatCount,
        Gap4RepeatCount, Gap5RepeatCount, Recent10Gap1RepeatCount, Recent10Gap2RepeatCount,
        Recent20Gap1RepeatCount, Recent20Gap2RepeatCount, ShortCycleRepeatCount, RepeatFrequencyTrend,
        ShortForbidden ? 1d : 0d, CurrentOmission, AverageOmission, MaximumOmission, OmissionDeviation,
        OmissionRatio, PreviousOmission, OmissionStdDev, Momentum5Vs20, Momentum10Vs50,
        Momentum20Vs100, CurrentStreak, LastDrawHit ? 1d : 0d, HistoricalRate, ColorTrend,
        ZodiacColorAffinity, OmissionXMomentum5Vs20, OmissionXMomentum10Vs50,
        Recent7XShortForbidden, RepeatXOmission, LongXShortTrend,
        OmissionRatioXRepeatTrend, Recent10RateXHistoricalRate, ColorAffinityXColorTrend
    };
}

public static class FeatureEngine
{
    public static IReadOnlyList<string> FeatureNames { get; } = new[]
    {
        "recent_3_count", "recent_5_count", "recent_7_count", "recent_10_count", "recent_15_count",
        "recent_20_count", "recent_30_count", "recent_50_count", "recent_100_count",
        "recent_3_rate", "recent_5_rate", "recent_10_rate", "recent_20_rate", "recent_50_rate",
        "recent_100_rate", "gap_1_repeat_count", "gap_2_repeat_count", "gap_3_repeat_count",
        "gap_4_repeat_count", "gap_5_repeat_count", "recent_10_gap_1_repeat", "recent_10_gap_2_repeat",
        "recent_20_gap_1_repeat", "recent_20_gap_2_repeat", "short_cycle_repeat_count",
        "repeat_frequency_trend", "short_forbidden", "current_omission", "average_omission",
        "maximum_omission", "omission_deviation", "omission_ratio", "previous_omission",
        "omission_stddev", "momentum_5_vs_20", "momentum_10_vs_50", "momentum_20_vs_100",
        "current_streak", "last_draw_hit", "historical_rate", "color_trend", "zodiac_color_affinity",
        "omission_x_momentum_5_20", "omission_x_momentum_10_50", "recent_7_x_short_forbidden",
        "repeat_x_omission", "long_x_short_trend", "omission_ratio_x_repeat_trend",
        "recent_10_rate_x_historical_rate", "color_affinity_x_color_trend"
    };
    private static readonly string[] ZodiacOrder = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };

    public static IReadOnlyList<ZodiacFeature> BuildFeatures(
        IReadOnlyList<DatabaseHelper.HistoryRecord> history, int window = 0)
    {
        var draws = Normalize(history);
        if (window > 0) draws = draws.TakeLast(window).ToList();
        return ZodiacOrder.Select(z => BuildOne(draws, z)).ToList();
    }

    public static ZodiacFeature? BuildFeature(
        IReadOnlyList<DatabaseHelper.HistoryRecord> history, string zodiac, int window = 0)
    {
        if (!ZodiacOrder.Contains(zodiac)) return null;
        var draws = Normalize(history);
        if (window > 0) draws = draws.TakeLast(window).ToList();
        return BuildOne(draws, zodiac);
    }

    private static ZodiacFeature BuildOne(IReadOnlyList<DatabaseHelper.HistoryRecord> draws, string zodiac)
    {
        int Count(int n) => draws.TakeLast(Math.Min(n, draws.Count)).Count(x => x.SpecialZodiac == zodiac);
        double Rate(int n) => draws.Count == 0 ? 0 : Count(n) / (double)Math.Min(n, draws.Count);
        int Gap(int gap)
        {
            int total = 0;
            for (int i = gap; i < draws.Count; i++)
                if (draws[i].SpecialZodiac == zodiac && draws[i - gap].SpecialZodiac == zodiac) total++;
            return total;
        }
        int omission = 0;
        for (int i = draws.Count - 1; i >= 0 && draws[i].SpecialZodiac != zodiac; i--) omission++;
        var omissions = OmissionRuns(draws, zodiac);
        double avg = omissions.Count == 0 ? draws.Count : omissions.Average();
        int max = omissions.Count == 0 ? draws.Count : omissions.Max();
        double std = omissions.Count == 0 ? 0 : Math.Sqrt(omissions.Average(x => Math.Pow(x - avg, 2)));
        int previousOmission = omissions.Count < 2 ? 0 : omissions[^2];
        int gap1 = Gap(1), gap2 = Gap(2), gap3 = Gap(3), gap4 = Gap(4), gap5 = Gap(5);
        var recent10 = draws.TakeLast(Math.Min(10, draws.Count)).ToList();
        var recent20 = draws.TakeLast(Math.Min(20, draws.Count)).ToList();
        var previous20 = draws.Skip(Math.Max(0, draws.Count - 40)).Take(Math.Min(20, Math.Max(0, draws.Count - 20))).ToList();
        int currentStreak = 0;
        for (int i = draws.Count - 1; i >= 0 && draws[i].SpecialZodiac == zodiac; i--) currentStreak++;
        double colorTrend = ComputeColorTrend(draws.TakeLast(Math.Min(30, draws.Count)));
        var zodiacDraws = draws.TakeLast(Math.Min(100, draws.Count)).Where(x => x.SpecialZodiac == zodiac).ToList();
        double zodiacColorAffinity = ComputeDominantColorShare(zodiacDraws);
        double recent5Rate = Rate(5), recent10Rate = Rate(10), recent20Rate = Rate(20);
        double recent50Rate = Rate(50), recent100Rate = Rate(100);
        double momentum5Vs20 = recent5Rate - recent20Rate;
        double momentum10Vs50 = recent10Rate - recent50Rate;
        double momentum20Vs100 = recent20Rate - recent100Rate;
        double omissionRatio = avg <= 0 ? 0 : omission / avg;
        int shortCycle = RepeatCount(recent20, zodiac, 1) + RepeatCount(recent20, zodiac, 2);
        double repeatTrend = shortCycle - RepeatCount(previous20, zodiac, 1) - RepeatCount(previous20, zodiac, 2);
        double historicalRate = draws.Count == 0 ? 0 : draws.Count(x => x.SpecialZodiac == zodiac) / (double)draws.Count;
        return new ZodiacFeature
        {
            Zodiac = zodiac, Recent3Count = Count(3), Recent5Count = Count(5), Recent7Count = Count(7),
            Recent10Count = Count(10), Recent15Count = Count(15), Recent20Count = Count(20),
            Recent30Count = Count(30), Recent50Count = Count(50), Recent100Count = Count(100),
            Recent3Rate = Rate(3), Recent5Rate = recent5Rate, Recent10Rate = recent10Rate, Recent20Rate = recent20Rate,
            Recent50Rate = recent50Rate, Recent100Rate = recent100Rate,
            Gap1RepeatCount = gap1, Gap2RepeatCount = gap2, Gap3RepeatCount = gap3,
            Gap4RepeatCount = gap4, Gap5RepeatCount = gap5,
            Recent10Gap1RepeatCount = RepeatCount(recent10, zodiac, 1),
            Recent10Gap2RepeatCount = RepeatCount(recent10, zodiac, 2),
            Recent20Gap1RepeatCount = RepeatCount(recent20, zodiac, 1),
            Recent20Gap2RepeatCount = RepeatCount(recent20, zodiac, 2),
            ShortCycleRepeatCount = shortCycle, RepeatFrequencyTrend = repeatTrend,
            ShortForbidden = Count(5) >= 2, CurrentOmission = omission, AverageOmission = avg, MaximumOmission = max,
            OmissionDeviation = omission - avg, OmissionRatio = omissionRatio,
            PreviousOmission = previousOmission, OmissionStdDev = std,
            Momentum5Vs20 = momentum5Vs20, Momentum10Vs50 = momentum10Vs50,
            Momentum20Vs100 = momentum20Vs100, CurrentStreak = currentStreak,
            LastDrawHit = draws.Count > 0 && draws[^1].SpecialZodiac == zodiac,
            HistoricalRate = historicalRate, ColorTrend = colorTrend, ZodiacColorAffinity = zodiacColorAffinity,
            OmissionXMomentum5Vs20 = omission * momentum5Vs20,
            OmissionXMomentum10Vs50 = omission * momentum10Vs50,
            Recent7XShortForbidden = Count(7) * (Count(5) >= 2 ? 1d : 0d),
            RepeatXOmission = shortCycle * omissionRatio,
            LongXShortTrend = momentum20Vs100 * momentum5Vs20,
            OmissionRatioXRepeatTrend = omissionRatio * repeatTrend,
            Recent10RateXHistoricalRate = recent10Rate * historicalRate,
            ColorAffinityXColorTrend = zodiacColorAffinity * colorTrend
        };
    }

    private static double ComputeColorTrend(IEnumerable<DatabaseHelper.HistoryRecord> records)
    {
        int red = 0, blue = 0, green = 0;
        foreach (var record in records)
        {
            if (!int.TryParse(record.SpecialNumber, out int number)) continue;
            switch (number % 3) { case 0: red++; break; case 1: blue++; break; default: green++; break; }
        }
        int total = red + blue + green;
        return total == 0 ? 0 : (red - blue) / (double)total;
    }

    private static double ComputeDominantColorShare(IEnumerable<DatabaseHelper.HistoryRecord> records)
    {
        int[] colors = new int[3];
        foreach (var record in records)
            if (int.TryParse(record.SpecialNumber, out int number)) colors[Math.Abs(number % 3)]++;
        int total = colors.Sum();
        return total == 0 ? 0 : colors.Max() / (double)total;
    }

    private static int RepeatCount(IReadOnlyList<DatabaseHelper.HistoryRecord> draws, string zodiac, int gap)
    {
        int count = 0;
        for (int i = gap; i < draws.Count; i++) if (draws[i].SpecialZodiac == zodiac && draws[i - gap].SpecialZodiac == zodiac) count++;
        return count;
    }

    private static List<int> OmissionRuns(IReadOnlyList<DatabaseHelper.HistoryRecord> draws, string zodiac)
    {
        var runs = new List<int>(); int gap = 0;
        foreach (var draw in draws)
        {
            if (draw.SpecialZodiac == zodiac) { runs.Add(gap); gap = 0; }
            else gap++;
        }
        if (gap > 0) runs.Add(gap);
        return runs;
    }

    private static List<DatabaseHelper.HistoryRecord> Normalize(IReadOnlyList<DatabaseHelper.HistoryRecord> history)
    {
        var list = history.Where(x => !string.IsNullOrWhiteSpace(x.SpecialZodiac)).ToList();
        if (list.All(x => int.TryParse(x.Period, out _))) return list.OrderBy(x => int.Parse(x.Period)).ToList();
        return list.AsEnumerable().Reverse().ToList();
    }
}
