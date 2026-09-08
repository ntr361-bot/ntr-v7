using 六合分析软件;
using System.Text.Json;
using System.Data.SQLite;

public static class IndependentLearningTests
{
    public static int Run()
    {
        var type = typeof(ModelMemory).Assembly.GetType("六合分析软件.IndependentLearningModel");
        if (type is null) throw new InvalidOperationException("FAIL: independent learning entrance is absent");
        string root = Path.Combine(Path.GetTempPath(), "liuhe-independent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string legacyPath = Path.Combine(root, "history.db");
        using (var c = new SQLiteConnection($"Data Source={legacyPath};Version=3;Pooling=False"))
        {
            c.Open();
            using var command = new SQLiteCommand("CREATE TABLE ModelMemory(K TEXT,J TEXT); INSERT INTO ModelMemory VALUES('v65-auto','legacy-one'),('intelligent-history','legacy-two'); CREATE TABLE PredictionHistory(I TEXT); INSERT INTO PredictionHistory VALUES('unchanged')", c);
            command.ExecuteNonQuery();
        }
        byte[] legacy = File.ReadAllBytes(legacyPath);
        dynamic model = Activator.CreateInstance(type, root)!;
        string[] z = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
        string Input(long issue, long cutoff, bool future = false) => JsonSerializer.Serialize(new {
            Issue = issue, HistoryCutoffIssue = cutoff,
            SourceGeneratedAt = DateTimeOffset.UtcNow.AddMinutes(future ? 10 : -10),
            SourceRankings = new Dictionary<string,string[]> {
                ["v65-50"] = z, ["v65-100"] = z.Reverse().ToArray(), ["v65-all"] = z.Reverse().ToArray() }
        });
        string n = Input(2026100, 2026099);
        string saved = model.Predict(n);
        using (var p = JsonDocument.Parse(saved))
        {
            Check(p.RootElement.GetProperty("UsedMemoryVersion").GetInt64() == 0, "new memory starts at zero, not inherited");
            Check(p.RootElement.GetProperty("Ranking").GetArrayLength() == 12, "complete twelve zodiac prediction persisted");
            Check(p.RootElement.GetProperty("Ranking")[0].GetString() == "猪", "weighted ranks produce hand-checked initial winner");
        }
        Check((string)model.Predict(n) == saved, "same input is immutable and idempotent");
        Reject(() => model.Predict(Input(2026100,2026099)), "conflicting input cannot overwrite prediction");
        Reject(() => model.Predict(Input(2026101,2026100)), "pending predecessor must learn before next prediction");
        Reject(() => model.Learn(2026101L, "鼠", 2026100L), "missing snapshot cannot be learned");
        Reject(() => model.Learn(2026100L, "无效", 2026099L), "invalid actual rejected");
        Check((bool)model.Learn(2026100L,"鼠",2026099L), "N learns using saved input");
        using (var receipt = JsonDocument.Parse((string)model.ReadReceiptJson(2026100L)))
            Check(receipt.RootElement.GetProperty("Actual").GetString() == "鼠" &&
                receipt.RootElement.GetProperty("Before").GetProperty("Version").GetInt64() == 0 &&
                receipt.RootElement.GetProperty("After").GetProperty("Version").GetInt64() == 1,
                "audit records actual and exact before/after memory");
        string learned = model.ReadStateJson();
        using (var state = JsonDocument.Parse(learned))
        {
            Check(state.RootElement.GetProperty("Version").GetInt64() == 1 && state.RootElement.GetProperty("LastIssue").GetInt64() == 2026100, "atomic cursor and version increment");
            var t = state.RootElement.GetProperty("Theta");
            Check(t[0].GetDouble() > 0 && t[1].GetDouble() < 0, "correct source gains trust after error feedback");
        }
        Check(!(bool)model.Learn(2026100L,"鼠",2026099L) && (string)model.ReadStateJson() == learned, "duplicate feedback changes nothing");
        Reject(() => model.Learn(2026100L,"牛",2026099L), "conflicting actual cannot rewrite receipt");
        dynamic reopened = Activator.CreateInstance(type, root)!;
        Check((string)reopened.ReadStateJson() == learned, "state survives restart");
        Reject(() => model.Predict(Input(2026101,2026101)), "target-period history is rejected");
        Reject(() => model.Predict(Input(2026101,2026100,true)), "future source timestamp is rejected");
        Reject(() => model.Predict(Input(2026102,2026101)), "unlearned gap cannot be skipped");
        string second = model.Predict(Input(2026101,2026100));
        using (var p = JsonDocument.Parse(second))
            Check(p.RootElement.GetProperty("UsedMemoryVersion").GetInt64() == 1, "N+1 uses learned memory version");
        string independentPath = Path.Combine(root,"experiments","v7-independent-learning-v1","learning.db");
        void Sql(string sql) {
            using var c = new SQLiteConnection($"Data Source={independentPath};Version=3;Pooling=False"); c.Open();
            using var command = new SQLiteCommand(sql,c); command.ExecuteNonQuery();
        }
        Sql("CREATE TRIGGER fail_receipt BEFORE INSERT ON LearningReceipt BEGIN SELECT RAISE(ABORT,'acceptance rollback'); END");
        bool aborted = false;
        try { model.Learn(2026101L,"牛",2026100L); } catch (SQLiteException) { aborted = true; }
        Check(aborted && (string)model.ReadStateJson() == learned, "receipt failure rolls back memory update");
        Sql("DROP TRIGGER fail_receipt");
        Check((bool)model.Learn(2026101L,"牛",2026100L), "failed learning can retry");
        Check((string)model.ReadPredictionJson(2026100L) == saved, "learning never rewrites historical prediction");
        Check(File.ReadAllBytes(legacyPath).SequenceEqual(legacy), "both legacy memories and history remain byte-identical");
        string archive=model.ExportArchive();
        var recovered=new IndependentLearningModel(Path.Combine(root,"recovered"));
        recovered.RestoreArchive(archive);
        Check(recovered.ExportArchive()==archive,"cloud archive reproduces predictions, receipts and exact state");
        Reject(()=>recovered.RestoreArchive(archive),"archive cannot overwrite an existing learning branch");
        var corrupt=System.Text.Json.Nodes.JsonNode.Parse(archive)!;
        corrupt["State"]="{}";
        var empty=new IndependentLearningModel(Path.Combine(root,"corrupt"));
        string clean=empty.ExportArchive();
        Reject(()=>empty.RestoreArchive(corrupt.ToJsonString()),"invalid final archive state is rejected");
        Check(empty.ExportArchive()==clean,"failed archive restoration rolls back every inserted row");
        Daily(root,z);
        Historical(root,z);
        Console.WriteLine("ACCEPTANCE_DATABASE=" + independentPath);
        return 0;
    }

    private static void Daily(string root,string[] z)
    {
        string directory=Path.Combine(root,"daily");
        var draws=new List<DatabaseHelper.HistoryRecord>{ new() { Period="2026199",SpecialZodiac="虎",OpenTime=DateTimeOffset.UtcNow.AddHours(-1).ToString("O") } };
        List<DatabaseHelper.PredictionRecord> Bases(long issue) => new[]{50,100,200}.Select(n=>new DatabaseHelper.PredictionRecord {
            Issue=issue.ToString(),ModelVersion="V6.5",AnalysisPeriods=n,PredictTime=DateTimeOffset.UtcNow.ToString("O"),
            FinalRankingJson=JsonSerializer.Serialize(z)
        }).ToList();
        var rows=Bases(2026200);
        string first=IndependentLearningDaily.Run(directory,2026200,draws,rows);
        Check(IndependentLearningDaily.Run(directory,2026200,draws,rows)==first,"daily retry reuses the frozen prediction");
        draws.Add(new() { Period="2026200",SpecialZodiac="牛",OpenTime=DateTimeOffset.UtcNow.AddHours(1).ToString("O") });
        Reject(()=>IndependentLearningDaily.Run(directory,2026201,draws,Bases(2026201)),"future-dated draw cannot update memory");
        Check(new IndependentLearningModel(directory).ReadReceiptJson(2026200)==null,"rejected future draw leaves no learning receipt");
        draws.RemoveAt(draws.Count-1);
        draws.Add(new() { Period="2026200",SpecialZodiac="牛",OpenTime=DateTimeOffset.UtcNow.ToString("O") });
        string second=IndependentLearningDaily.Run(directory,2026201,draws,Bases(2026201));
        using var p=JsonDocument.Parse(second);
        Check(p.RootElement.GetProperty("UsedMemoryVersion").GetInt64()==1,"daily adapter reveals N, learns N, predicts N+1");
        Reject(()=>IndependentLearningDaily.Run(directory,2026200,draws,rows),"daily adapter refuses post-draw reconstruction");
        Reject(()=>IndependentLearningDaily.Run(Path.Combine(root,"missing"),2026201,draws,Array.Empty<DatabaseHelper.PredictionRecord>()),"daily adapter rejects missing base snapshots");
        Check(new IndependentLearningModel(directory).ReadReceiptJson(2026200)!=null,"daily feedback is auditable");
    }

    private static void Historical(string root, string[] z)
    {
        string directory = Path.Combine(root, "historical");
        DateTimeOffset opened = DateTimeOffset.UtcNow.AddHours(-2);
        var samples = new[]
        {
            new HistoricalTrainingSample(2026100, 2026099, opened.AddHours(-2), opened.AddHours(-1), "鼠",
                new Dictionary<string, string[]> { ["v65-50"] = z, ["v65-100"] = z.Reverse().ToArray(), ["v65-all"] = z }, "测试冻结快照", "fixture-1"),
            new HistoricalTrainingSample(2026101, 2026100, opened.AddMinutes(-30), opened, "牛",
                new Dictionary<string, string[]> { ["v65-50"] = z.Reverse().ToArray(), ["v65-100"] = z, ["v65-all"] = z.Reverse().ToArray() }, "测试冻结快照", "fixture-2")
        };
        HistoricalTrainingReport report = IndependentLearningHistoricalTraining.Train(directory, samples);
        Check(report.Entries.Select(entry => entry.Issue).SequenceEqual(new long[] { 2026100, 2026101 }),
            "history trainer predicts then learns every contiguous frozen sample");
        Check(new IndependentLearningModel(IndependentLearningHistoricalTraining.Folder(directory)).ReadReceiptJson(2026101) is not null,
            "history trainer persists per-issue learning receipts");
        Check(IndependentLearningHistoricalTraining.LoadReport(directory)?.Entries.Length == 2, "history report persists independently");
        Reject(() => IndependentLearningHistoricalTraining.Train(directory, samples), "existing historical run cannot be overwritten");
        Reject(() => IndependentLearningHistoricalTraining.Train(Path.Combine(root,"bad-history"), new[]{samples[0], samples[1] with { HistoryCutoffIssue = 2026101 }}), "history cutoff cannot include target");
        Reject(() => IndependentLearningHistoricalTraining.Train(Path.Combine(root,"future-history"), new[]{samples[0] with { SourceGeneratedAt = opened }}), "post-draw source rejected");
        string archived = File.ReadAllText(Path.Combine(IndependentLearningHistoricalTraining.Folder(directory),"archive.json"));
        var restored = new IndependentLearningModel(Path.Combine(root,"history-restored"));
        restored.RestoreArchive(archived);
        Check(restored.ExportArchive()==archived,"historical archive reproduces every prediction and weight update");
        using var view = new IndependentLearningHistoryForm(directory);
        var grid = view.Controls.OfType<System.Windows.Forms.DataGridView>().Single();
        Check(grid.ReadOnly && grid.Rows.Count==2,"history viewer displays both learned periods read-only");
        Check(!File.Exists(Path.Combine(directory,"history.db")),"historical training and viewing never create production database");
    }

    private static void Check(bool condition,string name) {
        if (!condition) throw new InvalidOperationException("FAIL " + name);
        Console.WriteLine("PASS " + name);
    }
    private static void Reject(Action action,string name) {
        try { action(); } catch (InvalidDataException) { Check(true,name); return; }
        throw new InvalidOperationException("FAIL expected rejection: " + name);
    }
}
