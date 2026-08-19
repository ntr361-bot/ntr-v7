using System.Data.SQLite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace 六合分析软件;

public static class PredictionTraceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static void SaveLive(PredictionTraceSnapshot snapshot)
    {
        if (!string.Equals(snapshot.CaptureKind, "Live", StringComparison.Ordinal))
            throw new ArgumentException("PredictionTrace live capture kind must be Live.", nameof(snapshot));
        if (string.IsNullOrWhiteSpace(snapshot.Issue) || snapshot.BaseModels.Count != 3 ||
            snapshot.BaseModels.Any(model => model.Ranking.Count != 12) ||
            snapshot.AutoLearning.Zodiacs.Count != 12)
            throw new ArgumentException("PredictionTrace must contain three complete base rankings and one complete AutoLearning ranking.", nameof(snapshot));

        DatabaseHelper.InitializeDatabase();
        string payload = CanonicalPayload(snapshot);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

        using SQLiteConnection connection = DatabaseHelper.GetConnection();
        using SQLiteTransaction transaction = connection.BeginTransaction();
        using var existing = new SQLiteCommand(@"SELECT PayloadHash FROM PredictionTrace
            WHERE Issue=@issue AND TraceSchemaVersion=@schema AND CaptureKind=@kind", connection, transaction);
        existing.Parameters.AddWithValue("@issue", snapshot.Issue);
        existing.Parameters.AddWithValue("@schema", snapshot.TraceSchemaVersion);
        existing.Parameters.AddWithValue("@kind", snapshot.CaptureKind);
        object? savedHash = existing.ExecuteScalar();
        if (savedHash is not null && savedHash is not DBNull)
        {
            if (!string.Equals(Convert.ToString(savedHash), hash, StringComparison.Ordinal))
                throw new InvalidOperationException("PredictionTrace is immutable; the existing snapshot has different content.");
            transaction.Commit();
            return;
        }

        using var insert = new SQLiteCommand(@"INSERT INTO PredictionTrace
            (Issue, TraceSchemaVersion, CaptureKind, GeneratedAt, HistoryCutoffIssue, HistorySampleCount,
             ModelVersion, CodeVersion, Status, PayloadJson, PayloadHash, CreatedAt)
            VALUES (@issue,@schema,@kind,@generated,@cutoff,@samples,@model,@code,@status,@payload,@hash,@created);
            SELECT last_insert_rowid();", connection, transaction);
        insert.Parameters.AddWithValue("@issue", snapshot.Issue);
        insert.Parameters.AddWithValue("@schema", snapshot.TraceSchemaVersion);
        insert.Parameters.AddWithValue("@kind", snapshot.CaptureKind);
        insert.Parameters.AddWithValue("@generated", snapshot.GeneratedAt.ToString("O"));
        insert.Parameters.AddWithValue("@cutoff", snapshot.HistoryCutoffIssue);
        insert.Parameters.AddWithValue("@samples", snapshot.HistorySampleCount);
        insert.Parameters.AddWithValue("@model", snapshot.ModelVersion);
        insert.Parameters.AddWithValue("@code", snapshot.CodeVersion);
        insert.Parameters.AddWithValue("@status", snapshot.Status);
        insert.Parameters.AddWithValue("@payload", payload);
        insert.Parameters.AddWithValue("@hash", hash);
        insert.Parameters.AddWithValue("@created", DateTimeOffset.UtcNow.ToString("O"));
        long traceId = Convert.ToInt64(insert.ExecuteScalar());

        foreach (PredictionTraceBaseModel model in snapshot.BaseModels)
        foreach (PredictionTraceZodiac zodiac in model.Ranking)
        {
            using var detail = new SQLiteCommand(@"INSERT INTO PredictionTraceModel
                (TraceId, ModelKey, Zodiac, Rank, TotalScore, FactorsJson)
                VALUES (@traceId,@modelKey,@zodiac,@rank,@score,@factors)", connection, transaction);
            detail.Parameters.AddWithValue("@traceId", traceId);
            detail.Parameters.AddWithValue("@modelKey", model.ModelKey);
            detail.Parameters.AddWithValue("@zodiac", zodiac.Zodiac);
            detail.Parameters.AddWithValue("@rank", zodiac.Rank);
            detail.Parameters.AddWithValue("@score", zodiac.TotalScore);
            detail.Parameters.AddWithValue("@factors", JsonSerializer.Serialize(
                zodiac.Factors.OrderBy(item => item.Key).ToDictionary(item => item.Key, item => item.Value), JsonOptions));
            detail.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public static PredictionTraceSnapshot? GetLive(string issue)
    {
        DatabaseHelper.InitializeDatabase();
        using SQLiteConnection connection = DatabaseHelper.GetConnection();
        using var command = new SQLiteCommand(@"SELECT PayloadJson FROM PredictionTrace
            WHERE Issue=@issue AND CaptureKind='Live' ORDER BY Id DESC LIMIT 1", connection);
        command.Parameters.AddWithValue("@issue", issue);
        string? payload = Convert.ToString(command.ExecuteScalar());
        return string.IsNullOrWhiteSpace(payload)
            ? null
            : JsonSerializer.Deserialize<PredictionTraceSnapshot>(payload, JsonOptions)
              ?? throw new InvalidDataException("PredictionTrace payload cannot be read.");
    }

    public static void CaptureLive(string issue, string historyCutoffIssue, int historySampleCount,
        IReadOnlyList<AIEngine.PredictResult> baseResults, AutoLearningSnapshot autoLearning, string codeVersion)
    {
        // A force-rerun must never replace the first real-time snapshot with a later replay.
        if (GetLive(issue) is not null) return;
        if (baseResults.Count != 3)
            throw new ArgumentException("正式 Trace 必须接收50期、100期和全部历史三条基础预测。", nameof(baseResults));
        var models = baseResults.Select(ToBaseModel).ToArray();
        if (models.Select(model => model.ModelKey).Distinct(StringComparer.Ordinal).Count() != 3)
            throw new ArgumentException("正式 Trace 的基础模型身份不完整。", nameof(baseResults));
        DateTimeOffset generatedAt = baseResults.Select(result => new DateTimeOffset(result.PredictTime)).Max();
        SaveLive(new PredictionTraceSnapshot(issue, "Live", "trace-v1", generatedAt,
            historyCutoffIssue, historySampleCount, AIEngine.Version, codeVersion, "Complete", models,
            ToAutoLearning(autoLearning, models)));
    }

    public static void RecordLiveOutcome(string issue, string actualZodiac, string actualNumber,
        PredictionTraceLearningState beforeLearning, PredictionTraceLearningState afterLearning,
        bool weightUpdateTriggered)
    {
        PredictionTraceSnapshot trace = GetLive(issue)
            ?? throw new InvalidOperationException("不能为没有 Live Trace 的历史预测补写开奖结果。");
        var baseRanks = trace.BaseModels.ToDictionary(model => model.ModelKey,
            model => model.Ranking.Single(item => item.Zodiac == actualZodiac).Rank, StringComparer.Ordinal);
        int autoRank = trace.AutoLearning.Zodiacs.Single(item => item.Zodiac == actualZodiac).Rank;
        var outcome = new PredictionTraceOutcome(issue, actualZodiac, actualNumber, baseRanks, autoRank,
            autoRank <= 3, autoRank <= 6, weightUpdateTriggered, beforeLearning, afterLearning, DateTimeOffset.UtcNow);
        string payload = JsonSerializer.Serialize(outcome, JsonOptions);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

        DatabaseHelper.InitializeDatabase();
        using SQLiteConnection connection = DatabaseHelper.GetConnection();
        using SQLiteTransaction transaction = connection.BeginTransaction();
        using var traceCommand = new SQLiteCommand("SELECT Id FROM PredictionTrace WHERE Issue=@issue AND CaptureKind='Live' ORDER BY Id DESC LIMIT 1", connection, transaction);
        traceCommand.Parameters.AddWithValue("@issue", issue);
        long traceId = Convert.ToInt64(traceCommand.ExecuteScalar() ?? throw new InvalidOperationException("找不到 Live Trace。"));
        using var existing = new SQLiteCommand("SELECT OutcomeHash FROM PredictionTraceOutcome WHERE TraceId=@traceId", connection, transaction);
        existing.Parameters.AddWithValue("@traceId", traceId);
        object? existingHash = existing.ExecuteScalar();
        if (existingHash is not null && existingHash is not DBNull)
        {
            transaction.Commit();
            if (!string.Equals(Convert.ToString(existingHash), hash, StringComparison.Ordinal))
                throw new InvalidOperationException("PredictionTrace Outcome 已存在，不能覆盖。");
            return;
        }
        using var insert = new SQLiteCommand(@"INSERT INTO PredictionTraceOutcome
            (TraceId, Issue, ActualZodiac, ActualNumber, OutcomeJson, OutcomeHash, RecordedAt)
            VALUES (@traceId,@issue,@zodiac,@number,@payload,@hash,@recorded)", connection, transaction);
        insert.Parameters.AddWithValue("@traceId", traceId);
        insert.Parameters.AddWithValue("@issue", issue);
        insert.Parameters.AddWithValue("@zodiac", actualZodiac);
        insert.Parameters.AddWithValue("@number", actualNumber);
        insert.Parameters.AddWithValue("@payload", payload);
        insert.Parameters.AddWithValue("@hash", hash);
        insert.Parameters.AddWithValue("@recorded", outcome.RecordedAt.ToString("O"));
        insert.ExecuteNonQuery();
        transaction.Commit();
    }

    public static PredictionTraceOutcome? GetLiveOutcome(string issue)
    {
        DatabaseHelper.InitializeDatabase();
        using SQLiteConnection connection = DatabaseHelper.GetConnection();
        using var command = new SQLiteCommand(@"SELECT outcome.OutcomeJson FROM PredictionTraceOutcome outcome
            INNER JOIN PredictionTrace trace ON trace.Id=outcome.TraceId
            WHERE trace.Issue=@issue AND trace.CaptureKind='Live' ORDER BY trace.Id DESC LIMIT 1", connection);
        command.Parameters.AddWithValue("@issue", issue);
        string? payload = Convert.ToString(command.ExecuteScalar());
        return string.IsNullOrWhiteSpace(payload) ? null : JsonSerializer.Deserialize<PredictionTraceOutcome>(payload, JsonOptions);
    }

    private static PredictionTraceBaseModel ToBaseModel(AIEngine.PredictResult result)
    {
        int weightPeriod = result.AnalysisPeriods is 50 or 100
            ? result.AnalysisPeriods
            : AISettings.AllHistoryModeValue;
        V65RuleScoringEngine.WeightConfig weights = V65ExperimentPipeline.GetWeightsForPeriods(weightPeriod);
        var ranking = result.AllScores.OrderByDescending(score => score.TotalScore).Select((score, index) =>
            new PredictionTraceZodiac(score.Zodiac, index + 1, score.TotalScore,
                new Dictionary<string, PredictionTraceFactor>(StringComparer.Ordinal)
                {
                    ["F"] = new(score.FrequencyScore, score.FrequencyScore * weights.FrequencyWeight),
                    ["T"] = new(score.RecentTrendScore, score.RecentTrendScore * weights.RecentTrendWeight),
                    ["O"] = new(score.OmissionScore, score.OmissionScore * weights.OmissionWeight),
                    ["H"] = new(score.HotColdScore, score.HotColdScore * weights.HotColdWeight),
                    ["P"] = new(score.PeriodPatternScore, score.PeriodPatternScore * weights.PeriodPatternWeight),
                    ["C"] = new(score.ConsecutiveScore, score.ConsecutiveScore * weights.ConsecutiveWeight),
                    ["B"] = new(score.EightZodiacScore, score.EightZodiacScore)
                })).ToArray();
        return new PredictionTraceBaseModel(ExperimentModels.ForPeriods(result.AnalysisPeriods), result.Version,
            result.AnalysisPeriods, new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["F"] = weights.FrequencyWeight, ["T"] = weights.RecentTrendWeight,
                ["O"] = weights.OmissionWeight, ["H"] = weights.HotColdWeight,
                ["P"] = weights.PeriodPatternWeight, ["C"] = weights.ConsecutiveWeight
            }, ranking);
    }

    private static PredictionTraceAutoLearning ToAutoLearning(AutoLearningSnapshot snapshot,
        IReadOnlyList<PredictionTraceBaseModel> models)
    {
        var ranks = models.ToDictionary(model => model.ModelKey,
            model => model.Ranking.ToDictionary(item => item.Zodiac, item => item.Rank), StringComparer.Ordinal);
        IReadOnlyDictionary<string, double> coefficients = snapshot.MetaCoefficients ?? new Dictionary<string, double>();
        var normalized = new[] { "AI", "ML", "State", "V7" }.ToDictionary(source => source,
            source => Normalize(snapshot.Input.Zodiacs.ToDictionary(row => row.Zodiac,
                row => row.BaseScores.GetValueOrDefault(source))), StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, double> weights = snapshot.Weights.AsDictionary();
        return new PredictionTraceAutoLearning(snapshot.Result.Ranking.Select(row =>
        {
            ZodiacMetaFeatures input = snapshot.Input.Zodiacs.Single(item => item.Zodiac == row.Zodiac);
            double logit = normalized.Sum(source => weights[source.Key] * source.Value[row.Zodiac]) +
                input.FeatureGroups.Sum(feature => Math.Clamp(feature.Value, -1, 1) * coefficients.GetValueOrDefault(feature.Key));
            return new PredictionTraceAutoZodiac(row.Zodiac, row.Rank,
                ranks[ExperimentModels.Period50][row.Zodiac], ranks[ExperimentModels.Period100][row.Zodiac],
                ranks[ExperimentModels.AllHistory][row.Zodiac], normalized["AI"][row.Zodiac],
                normalized["ML"][row.Zodiac], normalized["State"][row.Zodiac],
                input.BaseScores.GetValueOrDefault("V7"), input.FeatureGroups.GetValueOrDefault("model_consensus"),
                logit, row.Probability);
        }).ToArray(), new Dictionary<string, double>(weights, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, double>(coefficients, StringComparer.OrdinalIgnoreCase),
            snapshot.Result.UsedFallback, snapshot.Result.FallbackReason);
    }

    private static IReadOnlyDictionary<string, double> Normalize(IReadOnlyDictionary<string, double> values)
    {
        double min = values.Values.Min();
        double max = values.Values.Max();
        double range = max - min;
        return values.ToDictionary(pair => pair.Key, pair => range < 1e-12 ? .5 : (pair.Value - min) / range,
            StringComparer.OrdinalIgnoreCase);
    }

    private static string CanonicalPayload(PredictionTraceSnapshot snapshot)
    {
        var canonical = new
        {
            snapshot.Issue,
            snapshot.CaptureKind,
            snapshot.TraceSchemaVersion,
            GeneratedAt = snapshot.GeneratedAt.ToString("O"),
            snapshot.HistoryCutoffIssue,
            snapshot.HistorySampleCount,
            snapshot.ModelVersion,
            snapshot.CodeVersion,
            snapshot.Status,
            BaseModels = snapshot.BaseModels.Select(model => new
            {
                model.ModelKey,
                model.ModelVersion,
                model.AnalysisPeriods,
                Weights = model.Weights.OrderBy(item => item.Key).ToDictionary(item => item.Key, item => item.Value),
                Ranking = model.Ranking.OrderBy(item => item.Rank).Select(zodiac => new
                {
                    zodiac.Zodiac,
                    zodiac.Rank,
                    zodiac.TotalScore,
                    Factors = zodiac.Factors.OrderBy(item => item.Key).ToDictionary(item => item.Key, item => item.Value)
                })
            }),
            AutoLearning = new
            {
                Zodiacs = snapshot.AutoLearning.Zodiacs.OrderBy(item => item.Rank),
                Weights = snapshot.AutoLearning.Weights.OrderBy(item => item.Key).ToDictionary(item => item.Key, item => item.Value),
                MetaCoefficients = snapshot.AutoLearning.MetaCoefficients.OrderBy(item => item.Key).ToDictionary(item => item.Key, item => item.Value),
                snapshot.AutoLearning.UsedFallback,
                snapshot.AutoLearning.FallbackReason
            }
        };
        return JsonSerializer.Serialize(canonical, JsonOptions);
    }
}
