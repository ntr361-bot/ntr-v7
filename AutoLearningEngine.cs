namespace 六合分析软件;

public sealed record PredictionFeedback(
    string Issue,
    int ActualRank,
    IReadOnlyDictionary<string, int> BaseModelRanks,
    IReadOnlyDictionary<string, double> FeatureSignals);

public sealed record LearningOutcome(
    bool Updated,
    bool FailureAnalysisTriggered,
    int ActualRank,
    ModelWeights Weights,
    string Reason);

public sealed class AutoLearningEngine
{
    private readonly WeightOptimizer optimizer;

    public AutoLearningEngine(WeightOptimizer? optimizer = null) => this.optimizer = optimizer ?? new WeightOptimizer();

    public LearningOutcome ApplyFeedback(ModelMemoryState memory, PredictionFeedback feedback)
    {
        if (feedback.ActualRank is < 1 or > 12)
            return new LearningOutcome(false, false, feedback.ActualRank, memory.Weights, "实际排名无效");
        if (memory.RecentFeedback.Any(item => item.Issue == feedback.Issue))
            return new LearningOutcome(false, false, feedback.ActualRank, memory.Weights, "该期已经学习");

        bool top3Hit = feedback.ActualRank <= 3;
        bool top6Hit = feedback.ActualRank <= 6;
        UpdateMissCounters(memory, top3Hit, top6Hit);
        memory.RecentTop3.Add(top3Hit);
        memory.RecentTop6.Add(top6Hit);
        memory.RecentReciprocalRanks.Add(1d/feedback.ActualRank);
        memory.RecentFeedback.Add(new FeedbackMemoryItem
        {
            Issue = feedback.Issue,
            ActualRank = feedback.ActualRank,
            BaseModelRanks = new Dictionary<string, int>(feedback.BaseModelRanks, StringComparer.OrdinalIgnoreCase),
            FeatureSignals = new Dictionary<string, double>(feedback.FeatureSignals, StringComparer.OrdinalIgnoreCase)
        });
        memory.LearnedSamples++;
        memory.LastTrainingIssue = feedback.Issue;

        bool top3Trigger = memory.ConsecutiveTop3Misses == 5 && !memory.Top3ThresholdFired;
        bool top6Trigger = memory.ConsecutiveTop6Misses == 3 && !memory.Top6ThresholdFired;
        bool triggered = top3Trigger || top6Trigger;
        string reason = "已记录开奖反馈";
        if (triggered)
        {
            if (top3Trigger) memory.Top3ThresholdFired = true;
            if (top6Trigger) memory.Top6ThresholdFired = true;
            int window = top6Trigger ? 3 : 5;
            reason = top6Trigger ? "TOP6连续3期未命中" : "TOP3连续5期未命中";
            ApplyFailureAnalysis(memory, window, reason, feedback.Issue);
        }

        ModelMemory.Validate(memory);
        return new LearningOutcome(true, triggered, feedback.ActualRank, memory.Weights, reason);
    }

    private static void UpdateMissCounters(ModelMemoryState memory, bool top3Hit, bool top6Hit)
    {
        if (top3Hit)
        {
            memory.ConsecutiveTop3Misses = 0;
            memory.Top3ThresholdFired = false;
        }
        else memory.ConsecutiveTop3Misses++;

        if (top6Hit)
        {
            memory.ConsecutiveTop6Misses = 0;
            memory.Top6ThresholdFired = false;
        }
        else memory.ConsecutiveTop6Misses++;
    }

    private void ApplyFailureAnalysis(ModelMemoryState memory, int windowSize, string reason, string issue)
    {
        FeedbackMemoryItem[] window = memory.RecentFeedback.TakeLast(windowSize).ToArray();
        string[] sources = { "AI", "ML", "State", "V7" };
        var averageRanks = sources.ToDictionary(source => source,
            source => window.Select(item => item.BaseModelRanks.GetValueOrDefault(source, 12)).Average(),
            StringComparer.OrdinalIgnoreCase);
        double mean = averageRanks.Values.Average();
        var utility = averageRanks.ToDictionary(pair => pair.Key, pair => mean-pair.Value,
            StringComparer.OrdinalIgnoreCase);
        ModelWeights old = memory.Weights;
        memory.Weights = optimizer.Adjust(old, new ModelFeedback(utility, reason));

        var features = window.SelectMany(item => item.FeatureSignals)
            .GroupBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Average(pair => pair.Value),
                StringComparer.OrdinalIgnoreCase);
        if (features.Count > 0)
        {
            string misleading = features.OrderBy(pair => pair.Value).First().Key;
            memory.MetaCoefficients[misleading] = Math.Clamp(
                memory.MetaCoefficients.GetValueOrDefault(misleading) - 0.05, -0.50, 0.50);
        }

        memory.RecentAdjustments.Add(new LearningAdjustmentRecord
        {
            Issue = issue,
            OldWeights = old,
            NewWeights = memory.Weights,
            FeatureContribution = features,
            Reason = reason
        });
    }
}
