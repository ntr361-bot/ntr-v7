using System.Collections.Immutable;

namespace 六合分析软件.MacroReasoning;

/// <summary>P18: ordinal ten-result maturity; target+10 arithmetic is not a draw calendar.</summary>
public sealed class MacroReflectionEngine : IMacroReflectionEngine
{
    public const string Version = "p18-reflection-ordinal10-v1";
    public MacroReflection Reflect(MacroReasoningAudit audit, ImmutableArray<ClosedResult> availableResults,
        ReasoningMemorySnapshot memory, DateTimeOffset asOf)
    {
        if (!MacroReasoningAuditStore.Verify(audit) || asOf <= audit.CreatedAt || memory.AvailableAt > asOf)
            throw new InvalidDataException("Reflection requires a frozen prior prediction and available memory");
        if (availableResults.GroupBy(x => x.Issue).Any(g => g.Count() != 1)
            || availableResults.Any(x => x.AvailableAt > asOf || x.OpenedAt > x.AvailableAt))
            throw new InvalidDataException("Unavailable or duplicate reflection results");
        var actual = availableResults.SingleOrDefault(x => x.Issue == audit.Issue) ?? throw new InvalidDataException("Target result not revealed");
        if (actual.OpenedAt <= audit.CreatedAt) throw new InvalidDataException("Prediction was not frozen before reveal");
        var horizon = availableResults.Where(x => x.Issue >= audit.Issue).OrderBy(x => x.Issue).Take(10).ToArray();
        int before = Rank(audit.Counterfactual.NoActionRanking, actual.ActualZodiac);
        int after = Rank(audit.Counterfactual.AppliedRanking, actual.ActualZodiac);
        int proposed = Rank(audit.Counterfactual.ProposedRanking, actual.ActualZodiac);
        bool rescue = before > 6 && after <= 6, harm = before <= 6 && after > 6;
        OutcomeType outcome = rescue ? OutcomeType.Rescue : harm ? OutcomeType.Harm : before == after ? OutcomeType.NoImpact
            : after <= 6 ? OutcomeType.NeutralPositive : OutcomeType.NeutralNegative;
        bool blocked = audit.Decision.CriticResult.Result != CriticVerdict.Pass
            && !MacroReasoningAuditStore.Same(audit.Decision.ExpertWeightsProposed, audit.Decision.ExpertWeightsApplied);
        var assessments = audit.Hypotheses.Candidates.Select(h => Assess(h, audit, horizon)).ToImmutableArray();
        // Expert rankings are persisted separately by P6; audit does not duplicate them.
        // Do not manufacture group outcomes if the caller has not supplied the frozen rankings.
        return new MacroReflection(MacroRecordCodec.Hash(new { audit.AuditHash, Stage = horizon.Length >= 10 ? "mature" : "outcome" }),
            audit.AuditHash, audit.Issue, asOf, actual, outcome, assessments,
            after < before ? Assessment.Correct : after > before ? Assessment.Incorrect : Assessment.Indeterminate,
            rescue, harm, blocked && before <= 6 && proposed > 6, blocked && before > 6 && proposed <= 6,
            Version, memory.Version, memory.Version + 1, ImmutableArray<CommonFailureEvent>.Empty, ImmutableArray<CommonSuccessEvent>.Empty);
    }
    public MacroReflection WithConsensus(MacroReflection reflection, MacroReasoningAudit audit, ImmutableArray<ExpertSnapshot> snapshots)
    {
        var current = snapshots.Where(x => x.TargetIssue == audit.Issue && audit.ExpertPool.IncludedExpertIds.Contains(x.ExpertId)).ToArray();
        if (current.Length != audit.ExpertPool.IncludedExpertIds.Length || current.Select(x => x.ExpertId).Distinct().Count() != current.Length
            || current.Any(x => !ExpertSnapshotIntegrity.Verify(x) || x.PayloadHash != audit.ExpertPool.IncludedSnapshotHashes[x.ExpertId]))
            throw new InvalidDataException("Consensus requires exact frozen pool snapshots");
        var ranks = current.ToImmutableDictionary(x => x.ExpertId, x => Rank(x.Ranking, reflection.Actual.ActualZodiac));
        var failed = ranks.Where(x => x.Value > 6).Select(x => x.Key).Order().ToImmutableArray();
        var successful = ranks.Where(x => x.Value <= 6).Select(x => x.Key).Order().ToImmutableArray();
        var badGroups = audit.Dependencies.DependencyGroups.Where(g => g.Value.Length > 0 && g.Value.All(id => ranks[id] > 6)).Select(g => g.Key).Order().ToImmutableArray();
        var goodGroups = audit.Dependencies.DependencyGroups.Where(g => g.Value.Length > 0 && g.Value.All(id => ranks[id] <= 6)).Select(g => g.Key).Order().ToImmutableArray();
        return reflection with {
            CommonFailures = badGroups.IsEmpty ? [] : [new(audit.Issue, reflection.Actual.ActualZodiac, ranks, failed, successful, badGroups, Version, reflection.CreatedAt)],
            CommonSuccesses = goodGroups.IsEmpty ? [] : [new(audit.Issue, reflection.Actual.ActualZodiac, ranks, successful, goodGroups, Version, reflection.CreatedAt)] };
    }
    private static HypothesisAssessment Assess(Hypothesis h, MacroReasoningAudit a, ClosedResult[] horizon)
    {
        var range = new IssueRange(horizon[0].Issue, horizon[^1].Issue, horizon.Length);
        if (horizon.Length < 10) return new(h.HypothesisId, Assessment.Pending, Version, range, "Await ten revealed draws, including target; numeric issue gaps do not count");
        // Frozen prediction-time reference, subsequently measured on disjoint future observations.
        double? baseline = null, measured = null; double threshold = 0;
        if (h.Type == HypothesisType.HotPersistenceIncreasing)
        {
            baseline = a.Observation.Facts.SingleOrDefault(x => x.SignalName == "ZodiacConcentration" && x.Window == 100)?.Value;
            // Unbiased pair concentration avoids the 10-vs-100 plug-in HHI sample-size bias.
            if (baseline.HasValue) {
                int n = a.Observation.Facts.Single(x => x.SignalName == "ZodiacConcentration" && x.Window == 100).EffectiveSamples;
                baseline = n > 1 ? (n * baseline.Value - 1) / (n - 1) : null;
            }
            measured = horizon.GroupBy(x => x.ActualZodiac).Sum(g => g.Count() * (g.Count() - 1)) / 90d; threshold = .02;
        }
        else if (h.Type == HypothesisType.RepeatRegimeIncreasing)
        {
            // Only immediate-repeat support is currently falsifiable from this contract.
            bool supported = a.SupportingEvidence.Items.Any(e => e.HypothesisId == h.HypothesisId && e.SignalName == "ImmediateRepeatRate");
            if (supported) {
                baseline = a.Observation.Facts.SingleOrDefault(x => x.SignalName == "ImmediateRepeatRate" && x.Window == 100)?.Value;
                measured = Enumerable.Range(1, 9).Count(i => horizon[i].ActualZodiac == horizon[i - 1].ActualZodiac) / 9d; threshold = .05;
            }
        }
        if (!baseline.HasValue || !measured.HasValue)
            return new(h.HypothesisId, Assessment.Indeterminate, Version, range, "No validated falsification measurement for this hypothesis/input; excluded from accuracy denominator");
        bool confirmed = measured.Value - baseline.Value >= threshold;
        return new(h.HypothesisId, confirmed ? Assessment.Correct : Assessment.Incorrect, Version, range,
            FormattableString.Invariant($"Disjoint horizon metric={measured:F6}; frozen baseline={baseline:F6}; threshold={threshold:F6}; operational confirmation, not causal proof"));
    }
    internal static int Rank(ImmutableArray<string> ranking, string zodiac)
    {
        ExpertSnapshotIntegrity.ValidateRanking(ranking); int rank = ranking.IndexOf(zodiac) + 1;
        return rank > 0 ? rank : throw new InvalidDataException("Invalid actual zodiac");
    }
}
