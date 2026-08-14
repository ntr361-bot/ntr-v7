using System.Collections.ObjectModel;

namespace 六合分析软件;

public sealed record PredictionTraceFactor(double Raw, double Contribution);

public sealed record PredictionTraceZodiac(
    string Zodiac,
    int Rank,
    double TotalScore,
    IReadOnlyDictionary<string, PredictionTraceFactor> Factors);

public sealed record PredictionTraceBaseModel(
    string ModelKey,
    string ModelVersion,
    int AnalysisPeriods,
    IReadOnlyDictionary<string, double> Weights,
    IReadOnlyList<PredictionTraceZodiac> Ranking);

public sealed record PredictionTraceAutoZodiac(
    string Zodiac,
    int Rank,
    int Rank50,
    int Rank100,
    int RankAll,
    double Normalized50,
    double Normalized100,
    double NormalizedAll,
    double RuleScore,
    double ModelConsensus,
    double Logit,
    double SoftmaxProbability);

public sealed record PredictionTraceAutoLearning(
    IReadOnlyList<PredictionTraceAutoZodiac> Zodiacs,
    IReadOnlyDictionary<string, double> Weights,
    IReadOnlyDictionary<string, double> MetaCoefficients,
    bool UsedFallback,
    string FallbackReason);

public sealed record PredictionTraceSnapshot(
    string Issue,
    string CaptureKind,
    string TraceSchemaVersion,
    DateTimeOffset GeneratedAt,
    string HistoryCutoffIssue,
    int HistorySampleCount,
    string ModelVersion,
    string CodeVersion,
    string Status,
    IReadOnlyList<PredictionTraceBaseModel> BaseModels,
    PredictionTraceAutoLearning AutoLearning);
