using System.Text.Json;
using 六合分析软件;

public static class LearningPipelineTests
{
    public static int Run()
    {
        string[] z = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
        var input = new MetaPredictionInput("100", z.Select((name, i) => new ZodiacMetaFeatures(name,
            new Dictionary<string, double> { ["AI"] = i, ["ML"] = 12-i, ["State"] = i/2d, ["V7"] = 2 },
            new Dictionary<string, double> { ["model_consensus"] = i/12d })).ToArray());
        var memory = new ModelMemoryState { LearnedSamples = 100, LastTrainingIssue = "99" };
        AutoLearningTrainer.LearnOne(input, z, "虎", memory);
        Check(memory.MemoryVersion == 1 && memory.LearnedSamples == 101 && memory.LastTrainingIssue == "100", "LearnOne increments version exactly once");
        string once = JsonSerializer.Serialize(memory);
        AutoLearningTrainer.LearnOne(input, z, "虎", memory);
        Check(JsonSerializer.Serialize(memory) == once, "duplicate cannot mutate meta coefficients");
        memory.RecentFeedback.Clear();
        once = JsonSerializer.Serialize(memory);
        AutoLearningTrainer.LearnOne(input, z, "虎", memory);
        Check(JsonSerializer.Serialize(memory) == once, "old issue rejected even after rolling feedback was trimmed");
        Integration();
        return 0;
    }

    private static void Integration()
    {
        string directory = Path.Combine(Path.GetTempPath(), "liuhe-learning-acceptance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        // Avoid DatabaseHelper's legacy database promotion: start with an explicitly empty isolated file.
        using (File.Create(Path.Combine(directory, "history.db"))) { }
        Environment.SetEnvironmentVariable("LIUHE_DATA_DIR", directory);
        DatabaseHelper.InitializeDatabase();
        using var validation = OnlineLearningPipeline.EnableIsolatedValidation();
        Check(DatabaseHelper.DatabasePath == Path.Combine(directory, "history.db"), "isolated database selected");
        string[] names = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
        void Draw(int n) => DatabaseHelper.InsertHistory((2026000+n).ToString(), "010203040506", "07",
            names[n % 12], new DateTime(2026, 1, 1).AddDays(n-1).ToString("yyyy-MM-dd HH:mm:ss"), "2026-01-01");
        for (int n = 1; n <= 99; n++) Draw(n);
        var baselineMemory = new ModelMemoryState { LearnedSamples = 100, LastTrainingIssue = "2026099" };
        new ModelMemory().Save(baselineMemory);
        var prefix = DatabaseHelper.GetHistory();
        var snapshot = OnlineLearningPipeline.Stamp(V65ExperimentPipeline.BuildSnapshot(prefix, "2026100", baselineMemory), prefix, baselineMemory);
        DatabaseHelper.SavePrediction("2026100", string.Join(",", snapshot.Result.Ranking.Take(3).Select(x=>x.Zodiac)),
            string.Join(",", snapshot.Result.Ranking.Take(6).Select(x=>x.Zodiac)), "", "V6.5 AutoLearning", 7250,
            "acceptance", "", snapshot.FinalRankingJson, snapshot.BaseModelScoresJson, snapshot.FeatureSnapshotJson, snapshot.WeightSnapshotJson);
        string frozen = OnlineLearningPipeline.GetSnapshot("2026100")!.InputHash;
        string formal = DatabaseHelper.GetPredictionHistory(int.MaxValue).Single(x=>x.Issue=="2026100").FinalRankingJson;
        Draw(100);
        string daily = DailyPredictionAutomation.Generate(2026101, Path.Combine(directory, "daily"));
        var state = OnlineLearningPipeline.ReadMemory();
        Check(state.MemoryVersion == 1 && state.LastTrainingIssue == "2026100" && state.LearnedSamples == 101, "daily production path learned N before generating N+1");
        var audit = OnlineLearningPipeline.ExportEvidence().Audits.Single(x=>x.Success);
        Check(!audit.Reconstructed && audit.InputSnapshotId == OnlineLearningPipeline.GetSnapshot("2026100")!.SnapshotId,
            "learning consumed original pre-draw snapshot");
        var next = OnlineLearningPipeline.GetSnapshot("2026101")!;
        Check(next.UsedMemoryVersion == state.MemoryVersion && next.HistoryCutoffIssue == "2026100", "N+1 persisted updated memory version and cutoff");
        Check(File.Exists(daily) && OnlineLearningPipeline.GetHealth("2026100").Status == "HEALTHY", "daily output and health accepted");
        string once = JsonSerializer.Serialize(state);
        Check(OnlineLearningPipeline.LearnOne("2026100", DatabaseHelper.GetHistory()).Reason == "AlreadyLearned" &&
            JsonSerializer.Serialize(OnlineLearningPipeline.ReadMemory()) == once, "durable duplicate receipt leaves memory unchanged");
        Check(OnlineLearningPipeline.GetSnapshot("2026100")!.InputHash == frozen &&
            DatabaseHelper.GetPredictionHistory(int.MaxValue).Single(x=>x.Issue=="2026100").FinalRankingJson == formal, "original prediction unchanged");
        Reject(() => OnlineLearningPipeline.Stamp(snapshot, DatabaseHelper.GetHistory(), state), "target draw in prediction history rejected");

        Draw(101);
        using (var conn = DatabaseHelper.GetConnection())
        using (var cmd = new System.Data.SQLite.SQLiteCommand("CREATE TRIGGER fail_acceptance_audit BEFORE INSERT ON LearningAudit WHEN NEW.Success=1 AND NEW.Issue='2026101' BEGIN SELECT RAISE(ABORT,'injected audit failure'); END",conn)) cmd.ExecuteNonQuery();
        Reject(()=>OnlineLearningPipeline.LearnOne("2026101", DatabaseHelper.GetHistory()), "audit write failure aborts learning");
        Check(JsonSerializer.Serialize(OnlineLearningPipeline.ReadMemory()) == once &&
            OnlineLearningPipeline.GetHealth("2026101").Status == "BROKEN", "transaction rolls memory back and records BROKEN");
        using (var conn = DatabaseHelper.GetConnection())
        using (var cmd = new System.Data.SQLite.SQLiteCommand("DROP TRIGGER fail_acceptance_audit",conn)) cmd.ExecuteNonQuery();
        OnlineLearningPipeline.CatchUp(DatabaseHelper.GetHistory());
        Check(OnlineLearningPipeline.ReadMemory().MemoryVersion == 2, "failed issue can be retried exactly once");
        Draw(102); Draw(103);
        Reject(()=>OnlineLearningPipeline.CatchUp(DatabaseHelper.GetHistory(), false), "missing snapshot blocks gap recovery");
        Reject(()=>OnlineLearningPipeline.LearnOne("2026103", DatabaseHelper.GetHistory()), "cannot skip missing 102 and learn 103");
        Check(OnlineLearningPipeline.ReadMemory().LastTrainingIssue == "2026101", "gap failure preserves cursor");
        OnlineLearningPipeline.CatchUp(DatabaseHelper.GetHistory());
        Check(OnlineLearningPipeline.ReadMemory().MemoryVersion == 4 && OnlineLearningPipeline.ReadMemory().LastTrainingIssue == "2026103", "gap recovery learns 102 then 103");
        Check(OnlineLearningPipeline.GetSnapshot("2026102")!.Reconstructed &&
            !DatabaseHelper.GetPredictionHistory(int.MaxValue).Any(x=>x.Issue=="2026102"), "reconstruction marked and never written as formal history");
        var evidence = OnlineLearningPipeline.ExportEvidence();
        string evidencePath = Path.Combine(directory, "learning-evidence.json");
        File.WriteAllText(evidencePath, JsonSerializer.Serialize(evidence,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine("EVIDENCE_PATH=" + evidencePath);
        Console.WriteLine("DATABASE_PATH=" + DatabaseHelper.DatabasePath);
    }

    private static void Reject(Action action, string name)
    {
        try { action(); }
        catch (InvalidDataException) { Check(true, name); return; }
        throw new InvalidOperationException("FAIL expected rejection: " + name);
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL " + name);
        Console.WriteLine("PASS " + name);
    }
}
