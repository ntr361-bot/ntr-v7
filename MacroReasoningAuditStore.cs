using System.Collections.Immutable;
using System.Data.SQLite;
using System.Text.Json;

namespace 六合分析软件.MacroReasoning;

/// <summary>P17/P19 shared transaction boundary: prediction, reflection, receipt and versioned memory.</summary>
public sealed class MacroReasoningAuditStore : IMacroReasoningAuditStore
{
    private readonly string path;
    public string ExperimentId { get; }
    public MacroReasoningAuditStore(string databasePath, RunIdentity run)
    {
        path = Path.GetFullPath(databasePath);
        // Explicit isolated file only; never reuse a production history or expert database.
        if (Path.GetFileName(path).Equals("history.db", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Macro research cannot use history.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ExperimentId = run.ExperimentId;
        using var c = Open();
        Exec(c, null, "CREATE TABLE IF NOT EXISTS MacroRun(Id TEXT PRIMARY KEY,Json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS MacroPrediction(Run TEXT,Issue INTEGER,Hash TEXT UNIQUE,Json TEXT,PRIMARY KEY(Run,Issue)); CREATE TABLE IF NOT EXISTS MacroReflection(Id TEXT PRIMARY KEY,Run TEXT,AuditHash TEXT,Json TEXT); CREATE TABLE IF NOT EXISTS MacroMemory(Run TEXT,Version INTEGER,LastIssue INTEGER,AvailableAt TEXT,Json TEXT,PRIMARY KEY(Run,Version)); CREATE TABLE IF NOT EXISTS MacroReceipt(Run TEXT,EventKey TEXT,PRIMARY KEY(Run,EventKey));");
        foreach (string table in new[] { "MacroRun", "MacroPrediction", "MacroReflection", "MacroMemory", "MacroReceipt" })
            Exec(c, null, $"CREATE TRIGGER IF NOT EXISTS {table}_no_update BEFORE UPDATE ON {table} BEGIN SELECT RAISE(ABORT,'append only'); END; CREATE TRIGGER IF NOT EXISTS {table}_no_delete BEFORE DELETE ON {table} BEGIN SELECT RAISE(ABORT,'append only'); END;");
        string? existing = Scalar(c, null, "SELECT Json FROM MacroRun WHERE Id=@r", ("@r", ExperimentId));
        if (existing is not null && MacroRecordCodec.Hash(JsonSerializer.Deserialize<RunIdentity>(existing)) != MacroRecordCodec.Hash(run))
            throw new InvalidDataException("ExperimentId already bound to a different configuration");
        if (existing is null) Exec(c, null, "INSERT INTO MacroRun VALUES(@r,@j)", ("@r", ExperimentId), ("@j", MacroRecordCodec.Json(run)));
    }
    public static MacroReasoningAudit Seal(MacroReasoningAudit audit) => audit with { AuditHash = MacroRecordCodec.Hash(audit with { AuditHash = "" }) };
    public static bool Verify(MacroReasoningAudit audit) => audit.AuditHash == Seal(audit).AuditHash;
    public void AppendPrediction(MacroReasoningAudit audit)
    {
        Validate(audit);
        using var c = Open(); using var tx = c.BeginTransaction();
        string? old = Scalar(c, tx, "SELECT Hash FROM MacroPrediction WHERE Run=@r AND Issue=@i", ("@r", ExperimentId), ("@i", audit.Issue));
        if (old is not null) { if (old != audit.AuditHash) throw new InvalidDataException("Prediction conflict: immutable audit"); return; }
        Exec(c, tx, "INSERT INTO MacroPrediction VALUES(@r,@i,@h,@j)", ("@r", ExperimentId), ("@i", audit.Issue), ("@h", audit.AuditHash), ("@j", MacroRecordCodec.Json(audit)));
        tx.Commit();
    }
    public MacroReasoningAudit? Read(string experimentId, long issue)
    {
        if (experimentId != ExperimentId) throw new InvalidDataException("Cross-experiment read");
        using var c = Open();
        string? json = Scalar(c, null, "SELECT Json FROM MacroPrediction WHERE Run=@r AND Issue=@i", ("@r", ExperimentId), ("@i", issue));
        var audit = json is null ? null : JsonSerializer.Deserialize<MacroReasoningAudit>(json);
        if (audit is not null && !Verify(audit)) throw new InvalidDataException("Audit corruption");
        return audit;
    }
    public ImmutableArray<MacroReasoningAudit> Predictions()
    {
        using var c = Open(); using var q = new SQLiteCommand("SELECT Json FROM MacroPrediction WHERE Run=@r ORDER BY Issue", c);
        q.Parameters.AddWithValue("@r", ExperimentId); using var r = q.ExecuteReader(); var list = ImmutableArray.CreateBuilder<MacroReasoningAudit>();
        while (r.Read()) { var a = JsonSerializer.Deserialize<MacroReasoningAudit>(r.GetString(0))!; if (!Verify(a)) throw new InvalidDataException("Audit corruption"); list.Add(a); }
        return list.ToImmutable();
    }
    public void AppendReflection(MacroReflection reflection) => CommitReflection(reflection, reflection.BeforeMemoryVersion);
    internal void CommitReflection(MacroReflection reflection, long expectedVersion)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        string? old = Scalar(c, tx, "SELECT Json FROM MacroReflection WHERE Id=@id", ("@id", reflection.ReflectionId));
        if (old is not null) { if (MacroRecordCodec.Hash(JsonSerializer.Deserialize<MacroReflection>(old)) != MacroRecordCodec.Hash(reflection)) throw new InvalidDataException("Reflection conflict"); return; }
        string json = Scalar(c, tx, "SELECT Json FROM MacroPrediction WHERE Run=@r AND Hash=@h", ("@r", ExperimentId), ("@h", reflection.PredictionAuditHash)) ?? throw new InvalidDataException("Prediction must precede reflection");
        var audit = JsonSerializer.Deserialize<MacroReasoningAudit>(json)!;
        if (!Verify(audit) || reflection.Issue != audit.Issue || reflection.Actual.Issue != audit.Issue
            || reflection.CreatedAt < reflection.Actual.AvailableAt || reflection.Actual.OpenedAt > reflection.Actual.AvailableAt
            || reflection.Actual.OpenedAt <= audit.CreatedAt || reflection.CreatedAt <= audit.CreatedAt)
            throw new InvalidDataException("Reflection chronology invalid");
        var memory = Latest(c, tx);
        if (memory.Version != expectedVersion || reflection.BeforeMemoryVersion != expectedVersion || reflection.AfterMemoryVersion != expectedVersion + 1
            || reflection.CreatedAt < memory.AvailableAt) throw new InvalidDataException("Memory version/time conflict");
        bool Claim(string key)
        {
            if (Scalar(c, tx, "SELECT EventKey FROM MacroReceipt WHERE Run=@r AND EventKey=@k", ("@r", ExperimentId), ("@k", key)) is not null) return false;
            Exec(c, tx, "INSERT INTO MacroReceipt VALUES(@r,@k)", ("@r", ExperimentId), ("@k", key)); return true;
        }
        var next = MacroReasoningMemory.Reduce(memory, audit, reflection, Claim);
        Exec(c, tx, "INSERT INTO MacroReflection VALUES(@id,@r,@h,@j)", ("@id", reflection.ReflectionId), ("@r", ExperimentId), ("@h", reflection.PredictionAuditHash), ("@j", MacroRecordCodec.Json(reflection)));
        Exec(c, tx, "INSERT INTO MacroMemory VALUES(@r,@v,@i,@t,@j)", ("@r", ExperimentId), ("@v", next.Version), ("@i", next.LastTrainingIssue), ("@t", next.AvailableAt.ToUniversalTime().ToString("O")), ("@j", MacroRecordCodec.Json(next)));
        tx.Commit();
    }
    public ReasoningMemorySnapshot ReadMemory(long targetIssue, DateTimeOffset asOf)
    {
        using var c = Open();
        string? json = Scalar(c, null, "SELECT Json FROM MacroMemory WHERE Run=@r AND LastIssue<@i AND AvailableAt<=@t ORDER BY Version DESC LIMIT 1", ("@r", ExperimentId), ("@i", targetIssue), ("@t", asOf.ToUniversalTime().ToString("O")));
        return json is null ? MacroReasoningMemory.Empty : JsonSerializer.Deserialize<ReasoningMemorySnapshot>(json)!;
    }
    public ReasoningMemorySnapshot LatestMemory() { using var c = Open(); return Latest(c, null); }
    private ReasoningMemorySnapshot Latest(SQLiteConnection c, SQLiteTransaction? tx)
    {
        string? json = Scalar(c, tx, "SELECT Json FROM MacroMemory WHERE Run=@r ORDER BY Version DESC LIMIT 1", ("@r", ExperimentId));
        return json is null ? MacroReasoningMemory.Empty : JsonSerializer.Deserialize<ReasoningMemorySnapshot>(json)!;
    }
    private void Validate(MacroReasoningAudit a)
    {
        if (a.Run.ExperimentId != ExperimentId || !Verify(a) || a.Issue <= a.CutoffIssue || a.Observation.Issue != a.Issue
            || a.ExpertPool.TargetIssue != a.Issue || a.Decision.Issue != a.Issue || a.Counterfactual.Issue != a.Issue
            || a.CreatedAt != a.ExpertPool.AsOf || a.SupportingEvidence.Items.Concat(a.CounterEvidence.Items).Any(e => e.SourceIssueRange.Last >= a.Issue))
            throw new InvalidDataException($"Incomplete prediction audit: run={a.Run.ExperimentId == ExperimentId}, hash={Verify(a)}, cutoff={a.Issue > a.CutoffIssue}, observation={a.Observation.Issue == a.Issue}, pool={a.ExpertPool.TargetIssue == a.Issue}, decision={a.Decision.Issue == a.Issue}, counterfactual={a.Counterfactual.Issue == a.Issue}, time={a.CreatedAt == a.ExpertPool.AsOf}, evidencePast={a.SupportingEvidence.Items.Concat(a.CounterEvidence.Items).All(e => e.SourceIssueRange.Last < a.Issue)}");
        foreach (var ranking in new[] { a.Counterfactual.BaseRanking, a.Counterfactual.NoActionRanking, a.Counterfactual.ProposedRanking, a.Counterfactual.AppliedRanking }) ExpertSnapshotIntegrity.ValidateRanking(ranking);
        if (!Same(a.Decision.ExpertWeightsApplied, a.ExpertDecisions.ExpertWeightsApplied) || !Same(a.Decision.ExpertWeightsBefore, a.ExpertDecisions.ExpertWeightsBefore)
            || !Same(a.Decision.ExpertWeightsProposed, a.ExpertDecisions.ExpertWeightsProposed)
            || !Same(a.Decision.ExpertWeightsApplied, a.Counterfactual.AppliedWeights)) throw new InvalidDataException("Conflicting audit weights");
        if (a.Decision.DecisionType != DecisionType.Apply && (!Same(a.Decision.ExpertWeightsBefore, a.Decision.ExpertWeightsApplied)
            || !a.Counterfactual.NoActionRanking.SequenceEqual(a.Counterfactual.AppliedRanking))) throw new InvalidDataException("HOLD/REJECT changed prediction");
        var ids = a.ExpertPool.IncludedExpertIds.Order().ToArray();
        if (!ids.SequenceEqual(a.ExpertDecisions.Decisions.Select(d => d.ExpertId).Order()) || !ids.SequenceEqual(a.Decision.ExpertWeightsApplied.Keys.Order())) throw new InvalidDataException("Audit expert support mismatch");
        foreach (var d in a.ExpertDecisions.Decisions)
            if (d.ExpertRevisionId != a.ExpertPool.ExpertRevisionIds[d.ExpertId] || d.Mode != a.ExpertDecisions.ExpertModes[d.ExpertId]
                || d.AppliedWeight != a.Decision.ExpertWeightsApplied[d.ExpertId] || d.Mode == ExpertActionMode.COUNTER)
                throw new InvalidDataException("Audit revision/mode mismatch or COUNTER not admitted");
    }
    internal static bool Same(ImmutableDictionary<string,double> a, ImmutableDictionary<string,double> b) => a.Count == b.Count && a.All(x => b.TryGetValue(x.Key, out double y) && Math.Abs(y - x.Value) < 1e-10);
    private SQLiteConnection Open() { var c = new SQLiteConnection($"Data Source={path};Version=3;Default Timeout=30;"); c.Open(); return c; }
    private static void Exec(SQLiteConnection c, SQLiteTransaction? tx, string sql, params (string, object)[] args) { using var q = new SQLiteCommand(sql, c, tx); foreach (var a in args) q.Parameters.AddWithValue(a.Item1, a.Item2); q.ExecuteNonQuery(); }
    private static string? Scalar(SQLiteConnection c, SQLiteTransaction? tx, string sql, params (string, object)[] args) { using var q = new SQLiteCommand(sql, c, tx); foreach (var a in args) q.Parameters.AddWithValue(a.Item1, a.Item2); return q.ExecuteScalar()?.ToString(); }
}
