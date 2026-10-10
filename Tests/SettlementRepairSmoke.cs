using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using 六合分析软件;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}

string root = Directory.GetCurrentDirectory();
string historyPath = Path.Combine(root, "site/data/history.json");
string statePath = Path.Combine(root, "site/data/runtime-state.json");
string dailyDirectory = Path.Combine(root, "site/data/daily-records");
string archivePath = Path.Combine(root, "site/data/prediction-traces/history.json.gz");
Check(File.Exists(archivePath), "Original trace archive must exist before isolated smoke test");

DatabaseHelper.InitializeDatabase();
CloudPredictionSyncService.ImportLocalHistoryArchive(historyPath);
CloudPredictionSyncService.ImportLocalRuntimeState(statePath);
using var archiveFile = File.OpenRead(archivePath);
using var archiveGzip = new GZipStream(archiveFile, CompressionMode.Decompress);
JsonObject archive = JsonNode.Parse(archiveGzip)!.AsObject();
int expected = archive["traces"]!.AsArray().Count + archive["outcomes"]!.AsArray().Count;
Check(PredictionTraceArchive.Import(archivePath) == expected,
    "Fresh database must restore all archived traces and outcomes");
Check(PredictionTraceArchive.Import(archivePath) == 0,
    "Repeated import must preserve frozen outcomes without duplicates");
Check(PredictionTraceService.GetLiveOutcome("2026280") is { ActualNumber: "15", ActualZodiac: "龙" },
    "280 original archived outcome must restore successfully");
Check(PredictionTraceService.GetLiveOutcome("2026281") is { ActualNumber: "10" },
    "281 original archived outcome must restore successfully");

var before = DatabaseHelper.GetPredictionHistory(int.MaxValue)
    .Where(x => x.Issue == "2026280")
    .OrderBy(x => x.ModelVersion).ThenBy(x => x.AnalysisPeriods)
    .ToArray();
Check(before.Length == 6, "280 must contain six frozen model forecasts");
var frozen = before.Select(x => new
{
    x.Issue, x.ModelVersion, x.AnalysisPeriods,
    x.PredictZodiac, x.Top6Zodiac, x.PredictNumber, x.FinalRankingJson,
    x.ScoreDetails, x.FeatureSnapshotJson, x.WeightSnapshotJson
}).ToArray();
int settled = PublishedSettlementReconciliation.Apply(dailyDirectory);
Check(settled >= 0, "Settlement reconciliation failed");
var after = DatabaseHelper.GetPredictionHistory(int.MaxValue)
    .Where(x => x.Issue == "2026280")
    .OrderBy(x => x.ModelVersion).ThenBy(x => x.AnalysisPeriods)
    .ToArray();
Check(after.Length == 6 && after.All(x => x.ActualNumber == "15" &&
    x.ActualZodiac == "龙" && x.HitResult != "未开奖" && x.Top6HitResult != "未开奖"),
    "280 still has pending settlement rows");
Check(JsonSerializer.Serialize(frozen) == JsonSerializer.Serialize(after.Select(x => new
{
    x.Issue, x.ModelVersion, x.AnalysisPeriods,
    x.PredictZodiac, x.Top6Zodiac, x.PredictNumber, x.FinalRankingJson,
    x.ScoreDetails, x.FeatureSnapshotJson, x.WeightSnapshotJson
}).ToArray()), "Reconciliation changed frozen predictions");
Check(PredictionTraceService.GetLiveOutcome("2026280") is { LearningObserved: false },
    "280 settlement-only observation not recorded with learning explicitly unobserved");
Check(PredictionTraceService.GetLiveOutcome("2026282") is null,
    "282 not-yet-drawn trace was incorrectly settled");
Check(PublishedSettlementReconciliation.Apply(dailyDirectory) == 0,
    "Settlement reconciliation is not idempotent");

string temporary = Path.Combine(Path.GetTempPath(), "v7-trace-smoke-" +
    Guid.NewGuid().ToString("N") + ".json.gz");
try
{
    Check(PredictionTraceArchive.Export(temporary) == archive["traces"]!.AsArray().Count,
        "Both immutable traces must survive archive export");
    Check(PredictionTraceArchive.Import(temporary) == 0,
        "Duplicate import must not create additional traces/outcomes");
    JsonObject conflicting = archive.DeepClone().AsObject();
    JsonObject entry = conflicting["outcomes"]!.AsArray()[0]!.AsObject();
    JsonObject payload = JsonNode.Parse(entry["outcomeJson"]!.GetValue<string>())!.AsObject();
    payload["actualNumber"] = "49";
    string changedPayload = payload.ToJsonString();
    entry["outcomeJson"] = changedPayload;
    entry["outcomeHash"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(changedPayload)));
    using (var output = File.Create(temporary))
    using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        JsonSerializer.Serialize(gzip, conflicting);
    bool rejected = false;
    try { PredictionTraceArchive.Import(temporary); }
    catch (InvalidDataException ex) when (ex.Message.Contains("Immutable outcome conflict")) { rejected = true; }
    Check(rejected, "Real immutable outcome conflicts must still be rejected");
    Check(PredictionTraceService.GetLiveOutcome("2026280") is { ActualNumber: "15" },
        "Rejected import must not overwrite the frozen original outcome");

}
finally
{
    if (File.Exists(temporary)) File.Delete(temporary);
}
Console.WriteLine("PASS original 280/281 trace recovery, settlement, immutability, and idempotence");
