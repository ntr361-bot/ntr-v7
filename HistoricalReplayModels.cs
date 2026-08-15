using System.Text.Json;

namespace 六合分析软件;

public static class HistoricalReplayModelIds
{
    public const string Period50 = "V6.5-50";
    public const string Period100 = "V6.5-100";
    public const string AllHistory = "V6.5-All";
    public const string BaseAverage = "BaseAverage";
    public const string FrozenV2 = AutoLearningV2Service.ModelKey;
    public const string Random = "RandomBaseline";
}

public sealed record HistoricalReplayOptions(
    int MinimumWarmupSamples = 100,
    string? ExperimentId = null,
    string? StorePath = null,
    int RandomSeed = 6501,
    bool PersistSnapshots = true)
{
    public HistoricalReplayOptions Validate()
    {
        if (MinimumWarmupSamples < 100) throw new ArgumentOutOfRangeException(nameof(MinimumWarmupSamples));
        return this with { ExperimentId = string.IsNullOrWhiteSpace(ExperimentId) ? Guid.NewGuid().ToString("N") : ExperimentId };
    }
}

public sealed record ReplayPredictionSnapshot(
    string ExperimentId,
    string TargetIssue,
    string ModelId,
    string ModelVersion,
    string HistoryCutoffIssue,
    int HistorySampleCount,
    IReadOnlyList<string> Ranking,
    IReadOnlyList<double> Scores,
    string? ActualZodiac,
    int? ActualRank,
    bool? Top1Hit,
    bool? Top3Hit,
    bool? Top6Hit,
    double? ReciprocalRank,
    string? StateBeforeJson = null,
    string? StateAfterJson = null,
    double? BaseScore = null,
    double? ResidualCorrection = null,
    double? ConsensusScore = null,
    double? JointFailureRisk = null,
    string? Confidence = null)
{
    public string RankingJson => JsonSerializer.Serialize(Ranking);
    public string ScoresJson => JsonSerializer.Serialize(Scores);
}

public sealed record ReplayLeakageFinding(string TargetIssue, string Source, string LeakedIssue);

public sealed record HistoricalReplayResult(
    string ExperimentId,
    string RequestedFrozenCommit,
    string ActualExecutionCommit,
    IReadOnlyList<string> TargetIssues,
    IReadOnlyList<ReplayPredictionSnapshot> Predictions,
    IReadOnlyList<ReplayLeakageFinding> LeakageFindings,
    bool FutureDataLeakageDetected,
    int WarmupSamples);

public sealed record ReplayMetricSummary(
    string ModelId,
    int SampleCount,
    int MissingPredictionCount,
    int Top1HitCount,
    int Top3HitCount,
    int Top6HitCount,
    double Top1Rate,
    double Top3Rate,
    double Top6Rate,
    double Mrr,
    double MeanRank,
    double MedianRank,
    int MaxTop1MissStreak,
    int MaxTop3MissStreak,
    int MaxTop6MissStreak);

public sealed record EvaluationReport(
    IReadOnlyList<ReplayMetricSummary> Models,
    IReadOnlyList<string> CommonEvaluationSet,
    int MissingPredictionCount,
    bool LeakageDetected,
    IReadOnlyList<RescueHarmSummary> RescueHarm,
    IReadOnlyList<RollingWindowSummary> Rolling,
    IReadOnlyList<YearlyMetricSummary> Yearly,
    IReadOnlyList<ModelRelationshipSummary> Relationships,
    IReadOnlyList<RankChangeSummary> RankChanges,
    IReadOnlyList<BinMetricSummary> ConsensusBins,
    IReadOnlyList<BinMetricSummary> JointFailureRiskBins,
    IReadOnlyList<BinMetricSummary> ConfidenceGroups,
    IReadOnlyList<ConfidenceIntervalSummary> Bootstrap95,
    IReadOnlyList<SplitMetricSummary> Splits,
    RandomMonteCarloSummary RandomMonteCarlo,
    IReadOnlyList<PairedComparisonSummary> PairedComparisons,
    IReadOnlyList<McNemarSummary> McNemar);

public sealed record RescueHarmSummary(string V2Model, int RescueOpportunity, int RescueSuccess, double RescueRate, int HarmOpportunity, int HarmCount, double HarmRate, int StrongRescue, int StrongHarm);
public sealed record RollingWindowSummary(string ModelId, int WindowSize, string BestStartIssue, string BestEndIssue, double BestTop3, double BestTop6, string WorstStartIssue, string WorstEndIssue, double WorstTop3, double WorstTop6);
public sealed record YearlyMetricSummary(string ModelId, string Year, int SampleCount, double Top1, double Top3, double Top6, double Mrr, double MeanRank, double MedianRank, int MaxTop3MissStreak, int MaxTop6MissStreak);
public sealed record ModelRelationshipSummary(string LeftModel, string RightModel, double Spearman, double MeanTop3Overlap, double MeanTop6Overlap);
public sealed record RankChangeSummary(string BaseModel, string V2Model, int ChangedRankCount, int ActualPulledForward, int ActualPushedBack, double MeanRankChange, int MaxImprovement, int MaxWorsening, int IdenticalTop3, int IdenticalTop6);
public sealed record BinMetricSummary(string ModelId, string Bin, int SampleCount, double Top3, double Top6, double Mrr, double MeanRank, int JointFailureCount = 0, double JointFailureRate = 0);
public sealed record ConfidenceIntervalSummary(string ModelId, string Metric, double Estimate, double Lower95, double Upper95, int Samples);
public sealed record SplitMetricSummary(string ModelId, string Split, string StartIssue, string EndIssue, int SampleCount, double Top1, double Top3, double Top6, double Mrr, double MeanRank);
public sealed record RandomMonteCarloSummary(int Seed, int Iterations, int SampleCount, double Top1Mean, double Top3Mean, double Top6Mean, double MrrMean, double MeanRankMean, double V2Top6Percentile, double RandomTop6Lower95, double RandomTop6Upper95);
public sealed record PairedComparisonSummary(string LeftModel, string RightModel, int BetterRankCount, int EqualRankCount, int WorseRankCount, double MeanRankDifference, double MedianRankDifference, int BothTop6, int LeftOnlyTop6, int RightOnlyTop6, int NeitherTop6);
public sealed record McNemarSummary(string LeftModel, string RightModel, int BothHit, int LeftOnly, int RightOnly, int Neither, double Statistic, double PApprox);
