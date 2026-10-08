using System.Text.Json;
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
Check(PredictionTraceArchive.Import(archivePath) == 2,
    "Two original Live traces must be restored from authenticated action artifacts");
Check(PredictionTraceService.GetLive("2026280") is not null,
    "Original 2026280 Live trace not restored");
Check(PredictionTraceService.GetLive("2026281") is not null,
    "Original 2026281 Live trace not restored");
Check(PredictionTraceService.GetLiveOutcome("2026280") is null,
    "280 outcome must not be fabricated before settlement-only reconciliation");

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
Check(settled >= 6, "280 settlement was not synchronized");
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
Check(PredictionTraceService.GetLiveOutcome("2026281") is null,
    "281 not-yet-drawn trace was incorrectly settled");
Check(PublishedSettlementReconciliation.Apply(dailyDirectory) == 0,
    "Settlement reconciliation is not idempotent");

string temporary = Path.Combine(Path.GetTempPath(), "v7-trace-smoke-" +
    Guid.NewGuid().ToString("N") + ".json.gz");
try
{
    Check(PredictionTraceArchive.Export(temporary) == 2,
        "Both immutable traces must survive archive export");
    Check(PredictionTraceArchive.Import(temporary) == 0,
        "Duplicate import must not create additional traces/outcomes");
}
finally
{
    if (File.Exists(temporary)) File.Delete(temporary);
}
Console.WriteLine("PASS original 280/281 trace recovery, settlement, immutability, and idempotence");
