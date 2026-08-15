using System.Text.Json;

namespace 六合分析软件;

public sealed record CandidateSnapshot(string ExperimentId, string CandidateId, string TargetIssue, string HistoryCutoffIssue, int HistorySampleCount,
    IReadOnlyList<string> Ranking, IReadOnlyDictionary<string, double> Scores, bool IncompleteRanking, string ActualZodiac, int? ActualRank,
    bool Top1Hit, bool Top3Hit, bool Top6Hit, string MarketState, double StateConfidence, string StateJson, bool LeakageAuditPassed,
    string TrainingMaxIssue, string FeatureSourceMaxIssue, int? ModelTrainingCount = null)
{
    public string RankingJson => JsonSerializer.Serialize(Ranking);
    public string ScoresJson => JsonSerializer.Serialize(Scores);
}

public sealed record CandidateAudit(string CandidateId, string Grade, bool Replayable, bool LeakageSafe, bool FullRanking, int RequestedN, int AvailableN, int MissingN, string FirstAvailableIssue, string LastAvailableIssue, string Notes);
public sealed record CandidateMetric(string CandidateId, int N, double Top1, double Top3, double Top6, double Mrr, double MeanRank, double MedianRank, int MaxTop3Miss, int MaxTop6Miss, bool IncompleteRanking);
public sealed record CandidateConditionalMetric(string CandidateId, string SetName, int N, double Top3, double Top6);
public sealed record CandidateRescueMetric(string CandidateId, int TripleFailureOpportunity, int RescueCount, double RescueRate, int StrongFailureOpportunity, int StrongRescue, double StrongRescueRate, int HarmOpportunity, int HarmCount, double HarmRate, int NetRescueCount, double RescueLower95, double RescueUpper95);
public sealed record CandidateDiversityMetric(string CandidateId, string ComparedWith, double Spearman, double Top3Overlap, double Top6Overlap, int BothHit, int CandidateOnlyHit, int BaseOnlyHit, int BothMiss, double JointFailureRate);
public sealed record MarketStateMetric(string CandidateId, string State, int N, double Top6, string BestModel);
public sealed record CandidateStage2Report(IReadOnlyList<CandidateAudit> Audits, IReadOnlyList<CandidateMetric> Performance,
    IReadOnlyList<CandidateRescueMetric> Rescue, IReadOnlyList<CandidateDiversityMetric> Diversity,
    IReadOnlyList<CandidateConditionalMetric> Conditional, IReadOnlyList<MarketStateMetric> MarketStates,
    IReadOnlyList<CandidateMetric> TrainingValidationHoldout, IReadOnlyList<RollingWindowSummary> Rolling,
    int TripleFailureOpportunity, int StrongFailureOpportunity, bool LeakageDetected, int RandomSeed, int MonteCarloIterations,
    bool MlModesDiffer, string ExperimentId, string StorePath, IReadOnlyList<CandidateMetric> SelectorComparison,
    IReadOnlyList<RandomConditionalMetric> RandomConditional);

public sealed record RandomConditionalMetric(string SetName, int N, int Iterations, double MeanTop6, double Lower95, double Upper95);
