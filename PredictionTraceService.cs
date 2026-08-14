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
