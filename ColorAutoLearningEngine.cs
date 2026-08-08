using System.Text;
using System.Text.Json;

namespace 六合分析软件;

public sealed record ColorLearningWeights(double Frequency, double Transition, double Omission)
{
    public static ColorLearningWeights Default { get; } = new(0.55, 0.25, 0.20);
    public double Sum => Frequency + Transition + Omission;
}

public sealed record ColorPredictionFeedback(
    string Issue,
    string ActualColor,
    string MainColor,
    string DefenseColor,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> FeatureSignals);

public sealed record ColorLearningOutcome(
    bool Updated,
    bool FailureAnalysisTriggered,
    bool MainHit,
    bool DualHit,
    ColorLearningWeights Weights,
    string Reason);

public sealed class ColorFeedbackMemoryItem
{
    public string Issue { get; set; } = "";
    public string ActualColor { get; set; } = "";
    public string MainColor { get; set; } = "";
    public string DefenseColor { get; set; } = "";
    public bool MainHit { get; set; }
    public bool DualHit { get; set; }
    public Dictionary<string, Dictionary<string, double>> FeatureSignals { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ColorLearningAdjustment
{
    public string Issue { get; set; } = "";
    public DateTime AdjustedAt { get; set; } = DateTime.Now;
    public ColorLearningWeights OldWeights { get; set; } = ColorLearningWeights.Default;
    public ColorLearningWeights NewWeights { get; set; } = ColorLearningWeights.Default;
    public string Reason { get; set; } = "";
}

public sealed class ColorLearningState
{
    public ColorLearningWeights Weights { get; set; } = ColorLearningWeights.Default;
    public int LearnedSamples { get; set; }
    public string LastTrainingIssue { get; set; } = "";
    public List<bool> RecentMainHits { get; set; } = new();
    public List<bool> RecentDualHits { get; set; } = new();
    public int ConsecutiveMainMisses { get; set; }
    public int ConsecutiveDualMisses { get; set; }
    public bool MainThresholdFired { get; set; }
    public bool DualThresholdFired { get; set; }
    public List<string> LearnedIssues { get; set; } = new();
    public List<ColorFeedbackMemoryItem> RecentFeedback { get; set; } = new();
    public List<ColorLearningAdjustment> RecentAdjustments { get; set; } = new();
}

public sealed class ColorPredictionSnapshot
{
    public string Issue { get; set; } = "";
    public string MainColor { get; set; } = "";
    public string DefenseColor { get; set; } = "";
    public ColorLearningWeights Weights { get; set; } = ColorLearningWeights.Default;
    public Dictionary<string, Dictionary<string, double>> FeatureSignals { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public static class ColorPredictionSnapshotCodec
{
    public const string Marker = "波色学习:";

    public static string Encode(string issue, ColorPredictionResult prediction)
    {
        var snapshot = new ColorPredictionSnapshot
        {
            Issue = issue,
            MainColor = prediction.Main,
            DefenseColor = prediction.Defense,
            Weights = prediction.Weights,
            FeatureSignals = prediction.FeatureSignals.ToDictionary(pair => pair.Key,
                pair => pair.Value.ToDictionary(value => value.Key, value => value.Value,
                    StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
        };
        string json = JsonSerializer.Serialize(snapshot);
        return Marker + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    public static bool TryDecode(string scoreDetails, out ColorPredictionSnapshot snapshot)
    {
        snapshot = new ColorPredictionSnapshot();
        int marker = scoreDetails?.IndexOf(Marker, StringComparison.Ordinal) ?? -1;
        if (marker < 0) return false;
        string encoded = scoreDetails![(marker + Marker.Length)..];
        int separator = encoded.IndexOf('|');
        if (separator >= 0) encoded = encoded[..separator];
        try
        {
            string json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded.Trim()));
            snapshot = JsonSerializer.Deserialize<ColorPredictionSnapshot>(json) ?? new ColorPredictionSnapshot();
            return snapshot.FeatureSignals.Count == 3 && snapshot.MainColor.Length > 0 && snapshot.DefenseColor.Length > 0;
        }
        catch
        {
            snapshot = new ColorPredictionSnapshot();
            return false;
        }
    }
}

public sealed class ColorAutoLearningEngine
{
    private static readonly string[] FeatureNames = { "frequency", "transition", "omission" };

    public ColorLearningOutcome ApplyFeedback(ColorLearningState state, ColorPredictionFeedback feedback)
    {
        state.Weights = Normalize(state.Weights);
        if (!IsColor(feedback.ActualColor) || !IsColor(feedback.MainColor) || !IsColor(feedback.DefenseColor))
            return new(false, false, false, false, state.Weights, "波色无效，跳过学习");
        if (state.LearnedIssues.Contains(feedback.Issue, StringComparer.OrdinalIgnoreCase))
            return new(false, false, false, false, state.Weights, "该期波色已经学习");

        bool mainHit = feedback.ActualColor == feedback.MainColor;
        bool dualHit = mainHit || feedback.ActualColor == feedback.DefenseColor;
        UpdateCounters(state, mainHit, dualHit);
        bool mainTrigger = state.ConsecutiveMainMisses == 5 && !state.MainThresholdFired;
        bool dualTrigger = state.ConsecutiveDualMisses == 3 && !state.DualThresholdFired;
        if (mainTrigger) state.MainThresholdFired = true;
        if (dualTrigger) state.DualThresholdFired = true;

        string reason = mainTrigger && dualTrigger ? "主波连续5期、双波连续3期未命中"
            : mainTrigger ? "主波连续5期未命中"
            : dualTrigger ? "双波连续3期未命中"
            : mainHit ? "主波命中反馈" : dualHit ? "防波命中反馈" : "双波未命中反馈";

        ColorLearningWeights old = state.Weights;
        if (!mainHit)
            state.Weights = Adjust(old, feedback, mainTrigger || dualTrigger ? 0.05 : 0.02);

        state.LearnedSamples++;
        state.LastTrainingIssue = feedback.Issue;
        state.LearnedIssues.Add(feedback.Issue);
        state.RecentMainHits.Add(mainHit);
        state.RecentDualHits.Add(dualHit);
        state.RecentFeedback.Add(new ColorFeedbackMemoryItem
        {
            Issue = feedback.Issue,
            ActualColor = feedback.ActualColor,
            MainColor = feedback.MainColor,
            DefenseColor = feedback.DefenseColor,
            MainHit = mainHit,
            DualHit = dualHit,
            FeatureSignals = feedback.FeatureSignals.ToDictionary(pair => pair.Key,
                pair => pair.Value.ToDictionary(value => value.Key, value => value.Value,
                    StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
        });
        if (state.Weights != old)
            state.RecentAdjustments.Add(new ColorLearningAdjustment
            {
                Issue = feedback.Issue,
                OldWeights = old,
                NewWeights = state.Weights,
                Reason = reason
            });
        Validate(state);
        return new(true, mainTrigger || dualTrigger, mainHit, dualHit, state.Weights, reason);
    }

    public static ColorLearningState Validate(ColorLearningState state)
    {
        state.Weights = Normalize(state.Weights);
        state.LearnedSamples = Math.Max(0, state.LearnedSamples);
        state.ConsecutiveMainMisses = Math.Max(0, state.ConsecutiveMainMisses);
        state.ConsecutiveDualMisses = Math.Max(0, state.ConsecutiveDualMisses);
        Trim(state.RecentMainHits, 500);
        Trim(state.RecentDualHits, 500);
        Trim(state.LearnedIssues, 1000);
        Trim(state.RecentFeedback, 500);
        Trim(state.RecentAdjustments, 100);
        return state;
    }

    public static ColorLearningWeights Normalize(ColorLearningWeights value)
    {
        double frequency = double.IsFinite(value.Frequency) ? Math.Clamp(value.Frequency, 0.05, 0.85) : 0.55;
        double transition = double.IsFinite(value.Transition) ? Math.Clamp(value.Transition, 0.05, 0.85) : 0.25;
        double omission = double.IsFinite(value.Omission) ? Math.Clamp(value.Omission, 0.05, 0.85) : 0.20;
        double total = frequency + transition + omission;
        if (total <= 0) return ColorLearningWeights.Default;
        return new(frequency / total, transition / total, omission / total);
    }

    private static ColorLearningWeights Adjust(ColorLearningWeights current,
        ColorPredictionFeedback feedback, double maximumStep)
    {
        if (!feedback.FeatureSignals.TryGetValue(feedback.ActualColor, out var actual) ||
            !feedback.FeatureSignals.TryGetValue(feedback.MainColor, out var predicted)) return current;
        var advantages = FeatureNames.ToDictionary(name => name,
            name => actual.GetValueOrDefault(name) - predicted.GetValueOrDefault(name),
            StringComparer.OrdinalIgnoreCase);
        string best = advantages.OrderByDescending(pair => pair.Value).First().Key;
        string worst = advantages.OrderBy(pair => pair.Value).First().Key;
        if (advantages[best] <= 0 || advantages[worst] >= 0 || best == worst) return current;

        double step = Math.Min(0.05, Math.Max(0, maximumStep));
        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["frequency"] = current.Frequency,
            ["transition"] = current.Transition,
            ["omission"] = current.Omission
        };
        double transferable = Math.Min(step, Math.Min(0.85 - values[best], values[worst] - 0.05));
        if (transferable <= 0) return current;
        values[best] += transferable;
        values[worst] -= transferable;
        return new(values["frequency"], values["transition"], values["omission"]);
    }

    private static void UpdateCounters(ColorLearningState state, bool mainHit, bool dualHit)
    {
        if (mainHit) { state.ConsecutiveMainMisses = 0; state.MainThresholdFired = false; }
        else state.ConsecutiveMainMisses++;
        if (dualHit) { state.ConsecutiveDualMisses = 0; state.DualThresholdFired = false; }
        else state.ConsecutiveDualMisses++;
    }

    private static bool IsColor(string color) => color is "红" or "蓝" or "绿";
    private static void Trim<T>(List<T> values, int maximum)
    {
        if (values.Count > maximum) values.RemoveRange(0, values.Count - maximum);
    }
}
