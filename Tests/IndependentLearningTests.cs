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
        Console.WriteLine("ACCEPTANCE_DATABASE=" + independentPath);
        return 0;
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
