using System.Text.Json;
using System.Text.Json.Serialization;

namespace 六合分析软件;

public sealed record SymmetricRuntimeStateSnapshot(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("model_version")] string ModelVersion,
    [property: JsonPropertyName("code_version")] string CodeVersion,
    [property: JsonPropertyName("predictions")] IReadOnlyList<DatabaseHelper.PredictionRecord> Predictions,
    [property: JsonPropertyName("model_memory")] IReadOnlyDictionary<string, string> ModelMemory,
    [property: JsonPropertyName("state_hash")] string StateHash,
    [property: JsonPropertyName("generated_at")] string GeneratedAt);

public static class SymmetricRuntimeStateSync
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static SymmetricRuntimeStateSnapshot Export(string codeVersion)
    {
        DatabaseHelper.InitializeDatabase();
        var memory = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string key in ExperimentModels.AllKeys.Append(ExperimentModels.IntelligentHistory).Distinct(StringComparer.Ordinal))
        {
            string? json = DatabaseHelper.LoadModelMemoryJson(ExperimentModels.MemoryKey(key));
            if (!string.IsNullOrWhiteSpace(json)) memory[ExperimentModels.MemoryKey(key)] = json;
        }
        var draft = new SymmetricRuntimeStateSnapshot("v1", AIEngine.Version, codeVersion,
            DatabaseHelper.GetPredictionHistory(int.MaxValue), memory, "", DateTimeOffset.UtcNow.ToString("O"));
        return draft with { StateHash = Hash(draft) };
    }

    public static string Hash(SymmetricRuntimeStateSnapshot snapshot)
    {
        string payload = JsonSerializer.Serialize(new
        {
            snapshot.SchemaVersion,
            snapshot.ModelVersion,
            snapshot.CodeVersion,
            Predictions = snapshot.Predictions.OrderBy(item => item.Issue).ThenBy(item => item.ModelVersion).ThenBy(item => item.AnalysisPeriods),
            ModelMemory = snapshot.ModelMemory.OrderBy(item => item.Key)
        }, Options);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload)));
    }

    public static int MergeIntoLocal(SymmetricRuntimeStateSnapshot incoming)
    {
        if (incoming.SchemaVersion != "v1" || incoming.ModelVersion != AIEngine.Version ||
            !string.Equals(Hash(incoming), incoming.StateHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("云端模型状态版本或哈希无效");
        DatabaseHelper.InitializeDatabase();
        foreach (DatabaseHelper.PredictionRecord row in incoming.Predictions)
            DatabaseHelper.ValidateSynchronizedPrediction(row);
        foreach ((string key, string json) in incoming.ModelMemory)
        {
            string? local = DatabaseHelper.LoadModelMemoryJson(key);
            if (!string.IsNullOrWhiteSpace(local) && !string.Equals(local, json, StringComparison.Ordinal))
                throw new InvalidDataException($"模型记忆冲突：{key}");
        }
        int merged = 0;
        foreach (DatabaseHelper.PredictionRecord row in incoming.Predictions)
            row.PredictionSource = "云端同步";
        foreach (DatabaseHelper.PredictionRecord row in incoming.Predictions)
            merged += DatabaseHelper.MergeSynchronizedPrediction(row);
        foreach ((string key, string json) in incoming.ModelMemory)
        {
            string? local = DatabaseHelper.LoadModelMemoryJson(key);
            if (string.IsNullOrWhiteSpace(local)) DatabaseHelper.SaveModelMemoryJson(key, json);
        }
        return merged;
    }
}
