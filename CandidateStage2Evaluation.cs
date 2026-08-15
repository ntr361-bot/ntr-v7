namespace 六合分析软件;

public static class CandidateStage2Evaluation
{
    private const double HighDisagreementRankStd = 2.0;
    private const double HighAgreementRankRange = 2.0;
    private static readonly string[] Complete = { CandidateStage2Ids.MlLgb, CandidateStage2Ids.MlXgb, CandidateStage2Ids.Ranking };

    public static CandidateStage2Report Evaluate(IReadOnlyList<CandidateSnapshot> candidates, IReadOnlyList<ReplayPredictionSnapshot> controls,
        string experimentId, string storePath, int seed = 6501, int monteCarloIterations = 10000)
    {
        var targetIssues = controls.Select(x => x.TargetIssue).Distinct().OrderBy(Parse).ToArray();
        var candidateIds = candidates.Select(x => x.CandidateId).Distinct().ToArray();
        var audits = candidateIds.Select(id => Audit(id, candidates.Where(x => x.CandidateId == id).ToArray(), targetIssues.Length)).ToArray();
        var performance = candidateIds.Select(id => Metric(id, candidates.Where(x => x.CandidateId == id).ToArray())).ToArray();
        var baseRows = ControlsByIssue(controls);
        var rescue = candidateIds.Select(id => Rescue(id, candidates.Where(x => x.CandidateId == id), baseRows, seed, monteCarloIterations)).ToArray();
        var diversity = candidateIds.SelectMany(id => new[] { HistoricalReplayModelIds.BaseAverage, HistoricalReplayModelIds.Period50, HistoricalReplayModelIds.Period100, HistoricalReplayModelIds.AllHistory }.Select(baseId => Diversity(id, candidates.Where(x => x.CandidateId == id), baseId, baseRows))).ToArray();
        var conditional = candidateIds.SelectMany(id => Conditional(id, candidates.Where(x => x.CandidateId == id), baseRows)).ToArray();
        var states = StateMetrics(candidates, baseRows);
        var splits = SplitMetrics(candidates);
        var rolling = Rolling(candidates);
        var selector = Array.Empty<CandidateMetric>();
        var random = RandomConditional(rescue, seed, monteCarloIterations);
        bool leakage = candidates.Any(x => !x.LeakageAuditPassed || Parse(x.HistoryCutoffIssue) >= Parse(x.TargetIssue) || Parse(x.TrainingMaxIssue) >= Parse(x.TargetIssue) || Parse(x.FeatureSourceMaxIssue) >= Parse(x.TargetIssue));
        bool mlDifferent = candidates.Where(x => x.CandidateId == CandidateStage2Ids.MlLgb).Select(x => string.Join(',', x.Ranking)).SequenceEqual(candidates.Where(x => x.CandidateId == CandidateStage2Ids.MlXgb).Select(x => string.Join(',', x.Ranking))) == false;
        return new(audits, performance, rescue, diversity, conditional, states, splits, rolling, targetIssues.Length, StrongFailureCount(baseRows), leakage, seed, monteCarloIterations, mlDifferent, experimentId, storePath, selector, random);
    }

    private static CandidateAudit Audit(string id, IReadOnlyList<CandidateSnapshot> rows, int requested) => new(id, rows.Any(x => x.IncompleteRanking) ? "A-Top6Only" : "A", true, rows.All(x => x.LeakageAuditPassed), rows.All(x => !x.IncompleteRanking), requested, rows.Count, requested - rows.Count, rows.OrderBy(x => Parse(x.TargetIssue)).FirstOrDefault()?.TargetIssue ?? "", rows.OrderBy(x => Parse(x.TargetIssue)).LastOrDefault()?.TargetIssue ?? "", rows.Any(x => x.IncompleteRanking) ? "IncompleteRanking: MRR/MeanRank/Spearman not applicable" : "Walk-forward adapter");

    private static CandidateMetric Metric(string id, IReadOnlyList<CandidateSnapshot> rows)
    {
        var valid = rows.Where(x => x.ActualRank is > 0).ToArray(); bool incomplete = rows.Any(x => x.IncompleteRanking); var ranks = valid.Select(x => x.ActualRank!.Value).ToArray();
        return new(id, rows.Count, Rate(valid.Count(x => x.Top1Hit), valid.Length), Rate(valid.Count(x => x.Top3Hit), valid.Length), Rate(valid.Count(x => x.Top6Hit), valid.Length), incomplete ? 0 : valid.Select(x => 1d / x.ActualRank!.Value).DefaultIfEmpty().Average(), incomplete ? 0 : valid.Select(x => (double)x.ActualRank!.Value).DefaultIfEmpty().Average(), incomplete ? 0 : Median(ranks), MaxMiss(rows, x => x.Top3Hit), MaxMiss(rows, x => x.Top6Hit), incomplete);
    }

    private static CandidateRescueMetric Rescue(string id, IEnumerable<CandidateSnapshot> source, IReadOnlyDictionary<string, IReadOnlyDictionary<string, ReplayPredictionSnapshot>> bases, int seed, int iterations)
    {
        int triple = 0, rescue = 0, strong = 0, strongRescue = 0, harmOpp = 0, harm = 0;
        foreach (var pair in bases)
        {
            int[] ranks = BaseRanks(pair.Value); var candidate = source.SingleOrDefault(x => x.TargetIssue == pair.Key); if (candidate is null || candidate.ActualRank is null) continue;
            if (ranks.All(x => x > 6)) { triple++; if (candidate.ActualRank <= 6) rescue++; }
            if (ranks.All(x => x >= 9)) { strong++; if (candidate.ActualRank <= 6) strongRescue++; }
            if (ranks.Count(x => x <= 6) >= 2) { harmOpp++; if (candidate.ActualRank > 6) harm++; }
        }
        var ci = BootstrapRate(rescue, triple, seed);
        return new(id, triple, rescue, Rate(rescue, triple), strong, strongRescue, Rate(strongRescue, strong), harmOpp, harm, Rate(harm, harmOpp), rescue - harm, ci.Lower, ci.Upper);
    }

    private static CandidateDiversityMetric Diversity(string id, IEnumerable<CandidateSnapshot> source, string baseId, IReadOnlyDictionary<string, IReadOnlyDictionary<string, ReplayPredictionSnapshot>> bases)
    {
        var pairs = source.Join(bases, x => x.TargetIssue, x => x.Key, (candidate, b) => (candidate, baseRow: b.Value.GetValueOrDefault(baseId))).Where(x => x.baseRow is not null && x.candidate.ActualRank is not null).ToArray();
        if (pairs.Length == 0) return new(id, baseId, 0, 0, 0, 0, 0, 0, 0, 0);
        int both = pairs.Count(x => x.candidate.Top6Hit && x.baseRow!.Top6Hit == true), co = pairs.Count(x => x.candidate.Top6Hit && x.baseRow!.Top6Hit != true), bo = pairs.Count(x => !x.candidate.Top6Hit && x.baseRow!.Top6Hit == true), miss = pairs.Length - both - co - bo;
        return new(id, baseId, MeanSpearman(pairs.Select(x => x.candidate.Ranking), pairs.Select(x => x.baseRow!.Ranking)), pairs.Average(x => Overlap(x.candidate.Ranking, x.baseRow!.Ranking, 3)), pairs.Average(x => Overlap(x.candidate.Ranking, x.baseRow!.Ranking, 6)), both, co, bo, miss, Rate(pairs.Count(x => !x.candidate.Top6Hit && x.baseRow!.Top6Hit != true), pairs.Length));
    }

    private static IReadOnlyList<CandidateConditionalMetric> Conditional(string id, IEnumerable<CandidateSnapshot> source, IReadOnlyDictionary<string, IReadOnlyDictionary<string, ReplayPredictionSnapshot>> bases)
    {
        var output = new List<CandidateConditionalMetric>();
        foreach (var set in BuildSets(bases)) { var rows = source.Where(x => set.Value.Contains(x.TargetIssue)).ToArray(); output.Add(new(id, set.Key, rows.Length, Rate(rows.Count(x => x.Top3Hit), rows.Length), Rate(rows.Count(x => x.Top6Hit), rows.Length))); }
        return output;
    }

    private static IReadOnlyList<MarketStateMetric> StateMetrics(IEnumerable<CandidateSnapshot> candidates, IReadOnlyDictionary<string, IReadOnlyDictionary<string, ReplayPredictionSnapshot>> bases)
    {
        var result = new List<MarketStateMetric>(); var ids = candidates.Select(x => x.CandidateId).Distinct().ToArray();
        foreach (string state in candidates.Select(x => x.MarketState).Distinct()) foreach (string id in ids)
        { var rows = candidates.Where(x => x.MarketState == state && x.CandidateId == id).ToArray(); result.Add(new(id, state, rows.Length, Rate(rows.Count(x => x.Top6Hit), rows.Length), "")); }
        return result;
    }

    private static IReadOnlyList<CandidateMetric> SplitMetrics(IEnumerable<CandidateSnapshot> candidates) => candidates.GroupBy(x => x.CandidateId).SelectMany(g => { var rows = g.OrderBy(x => Parse(x.TargetIssue)).ToArray(); int a = rows.Length * 60 / 100, b = rows.Length * 20 / 100; return new[] { Metric(g.Key + ":Training", rows.Take(a).ToArray()), Metric(g.Key + ":Validation", rows.Skip(a).Take(b).ToArray()), Metric(g.Key + ":Holdout", rows.Skip(a + b).ToArray()) }; }).ToArray();
    private static IReadOnlyList<RollingWindowSummary> Rolling(IEnumerable<CandidateSnapshot> candidates) => candidates.GroupBy(x => x.CandidateId).SelectMany(g => new[] { 20, 50, 100 }.Where(n => g.Count() >= n).Select(n => { var r = g.OrderBy(x => Parse(x.TargetIssue)).ToArray(); var ws = Enumerable.Range(0, r.Length - n + 1).Select(i => { var x = r.Skip(i).Take(n).ToArray(); return (S: x[0].TargetIssue, E: x[^1].TargetIssue, T3: Rate(x.Count(y => y.Top3Hit), n), T6: Rate(x.Count(y => y.Top6Hit), n)); }).ToArray(); var hi = ws.OrderByDescending(x => x.T6).First(); var lo = ws.OrderBy(x => x.T6).First(); return new RollingWindowSummary(g.Key, n, hi.S, hi.E, hi.T3, hi.T6, lo.S, lo.E, lo.T3, lo.T6); })).ToArray();

    private static IReadOnlyList<RandomConditionalMetric> RandomConditional(IEnumerable<CandidateRescueMetric> rescue, int seed, int iterations) => rescue.Select(x => new RandomConditionalMetric("TripleFailure", x.TripleFailureOpportunity, iterations, .5, .5 - 1.96 * Math.Sqrt(.25 / Math.Max(1, x.TripleFailureOpportunity)), .5 + 1.96 * Math.Sqrt(.25 / Math.Max(1, x.TripleFailureOpportunity)))).ToArray();
    private static Dictionary<string, IReadOnlyDictionary<string, ReplayPredictionSnapshot>> ControlsByIssue(IEnumerable<ReplayPredictionSnapshot> controls) => controls.GroupBy(x => x.TargetIssue).ToDictionary(g => g.Key, g => (IReadOnlyDictionary<string, ReplayPredictionSnapshot>)g.ToDictionary(x => x.ModelId));
    private static IEnumerable<KeyValuePair<string, HashSet<string>>> BuildSets(IReadOnlyDictionary<string, IReadOnlyDictionary<string, ReplayPredictionSnapshot>> bases) { var all = bases.Keys.ToHashSet(); var triple = bases.Where(x => BaseRanks(x.Value).All(r => r > 6)).Select(x => x.Key).ToHashSet(); var strong = bases.Where(x => BaseRanks(x.Value).All(r => r >= 9)).Select(x => x.Key).ToHashSet(); var baseStrong = bases.Where(x => BaseRanks(x.Value).Count(r => r <= 6) >= 2).Select(x => x.Key).ToHashSet(); var disagreement = bases.Where(x => RankStd(BaseRanks(x.Value)) >= HighDisagreementRankStd).Select(x => x.Key).ToHashSet(); var agreement = bases.Where(x => RankRange(BaseRanks(x.Value)) <= HighAgreementRankRange).Select(x => x.Key).ToHashSet(); return new[] { new KeyValuePair<string, HashSet<string>>("All", all), new("TripleFailure", triple), new("StrongFailure", strong), new("BaseStrong", baseStrong), new("HighDisagreement", disagreement), new("HighAgreement", agreement) }; }
    private static int[] BaseRanks(IReadOnlyDictionary<string, ReplayPredictionSnapshot> rows) => new[] { rows[HistoricalReplayModelIds.Period50].ActualRank ?? 13, rows[HistoricalReplayModelIds.Period100].ActualRank ?? 13, rows[HistoricalReplayModelIds.AllHistory].ActualRank ?? 13 };
    private static int StrongFailureCount(IReadOnlyDictionary<string, IReadOnlyDictionary<string, ReplayPredictionSnapshot>> bases) => bases.Count(x => BaseRanks(x.Value).All(r => r >= 9));
    private static (double Lower, double Upper) BootstrapRate(int hit, int n, int seed) { if (n == 0) return (0, 0); var random = new Random(seed); var values = new double[2000]; for (int i = 0; i < values.Length; i++) { int h = 0; for (int j = 0; j < n; j++) if (random.NextDouble() < hit / (double)n) h++; values[i] = h / (double)n; } Array.Sort(values); return (values[50], values[1949]); }
    private static double MeanSpearman(IEnumerable<IReadOnlyList<string>> a, IEnumerable<IReadOnlyList<string>> b) => a.Zip(b).Select(x => Spearman(x.First, x.Second)).DefaultIfEmpty().Average();
    private static double Spearman(IReadOnlyList<string> a, IReadOnlyList<string> b) { var ar = a.Select((x, i) => (x, i)).ToDictionary(x => x.x, x => x.i + 1); var br = b.Select((x, i) => (x, i)).ToDictionary(x => x.x, x => x.i + 1); return 1 - 6 * ar.Keys.Sum(x => Math.Pow(ar[x] - br[x], 2)) / 1716d; }
    private static double Overlap(IReadOnlyList<string> a, IReadOnlyList<string> b, int n) => a.Take(n).Intersect(b.Take(n)).Count() / (double)n;
    private static double RankStd(IEnumerable<int> x) { var a = x.Select(v => (double)v).ToArray(); var m = a.Average(); return Math.Sqrt(a.Average(v => Math.Pow(v - m, 2))); }
    private static int RankRange(IEnumerable<int> x) { var a = x.ToArray(); return a.Max() - a.Min(); }
    private static double Rate(int a, int n) => n == 0 ? 0 : a / (double)n;
    private static int MaxMiss(IEnumerable<CandidateSnapshot> rows, Func<CandidateSnapshot, bool> hit) { int c = 0, m = 0; foreach (var row in rows.OrderBy(x => Parse(x.TargetIssue))) { c = hit(row) ? 0 : c + 1; m = Math.Max(m, c); } return m; }
    private static int MaxMiss(IEnumerable<ReplayPredictionSnapshot> rows, Func<ReplayPredictionSnapshot, bool?> hit) { int c = 0, m = 0; foreach (var row in rows.OrderBy(x => Parse(x.TargetIssue))) { c = hit(row) == true ? 0 : c + 1; m = Math.Max(m, c); } return m; }
    private static double Median(IReadOnlyList<int> a) { if (a.Count == 0) return 0; var x = a.OrderBy(v => v).ToArray(); return x.Length % 2 == 0 ? (x[x.Length / 2 - 1] + x[x.Length / 2]) / 2d : x[x.Length / 2]; }
    private static long Parse(string x) => long.Parse(x);
}
