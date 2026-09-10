using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace 六合分析软件.MacroReasoning;

/// <summary>Canonical hashing for persisted research records; dictionary order is irrelevant.</summary>
public static class MacroRecordCodec
{
    public static string Json<T>(T value) => JsonSerializer.Serialize(value);
    public static string Hash<T>(T value)
    {
        using var document = JsonDocument.Parse(Json(value));
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes)) Write(writer, document.RootElement);
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }
    private static void Write(Utf8JsonWriter w, JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            w.WriteStartObject();
            foreach (var p in e.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal)) { w.WritePropertyName(p.Name); Write(w, p.Value); }
            w.WriteEndObject();
        }
        else if (e.ValueKind == JsonValueKind.Array) { w.WriteStartArray(); foreach (var x in e.EnumerateArray()) Write(w, x); w.WriteEndArray(); }
        else e.WriteTo(w);
    }
}

/// <summary>P16: all four rankings use the same immutable snapshots. COUNTER is not activated here.</summary>
public sealed class MacroCounterfactualEngine : IMacroCounterfactualEngine
{
    public CounterfactualSnapshot Evaluate(ImmutableArray<ExpertSnapshot> experts, MacroDecision decision,
        ImmutableDictionary<string, double> fixedControlWeights)
    {
        if (experts.IsEmpty || experts.Any(x => x.TargetIssue != decision.Issue || !ExpertSnapshotIntegrity.Verify(x)))
            throw new InvalidDataException("P16 snapshot identity/hash mismatch");
        var baseline = Rank(experts, fixedControlWeights);
        var noAction = Rank(experts, decision.ExpertWeightsBefore);
        var proposed = Rank(experts, decision.ExpertWeightsProposed);
        var applied = Rank(experts, decision.ExpertWeightsApplied);
        var changed = noAction.Take(6).Except(applied.Take(6)).Concat(applied.Take(6).Except(noAction.Take(6))).Order().ToImmutableArray();
        return new(decision.Issue, fixedControlWeights, decision.ExpertWeightsApplied,
            baseline, noAction, proposed, applied, changed.Length > 0, changed);
    }

    public static ImmutableArray<ExpertDecision> FollowDecisions(ImmutableArray<ExpertSnapshot> experts,
        ImmutableDictionary<string, double> weights)
    {
        return experts.Select(s => new ExpertDecision(s.ExpertId, s.ExpertRevisionId,
            weights[s.ExpertId] == 0 ? ExpertActionMode.IGNORE : ExpertActionMode.FOLLOW,
            null, null, null, null, null, null, null, weights[s.ExpertId], weights[s.ExpertId], weights[s.ExpertId], 0,
            ImmutableArray.Create("p16-follow-only-view"), ImmutableArray.Create(s.PayloadHash),
            new ExpertModeSelection(s.TargetIssue, s.HistoryCutoffIssue, s.AvailableAt, s.AvailableAt,
                "p16-follow-only-v1", s.PayloadHash, null, null))).ToImmutableArray();
    }

    public static ImmutableArray<string> Rank(ImmutableArray<ExpertSnapshot> experts, ImmutableDictionary<string, double> weights)
    {
        if (!experts.Select(x => x.ExpertId).Order().SequenceEqual(weights.Keys.Order()))
            throw new InvalidDataException("P16 expert/weight support mismatch");
        // Reconstruction timestamps remain real timestamps. No backdated Live views are manufactured.
        return new MacroGatingModel().Rank(experts, weights, FollowDecisions(experts, weights), ImmutableArray<ExpertCounterView>.Empty);
    }
}
