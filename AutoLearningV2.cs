using System.Text.Json;

namespace 六合分析软件;

public sealed record AutoLearningV2Config(
    double Lambda = .10,
    double Decay = .98,
    double LearningRate = .01,
    double MaximumWeightStep = .02,
    double MinimumWeight = .05,
    double MaximumWeight = .70)
{
    public AutoLearningV2Config Validate() =>
        Lambda is .05 or .10 or .15 && Decay is >= .90 and <= .999 &&
        LearningRate > 0 && MaximumWeightStep is > 0 and <= .02 &&
        MinimumWeight >= 0 && MaximumWeight <= 1 && MinimumWeight <= MaximumWeight
            ? this : throw new ArgumentOutOfRangeException(nameof(AutoLearningV2Config));
}

public sealed record AutoLearningV2HistoryFeatures(
    int StructureSampleCount,
    int RecentSampleCount,
    double RecentTop3FailureRate,
    double RecentTop6FailureRate,
    double ConsensusTop3HitRate,
    double ConsensusTop6HitRate);

public sealed record AutoLearningV2Explanation(
    string MaxPositiveFeature,
    double MaxPositiveContribution,
    string MaxNegativeFeature,
    double MaxNegativeContribution);

public sealed record AutoLearningV2Zodiac(
    string Zodiac,
    int Rank,
    int Rank50,
    int Rank100,
    int RankAll,
    double Score50,
    double Score100,
    double ScoreAll,
    double RankMean,
    double RankMedian,
    double RankStd,
    double RankRange,
    int Top3VoteCount,
    int Top6VoteCount,
    double SpearmanLocalAgreement,
    double ConsensusScore,
    IReadOnlyDictionary<string, double> FactorFeatures,
    double BaseScore,
    double ResidualCorrection,
    double FinalScore,
    double JointFailureRisk,
    AutoLearningV2Explanation Explanation);

public sealed record AutoLearningV2Snapshot(
    string Issue,
    string ModelKey,
    AutoLearningV2Config Config,
    AutoLearningV2HistoryFeatures HistoryFeatures,
    IReadOnlyList<AutoLearningV2Zodiac> Zodiacs,
    string Confidence,
    double JointFailureRisk,
    DateTimeOffset GeneratedAt)
{
    public string FinalRankingJson => JsonSerializer.Serialize(Zodiacs.OrderBy(row => row.Rank).Select(row => row.Zodiac));
}

public sealed class AutoLearningV2State
{
    public ModelWeights Weights { get; set; } = ModelWeights.Default;
    public double Decay { get; set; } = .98;
    public int ObservedSamples { get; set; }
    public Dictionary<string, double> FeatureWeights { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record AutoLearningV2ExperimentRun(string RunId, string CodeVersion, string Lambda,
    string Decay, string TrainingStartIssue, string TrainingEndIssue, string ValidationEndIssue, string HoldoutEndIssue)
{
    public string ModelKey => AutoLearningV2Service.ModelKey;
}

public sealed record AutoLearningV2ExperimentPrediction(string Issue, string Top6, int ActualRank,
    double BaseScore, double ResidualCorrection, double FinalScore, string Confidence);

public static class AutoLearningV2Service
{
    public const string ModelKey = "v65-auto-v2-experiment";
    private static readonly string[] Factors = { "F", "T", "O", "H", "P", "C", "B" };

    public static AutoLearningV2Snapshot BuildSnapshot(PredictionTraceSnapshot trace,
        AutoLearningV2HistoryFeatures history, AutoLearningV2Config? config = null)
    {
        config = (config ?? new AutoLearningV2Config()).Validate();
        if (trace.BaseModels.Count != 3 || trace.BaseModels.Any(model => model.Ranking.Count != 12))
            throw new InvalidDataException("AutoLearningV2 需要三条完整基础模型 Trace。");
        string[] keys = { ExperimentModels.Period50, ExperimentModels.Period100, ExperimentModels.AllHistory };
        var models = keys.Select(key => trace.BaseModels.Single(model => model.ModelKey == key)).ToArray();
        var rows = trace.BaseModels[0].Ranking.Select(item => item.Zodiac).Select(zodiac =>
        {
            PredictionTraceZodiac[] items = models.Select(model => model.Ranking.Single(row => row.Zodiac == zodiac)).ToArray();
            int[] ranks = items.Select(item => item.Rank).ToArray();
            double[] scores = items.Select(item => item.TotalScore).ToArray();
            var factors = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (string factor in Factors)
            {
                double[] values = items.Select(item => item.Factors.GetValueOrDefault(factor)?.Raw ?? 0).ToArray();
                double[] contributions = items.Select(item => item.Factors.GetValueOrDefault(factor)?.Contribution ?? 0).ToArray();
                factors[$"{factor}_mean"] = values.Average();
                factors[$"{factor}_std"] = Std(values);
                factors[$"{factor}_contribution_mean"] = contributions.Average();
            }
            double baseScore = scores.Select((score, index) => NormalizeScore(score, models[index].Ranking.Select(row => row.TotalScore))).Average();
            double rankStd = Std(ranks.Select(rank => (double)rank).ToArray());
            double consensus = Math.Clamp((1 - rankStd / 6) * .55 + ranks.Count(rank => rank <= 6) / 3d * .25 +
                (1 - Math.Abs(ranks[0] - ranks[1]) / 11d) * .20, 0, 1);
            double disagreement = Math.Clamp(rankStd / 6, 0, 1);
            double residual = config.Lambda * (history.ConsensusTop6HitRate - .5) * (consensus - .5) -
                config.LearningRate * disagreement * (history.RecentTop6FailureRate - .5);
            var featureContributions = new Dictionary<string, double>
            {
                ["consensus"] = (history.ConsensusTop6HitRate - .5) * (consensus - .5),
                ["recent_failure"] = -(history.RecentTop6FailureRate - .5) * disagreement,
                ["rank_spread"] = -disagreement
            };
            var explanation = new AutoLearningV2Explanation(
                featureContributions.OrderByDescending(pair => pair.Value).First().Key,
                featureContributions.Values.Max(),
                featureContributions.OrderBy(pair => pair.Value).First().Key,
                featureContributions.Values.Min());
            double risk = Math.Clamp(.5 * (ranks.All(rank => rank > 6) ? 1 : 0) + .5 * disagreement, 0, 1);
            return new AutoLearningV2Zodiac(zodiac, 0, ranks[0], ranks[1], ranks[2], scores[0], scores[1], scores[2],
                ranks.Average(), Median(ranks), rankStd, ranks.Max() - ranks.Min(), ranks.Count(rank => rank <= 3),
                ranks.Count(rank => rank <= 6), SpearmanAgreement(ranks), consensus, factors, baseScore, residual,
                baseScore + residual, risk, explanation);
        }).ToArray();
        var ranked = rows.OrderByDescending(row => row.FinalScore).ThenBy(row => row.Rank50).Select((row, index) => row with { Rank = index + 1 }).ToArray();
        double jointRisk = ranked.Average(row => row.JointFailureRisk);
        double marginTop = ranked[0].FinalScore - ranked[1].FinalScore;
        double marginBoundary = ranked[5].FinalScore - ranked[6].FinalScore;
        string confidence = history.StructureSampleCount < 10 || jointRisk > .75 ? "Low" :
            marginTop > .08 && marginBoundary > .04 && history.ConsensusTop6HitRate >= .5 ? "High" : "Medium";
        return new AutoLearningV2Snapshot(trace.Issue, ModelKey, config, history, ranked, confidence, jointRisk, DateTimeOffset.UtcNow);
    }

    public static AutoLearningV2State UpdateState(AutoLearningV2State state, AutoLearningV2Snapshot snapshot, string actualZodiac)
    {
        if (!snapshot.Zodiacs.Any(row => row.Zodiac == actualZodiac)) throw new ArgumentException("实际生肖不在V2快照中", nameof(actualZodiac));
        double decay = snapshot.Config.Decay;
        var old = state.Weights.AsDictionary().ToDictionary(pair => pair.Key, pair => pair.Value * decay, StringComparer.OrdinalIgnoreCase);
        double actualRank = snapshot.Zodiacs.Single(row => row.Zodiac == actualZodiac).Rank;
        double signal = Math.Clamp((7 - actualRank) / 6, -1, 1);
        old["AI"] += snapshot.Config.LearningRate * signal;
        old["ML"] -= snapshot.Config.LearningRate * signal;
        double sum = old.Values.Sum();
        var projected = old.ToDictionary(pair => pair.Key, pair => Math.Clamp(pair.Value / sum, snapshot.Config.MinimumWeight, snapshot.Config.MaximumWeight), StringComparer.OrdinalIgnoreCase);
        double projectedSum = projected.Values.Sum();
        projected = projected.ToDictionary(pair => pair.Key, pair => pair.Value / projectedSum, StringComparer.OrdinalIgnoreCase);
        var bounded = state.Weights.AsDictionary().ToDictionary(pair => pair.Key,
            pair => Math.Clamp(projected[pair.Key], pair.Value - snapshot.Config.MaximumWeightStep, pair.Value + snapshot.Config.MaximumWeightStep), StringComparer.OrdinalIgnoreCase);
        double boundedSum = bounded.Values.Sum();
        state.Weights = new ModelWeights(bounded["AI"] / boundedSum, bounded["ML"] / boundedSum, bounded["State"] / boundedSum, bounded["V7"] / boundedSum);
        state.Decay = decay;
        state.ObservedSamples++;
        return state;
    }

    private static double NormalizeScore(double value, IEnumerable<double> values)
    {
        double min = values.Min(), max = values.Max();
        return max - min < 1e-12 ? .5 : (value - min) / (max - min);
    }
    private static double Std(IReadOnlyList<double> values) { double mean = values.Average(); return Math.Sqrt(values.Select(value => Math.Pow(value - mean, 2)).Average()); }
    private static double Median(IReadOnlyList<int> values)
    {
        int[] sorted = values.OrderBy(value => value).ToArray();
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2d : sorted[middle];
    }
    private static double SpearmanAgreement(IReadOnlyList<int> ranks) => Math.Clamp(1 - Std(ranks.Select(rank => (double)rank).ToArray()) / 6, 0, 1);
}
