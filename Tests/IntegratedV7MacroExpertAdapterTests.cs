using System.Collections.Immutable;
using System.Text.Json;
using 六合分析软件;
using 六合分析软件.MacroReasoning;

public static class IntegratedV7MacroExpertAdapterTests
{
    private static readonly string[] Zodiac = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };

    public static int Run()
    {
        VerifyRanking(Array.Empty<string>(), new[] { "鼠", "牛", "虎", "兔", "龙" }, 0);
        IntegratedV7MacroRanking one = VerifyRanking(new[] { "鼠" }, new[] { "鼠", "鼠", "牛", "虎", "兔" }, 1);
        Check(one.Ranking[^1] == "鼠", "一个硬排除生肖固定排第12名");

        IntegratedV7MacroRanking two = VerifyRanking(new[] { "鼠", "牛" }, new[] { "鼠", "鼠", "牛", "牛", "虎" }, 2);
        Check(two.Ranking[^2] == "鼠" && two.Ranking[^1] == "牛", "两个硬排除生肖按底层分数排第11和12名");
        Check(two.Ranking.SequenceEqual(IntegratedV7MacroExpertAdapter.Build(BuildHistory(new[] { "鼠", "鼠", "牛", "牛", "虎" })).Ranking),
            "相同输入产生确定性完整排名");

        VerifySnapshotAndIsolation();
        VerifyCatalogRevisionIsAppendOnly();
        Console.WriteLine("INTEGRATED V7 MACRO ADAPTER PASS");
        return 0;
    }

    private static IntegratedV7MacroRanking VerifyRanking(string[] expectedExcluded, string[] lastFive, int excludedCount)
    {
        DatabaseHelper.HistoryRecord[] history = BuildHistory(lastFive);
        V7PredictionResult formalBefore = V7Engine.Predict(history);
        string formalBeforeProbabilities = JsonSerializer.Serialize(formalBefore.Probabilities);

        IntegratedV7MacroRanking macro = IntegratedV7MacroExpertAdapter.Build(history);

        V7PredictionResult formalAfter = V7Engine.Predict(history);
        Check(macro.Ranking.Length == 12 && macro.Ranking.Distinct(StringComparer.Ordinal).Count() == 12 &&
              macro.Ranking.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(Zodiac.OrderBy(x => x, StringComparer.Ordinal)),
            $"{excludedCount}个硬排除时仍为无重无漏12生肖");
        Check(macro.Items.Count(x => x.ShortForbidden) == excludedCount &&
              macro.Items.Where(x => x.ShortForbidden).Select(x => x.Zodiac).OrderBy(x => x, StringComparer.Ordinal)
                  .SequenceEqual(expectedExcluded.OrderBy(x => x, StringComparer.Ordinal)),
            $"{excludedCount}个硬排除识别正确");
        string[] formalCandidateOrder = formalBefore.Probabilities.OrderByDescending(x => x.Value).ThenBy(x => x.Key)
            .Select(x => x.Key).ToArray();
        Check(macro.Ranking.Take(formalCandidateOrder.Length).SequenceEqual(formalCandidateOrder),
            $"{excludedCount}个硬排除时正常生肖保持正式V7原排序");
        Check(formalBefore.Top6.SequenceEqual(formalAfter.Top6) &&
              formalBeforeProbabilities == JsonSerializer.Serialize(formalAfter.Probabilities),
            $"{excludedCount}个硬排除时适配前后正式V7输出完全一致");
        return macro;
    }

    private static void VerifySnapshotAndIsolation()
    {
        string dir = Path.Combine(Path.GetTempPath(), "liuhe-integrated-v7-macro-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("LIUHE_DATA_DIR", Path.Combine(dir, "formal-data"));
        DatabaseHelper.InitializeDatabase();
        string before = JsonSerializer.Serialize(DatabaseHelper.GetPredictionHistory(int.MaxValue));

        DateTimeOffset registeredAt = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        DateTimeOffset generatedAt = registeredAt.AddHours(1);
        DateTimeOffset freezeAt = generatedAt.AddMinutes(1);
        const string revision = "Integrated-V7@macro-full12-f055-o045-v1";
        const string algorithm = "integrated-v7-macro-full12-f055-o045-v1";
        const string code = "test-code-v1";
        var registry = new VersionedExpertRegistry(Path.Combine(dir, "registry.db"));
        registry.AppendRevision(Registration(revision, algorithm, code, registeredAt));
        var store = new ImmutableExpertSnapshotStore(Path.Combine(dir, "snapshots.db"), registry);
        DatabaseHelper.HistoryRecord[] history = BuildHistory(new[] { "鼠", "鼠", "牛", "牛", "虎" });

        ExpertSnapshot frozen = IntegratedV7MacroExpertSnapshotService.FreezeLive(store, history, 2026041, 2026040,
            generatedAt, freezeAt, revision, code);
        Check(ExpertSnapshotIntegrity.Verify(frozen) && frozen.Ranking.Length == 12,
            "Integrated-V7完整排名通过P6密封与哈希校验");
        Check(store.ReadSnapshot(revision, 2026041, SnapshotOrigin.LiveFrozen)?.PayloadHash == frozen.PayloadHash,
            "Integrated-V7快照已通过FreezeAndAppend持久化");
        Check(IntegratedV7MacroExpertSnapshotService.FreezeLive(store, history, 2026041, 2026040,
            generatedAt, freezeAt, revision, code).PayloadHash == frozen.PayloadHash,
            "相同Integrated-V7快照重复冻结保持幂等");
        Reject(() => IntegratedV7MacroExpertSnapshotService.FreezeLive(store, history, 2026041, 2026041,
            generatedAt, freezeAt, revision, code), "HistoryCutoff未早于TargetIssue时拒绝冻结");
        Check(before == JsonSerializer.Serialize(DatabaseHelper.GetPredictionHistory(int.MaxValue)),
            "Macro快照适配不写入PredictionHistory");
    }

    private static void VerifyCatalogRevisionIsAppendOnly()
    {
        string dir = Path.Combine(Path.GetTempPath(), "liuhe-integrated-v7-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var registry = new VersionedExpertRegistry(Path.Combine(dir, "registry.db"));
        DateTimeOffset registeredAt = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);
        CurrentExpertCatalog.Seed(registry, registeredAt, "catalog-code-v1");
        ExpertRegistration[] revisions = registry.ReadAsOf(registeredAt.AddMinutes(1)).Experts
            .Where(x => x.ExpertId == "Integrated-V7").OrderBy(x => x.ExpertRevisionId).ToArray();
        Check(revisions.Length == 2 && revisions.Any(x => x.ExpertRevisionId == "Integrated-V7@f055-o045-v1") &&
              revisions.Any(x => x.ExpertRevisionId == "Integrated-V7@macro-full12-f055-o045-v1"),
            "Integrated-V7旧Revision保留且新Revision只追加");
        ExpertRegistration adapted = revisions.Single(x => x.ExpertRevisionId == "Integrated-V7@macro-full12-f055-o045-v1");
        Check(adapted.LeakageAuditStatus == ExpertAuditStatus.Passed &&
              adapted.SnapshotIntegrityStatus == ExpertAuditStatus.Passed &&
              !adapted.EligibleForMacro && !adapted.Enabled,
            "新Revision双审计通过但保持未准入未启用");
    }

    private static ExpertRegistration Registration(string revision, string algorithm, string code, DateTimeOffset at) => new()
    {
        ExpertId = "Integrated-V7", ExpertRevisionId = revision, DisplayName = "整合V7 Macro FullRanking12",
        ModelFamily = "V7", ModelType = ExpertModelType.Composite, AlgorithmVersion = algorithm,
        CodeVersion = code, IsBaseExpert = true, PredictionSnapshotType = "FullRanking12", HasFullRanking12 = true,
        LeakageAuditStatus = ExpertAuditStatus.Passed, SnapshotIntegrityStatus = ExpertAuditStatus.Passed,
        EligibleForMacro = false, Enabled = false, RegisteredAt = at, EffectiveFrom = at,
        InputDependencyIds = ImmutableArray.Create("history.db", "FeatureEngine", "IntegratedV7MacroExpertAdapter"),
        DependencyEvidenceReference = "IntegratedV7MacroExpertAdapter.cs; IntegratedV7MacroExpertSnapshotService.cs"
    };

    private static DatabaseHelper.HistoryRecord[] BuildHistory(IReadOnlyList<string> lastFive)
    {
        var zodiacs = Enumerable.Range(0, 35).Select(i => Zodiac[i % Zodiac.Length]).Concat(lastFive).ToArray();
        return zodiacs.Select((zodiac, index) => new DatabaseHelper.HistoryRecord
        {
            Period = (2026001 + index).ToString(), SpecialZodiac = zodiac,
            SpecialNumber = ((index % 49) + 1).ToString("00"),
            OpenTime = new DateTime(2026, 1, 1).AddDays(index).ToString("yyyy-MM-dd HH:mm:ss")
        }).ToArray();
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
