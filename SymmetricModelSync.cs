using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace 六合分析软件;

public sealed record SymmetricPredictionSnapshot(
    [property: JsonPropertyName("issue")] string Issue,
    [property: JsonPropertyName("model_version")] string ModelVersion,
    [property: JsonPropertyName("analysis_periods")] int AnalysisPeriods,
    [property: JsonPropertyName("predict_zodiac")] string PredictZodiac,
    [property: JsonPropertyName("top6_zodiac")] string Top6Zodiac,
    [property: JsonPropertyName("scores_hash")] string ScoresHash);

public sealed record SymmetricLearningEvent(
    [property: JsonPropertyName("issue")] string Issue,
    [property: JsonPropertyName("model_version")] string ModelVersion,
    [property: JsonPropertyName("before_state_hash")] string BeforeStateHash,
    [property: JsonPropertyName("actual_zodiac")] string ActualZodiac,
    [property: JsonPropertyName("after_state_hash")] string AfterStateHash);

public sealed record SymmetricModelStateSnapshot(
    [property: JsonPropertyName("model_version")] string ModelVersion,
    [property: JsonPropertyName("code_version")] string CodeVersion,
    [property: JsonPropertyName("generated_at")] string GeneratedAt,
    [property: JsonPropertyName("predictions")] IReadOnlyList<SymmetricPredictionSnapshot> Predictions,
    [property: JsonPropertyName("learning_events")] IReadOnlyList<SymmetricLearningEvent> LearningEvents);

public static class SymmetricModelSync
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static string CanonicalHash(SymmetricModelStateSnapshot snapshot)
    {
        string json = JsonSerializer.Serialize(new
        {
            snapshot.ModelVersion,
            snapshot.CodeVersion,
            snapshot.GeneratedAt,
            Predictions = snapshot.Predictions.OrderBy(item => item.Issue).ThenBy(item => item.ModelVersion).ThenBy(item => item.AnalysisPeriods),
            LearningEvents = snapshot.LearningEvents.OrderBy(item => item.Issue).ThenBy(item => item.ModelVersion)
        }, Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    public static void ValidateEventConflict(IReadOnlyList<SymmetricLearningEvent> existing,
        SymmetricLearningEvent incoming)
    {
        SymmetricLearningEvent? same = existing.FirstOrDefault(item =>
            item.Issue == incoming.Issue && item.ModelVersion == incoming.ModelVersion);
        if (same is not null && !string.Equals(JsonSerializer.Serialize(same, Options),
            JsonSerializer.Serialize(incoming, Options), StringComparison.Ordinal))
            throw new InvalidDataException($"学习事件冲突：{incoming.Issue}/{incoming.ModelVersion}");
    }

    public static SymmetricModelStateSnapshot Merge(SymmetricModelStateSnapshot current,
        SymmetricModelStateSnapshot incoming)
    {
        if (!string.Equals(current.ModelVersion, incoming.ModelVersion, StringComparison.Ordinal))
            throw new InvalidDataException("模型版本不一致，拒绝合并同步状态");
        foreach (SymmetricLearningEvent item in incoming.LearningEvents)
            ValidateEventConflict(current.LearningEvents, item);
        var predictions = current.Predictions.Concat(incoming.Predictions)
            .GroupBy(item => $"{item.Issue}|{item.ModelVersion}|{item.AnalysisPeriods}", StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.Issue).ThenBy(item => item.ModelVersion).ThenBy(item => item.AnalysisPeriods)
            .ToArray();
        var events = current.LearningEvents.Concat(incoming.LearningEvents)
            .GroupBy(item => $"{item.Issue}|{item.ModelVersion}", StringComparer.Ordinal)
            .Select(group => group.First()).OrderBy(item => item.Issue).ThenBy(item => item.ModelVersion).ToArray();
        return current with { Predictions = predictions, LearningEvents = events,
            GeneratedAt = string.CompareOrdinal(current.GeneratedAt, incoming.GeneratedAt) >= 0 ? current.GeneratedAt : incoming.GeneratedAt };
    }
}
