namespace 六合分析软件;

public static class EvaluationPipeline
{
    private static readonly int[] Windows = { 20, 50, 100 };
    private static readonly string[] ZodiacOrder = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };

    public static EvaluationReport Evaluate(IReadOnlyList<ReplayPredictionSnapshot> predictions, int randomSeed = 6501, int monteCarloIterations = 10000)
    {
        AssertTargetSetIsUniform(predictions);
        string[] models = predictions.Select(row => row.ModelId).Distinct(StringComparer.Ordinal).ToArray();
        string[] common = predictions.GroupBy(row => row.TargetIssue).Where(group => models.All(model => group.Any(row => row.ModelId == model))).Select(group => group.Key).OrderBy(ParseIssue).ToArray();
        var commonRows = predictions.Where(row => common.Contains(row.TargetIssue)).ToArray();
        var summaries = models.Select(model => Summarize(model, commonRows.Where(row => row.ModelId == model).ToArray(), common.Length)).ToArray();
        string v2 = HistoricalReplayModelIds.FrozenV2;
        ReplayPredictionSnapshot[] v2Rows = commonRows.Where(row => row.ModelId == v2).OrderBy(row => ParseIssue(row.TargetIssue)).ToArray();
        var baseRows = commonRows.Where(row => row.ModelId is HistoricalReplayModelIds.Period50 or HistoricalReplayModelIds.Period100 or HistoricalReplayModelIds.AllHistory).ToArray();
        return new(summaries, common, predictions.Select(row => row.TargetIssue).Distinct().Count() * models.Length - predictions.Count, false,
            RescueHarm(baseRows, v2Rows), Rolling(commonRows), Yearly(commonRows), Relationships(commonRows), RankChanges(commonRows),
            Bins(v2Rows, "Consensus", row => row.ConsensusScore, new[] { 0d, .2, .4, .6, .8, 1d }), RiskBins(v2Rows, baseRows), ConfidenceBins(v2Rows),
            Bootstrap(summaries, commonRows, randomSeed), Splits(commonRows), MonteCarlo(v2Rows, randomSeed, monteCarloIterations), Paired(commonRows, v2), McNemar(commonRows, v2));
    }

    public static void AssertTargetSetIsUniform(IReadOnlyList<ReplayPredictionSnapshot> predictions)
    {
        foreach (var group in predictions.GroupBy(row => row.TargetIssue))
        {
            if (group.Any(row => row.Ranking.Count != 12 || row.Ranking.Distinct().Count() != 12)) throw new InvalidDataException($"期号 {group.Key} 存在不完整预测。");
            if (group.Any(row => ParseIssue(row.HistoryCutoffIssue) >= ParseIssue(group.Key))) throw new InvalidDataException($"期号 {group.Key} 的 cutoff 未早于目标期。");
        }
    }

    private static ReplayMetricSummary Summarize(string model, IReadOnlyList<ReplayPredictionSnapshot> rows, int commonCount)
    {
        int[] ranks = rows.Where(row => row.ActualRank is > 0).Select(row => row.ActualRank!.Value).ToArray();
        return new(model, rows.Count, commonCount - rows.Count, rows.Count(row => row.Top1Hit == true), rows.Count(row => row.Top3Hit == true), rows.Count(row => row.Top6Hit == true), Rate(rows, row => row.Top1Hit), Rate(rows, row => row.Top3Hit), Rate(rows, row => row.Top6Hit), rows.Select(row => row.ReciprocalRank ?? 0).DefaultIfEmpty().Average(), ranks.DefaultIfEmpty().Average(), Median(ranks), MaxMiss(rows, row => row.Top1Hit), MaxMiss(rows, row => row.Top3Hit), MaxMiss(rows, row => row.Top6Hit));
    }

    private static IReadOnlyList<RescueHarmSummary> RescueHarm(IReadOnlyList<ReplayPredictionSnapshot> bases, IReadOnlyList<ReplayPredictionSnapshot> v2)
    {
        int rescueOpportunity = 0, rescueSuccess = 0, harmOpportunity = 0, harm = 0, strongRescue = 0, strongHarm = 0;
        foreach (var group in bases.GroupBy(row => row.TargetIssue))
        {
            var b = group.ToDictionary(row => row.ModelId); var v = v2.SingleOrDefault(row => row.TargetIssue == group.Key); if (v is null) continue;
            int[] ranks = b.Values.Select(row => row.ActualRank ?? 13).ToArray(); int vr = v.ActualRank ?? 13;
            if (ranks.All(rank => rank > 6)) { rescueOpportunity++; if (vr <= 6) rescueSuccess++; }
            if (ranks.Any(rank => rank <= 6)) { harmOpportunity++; if (vr > 6) harm++; }
            if (ranks.All(rank => rank >= 9) && vr <= 6) strongRescue++;
            if (ranks.Count(rank => rank <= 6) >= 2 && vr > 6) strongHarm++;
        }
        return new[] { new RescueHarmSummary(HistoricalReplayModelIds.FrozenV2, rescueOpportunity, rescueSuccess, Rate(rescueSuccess, rescueOpportunity), harmOpportunity, harm, Rate(harm, harmOpportunity), strongRescue, strongHarm) };
    }

    private static IReadOnlyList<RollingWindowSummary> Rolling(IReadOnlyList<ReplayPredictionSnapshot> rows) => rows.GroupBy(row => row.ModelId).SelectMany(group => Windows.Where(size => group.Count() >= size).Select(size => { var ordered = group.OrderBy(row => ParseIssue(row.TargetIssue)).ToArray(); var windows = Enumerable.Range(0, ordered.Length - size + 1).Select(i => { var sample = ordered.Skip(i).Take(size).ToArray(); return (Start: sample[0].TargetIssue, End: sample[^1].TargetIssue, Top3: Rate(sample.Count(row => row.Top3Hit == true), size), Top6: Rate(sample.Count(row => row.Top6Hit == true), size)); }).ToArray(); var best = windows.OrderByDescending(x => x.Top6).ThenByDescending(x => x.Top3).First(); var worst = windows.OrderBy(x => x.Top6).ThenBy(x => x.Top3).First(); return new RollingWindowSummary(group.Key, size, best.Start, best.End, best.Top3, best.Top6, worst.Start, worst.End, worst.Top3, worst.Top6); })).ToArray();

    private static IReadOnlyList<YearlyMetricSummary> Yearly(IReadOnlyList<ReplayPredictionSnapshot> rows) => rows.GroupBy(row => (row.ModelId, Year: row.TargetIssue[..4])).Select(group => { var s = Summarize(group.Key.ModelId, group.ToArray(), group.Count()); return new YearlyMetricSummary(group.Key.ModelId, group.Key.Year, s.SampleCount, s.Top1Rate, s.Top3Rate, s.Top6Rate, s.Mrr, s.MeanRank, s.MedianRank, s.MaxTop3MissStreak, s.MaxTop6MissStreak); }).OrderBy(row => row.Year).ThenBy(row => row.ModelId).ToArray();

    private static IReadOnlyList<SplitMetricSummary> Splits(IReadOnlyList<ReplayPredictionSnapshot> rows)
    {
        var output = new List<SplitMetricSummary>();
        foreach (var group in rows.GroupBy(row => row.ModelId))
        {
            var ordered = group.OrderBy(row => ParseIssue(row.TargetIssue)).ToArray();
            int train = ordered.Length * 60 / 100, validation = ordered.Length * 20 / 100;
            foreach (var part in new[] { (Name: "Training", Start: 0, Count: train), (Name: "Validation", Start: train, Count: validation), (Name: "Holdout", Start: train + validation, Count: ordered.Length - train - validation) })
            {
                var sample = ordered.Skip(part.Start).Take(part.Count).ToArray(); var summary = Summarize(group.Key, sample, sample.Length);
                output.Add(new(group.Key, part.Name, sample.FirstOrDefault()?.TargetIssue ?? "", sample.LastOrDefault()?.TargetIssue ?? "", sample.Length, summary.Top1Rate, summary.Top3Rate, summary.Top6Rate, summary.Mrr, summary.MeanRank));
            }
        }
        return output;
    }

    private static IReadOnlyList<ModelRelationshipSummary> Relationships(IReadOnlyList<ReplayPredictionSnapshot> rows)
    {
        string[] models = rows.Select(row => row.ModelId).Distinct().ToArray(); var result = new List<ModelRelationshipSummary>();
        for (int i = 0; i < models.Length; i++) for (int j = i + 1; j < models.Length; j++) { var pairs = rows.GroupBy(row => row.TargetIssue).Select(g => (A: g.SingleOrDefault(x => x.ModelId == models[i]), B: g.SingleOrDefault(x => x.ModelId == models[j]))).Where(x => x.A is not null && x.B is not null).Select(x => (x.A!, x.B!)).ToArray(); if (pairs.Length > 0) result.Add(new(models[i], models[j], Spearman(pairs.Select(x => x.Item1.Ranking).ToArray(), pairs.Select(x => x.Item2.Ranking).ToArray()), pairs.Average(x => Overlap(x.Item1, x.Item2, 3)), pairs.Average(x => Overlap(x.Item1, x.Item2, 6)))); }
        return result;
    }

    private static IReadOnlyList<RankChangeSummary> RankChanges(IReadOnlyList<ReplayPredictionSnapshot> rows) => new[] { HistoricalReplayModelIds.BaseAverage }.Select(baseModel => { var pairs = rows.GroupBy(row => row.TargetIssue).Select(g => new { B = g.SingleOrDefault(x => x.ModelId == baseModel), V = g.SingleOrDefault(x => x.ModelId == HistoricalReplayModelIds.FrozenV2) }).Where(x => x.B is not null && x.V is not null).ToArray(); var changes = pairs.Select(x => (Base: x.B!.ActualRank ?? 13, V2: x.V!.ActualRank ?? 13, Same3: x.B.Ranking.Take(3).SequenceEqual(x.V.Ranking.Take(3)), Same6: x.B.Ranking.Take(6).SequenceEqual(x.V.Ranking.Take(6)))).ToArray(); return new RankChangeSummary(baseModel, HistoricalReplayModelIds.FrozenV2, changes.Count(x => x.Base != x.V2), changes.Count(x => x.V2 < x.Base), changes.Count(x => x.V2 > x.Base), changes.Length == 0 ? 0 : changes.Average(x => x.Base - x.V2), changes.Length == 0 ? 0 : changes.Max(x => x.Base - x.V2), changes.Length == 0 ? 0 : changes.Max(x => x.V2 - x.Base), changes.Count(x => x.Same3), changes.Count(x => x.Same6)); }).ToArray();

    private static IReadOnlyList<BinMetricSummary> Bins(IReadOnlyList<ReplayPredictionSnapshot> rows, string name, Func<ReplayPredictionSnapshot, double?> selector, double[] edges) => Enumerable.Range(0, edges.Length - 1).Select(i => { var selected = rows.Where(row => { double? value = selector(row); return value.HasValue && value.Value >= edges[i] && (i == edges.Length - 2 ? value.Value <= edges[i + 1] : value.Value < edges[i + 1]); }).ToArray(); return Bin(name, $"[{edges[i]:0.0},{edges[i + 1]:0.0}{(i == edges.Length - 2 ? "]" : ")")}", selected); }).ToArray();
    private static IReadOnlyList<BinMetricSummary> RiskBins(IReadOnlyList<ReplayPredictionSnapshot> rows, IReadOnlyList<ReplayPredictionSnapshot> bases) => new[] { (0d, .33, "Low"), (.33, .66, "Medium"), (.66, 1.01, "High") }.Select(x => { var selected = rows.Where(row => row.JointFailureRisk.HasValue && row.JointFailureRisk.Value >= x.Item1 && row.JointFailureRisk.Value < x.Item2).ToArray(); int joint = selected.Count(row => bases.Where(b => b.TargetIssue == row.TargetIssue).All(b => b.Top6Hit == false)); return Bin("JointFailureRisk", x.Item3, selected) with { JointFailureCount = joint, JointFailureRate = Rate(joint, selected.Length) }; }).ToArray();
    private static IReadOnlyList<BinMetricSummary> ConfidenceBins(IReadOnlyList<ReplayPredictionSnapshot> rows) => new[] { "Low", "Medium", "High" }.Select(name => Bin("Confidence", name, rows.Where(row => string.Equals(row.Confidence, name, StringComparison.OrdinalIgnoreCase)).ToArray())).ToArray();
    private static BinMetricSummary Bin(string model, string bin, IReadOnlyList<ReplayPredictionSnapshot> rows) { var s = Summarize(model, rows, rows.Count); return new(model, bin, rows.Count, s.Top3Rate, s.Top6Rate, s.Mrr, s.MeanRank); }

    private static IReadOnlyList<ConfidenceIntervalSummary> Bootstrap(IReadOnlyList<ReplayMetricSummary> summaries, IReadOnlyList<ReplayPredictionSnapshot> rows, int seed) { var random = new Random(seed); var output = new List<ConfidenceIntervalSummary>(); foreach (var model in summaries.Select(x => x.ModelId)) { var source = rows.Where(x => x.ModelId == model).ToArray(); foreach (string metric in new[] { "Top3", "Top6", "MRR", "MeanRank" }) { var values = new double[1000]; for (int b = 0; b < values.Length; b++) { var sample = Enumerable.Range(0, source.Length).Select(_ => source[random.Next(source.Length)]).ToArray(); values[b] = metric switch { "Top3" => Rate(sample.Count(x => x.Top3Hit == true), sample.Length), "Top6" => Rate(sample.Count(x => x.Top6Hit == true), sample.Length), "MRR" => sample.Average(x => x.ReciprocalRank ?? 0), _ => sample.Average(x => x.ActualRank ?? 13) }; } Array.Sort(values); double estimate = metric switch { "Top3" => Rate(source.Count(x => x.Top3Hit == true), source.Length), "Top6" => Rate(source.Count(x => x.Top6Hit == true), source.Length), "MRR" => source.Average(x => x.ReciprocalRank ?? 0), _ => source.Average(x => x.ActualRank ?? 13) }; output.Add(new(model, metric, estimate, values[25], values[974], source.Length)); } } return output; }

    private static RandomMonteCarloSummary MonteCarlo(IReadOnlyList<ReplayPredictionSnapshot> rows, int seed, int iterations) { var random = new Random(seed); var top1 = new double[iterations]; var top3 = new double[iterations]; var top6 = new double[iterations]; var mrr = new double[iterations]; var mean = new double[iterations]; for (int i = 0; i < iterations; i++) { int a = 0, b = 0, c = 0; double mr = 0, rank = 0; foreach (var _ in rows) { int r = random.Next(1, 13); a += r == 1 ? 1 : 0; b += r <= 3 ? 1 : 0; c += r <= 6 ? 1 : 0; mr += 1d / r; rank += r; } top1[i] = Rate(a, rows.Count); top3[i] = Rate(b, rows.Count); top6[i] = Rate(c, rows.Count); mrr[i] = mr / rows.Count; mean[i] = rank / rows.Count; } Array.Sort(top6); double actual = Rate(rows.Count(x => x.Top6Hit == true), rows.Count); return new(seed, iterations, rows.Count, top1.Average(), top3.Average(), top6.Average(), mrr.Average(), mean.Average(), top6.Count(x => x <= actual) / (double)top6.Length, top6[(int)(iterations * .025)], top6[(int)(iterations * .975)]); }

    private static IReadOnlyList<PairedComparisonSummary> Paired(IReadOnlyList<ReplayPredictionSnapshot> rows, string v2) => rows.Select(x => x.ModelId).Distinct().Where(model => model != v2).Select(model => { var pairs = rows.GroupBy(x => x.TargetIssue).Select(g => (L: g.SingleOrDefault(x => x.ModelId == v2), R: g.SingleOrDefault(x => x.ModelId == model))).Where(x => x.L is not null && x.R is not null).Select(x => (x.L!, x.R!)).ToArray(); return new PairedComparisonSummary(v2, model, pairs.Count(x => (x.Item1.ActualRank ?? 13) < (x.Item2.ActualRank ?? 13)), pairs.Count(x => (x.Item1.ActualRank ?? 13) == (x.Item2.ActualRank ?? 13)), pairs.Count(x => (x.Item1.ActualRank ?? 13) > (x.Item2.ActualRank ?? 13)), pairs.Length == 0 ? 0 : pairs.Average(x => (x.Item2.ActualRank ?? 13) - (x.Item1.ActualRank ?? 13)), Median(pairs.Select(x => (double)((x.Item2.ActualRank ?? 13) - (x.Item1.ActualRank ?? 13))).ToArray()), pairs.Count(x => x.Item1.Top6Hit == true && x.Item2.Top6Hit == true), pairs.Count(x => x.Item1.Top6Hit == true && x.Item2.Top6Hit != true), pairs.Count(x => x.Item1.Top6Hit != true && x.Item2.Top6Hit == true), pairs.Count(x => x.Item1.Top6Hit != true && x.Item2.Top6Hit != true)); }).ToArray();
    private static IReadOnlyList<McNemarSummary> McNemar(IReadOnlyList<ReplayPredictionSnapshot> rows, string v2) => Paired(rows, v2).Select(x => new McNemarSummary(x.LeftModel, x.RightModel, x.BothTop6, x.LeftOnlyTop6, x.RightOnlyTop6, x.NeitherTop6, x.LeftOnlyTop6 + x.RightOnlyTop6 == 0 ? 0 : Math.Pow(Math.Abs(x.LeftOnlyTop6 - x.RightOnlyTop6) - 1, 2) / (x.LeftOnlyTop6 + x.RightOnlyTop6), x.LeftOnlyTop6 + x.RightOnlyTop6 == 0 ? 1 : Math.Exp(-Math.Pow(Math.Abs(x.LeftOnlyTop6 - x.RightOnlyTop6) - 1, 2) / (x.LeftOnlyTop6 + x.RightOnlyTop6)))).ToArray();
    private static double Rate(int numerator, int denominator) => denominator == 0 ? 0 : numerator / (double)denominator;
    private static double Rate(IEnumerable<ReplayPredictionSnapshot> rows, Func<ReplayPredictionSnapshot, bool?> selector) { var a = rows.ToArray(); return a.Length == 0 ? 0 : a.Count(x => selector(x) == true) / (double)a.Length; }
    private static int MaxMiss(IEnumerable<ReplayPredictionSnapshot> rows, Func<ReplayPredictionSnapshot, bool?> selector) { int current = 0, max = 0; foreach (var row in rows.OrderBy(x => ParseIssue(x.TargetIssue))) { current = selector(row) == true ? 0 : current + 1; max = Math.Max(max, current); } return max; }
    private static double Median(IReadOnlyList<int> values) { if (values.Count == 0) return 0; int[] sorted = values.OrderBy(x => x).ToArray(); int m = sorted.Length / 2; return sorted.Length % 2 == 0 ? (sorted[m - 1] + sorted[m]) / 2d : sorted[m]; }
    private static double Median(IReadOnlyList<double> values) { if (values.Count == 0) return 0; double[] sorted = values.OrderBy(x => x).ToArray(); int m = sorted.Length / 2; return sorted.Length % 2 == 0 ? (sorted[m - 1] + sorted[m]) / 2d : sorted[m]; }
    private static double Spearman(IReadOnlyList<IReadOnlyList<string>> left, IReadOnlyList<IReadOnlyList<string>> right) { if (left.Count == 0) return 0; return left.Select((ranking, i) => { var a = ranking.Select((z, index) => (z, index)).ToDictionary(x => x.z, x => x.index + 1); var b = right[i].Select((z, index) => (z, index)).ToDictionary(x => x.z, x => x.index + 1); double d = ZodiacOrder.Sum(z => Math.Pow(a[z] - b[z], 2)); return 1 - 6 * d / (12 * 143d); }).Average(); }
    private static double Overlap(ReplayPredictionSnapshot a, ReplayPredictionSnapshot b, int n) => a.Ranking.Take(n).Intersect(b.Ranking.Take(n)).Count() / (double)n;
    private static long ParseIssue(string issue) => long.TryParse(issue, out long value) ? value : long.MaxValue;
}
