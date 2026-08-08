namespace 六合分析软件;

public sealed record ColorBacktestRow(
    string Period,
    string Actual,
    string Main,
    string Defense,
    string Excluded,
    bool MainHit,
    bool MainDefenseHit,
    bool ExclusionSuccess);

public sealed class ColorBacktestReport
{
    public int Warmup { get; init; }
    public List<ColorBacktestRow> Rows { get; init; } = new();
    public int Samples => Rows.Count;
    public int MainHits => Rows.Count(x => x.MainHit);
    public int MainDefenseHits => Rows.Count(x => x.MainDefenseHit);
    public int ExclusionSuccesses => Rows.Count(x => x.ExclusionSuccess);
    public double MainHitRate => Samples == 0 ? 0 : MainHits / (double)Samples;
    public double MainDefenseHitRate => Samples == 0 ? 0 : MainDefenseHits / (double)Samples;
    public double ExclusionSuccessRate => Samples == 0 ? 0 : ExclusionSuccesses / (double)Samples;
    public int MaximumConsecutiveMainDefenseMisses { get; init; }
}

public static class ColorBacktestEngine
{
    public static ColorBacktestReport Run(IReadOnlyList<DatabaseHelper.HistoryRecord> history, int warmup = 100)
    {
        var draws = history.Where(x => int.TryParse(x.SpecialNumber, out _))
            .OrderBy(x => long.TryParse(x.Period, out long period) ? period : long.MaxValue)
            .ToList();
        int start = Math.Clamp(warmup, 1, draws.Count);
        var rows = new List<ColorBacktestRow>();
        int currentMisses = 0, maximumMisses = 0;
        for (int target = start; target < draws.Count; target++)
        {
            var prediction = ColorEngine.Predict(draws.Take(target).ToList());
            string actual = ColorEngine.ColorOf(draws[target].SpecialNumber);
            bool mainHit = actual == prediction.Main;
            bool covered = mainHit || actual == prediction.Defense;
            bool exclusionSuccess = actual != prediction.Excluded;
            currentMisses = covered ? 0 : currentMisses + 1;
            maximumMisses = Math.Max(maximumMisses, currentMisses);
            rows.Add(new ColorBacktestRow(draws[target].Period, actual, prediction.Main, prediction.Defense,
                prediction.Excluded, mainHit, covered, exclusionSuccess));
        }
        return new ColorBacktestReport { Warmup = start, Rows = rows, MaximumConsecutiveMainDefenseMisses = maximumMisses };
    }
}
