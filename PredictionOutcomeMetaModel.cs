using System;
using System.Collections.Generic;
using System.Linq;

namespace PredictionResearch;

/// <summary>
/// Estimates whether the base prediction is likely to hit based on prior,
/// already verified outcomes. It does not change the zodiac ranking.
/// </summary>
public sealed class PredictionOutcomeMetaModel
{
    public sealed record OutcomeSample(bool Top3Hit, bool Top6Hit);

    public sealed record Estimate(
        double Top3Probability,
        double Top6Probability,
        int PriorSamples,
        string Context);

    public sealed record Evaluation(
        int Samples,
        double BaselineTop3Probability,
        double BaselineTop6Probability,
        double MetaTop3Brier,
        double BaselineTop3Brier,
        double MetaTop6Brier,
        double BaselineTop6Brier,
        double MetaTop3LogLoss,
        double BaselineTop3LogLoss,
        double MetaTop6LogLoss,
        double BaselineTop6LogLoss)
    {
        public double Top3BrierImprovement => BaselineTop3Brier - MetaTop3Brier;
        public double Top6BrierImprovement => BaselineTop6Brier - MetaTop6Brier;
    }

    private const int MaxStreakBucket = 5;
    private readonly List<OutcomeSample> _samples = new();
    private readonly Dictionary<string, (int Hits, int Total)> _top3Contexts = new();
    private readonly Dictionary<string, (int Hits, int Total)> _top6Contexts = new();

    public int SampleCount => _samples.Count;

    /// <summary>
    /// Adds one verified result. Call this only after the result is known.
    /// </summary>
    public void Add(OutcomeSample sample)
    {
        AddContext(_top3Contexts, GetContext(_samples, true), sample.Top3Hit);
        AddContext(_top6Contexts, GetContext(_samples, false), sample.Top6Hit);
        _samples.Add(sample);
    }

    public Estimate Predict()
    {
        string top3Context = GetContext(_samples, true);
        string top6Context = GetContext(_samples, false);
        double priorTop3 = _samples.Count == 0 ? 0.25 : _samples.Average(s => s.Top3Hit ? 1.0 : 0.0);
        double priorTop6 = _samples.Count == 0 ? 0.50 : _samples.Average(s => s.Top6Hit ? 1.0 : 0.0);

        double top3 = _top3Contexts.TryGetValue(top3Context, out var top3Stats)
            ? SmoothedRate(top3Stats.Hits, top3Stats.Total, priorTop3)
            : priorTop3;
        double top6 = _top6Contexts.TryGetValue(top6Context, out var top6Stats)
            ? SmoothedRate(top6Stats.Hits, top6Stats.Total, priorTop6)
            : priorTop6;

        return new Estimate(
            top3,
            top6,
            _samples.Count,
            $"Top3={top3Context};Top6={top6Context}");
    }

    /// <summary>
    /// Walk-forward evaluation. The sample being evaluated is added only after
    /// its probability has been calculated.
    /// </summary>
    public static Evaluation Evaluate(IEnumerable<OutcomeSample> source)
    {
        var samples = source.ToList();
        if (samples.Count == 0)
            return new Evaluation(0, 0.25, 0.50, 0, 0, 0, 0, 0, 0, 0, 0);

        var model = new PredictionOutcomeMetaModel();
        double top3Prior = 0.25;
        double top6Prior = 0.50;
        double metaTop3Brier = 0, baselineTop3Brier = 0;
        double metaTop6Brier = 0, baselineTop6Brier = 0;
        double metaTop3LogLoss = 0, baselineTop3LogLoss = 0;
        double metaTop6LogLoss = 0, baselineTop6LogLoss = 0;

        foreach (var sample in samples)
        {
            Estimate estimate = model.Predict();
            metaTop3Brier += Brier(estimate.Top3Probability, sample.Top3Hit);
            baselineTop3Brier += Brier(top3Prior, sample.Top3Hit);
            metaTop6Brier += Brier(estimate.Top6Probability, sample.Top6Hit);
            baselineTop6Brier += Brier(top6Prior, sample.Top6Hit);
            metaTop3LogLoss += LogLoss(estimate.Top3Probability, sample.Top3Hit);
            baselineTop3LogLoss += LogLoss(top3Prior, sample.Top3Hit);
            metaTop6LogLoss += LogLoss(estimate.Top6Probability, sample.Top6Hit);
            baselineTop6LogLoss += LogLoss(top6Prior, sample.Top6Hit);
            model.Add(sample);
            top3Prior = model._samples.Average(s => s.Top3Hit ? 1.0 : 0.0);
            top6Prior = model._samples.Average(s => s.Top6Hit ? 1.0 : 0.0);
        }

        int count = samples.Count;
        return new Evaluation(
            count,
            top3Prior,
            top6Prior,
            metaTop3Brier / count,
            baselineTop3Brier / count,
            metaTop6Brier / count,
            baselineTop6Brier / count,
            metaTop3LogLoss / count,
            baselineTop3LogLoss / count,
            metaTop6LogLoss / count,
            baselineTop6LogLoss / count);
    }

    private static void AddContext(
        Dictionary<string, (int Hits, int Total)> contexts,
        string context,
        bool hit)
    {
        contexts.TryGetValue(context, out var stats);
        contexts[context] = (stats.Hits + (hit ? 1 : 0), stats.Total + 1);
    }

    private static string GetContext(IReadOnlyList<OutcomeSample> samples, bool top3)
    {
        if (samples.Count == 0) return "none";

        bool lastHit = top3 ? samples[^1].Top3Hit : samples[^1].Top6Hit;
        int streak = 0;
        for (int i = samples.Count - 1;
             i >= 0 && (top3 ? samples[i].Top3Hit : samples[i].Top6Hit) == lastHit;
             i--)
            streak++;

        int window = Math.Min(10, samples.Count);
        double recentRate = samples
            .Skip(samples.Count - window)
            .Average(s => (top3 ? s.Top3Hit : s.Top6Hit) ? 1.0 : 0.0);
        string rateBucket = recentRate < 0.30 ? "low" : recentRate > 0.50 ? "high" : "mid";
        return $"{(top3 ? "top3" : "top6")}:{(lastHit ? "hit" : "miss")}:{Math.Min(streak, MaxStreakBucket)}:{rateBucket}";
    }

    private static double SmoothedRate(int hits, int total, double prior)
    {
        // One prior-equivalent observation prevents tiny streak buckets from
        // producing an unjustified 0% or 100% probability.
        return (hits + prior) / (total + 1.0);
    }

    private static double Brier(double probability, bool hit)
    {
        double target = hit ? 1 : 0;
        return Math.Pow(probability - target, 2);
    }

    private static double LogLoss(double probability, bool hit)
    {
        probability = Math.Clamp(probability, 0.001, 0.999);
        return hit ? -Math.Log(probability) : -Math.Log(1 - probability);
    }
}
