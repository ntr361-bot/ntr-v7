using System.Collections.Immutable;
using System.Text.Json;
using 六合分析软件;
using 六合分析软件.MacroReasoning;

public static class V65BaseMacroExpertAdapterTests
{
    private static readonly string[] Zodiac = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };

    public static int Run()
    {
        VerifyDefinitionsAndFormalIsolation();
        VerifyLiveSnapshotsAndTemporalGuard();
        VerifyCausalReconstruction();
        VerifyCatalogAppendOnlyAdmission();
        Console.WriteLine("V65 BASE MACRO ADAPTER PASS");
        return 0;
    }

    private static void VerifyDefinitionsAndFormalIsolation()
    {
        DatabaseHelper.HistoryRecord[] history = BuildHistory(140);
        foreach (V65BaseMacroExpertDefinition definition in V65BaseMacroExpertAdapter.Definitions)
        {
            var engine = new V65RuleScoringEngine();
            V65RuleScoringEngine.PredictResultV2 formalBefore = engine.Predict(history, definition.AnalysisPeriods,
                V65ExperimentPipeline.GetWeightsForPeriods(definition.AnalysisPeriods));
            string formalBeforeJson = JsonSerializer.Serialize(formalBefore);
            string[] formalRanking = formalBefore.AllScores.OrderByDescending(x => x.TotalScore).Select(x => x.Zodiac).ToArray();

            V65BaseMacroRanking first = V65BaseMacroExpertAdapter.Build(history, definition.ExpertId);
            V65BaseMacroRanking second = V65BaseMacroExpertAdapter.Build(history, definition.ExpertId);
            V65RuleScoringEngine.PredictResultV2 formalAfter = engine.Predict(history, definition.AnalysisPeriods,
                V65ExperimentPipeline.GetWeightsForPeriods(definition.AnalysisPeriods));

            Check(!definition.ExpertRevisionId.Contains("unknown", StringComparison.OrdinalIgnoreCase) &&
                  !definition.ExpertRevisionId.Contains("current", StringComparison.OrdinalIgnoreCase) &&
                  !definition.AlgorithmVersion.Contains("unknown", StringComparison.OrdinalIgnoreCase) &&
                  !definition.AlgorithmVersion.Contains("current", StringComparison.OrdinalIgnoreCase),
                $"{definition.ExpertId}使用明确不可变版本");
            Check(IsFullRanking(first.Ranking), $"{definition.ExpertId}输出完整12生肖");
            Check(first.Ranking.SequenceEqual(formalRanking), $"{definition.ExpertId} Macro排名与正式规则排名一致");
            Check(JsonSerializer.Serialize(first) == JsonSerializer.Serialize(second), $"{definition.ExpertId}相同前缀结果确定");
            Check(formalBeforeJson == JsonSerializer.Serialize(formalAfter), $"{definition.ExpertId}适配前后正式输出完全不变");
        }
    }

    private static void VerifyLiveSnapshotsAndTemporalGuard()
    {
        string dir = NewTempDirectory("liuhe-v65-base-live-");
        Environment.SetEnvironmentVariable("LIUHE_DATA_DIR", Path.Combine(dir, "formal-data"));
        DatabaseHelper.InitializeDatabase();
        string predictionHistoryBefore = JsonSerializer.Serialize(DatabaseHelper.GetPredictionHistory(int.MaxValue));
        DateTimeOffset registeredAt = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset generatedAt = registeredAt.AddHours(1);
        DateTimeOffset freezeAt = generatedAt.AddMinutes(1);
        const string codeVersion = "v65-base-test-code-v1";
        var registry = CreateRegistry(dir, registeredAt, codeVersion);
        var store = new ImmutableExpertSnapshotStore(Path.Combine(dir, "snapshots.db"), registry);
        DatabaseHelper.HistoryRecord[] history = BuildHistory(140);
        long cutoff = long.Parse(history.MaxBy(x => long.Parse(x.Period))!.Period);

        foreach (V65BaseMacroExpertDefinition definition in V65BaseMacroExpertAdapter.Definitions)
        {
            long target = cutoff + 1;
            ExpertSnapshot first = V65BaseMacroExpertSnapshotService.FreezeLive(store, history, definition.ExpertId,
                target, cutoff, generatedAt, freezeAt, codeVersion);
            ExpertSnapshot repeated = V65BaseMacroExpertSnapshotService.FreezeLive(store, history, definition.ExpertId,
                target, cutoff, generatedAt, freezeAt, codeVersion);
            Check(ExpertSnapshotIntegrity.Verify(first) && IsFullRanking(first.Ranking),
                $"{definition.ExpertId} PayloadHash和FullRanking12有效");
            Check(first.PayloadHash == repeated.PayloadHash &&
                  store.ReadSnapshot(definition.ExpertRevisionId, target, SnapshotOrigin.LiveFrozen)?.PayloadHash == first.PayloadHash,
                $"{definition.ExpertId}不可变追加幂等");
            Reject(() => V65BaseMacroExpertSnapshotService.FreezeLive(store, history, definition.ExpertId,
                target, cutoff, generatedAt.AddSeconds(1), freezeAt, codeVersion),
                $"{definition.ExpertId}冲突覆盖被拒绝");
            Reject(() => V65BaseMacroExpertSnapshotService.FreezeLive(store, history, definition.ExpertId,
                target, target, generatedAt, freezeAt, codeVersion),
                $"{definition.ExpertId}拒绝HistoryCutoff不早于TargetIssue");
            DatabaseHelper.HistoryRecord[] future = history.Append(Clone(history[^1], target.ToString())).ToArray();
            Reject(() => V65BaseMacroExpertSnapshotService.FreezeLive(store, future, definition.ExpertId,
                target, cutoff, generatedAt, freezeAt, codeVersion),
                $"{definition.ExpertId}拒绝目标期或未来数据");
        }

        Check(predictionHistoryBefore == JsonSerializer.Serialize(DatabaseHelper.GetPredictionHistory(int.MaxValue)),
            "V65基础Macro适配不写PredictionHistory");
    }

    private static void VerifyCausalReconstruction()
    {
        string dir = NewTempDirectory("liuhe-v65-base-rebuild-");
        DateTimeOffset registeredAt = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset simulatedAsOf = new(2026, 5, 20, 23, 0, 0, TimeSpan.Zero);
        DateTimeOffset reconstructedAt = registeredAt.AddHours(2);
        DateTimeOffset freezeAt = reconstructedAt.AddMinutes(1);
        const string codeVersion = "v65-base-rebuild-code-v1";
        var registry = CreateRegistry(dir, registeredAt, codeVersion);
        var store = new ImmutableExpertSnapshotStore(Path.Combine(dir, "snapshots.db"), registry);
        DatabaseHelper.HistoryRecord[] history = BuildHistory(140);
        long cutoff = long.Parse(history.MaxBy(x => long.Parse(x.Period))!.Period);

        foreach (V65BaseMacroExpertDefinition definition in V65BaseMacroExpertAdapter.Definitions)
        {
            ExpertSnapshot snapshot = V65BaseMacroExpertSnapshotService.FreezeCausalReconstruction(store, history,
                definition.ExpertId, cutoff + 1, cutoff, simulatedAsOf, reconstructedAt, freezeAt, codeVersion);
            Check(snapshot.Origin == SnapshotOrigin.CausalReconstruction && snapshot.Reconstruction is
                  { Reconstructed: true, MemoryRebuiltFromScratch: true } &&
                  snapshot.Reconstruction.HistoryCutoff == cutoff &&
                  !string.IsNullOrWhiteSpace(snapshot.Reconstruction.TrainingPrefixHash) &&
                  ExpertSnapshotIntegrity.Verify(snapshot), $"{definition.ExpertId}可严格历史因果重建");
            Check(store.ReadPrefixSnapshots(cutoff + 1, simulatedAsOf, HistoricalEvaluationMode.CausalReconstruction)
                .Any(x => x.ExpertRevisionId == definition.ExpertRevisionId),
                $"{definition.ExpertId}重建快照按模拟AsOf可读取");
        }
    }

    private static void VerifyCatalogAppendOnlyAdmission()
    {
        string dir = NewTempDirectory("liuhe-v65-base-catalog-");
        var registry = new VersionedExpertRegistry(Path.Combine(dir, "registry.db"));
        DateTimeOffset at = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        CurrentExpertCatalog.Seed(registry, at, "catalog-v65-base-code-v1");
        ExpertRegistration[] experts = registry.ReadAsOf(at.AddMinutes(1)).Experts.ToArray();
        foreach (V65BaseMacroExpertDefinition definition in V65BaseMacroExpertAdapter.Definitions)
        {
            ExpertRegistration[] revisions = experts.Where(x => x.ExpertId == definition.ExpertId).ToArray();
            Check(revisions.Length == 2 && revisions.Any(x => x.ExpertRevisionId.EndsWith("@v65-rule-current", StringComparison.Ordinal)) &&
                  revisions.Any(x => x.ExpertRevisionId == definition.ExpertRevisionId),
                $"{definition.ExpertId}保留旧Revision并追加新Revision");
            ExpertRegistration admitted = revisions.Single(x => x.ExpertRevisionId == definition.ExpertRevisionId);
            Check(admitted.AlgorithmVersion == definition.AlgorithmVersion &&
                  admitted.LeakageAuditStatus == ExpertAuditStatus.Passed &&
                  admitted.SnapshotIntegrityStatus == ExpertAuditStatus.Passed &&
                  !admitted.EligibleForMacro && !admitted.Enabled,
                $"{definition.ExpertId}双审计Passed但保持未准入未启用");
        }
    }

    private static VersionedExpertRegistry CreateRegistry(string dir, DateTimeOffset at, string codeVersion)
    {
        var registry = new VersionedExpertRegistry(Path.Combine(dir, "registry.db"));
        foreach (V65BaseMacroExpertDefinition definition in V65BaseMacroExpertAdapter.Definitions)
            registry.AppendRevision(new ExpertRegistration
            {
                ExpertId = definition.ExpertId, ExpertRevisionId = definition.ExpertRevisionId,
                DisplayName = definition.DisplayName, ModelFamily = "V65", ModelType = ExpertModelType.Base,
                AlgorithmVersion = definition.AlgorithmVersion, CodeVersion = codeVersion, IsBaseExpert = true,
                PredictionSnapshotType = "FullRanking12", HasFullRanking12 = true,
                LeakageAuditStatus = ExpertAuditStatus.Passed, SnapshotIntegrityStatus = ExpertAuditStatus.Passed,
                EligibleForMacro = false, Enabled = false, RegisteredAt = at, EffectiveFrom = at,
                InputDependencyIds = V65BaseMacroExpertSnapshotService.InputDependencyIds,
                DependencyEvidenceReference = "V65BaseMacroExpertAdapter.cs; V65BaseMacroExpertSnapshotService.cs"
            });
        return registry;
    }

    private static DatabaseHelper.HistoryRecord[] BuildHistory(int count) => Enumerable.Range(0, count)
        .Select(i => new DatabaseHelper.HistoryRecord
        {
            Period = (2026001 + i).ToString(), SpecialZodiac = Zodiac[(i * 5 + i / 7) % Zodiac.Length],
            SpecialNumber = ((i * 7 % 49) + 1).ToString("00"),
            OpenTime = new DateTime(2026, 1, 1).AddDays(i).ToString("yyyy-MM-dd HH:mm:ss")
        }).ToArray();

    private static DatabaseHelper.HistoryRecord Clone(DatabaseHelper.HistoryRecord source, string period) => new()
    {
        Period = period, SpecialZodiac = source.SpecialZodiac, SpecialNumber = source.SpecialNumber, OpenTime = source.OpenTime
    };

    private static bool IsFullRanking(IEnumerable<string> ranking)
    {
        string[] values = ranking.ToArray();
        return values.Length == 12 && values.Distinct(StringComparer.Ordinal).Count() == 12 &&
               values.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(Zodiac.OrderBy(x => x, StringComparer.Ordinal));
    }

    private static string NewTempDirectory(string prefix)
    {
        string path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Reject(Action action, string name)
    {
        try { action(); }
        catch (InvalidDataException) { Console.WriteLine("PASS " + name); return; }
        throw new InvalidOperationException("FAIL " + name);
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL " + name);
        Console.WriteLine("PASS " + name);
    }
}
