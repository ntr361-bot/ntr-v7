using System.Text.Json;

namespace 六合分析软件;

public sealed class FeedbackMemoryItem
{
    public string Issue { get; set; } = "";
    public int ActualRank { get; set; }
    public Dictionary<string, int> BaseModelRanks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> FeatureSignals { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class LearningAdjustmentRecord
{
    public long Id { get; set; }
    public string Issue { get; set; } = "";
    public DateTime AdjustedAt { get; set; } = DateTime.Now;
    public ModelWeights OldWeights { get; set; } = ModelWeights.Default;
    public ModelWeights NewWeights { get; set; } = ModelWeights.Default;
    public Dictionary<string, double> FeatureContribution { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Reason { get; set; } = "";
}

public sealed class ModelMemoryState
{
    public ModelWeights Weights { get; set; } = ModelWeights.Default;
    public Dictionary<string, double> MetaCoefficients { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> FeatureContributions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int LearnedSamples { get; set; }
    public string LastTrainingIssue { get; set; } = "";
    public List<bool> RecentTop3 { get; set; } = new();
    public List<bool> RecentTop6 { get; set; } = new();
    public List<double> RecentReciprocalRanks { get; set; } = new();
    public int ConsecutiveTop3Misses { get; set; }
    public int ConsecutiveTop6Misses { get; set; }
    public bool Top3ThresholdFired { get; set; }
    public bool Top6ThresholdFired { get; set; }
    public List<FeedbackMemoryItem> RecentFeedback { get; set; } = new();
    public List<LearningAdjustmentRecord> RecentAdjustments { get; set; } = new();
    public ColorLearningState ColorLearning { get; set; } = new();
}

public interface IModelMemoryStore
{
    string? Load(string key);
    void Save(string key, string json);
}

public sealed class DatabaseModelMemoryStore : IModelMemoryStore
{
    public string? Load(string key) => DatabaseHelper.LoadModelMemoryJson(key);
    public void Save(string key, string json) => DatabaseHelper.SaveModelMemoryJson(key, json);
}

public sealed class ModelMemory
{
    public const string LegacyMemoryKey = "auto-learning-meta-v1";
    public string MemoryKey { get; }
    public static string MemoryKeyFor(string experimentKey) => ExperimentModels.MemoryKey(experimentKey);
    private readonly IModelMemoryStore store;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public ModelMemory(string experimentKey = ExperimentModels.AutoLearning, IModelMemoryStore? store = null)
    {
        MemoryKey = ExperimentModels.MemoryKey(experimentKey);
        this.store = store ?? new DatabaseModelMemoryStore();
    }

    public ModelMemoryState LoadOrCreate()
    {
        try
        {
            string? json = store.Load(MemoryKey);
            var state = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ModelMemoryState>(json, JsonOptions);
            return state is null ? new ModelMemoryState() : Validate(state);
        }
        catch (Exception ex)
        {
            AppLogger.Error("加载自动学习模型记忆", ex);
            return new ModelMemoryState();
        }
    }

    public void Save(ModelMemoryState state)
    {
        state = Validate(state);
        store.Save(MemoryKey, JsonSerializer.Serialize(state, JsonOptions));
    }

    public static ModelMemoryState Validate(ModelMemoryState state)
    {
        state.Weights = WeightOptimizer.Normalize(state.Weights);
        state.LearnedSamples = Math.Max(0, state.LearnedSamples);
        state.ConsecutiveTop3Misses = Math.Max(0, state.ConsecutiveTop3Misses);
        state.ConsecutiveTop6Misses = Math.Max(0, state.ConsecutiveTop6Misses);
        Trim(state.RecentTop3, 500);
        Trim(state.RecentTop6, 500);
        Trim(state.RecentReciprocalRanks, 500);
        Trim(state.RecentFeedback, 500);
        Trim(state.RecentAdjustments, 100);
        RemoveInvalid(state.MetaCoefficients);
        RemoveInvalid(state.FeatureContributions);
        state.ColorLearning ??= new ColorLearningState();
        ColorAutoLearningEngine.Validate(state.ColorLearning);
        return state;
    }

    private static void RemoveInvalid(Dictionary<string, double> values)
    {
        foreach (string key in values.Where(pair => !double.IsFinite(pair.Value)).Select(pair => pair.Key).ToArray())
            values.Remove(key);
    }

    private static void Trim<T>(List<T> values, int maximum)
    {
        if (values.Count > maximum) values.RemoveRange(0, values.Count - maximum);
    }
}
