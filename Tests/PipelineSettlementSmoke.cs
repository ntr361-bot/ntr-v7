using System.Data.SQLite;
using System.Text.Json;
using System.Text.Json.Nodes;
using 六合分析软件;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}
// Only an explicit isolated database directory is accepted. Never use production defaults.
string? data = Environment.GetEnvironmentVariable("LIUHE_DATA_DIR");
Check(!string.IsNullOrWhiteSpace(data) && data.Contains("pipeline-stability-test"), "Isolated test directory required");
string source = Path.Combine(Directory.GetCurrentDirectory(), "site", "data");
string temporary = Path.Combine(data!, "daily");
Directory.CreateDirectory(temporary);
foreach (string file in Directory.GetFiles(Path.Combine(source, "daily-records"), "*.json"))
    File.Copy(file, Path.Combine(temporary, Path.GetFileName(file)), true);
DailyPredictionAutomation.UpdateManifest(temporary);
string manifestBefore = File.ReadAllText(Path.Combine(temporary, "manifest.json"));
string historyBefore = File.ReadAllText(Path.Combine(temporary, "history.json"));
DailyPredictionAutomation.UpdateManifest(temporary);
Check(manifestBefore == File.ReadAllText(Path.Combine(temporary, "manifest.json")) &&
    historyBefore == File.ReadAllText(Path.Combine(temporary, "history.json")), "Timestamp-only aggregate rewrite");
string stablePath = Path.Combine(data!, "runtime-envelope.json");
File.WriteAllText(stablePath, "{\"generated_at\":\"old\",\"predictions\":[{\"issue\":1,\"hit\":false}]}");
Check(StableSnapshotFile.HasSameContent(stablePath, "{\"predictions\":[{\"issue\":1,\"hit\":false}],\"generated_at\":\"new\"}", "generated_at"), "Envelope timestamp caused content change");
Check(!StableSnapshotFile.HasSameContent(stablePath, "{\"predictions\":[{\"issue\":1,\"hit\":true}],\"generated_at\":\"new\"}", "generated_at"), "Settlement change was suppressed");
Check(!StableSnapshotFile.HasSameContent(stablePath, "{\"predictions\":[{\"issue\":2,\"hit\":false}],\"generated_at\":\"new\"}", "generated_at"), "Next issue was suppressed");
DatabaseHelper.InitializeDatabase();
CloudPredictionSyncService.ImportLocalHistoryArchive(Path.Combine(source, "history.json"));
CloudPredictionSyncService.ImportLocalRuntimeState(Path.Combine(source, "runtime-state.json"));
string issue = DatabaseHelper.GetLatestPeriod();
var before = DatabaseHelper.GetPredictionHistory(int.MaxValue).Where(x => x.Issue == issue).OrderBy(x => x.ModelVersion).ThenBy(x => x.AnalysisPeriods).ToArray();
Check(before.Length > 0, "Latest draw has no frozen model rows");
string frozen = JsonSerializer.Serialize(before.Select(x => new { x.Issue, x.ModelVersion, x.AnalysisPeriods,
    x.PredictZodiac, x.Top6Zodiac, x.PredictNumber, x.FinalRankingJson,
    x.ScoreDetails, x.FeatureSnapshotJson, x.WeightSnapshotJson }));
using (var connection = DatabaseHelper.GetConnection())
using (var reset = new SQLiteCommand("UPDATE PredictionHistory SET ActualNumber='', ActualZodiac='', HitResult='未开奖', Top6HitResult='未开奖', ActualRank=0 WHERE Issue=@issue", connection))
{
    reset.Parameters.AddWithValue("@issue", issue); reset.ExecuteNonQuery();
}
string path = Path.Combine(temporary, issue + ".json");
var row = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
row["verification"] = new JsonObject { ["status"] = "pending" };
File.WriteAllText(path, row.ToJsonString());
Check(DailyPredictionAutomation.VerifyPublishedPredictions(temporary) == 1, "Daily settlement did not complete");
Check(PublishedSettlementReconciliation.Apply(temporary) == before.Length, "All formal model rows must settle together");
var after = DatabaseHelper.GetPredictionHistory(int.MaxValue).Where(x => x.Issue == issue).OrderBy(x => x.ModelVersion).ThenBy(x => x.AnalysisPeriods).ToArray();
Check(frozen == JsonSerializer.Serialize(after.Select(x => new { x.Issue, x.ModelVersion, x.AnalysisPeriods,
    x.PredictZodiac, x.Top6Zodiac, x.PredictNumber, x.FinalRankingJson,
    x.ScoreDetails, x.FeatureSnapshotJson, x.WeightSnapshotJson })), "Frozen forecast content changed");
Check(after.All(x => x.HitResult != "未开奖" && x.Top6HitResult != "未开奖"), "Partial model settlement");
Check(DailyPredictionAutomation.VerifyPublishedPredictions(temporary) == 0 && PublishedSettlementReconciliation.Apply(temporary) == 0, "Repeated settlement changed data");
row = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
row["verification"]!["actual_zodiac"] = "invalid";
File.WriteAllText(path, row.ToJsonString());
bool rejected = false;
try { DailyPredictionAutomation.VerifyPublishedPredictions(temporary); }
catch (InvalidDataException) { rejected = true; }
Check(rejected, "Conflicting frozen settlement was silently replaced");
Console.WriteLine("PASS: all formal models settle together, repeat is a no-op, forecasts unchanged, conflict rejected; no model generated");
