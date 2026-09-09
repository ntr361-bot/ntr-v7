using System.Collections.Immutable;

namespace 六合分析软件.MacroReasoning;

// Contract-only architecture. No implementations, registration, storage or production calls.
public enum HypothesisType { ColdReturnIncreasing, HotPersistenceIncreasing, TrendSignalDegrading,
    RepeatRegimeIncreasing, ModelPerformanceShift, RandomFluctuation, NoMeaningfulChange }
public enum CriticVerdict { Pass, Caution, Reject }
public enum DecisionType { Apply, Hold, Reject }
public enum OutcomeType { Rescue, Harm, NeutralPositive, NeutralNegative, NoImpact }
public enum Assessment { Pending, Correct, Incorrect, Indeterminate }
public enum HypothesisStatus { Active, PendingValidation, Supported, Refuted, Inconclusive }
public enum ColdStructure { ColdReturn, DeepCold, RecentlyCooling, PreviouslyActiveThenCold, LongTermRare }
public enum HotStructure { RecentHot, PersistentHot, RepeatHot, CoolingFromHot, ShortBurst }

public sealed record RunIdentity(string ExperimentId, string SchemaVersion, string AlgorithmVersion,
    string ParametersHash, string CodeCommit, int RandomSeed);
public sealed record IssueRange(long First, long Last, int Samples);
public sealed record ExpertSnapshot(string ExpertId, long TargetIssue, long HistoryCutoffIssue,
    DateTimeOffset GeneratedAt, ImmutableArray<string> Ranking, string PayloadHash,
    string AlgorithmVersion, string CodeVersion, string RegistryVersion, DateTimeOffset AvailableAt,
    SnapshotOrigin Origin, ImmutableArray<string> ParentSnapshotHashes, ImmutableArray<string> InputDependencyIds,
    string ExpertRevisionId, ReconstructionProvenance? Reconstruction);
public sealed record ClosedResult(long Issue, string ActualZodiac, DateTimeOffset OpenedAt,
    DateTimeOffset AvailableAt, string SourceHash);
// Must be built by a prefix-verifying adapter. A DTO alone does not enforce absence of leakage.
public sealed record PrefixContext(RunIdentity Run, long TargetIssue, long CutoffIssue,
    DateTimeOffset AsOf, ImmutableArray<ClosedResult> PastResults,
    ImmutableArray<ExpertSnapshot> ExpertSnapshots, string SourceManifestHash,
    ReasoningMemorySnapshot Memory, ExpertPoolSnapshot ExpertPool);
public sealed record ObservationFact(string SignalName, string ExpertId, int Window, double? Value,
    double? BaselineValue, int EffectiveSamples, IssueRange SourceIssueRange, string DefinitionVersion);
public sealed record MacroObservationSnapshot(long Issue, long CutoffIssue,
    ImmutableArray<ObservationFact> Facts, string SnapshotHash, ExpertPoolSnapshot ExpertPool,
    ImmutableArray<ExpertObservation> ExpertObservations, ExpertDependencySnapshot Dependencies);
public sealed record Hypothesis(string HypothesisId, HypothesisType Type, long CreatedIssue,
    string Description, double PriorProbability, double CurrentProbability, double HypothesisScore,
    HypothesisStatus Status, ImmutableArray<string> SupportingEvidenceIds,
    ImmutableArray<string> CounterEvidenceIds, string FalsificationRuleVersion, long EvaluationDueIssue);
public sealed record Evidence(string EvidenceId, string HypothesisId, string SignalName, int Window,
    double Value, double BaselineValue, double EffectSize, double Reliability, string Direction,
    IssueRange SourceIssueRange, int EffectiveSamples, string DefinitionVersion, string DependencyGroup);
// Empty counter-evidence is distinct from an unperformed check.
public sealed record EvidenceSearch(bool Completed, ImmutableArray<string> CheckedSignalIds,
    ImmutableArray<Evidence> Items, ImmutableArray<string> MissingInputs);
public sealed record HypothesisSet(ImmutableArray<Hypothesis> Candidates,
    ImmutableArray<string> AlternativeHypothesisIds);
public sealed record ActionProposal(ImmutableDictionary<string,double> Before,
    ImmutableDictionary<string,double> Proposed, double ActionMagnitude, string RuleVersion);
public sealed record CriticResult(CriticVerdict Result, ImmutableArray<string> Reasons,
    ImmutableArray<string> CompletedChecks, double MaximumMagnitude);
public sealed record ConfidenceResult(double Value, ImmutableDictionary<string,double?> Components,
    string CalibrationVersion, int CalibrationSamples);
public sealed record MacroDecision(long Issue, ImmutableArray<string> SelectedHypothesisIds,
    ImmutableArray<string> RejectedHypothesisIds, DecisionType DecisionType,
    ImmutableDictionary<string,double> ExpertWeightsBefore,
    ImmutableDictionary<string,double> ExpertWeightsProposed,
    ImmutableDictionary<string,double> ExpertWeightsApplied, double ActionMagnitude,
    ConfidenceResult Confidence, string ReasonSummary, CriticResult CriticResult);
public sealed record CounterfactualSnapshot(long Issue, ImmutableDictionary<string,double> BaseWeights,
    ImmutableDictionary<string,double> AppliedWeights, ImmutableArray<string> BaseRanking,
    ImmutableArray<string> NoActionRanking, ImmutableArray<string> ProposedRanking,
    ImmutableArray<string> AppliedRanking, bool Top6Changed, ImmutableArray<string> ChangedZodiacs);
public sealed record MacroReasoningAudit(RunIdentity Run, long Issue, long CutoffIssue,
    DateTimeOffset CreatedAt, long UsedMemoryVersion, MacroObservationSnapshot Observation,
    HypothesisSet Hypotheses, EvidenceSearch SupportingEvidence, EvidenceSearch CounterEvidence,
    ActionProposal ProposedAction, MacroDecision Decision, CounterfactualSnapshot Counterfactual,
    string AuditHash, ExpertPoolSnapshot ExpertPool, ExpertDependencySnapshot Dependencies,
    ExpertPoolDecisionAudit ExpertDecisions);
public sealed record HypothesisAssessment(string HypothesisId, Assessment HypothesisCorrect,
    string EvidenceRuleVersion, IssueRange EvaluatedRange, string Explanation);
// Append-only record referencing a frozen audit, not an update of that audit.
public sealed record MacroReflection(string ReflectionId, string PredictionAuditHash, long Issue,
    DateTimeOffset CreatedAt, ClosedResult Actual, OutcomeType Outcome,
    ImmutableArray<HypothesisAssessment> HypothesisAssessments, Assessment DecisionCorrect,
    bool Rescue, bool Harm, bool CriticPreventedHarm, bool CriticMissedRescue,
    string ReflectionRuleVersion, long BeforeMemoryVersion, long AfterMemoryVersion,
    ImmutableArray<CommonFailureEvent> CommonFailures, ImmutableArray<CommonSuccessEvent> CommonSuccesses);
public sealed record ReliabilityStats(int Triggered, int Matured, int Correct, int Rescue, int Harm,
    double? EstimatedReliability, string EstimatorVersion);
public sealed record ConfidenceCalibrationStats(double LowerInclusive, double Upper,
    int MaturedSamples, double? MeanConfidence, double? ReasoningCorrectRate, double? CalibrationError);
public sealed record ReasoningMemorySnapshot(long Version, long LastTrainingIssue,
    DateTimeOffset AvailableAt, ImmutableDictionary<string,ReliabilityStats> Hypotheses,
    ImmutableDictionary<string,ReliabilityStats> Evidence,
    ImmutableDictionary<string,ReliabilityStats> CounterEvidence,
    ImmutableDictionary<string,ReliabilityStats> Actions,
    ImmutableDictionary<string,ReliabilityStats> ExpertEnvironments,
    ImmutableArray<ConfidenceCalibrationStats> Calibration, string MemoryHash);
public sealed record WeaknessSummary(ImmutableDictionary<string,ReliabilityStats> Signals);
public sealed record ReasoningMetrics(ImmutableDictionary<string,double?> Values,
    ImmutableDictionary<string,int> Denominators, string Split, string DefinitionVersion);

public interface IMacroObservationEngine { MacroObservationSnapshot Observe(PrefixContext prefix); }
public interface IMacroHypothesisEngine { HypothesisSet Build(MacroObservationSnapshot observation, ReasoningMemorySnapshot memory); }
public interface IMacroEvidenceEngine { EvidenceSearch Search(MacroObservationSnapshot observation, HypothesisSet hypotheses, PrefixContext prefix); }
public interface IMacroCounterEvidenceEngine { EvidenceSearch Search(MacroObservationSnapshot observation, HypothesisSet hypotheses, EvidenceSearch support, PrefixContext prefix); }
public interface IMacroReasoningCritic { CriticResult Review(PrefixContext prefix, HypothesisSet hypotheses, EvidenceSearch support, EvidenceSearch counter, ActionProposal proposal); }
public interface IMacroConfidenceEngine { ConfidenceResult Estimate(HypothesisSet hypotheses, EvidenceSearch support, EvidenceSearch counter, CriticResult critic, ReasoningMemorySnapshot memory); }
public interface IMacroDecisionEngine { MacroDecision Decide(long issue, HypothesisSet hypotheses, EvidenceSearch support, EvidenceSearch counter, ActionProposal proposal, CriticResult critic, ConfidenceResult confidence); }
public interface IMacroUncertaintyController { MacroDecision Constrain(MacroDecision decision); }
public interface IMacroGatingModel { ImmutableArray<string> Rank(ImmutableArray<ExpertSnapshot> experts,
    ImmutableDictionary<string,double> weights, ImmutableArray<ExpertDecision> decisions,
    ImmutableArray<ExpertCounterView> counterViews); }
public interface IMacroCounterfactualEngine { CounterfactualSnapshot Evaluate(ImmutableArray<ExpertSnapshot> experts, MacroDecision decision, ImmutableDictionary<string,double> fixedControlWeights); }
public interface IMacroReflectionEngine { MacroReflection Reflect(MacroReasoningAudit audit, ImmutableArray<ClosedResult> availableResults, ReasoningMemorySnapshot memory, DateTimeOffset asOf); }
public interface IMacroReasoningMemory { ReasoningMemorySnapshot ReadAsOf(long targetIssue, DateTimeOffset asOf); void Append(MacroReflection reflection, long expectedVersion); }
public interface IMacroReasoningAuditStore { void AppendPrediction(MacroReasoningAudit audit); void AppendReflection(MacroReflection reflection); MacroReasoningAudit? Read(string experimentId, long issue); }
public interface IMacroWalkForwardEvaluator { ReasoningMetrics Evaluate(RunIdentity run, ImmutableArray<long> issues, string frozenSplitDefinition); }
// Explanation service receives a stored audit identifier; it has no rank, weight or memory write API.
public interface IModelExplanationService { string Explain(string experimentId, long issue, string question); }
