using System.Collections.Immutable;

namespace 六合分析软件.MacroReasoning;

/// <summary>P19: append-only, as-of memory. Pending/indeterminate are never labelled incorrect.</summary>
public sealed class MacroReasoningMemory(MacroReasoningAuditStore store) : IMacroReasoningMemory
{
    public static ReasoningMemorySnapshot Empty { get; } = Seal(new(0, 0, DateTimeOffset.MinValue,
        ImmutableDictionary<string,ReliabilityStats>.Empty, ImmutableDictionary<string,ReliabilityStats>.Empty,
        ImmutableDictionary<string,ReliabilityStats>.Empty, ImmutableDictionary<string,ReliabilityStats>.Empty,
        ImmutableDictionary<string,ReliabilityStats>.Empty, ImmutableArray<ConfidenceCalibrationStats>.Empty, ""));
    public ReasoningMemorySnapshot ReadAsOf(long targetIssue, DateTimeOffset asOf)
    {
        var memory = store.ReadMemory(targetIssue, asOf);
        if (memory.MemoryHash != Seal(memory).MemoryHash) throw new InvalidDataException("Memory hash mismatch");
        return memory;
    }
    public void Append(MacroReflection reflection, long expectedVersion) => store.CommitReflection(reflection, expectedVersion);
    public WeaknessSummary Weaknesses(long targetIssue, DateTimeOffset asOf) => new(ReadAsOf(targetIssue, asOf).Hypotheses);
    private static ReasoningMemorySnapshot Seal(ReasoningMemorySnapshot value) => value with { MemoryHash = MacroRecordCodec.Hash(value with { MemoryHash = "" }) };
    internal static ReasoningMemorySnapshot Reduce(ReasoningMemorySnapshot before, MacroReasoningAudit audit,
        MacroReflection reflection, Func<string,bool> claim)
    {
        var hypotheses = before.Hypotheses; var evidence = before.Evidence; var counter = before.CounterEvidence;
        var actions = before.Actions; var environments = before.ExpertEnvironments; var calibration = before.Calibration;
        foreach (var h in audit.Hypotheses.Candidates)
        {
            if (claim(audit.AuditHash + "|trigger|" + h.HypothesisId)) hypotheses = Increment(hypotheses, h.Type.ToString(), true, null, false, false);
            var assessment = reflection.HypothesisAssessments.SingleOrDefault(x => x.HypothesisId == h.HypothesisId)
                ?? throw new InvalidDataException("Reflection missing hypothesis assessment");
            if (assessment.HypothesisCorrect is not (Assessment.Correct or Assessment.Incorrect)) continue;
            if (!claim(audit.AuditHash + "|mature|" + h.HypothesisId)) continue;
            bool correct = assessment.HypothesisCorrect == Assessment.Correct;
            hypotheses = Increment(hypotheses, h.Type.ToString(), false, correct, reflection.Rescue, reflection.Harm);
            foreach (var e in audit.SupportingEvidence.Items.Where(e => e.HypothesisId == h.HypothesisId).DistinctBy(e => (e.SignalName, e.DefinitionVersion)))
                evidence = Increment(evidence, e.DefinitionVersion + ":" + e.SignalName, true, correct, false, false);
            foreach (var e in audit.CounterEvidence.Items.Where(e => e.HypothesisId == h.HypothesisId).DistinctBy(e => (e.SignalName, e.DefinitionVersion)))
                counter = Increment(counter, e.DefinitionVersion + ":" + e.SignalName, true, !correct, false, false);
            // Calibration measures selected, decidable hypotheses only, not all seven correlated candidates.
            if (audit.Decision.SelectedHypothesisIds.Contains(h.HypothesisId)) calibration = Calibrate(calibration, audit.Decision.Confidence.Value, correct);
        }
        if (claim(audit.AuditHash + "|action"))
        {
            bool? correct = reflection.DecisionCorrect == Assessment.Correct ? true : reflection.DecisionCorrect == Assessment.Incorrect ? false : null;
            actions = Increment(actions, audit.ProposedAction.RuleVersion + ":" + audit.Decision.DecisionType, true, correct, reflection.Rescue, reflection.Harm);
            // Context and revision are part of the key; no mixing revisions under a display name.
            var rankEvents = reflection.CommonFailures.Select(x => x.ActualRanks).Concat(reflection.CommonSuccesses.Select(x => x.ActualRanks)).FirstOrDefault();
            if (rankEvents is not null) foreach (var pair in rankEvents)
                environments = Increment(environments, audit.ExpertPool.ExpertRevisionIds[pair.Key] + ":global", true, pair.Value <= 6, false, false);
        }
        long consumed = Math.Max(reflection.Issue, reflection.HypothesisAssessments.Select(x => x.EvaluatedRange.Last).DefaultIfEmpty(0).Max());
        return Seal(new(before.Version + 1, Math.Max(before.LastTrainingIssue, consumed), reflection.CreatedAt,
            hypotheses, evidence, counter, actions, environments, calibration, ""));
    }
    private static ImmutableDictionary<string,ReliabilityStats> Increment(ImmutableDictionary<string,ReliabilityStats> map,
        string key, bool triggered, bool? correct, bool rescue, bool harm)
    {
        var old = map.GetValueOrDefault(key) ?? new ReliabilityStats(0,0,0,0,0,null,"p19-beta1-v1-no-decay");
        int n = old.Matured + (correct.HasValue ? 1 : 0), successes = old.Correct + (correct == true ? 1 : 0);
        return map.SetItem(key, old with { Triggered = old.Triggered + (triggered ? 1 : 0), Matured = n, Correct = successes,
            Rescue = old.Rescue + (correct.HasValue && rescue ? 1 : 0), Harm = old.Harm + (correct.HasValue && harm ? 1 : 0),
            EstimatedReliability = n == 0 ? null : (successes + 1d) / (n + 2d) });
    }
    private static ImmutableArray<ConfidenceCalibrationStats> Calibrate(ImmutableArray<ConfidenceCalibrationStats> source, double confidence, bool correct)
    {
        var bins = source.IsEmpty ? Enumerable.Range(0,5).Select(i => new ConfidenceCalibrationStats(i / 5d,(i+1)/5d,0,null,null,null)).ToArray() : source.ToArray();
        int index = Math.Min(4,(int)(confidence*5)); var b = bins[index]; int n = b.MaturedSamples+1;
        double mean = ((b.MeanConfidence ?? 0)*b.MaturedSamples+confidence)/n;
        double accuracy = ((b.ReasoningCorrectRate ?? 0)*b.MaturedSamples+(correct?1:0))/n;
        bins[index] = new(b.LowerInclusive,b.Upper,n,mean,accuracy,Math.Abs(mean-accuracy));
        return bins.ToImmutableArray();
    }
}
