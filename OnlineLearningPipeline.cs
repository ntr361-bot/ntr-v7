using System.Data.SQLite;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace 六合分析软件;

public sealed record LearningInputSnapshot(string SnapshotId, string ModelKey, string Issue,
    string GeneratedAt, string HistoryCutoffIssue, bool Reconstructed, string ReconstructionReason,
    MetaPredictionInput Input, IReadOnlyList<string> Ranking, IReadOnlyList<string> BaselineRanking,
    long UsedMemoryVersion, string InputHash, string CodeVersion, string CommitSha);

public sealed record LearningAuditEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string ModelKey { get; init; } = ExperimentModels.AutoLearning;
    public string Issue { get; init; } = "";
    public string ActualZodiac { get; init; } = "";
    public string StartedAt { get; init; } = "";
    public string FinishedAt { get; init; } = "";
    public bool Success { get; init; }
    public string ErrorMessage { get; init; } = "";
    public long MemoryVersionBefore { get; init; }
    public long MemoryVersionAfter { get; init; }
    public int LearnedSamplesBefore { get; init; }
    public int LearnedSamplesAfter { get; init; }
    public string LastTrainingIssueBefore { get; init; } = "";
    public string LastTrainingIssueAfter { get; init; } = "";
    public string MemoryBeforeJson { get; init; } = "";
    public string MemoryAfterJson { get; init; } = "";
    public string WeightsBeforeJson { get; init; } = "";
    public string WeightsAfterJson { get; init; } = "";
    public string MetaCoefficientsBeforeJson { get; init; } = "";
    public string MetaCoefficientsAfterJson { get; init; } = "";
    public string FeatureReliabilityBeforeJson { get; init; } = "";
    public string FeatureReliabilityAfterJson { get; init; } = "";
    // Counterfactual inference on the SAME frozen input; never a replacement formal prediction.
    public string PredictionBeforeJson { get; init; } = "";
    public string PredictionAfterJson { get; init; } = "";
    public bool RankingChanged { get; init; }
    public bool Top3Changed { get; init; }
    public bool Top6Changed { get; init; }
    public bool DecisionImpact { get; init; }
    public string InputSnapshotId { get; init; } = "";
    public bool Reconstructed { get; init; }
    public string CodeVersion { get; init; } = OnlineLearningPipeline.CodeVersion;
    public string CommitSha { get; init; } = OnlineLearningPipeline.CommitSha;
}

public sealed record LearningHealth(string Status, string Reason, string LatestCompletedIssue,
    string LastTrainingIssue, long MemoryVersion, string? LastSuccessfulLearningAt);

public sealed record LearningEvidence(IReadOnlyList<LearningInputSnapshot> Snapshots,
    IReadOnlyList<LearningAuditEntry> Audits);

/// <summary>V6.5 legacy Auto lifecycle in the V7 application. No scoring formulas live here.</summary>
public static class OnlineLearningPipeline
{
    // P0–P4 is unfinished. Keep production on the existing lifecycle until acceptance passes.
    private static readonly AsyncLocal<bool> ValidationEnabled = new();
    public static bool IsEnabled => ValidationEnabled.Value;

    public static IDisposable EnableIsolatedValidation()
    {
        string directory = Path.GetFullPath(Path.GetDirectoryName(DatabaseHelper.DatabasePath)!);
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(directory).StartsWith("liuhe-learning-acceptance-", StringComparison.Ordinal))
            throw new InvalidOperationException("学习验收只允许专用临时数据库");
        bool previous = ValidationEnabled.Value;
        ValidationEnabled.Value = true;
        return new ValidationScope(previous);
    }

    private sealed class ValidationScope(bool previous) : IDisposable
    {
        public void Dispose() => ValidationEnabled.Value = previous;
    }
    public const string CodeVersion = "V7-Learning-P0P4-v1";
    public static string CommitSha => typeof(OnlineLearningPipeline).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(x => x.Key == "LearningCommitSha")?.Value ?? "unknown";
    private const string ModelKey = ExperimentModels.AutoLearning;
    private static string Now => DateTimeOffset.UtcNow.ToString("O");
    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json)
        ?? throw new InvalidDataException("学习状态 JSON 为空");
    private static long Issue(string value) => long.TryParse(value, out long n) && n > 0
        ? n : throw new InvalidDataException($"非法学习期号：{value}");

    public static void EnsureSchema(SQLiteConnection conn)
    {
        Execute(conn, """
            CREATE TABLE IF NOT EXISTS LearningInputSnapshot (
                ModelKey TEXT NOT NULL, Issue TEXT NOT NULL, SnapshotId TEXT NOT NULL UNIQUE,
                SnapshotJson TEXT NOT NULL, PRIMARY KEY(ModelKey, Issue));
            CREATE TABLE IF NOT EXISTS LearningAudit (
                Id TEXT PRIMARY KEY, ModelKey TEXT NOT NULL, Issue TEXT NOT NULL,
                Success INTEGER NOT NULL, MemoryVersionBefore INTEGER NOT NULL,
                MemoryVersionAfter INTEGER NOT NULL, InputSnapshotId TEXT NOT NULL,
                FinishedAt TEXT NOT NULL, AuditJson TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_learning_success ON LearningAudit(ModelKey, Issue) WHERE Success=1;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_learning_version ON LearningAudit(ModelKey, MemoryVersionAfter) WHERE Success=1;
            """);
    }

    private static SQLiteConnection Open()
    {
        var conn = DatabaseHelper.GetConnection();
        EnsureSchema(conn);
        return conn;
    }

    private static int Execute(SQLiteConnection c, string sql, params (string Key, object? Value)[] args)
    {
        using var command = new SQLiteCommand(sql, c);
        foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command.ExecuteNonQuery();
    }

    private static string? Scalar(SQLiteConnection c, string sql, params (string Key, object? Value)[] args)
    {
        using var command = new SQLiteCommand(sql, c);
        foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        object? valueResult = command.ExecuteScalar();
        return valueResult is null or DBNull ? null : Convert.ToString(valueResult);
    }

    private static ModelMemoryState Memory(SQLiteConnection conn) =>
        Read<ModelMemoryState>(Scalar(conn, "SELECT MemoryJson FROM ModelMemory WHERE MemoryKey=@k",
            ("@k", ExperimentModels.MemoryKey(ModelKey))) ?? "{}");

    public static ModelMemoryState ReadMemory()
    {
        using var conn = Open();
        return Memory(conn); // Deliberately do not silently reset corrupt memory.
    }

    public static AutoLearningSnapshot Stamp(AutoLearningSnapshot snapshot,
        IReadOnlyList<DatabaseHelper.HistoryRecord> history, ModelMemoryState memory)
    {
        long target = Issue(snapshot.Input.Issue);
        if (history.Count == 0 || history.Any(x => Issue(x.Period) >= target))
            throw new InvalidDataException("LearningPipelineBroken：预测输入含目标期/未来期或缺少历史");
        if (!string.IsNullOrEmpty(memory.LastTrainingIssue) && Issue(memory.LastTrainingIssue) >= target)
            throw new InvalidDataException("LearningPipelineBroken：Memory 已学习目标期/未来期");
        return snapshot with { Input = snapshot.Input with
        {
            HistoryCutoffIssue = history.Max(x => Issue(x.Period)).ToString(),
            UsedMemoryVersion = memory.MemoryVersion,
            MemoryLastTrainingIssue = memory.LastTrainingIssue,
            BaselineRanking = snapshot.BaselineRanking.ToArray()
        }};
    }

    public static void CaptureFormal(SQLiteConnection conn, string issue, string inputJson, string rankingJson)
    {
        var input = Read<MetaPredictionInput>(inputJson);
        if (input.UsedMemoryVersion is null) return; // Legacy import has no invented provenance.
        ModelMemoryState memory = Memory(conn);
        if (input.Issue != issue || input.UsedMemoryVersion != memory.MemoryVersion ||
            input.MemoryLastTrainingIssue != memory.LastTrainingIssue ||
            input.HistoryCutoffIssue != memory.LastTrainingIssue)
            throw new InvalidDataException("LearningPipelineBroken：新预测未使用学习后的 Memory/截止期");
        var ranking = Read<string[]>(rankingJson);
        var snapshot = MakeSnapshot(input, ranking, false, "", Now);
        ValidateSnapshot(snapshot);
        InsertSnapshot(conn, snapshot);
    }

    private static LearningInputSnapshot MakeSnapshot(MetaPredictionInput input, IReadOnlyList<string> ranking,
        bool reconstructed, string reason, string generatedAt)
    {
        return new LearningInputSnapshot(Guid.NewGuid().ToString("N"), ModelKey, input.Issue, generatedAt,
            input.HistoryCutoffIssue, reconstructed, reason, input, ranking,
            input.BaselineRanking ?? throw new InvalidDataException("缺少基础排序"),
            input.UsedMemoryVersion ?? throw new InvalidDataException("缺少预测 MemoryVersion"),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json(input)))), CodeVersion, CommitSha);
    }

    private static void ValidateSnapshot(LearningInputSnapshot snapshot)
    {
        long target = Issue(snapshot.Issue);
        if (snapshot.ModelKey != ModelKey || snapshot.Input.Issue != snapshot.Issue ||
            Issue(snapshot.HistoryCutoffIssue) >= target || snapshot.Input.HistoryCutoffIssue != snapshot.HistoryCutoffIssue ||
            (!string.IsNullOrEmpty(snapshot.Input.MemoryLastTrainingIssue) && Issue(snapshot.Input.MemoryLastTrainingIssue) >= target) ||
            snapshot.Input.UsedMemoryVersion != snapshot.UsedMemoryVersion ||
            snapshot.InputHash != Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json(snapshot.Input)))))
            throw new InvalidDataException("学习快照截止期/版本/哈希无效（未来数据风险）");
        string[] names = snapshot.Input.Zodiacs.Select(x => x.Zodiac).ToArray();
        if (names.Length != 12 || names.Distinct().Count() != 12 ||
            snapshot.Ranking.Count != 12 || snapshot.BaselineRanking.Count != 12 ||
            !snapshot.Ranking.Order().SequenceEqual(names.Order()) ||
            !snapshot.BaselineRanking.Order().SequenceEqual(names.Order()) ||
            snapshot.Input.Zodiacs.Any(z => new[] { "AI", "ML", "State", "V7" }
                .Any(k => !z.BaseScores.TryGetValue(k, out double v) || !double.IsFinite(v)) ||
                z.FeatureGroups.Values.Any(v => !double.IsFinite(v))))
            throw new InvalidDataException("学习快照缺少合法的完整12生肖输入");
    }

    private static void InsertSnapshot(SQLiteConnection conn, LearningInputSnapshot snapshot) => Execute(conn,
        "INSERT INTO LearningInputSnapshot(ModelKey,Issue,SnapshotId,SnapshotJson) VALUES(@m,@i,@s,@j)",
        ("@m", snapshot.ModelKey), ("@i", snapshot.Issue), ("@s", snapshot.SnapshotId), ("@j", Json(snapshot)));

    public static LearningInputSnapshot? GetSnapshot(string issue)
    {
        using var conn = Open();
        return Snapshot(conn, issue);
    }

    private static LearningInputSnapshot? Snapshot(SQLiteConnection conn, string issue)
    {
        string? json = Scalar(conn, "SELECT SnapshotJson FROM LearningInputSnapshot WHERE ModelKey=@m AND Issue=@i",
            ("@m", ModelKey), ("@i", issue));
        return json is null ? null : Read<LearningInputSnapshot>(json);
    }

    public static ModelMemoryState CatchUp(IReadOnlyList<DatabaseHelper.HistoryRecord> history, bool allowReconstruction = true)
    {
        var draws = OrderedHistory(history);
        if (draws.Length == 0) throw new InvalidDataException("LearningPipelineBroken：没有已开奖历史");
        ModelMemoryState memory = ReadMemory();
        if (!string.IsNullOrEmpty(memory.LastTrainingIssue) && Issue(memory.LastTrainingIssue) > Issue(draws[^1].Period))
            throw new InvalidDataException("LearningPipelineBroken：Memory 晚于可见历史，禁止历史补齐使用未来记忆");
        // Preserve the original 30-row warm-up. With a migrated memory, the cursor is authoritative.
        int start = string.IsNullOrEmpty(memory.LastTrainingIssue) ? 30 :
            Array.FindIndex(draws, x => Issue(x.Period) > Issue(memory.LastTrainingIssue));
        for (int index = start; index >= 0 && index < draws.Length; index++)
        {
            LearnOne(draws[index].Period, history, allowReconstruction);
        }
        var health = GetHealth(draws[^1].Period);
        if (health.Status is "BROKEN" or "PENDING" || health.LastTrainingIssue != draws[^1].Period)
            throw new InvalidDataException($"LearningPipelineBroken：{health.Reason}");
        return ReadMemory();
    }

    private static DatabaseHelper.HistoryRecord[] OrderedHistory(IReadOnlyList<DatabaseHelper.HistoryRecord> history)
    {
        var sorted = history.OrderBy(x => Issue(x.Period)).ToArray();
        if (sorted.Select(x => x.Period).Distinct().Count() != sorted.Length)
            throw new InvalidDataException("学习历史有重复期号");
        return sorted;
    }

    public static LearningOutcome LearnOne(string issue, IReadOnlyList<DatabaseHelper.HistoryRecord> history,
        bool allowReconstruction = true)
    {
        using var conn = Open();
        string started = Now;
        ModelMemoryState? before = null;
        LearningInputSnapshot? snapshot = null;
        string actual = "";
        Execute(conn, "BEGIN IMMEDIATE");
        try
        {
            before = Memory(conn);
            string? success = Scalar(conn, "SELECT AuditJson FROM LearningAudit WHERE ModelKey=@m AND Issue=@i AND Success=1",
                ("@m", ModelKey), ("@i", issue));
            if (success is not null)
            {
                var receipt = Read<LearningAuditEntry>(success);
                if (before.MemoryVersion < receipt.MemoryVersionAfter || Issue(before.LastTrainingIssue) < Issue(issue))
                    throw new InvalidDataException("成功审计与 Memory 不一致");
                Execute(conn, "COMMIT");
                return new LearningOutcome(false, false, 0, before.Weights, "AlreadyLearned");
            }
            long target = Issue(issue);
            if (!string.IsNullOrEmpty(before.LastTrainingIssue) && target <= Issue(before.LastTrainingIssue))
            {
                Execute(conn, "COMMIT");
                return new LearningOutcome(false, false, 0, before.Weights, "AlreadyLearned (LegacyBaseline)");
            }
            var draws = OrderedHistory(history);
            int index = Array.FindIndex(draws, d => d.Period == issue);
            if (index < 0) throw new InvalidDataException("尚无该期开奖，禁止提前学习");
            int expected = string.IsNullOrEmpty(before.LastTrainingIssue) ? 30 :
                Array.FindIndex(draws, d => Issue(d.Period) > Issue(before.LastTrainingIssue));
            if (index != expected) throw new InvalidDataException("学习不能越过缺失期，必须按历史顺序处理");
            actual = draws[index].SpecialZodiac;
            if (string.IsNullOrWhiteSpace(actual)) throw new InvalidDataException("实际生肖缺失");
            snapshot = Snapshot(conn, issue);
            if (snapshot is null)
            {
                if (!allowReconstruction) throw new InvalidDataException("缺少合法 Snapshot，补学已停止");
                var prefix = draws.Take(index).ToArray();
                if (prefix.Length < 30) throw new InvalidDataException("合法历史不足30期，无法重建 Snapshot");
                AutoLearningSnapshot rebuilt;
                using (DatabaseHelper.UseHistoryThroughIssue(Issue(prefix[^1].Period)))
                    rebuilt = Stamp(V65ExperimentPipeline.BuildSnapshot(prefix, issue, before), prefix, before);
                snapshot = MakeSnapshot(rebuilt.Input, rebuilt.Result.Ranking.Select(x => x.Zodiac).ToArray(), true,
                    "MissingVersionedSnapshot: strict historical prefix, current causal memory; not a formal prediction", Now);
                InsertSnapshot(conn, snapshot);
            }
            ValidateSnapshot(snapshot);
            if (snapshot.UsedMemoryVersion > before.MemoryVersion)
                throw new InvalidDataException("Snapshot 使用了尚未存在的 MemoryVersion");
            string beforeJson = Json(before);
            var memory = Read<ModelMemoryState>(beforeJson);
            var engine = new MetaPredictionEngine();
            string[] beforeRanking = engine.Predict(snapshot.Input, before, snapshot.BaselineRanking).Ranking.Select(x => x.Zodiac).ToArray();
            AutoLearningTrainer.LearnOne(snapshot.Input, snapshot.Ranking, actual, memory);
            if (memory.MemoryVersion != before.MemoryVersion + 1 || memory.LearnedSamples != before.LearnedSamples + 1 ||
                memory.LastTrainingIssue != issue) throw new InvalidDataException("LearnOne 未形成完整状态更新");
            string[] afterRanking = engine.Predict(snapshot.Input, memory, snapshot.BaselineRanking).Ranking.Select(x => x.Zodiac).ToArray();
            var audit = new LearningAuditEntry
            {
                Issue = issue, ActualZodiac = actual, StartedAt = started, FinishedAt = Now, Success = true,
                MemoryVersionBefore = before.MemoryVersion, MemoryVersionAfter = memory.MemoryVersion,
                LearnedSamplesBefore = before.LearnedSamples, LearnedSamplesAfter = memory.LearnedSamples,
                LastTrainingIssueBefore = before.LastTrainingIssue, LastTrainingIssueAfter = memory.LastTrainingIssue,
                MemoryBeforeJson = beforeJson, MemoryAfterJson = Json(memory),
                WeightsBeforeJson = Json(before.Weights), WeightsAfterJson = Json(memory.Weights),
                MetaCoefficientsBeforeJson = Json(before.MetaCoefficients), MetaCoefficientsAfterJson = Json(memory.MetaCoefficients),
                FeatureReliabilityBeforeJson = Json(before.FeatureContributions), FeatureReliabilityAfterJson = Json(memory.FeatureContributions),
                PredictionBeforeJson = Json(beforeRanking), PredictionAfterJson = Json(afterRanking),
                RankingChanged = !beforeRanking.SequenceEqual(afterRanking),
                Top3Changed = !beforeRanking.Take(3).ToHashSet().SetEquals(afterRanking.Take(3)),
                Top6Changed = !beforeRanking.Take(6).ToHashSet().SetEquals(afterRanking.Take(6)),
                DecisionImpact = !beforeRanking.SequenceEqual(afterRanking),
                InputSnapshotId = snapshot.SnapshotId, Reconstructed = snapshot.Reconstructed
            };
            Execute(conn, "INSERT INTO ModelMemory(MemoryKey,MemoryJson,UpdatedAt) VALUES(@k,@j,@t) ON CONFLICT(MemoryKey) DO UPDATE SET MemoryJson=@j,UpdatedAt=@t",
                ("@k", ExperimentModels.MemoryKey(ModelKey)), ("@j", Json(memory)), ("@t", Now));
            SaveAudit(conn, audit);
            int actualRank = snapshot.Ranking.ToList().IndexOf(actual)+1;
            // A reconstruction is learning-only; never relabel its ranking as the historical formal rank.
            if (!snapshot.Reconstructed)
                Execute(conn, "UPDATE PredictionHistory SET LearningStatus='Learned',LearnedAt=@t WHERE Issue=@i AND ModelVersion='V6.5 AutoLearning'",
                    ("@t", Now), ("@i", issue));
            Execute(conn, "COMMIT");
            Console.WriteLine($"[LEARNING] {issue}: MemoryVersion {before.MemoryVersion}->{memory.MemoryVersion}; Audit={audit.Id}; Reconstructed={snapshot.Reconstructed}");
            return new LearningOutcome(true, memory.RecentAdjustments.Count > before.RecentAdjustments.Count,
                actualRank, memory.Weights, "LearnedWithAudit");
        }
        catch (Exception ex)
        {
            Execute(conn, "ROLLBACK");
            SaveAudit(conn, new LearningAuditEntry
            {
                Issue = issue, ActualZodiac = actual, StartedAt = started, FinishedAt = Now, Success = false,
                ErrorMessage = ex.Message, MemoryVersionBefore = before?.MemoryVersion ?? 0, MemoryVersionAfter = before?.MemoryVersion ?? 0,
                LearnedSamplesBefore = before?.LearnedSamples ?? 0, LearnedSamplesAfter = before?.LearnedSamples ?? 0,
                LastTrainingIssueBefore = before?.LastTrainingIssue ?? "", LastTrainingIssueAfter = before?.LastTrainingIssue ?? "",
                InputSnapshotId = snapshot?.SnapshotId ?? "", Reconstructed = snapshot?.Reconstructed ?? false
            });
            throw new InvalidDataException($"LearningPipelineBroken ({issue}): {ex.Message}", ex);
        }
    }

    private static void SaveAudit(SQLiteConnection conn, LearningAuditEntry audit) => Execute(conn,
        "INSERT INTO LearningAudit(Id,ModelKey,Issue,Success,MemoryVersionBefore,MemoryVersionAfter,InputSnapshotId,FinishedAt,AuditJson) VALUES(@id,@m,@i,@s,@b,@a,@snap,@t,@j)",
        ("@id", audit.Id), ("@m", audit.ModelKey), ("@i", audit.Issue), ("@s", audit.Success ? 1 : 0),
        ("@b", audit.MemoryVersionBefore), ("@a", audit.MemoryVersionAfter), ("@snap", audit.InputSnapshotId),
        ("@t", audit.FinishedAt), ("@j", Json(audit)));

    public static LearningEvidence ExportEvidence()
    {
        using var conn = Open();
        return new LearningEvidence(Rows<LearningInputSnapshot>(conn, "SELECT SnapshotJson FROM LearningInputSnapshot ORDER BY ModelKey,Issue"),
            Rows<LearningAuditEntry>(conn, "SELECT AuditJson FROM LearningAudit ORDER BY ModelKey,Issue,FinishedAt,Id"));
    }

    private static List<T> Rows<T>(SQLiteConnection conn, string sql)
    {
        using var cmd = new SQLiteCommand(sql, conn);
        using var reader = cmd.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(Read<T>(reader.GetString(0)));
        return rows;
    }

    public static LearningHealth GetHealth(string latest)
    {
        var memory = ReadMemory();
        var audits = ExportEvidence().Audits;
        var success = audits.Where(a => a.Success).OrderBy(a => a.MemoryVersionAfter).LastOrDefault();
        string status = "HEALTHY", reason = "学习审计与最新开奖一致";
        if (memory.LastTrainingIssue != latest)
        {
            status = audits.Any(a => !a.Success && (string.IsNullOrEmpty(memory.LastTrainingIssue) || Issue(a.Issue) > Issue(memory.LastTrainingIssue))) ? "BROKEN" : "PENDING";
            reason = "LastTrainingIssue 尚未追上最新开奖";
        }
        else if (success is null || success.MemoryVersionAfter != memory.MemoryVersion || success.LastTrainingIssueAfter != latest)
        {
            status = "BROKEN";
            reason = "当前 Memory 缺少匹配成功审计（旧状态保留，不伪造学习证明）";
        }
        else if (audits.Where(a => a.Success).TakeLast(30).ToArray() is { Length: 30 } recent && recent.All(a => !a.DecisionImpact))
        {
            status = "NO_DECISION_IMPACT";
            reason = "连续30次学习在相同冻结输入上没有改变排名";
        }
        return new LearningHealth(status, reason, latest, memory.LastTrainingIssue, memory.MemoryVersion, success?.FinishedAt);
    }
}
