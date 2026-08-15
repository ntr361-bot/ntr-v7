using System.Data.SQLite;

namespace 六合分析软件;

public static class ReplayExperimentStore
{
    public static void Save(HistoricalReplayResult result, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        using var connection = new SQLiteConnection($"Data Source={path};Version=3;");
        connection.Open();
        using var schema = new SQLiteCommand(@"CREATE TABLE IF NOT EXISTS ExperimentRuns (ExperimentId TEXT PRIMARY KEY, RequestedCommit TEXT, ActualCommit TEXT, Warmup INTEGER, Leakage INTEGER);
CREATE TABLE IF NOT EXISTS ReplayPredictions (ExperimentId TEXT, TargetIssue TEXT, ModelId TEXT, ModelVersion TEXT, HistoryCutoff TEXT, HistorySampleCount INTEGER, RankingJson TEXT, ScoresJson TEXT, ActualZodiac TEXT, ActualRank INTEGER, Top1Hit INTEGER, Top3Hit INTEGER, Top6Hit INTEGER, ReciprocalRank REAL, StateBeforeJson TEXT, StateAfterJson TEXT, BaseScore REAL, ResidualCorrection REAL, ConsensusScore REAL, JointFailureRisk REAL, Confidence TEXT, PRIMARY KEY (ExperimentId, TargetIssue, ModelId));
CREATE TABLE IF NOT EXISTS ReplayMetrics (ExperimentId TEXT PRIMARY KEY, PayloadJson TEXT);
CREATE TABLE IF NOT EXISTS ReplayRollingMetrics (ExperimentId TEXT, WindowSize INTEGER, PayloadJson TEXT);
CREATE TABLE IF NOT EXISTS ReplayComparisons (ExperimentId TEXT, ComparisonKey TEXT, PayloadJson TEXT);
CREATE TABLE IF NOT EXISTS ReplayStateSnapshots (ExperimentId TEXT, TargetIssue TEXT, StateBeforeJson TEXT, StateAfterJson TEXT, PRIMARY KEY (ExperimentId, TargetIssue));", connection); schema.ExecuteNonQuery();
        using var tx = connection.BeginTransaction();
        using var run = new SQLiteCommand("INSERT OR REPLACE INTO ExperimentRuns VALUES (@id,@requested,@actual,@warmup,@leakage)", connection, tx);
        run.Parameters.AddWithValue("@id", result.ExperimentId); run.Parameters.AddWithValue("@requested", result.RequestedFrozenCommit); run.Parameters.AddWithValue("@actual", result.ActualExecutionCommit); run.Parameters.AddWithValue("@warmup", result.WarmupSamples); run.Parameters.AddWithValue("@leakage", result.FutureDataLeakageDetected ? 1 : 0); run.ExecuteNonQuery();
        foreach (ReplayPredictionSnapshot row in result.Predictions)
        {
            using var command = new SQLiteCommand(@"INSERT OR REPLACE INTO ReplayPredictions VALUES (@id,@issue,@model,@version,@cutoff,@count,@ranking,@scores,@actual,@rank,@top1,@top3,@top6,@mrr,@before,@after,@base,@residual,@consensus,@risk,@confidence)", connection, tx);
            command.Parameters.AddWithValue("@id", row.ExperimentId); command.Parameters.AddWithValue("@issue", row.TargetIssue); command.Parameters.AddWithValue("@model", row.ModelId); command.Parameters.AddWithValue("@version", row.ModelVersion); command.Parameters.AddWithValue("@cutoff", row.HistoryCutoffIssue); command.Parameters.AddWithValue("@count", row.HistorySampleCount); command.Parameters.AddWithValue("@ranking", row.RankingJson); command.Parameters.AddWithValue("@scores", row.ScoresJson); command.Parameters.AddWithValue("@actual", row.ActualZodiac); command.Parameters.AddWithValue("@rank", row.ActualRank); command.Parameters.AddWithValue("@top1", row.Top1Hit); command.Parameters.AddWithValue("@top3", row.Top3Hit); command.Parameters.AddWithValue("@top6", row.Top6Hit); command.Parameters.AddWithValue("@mrr", row.ReciprocalRank); command.Parameters.AddWithValue("@before", row.StateBeforeJson); command.Parameters.AddWithValue("@after", row.StateAfterJson); command.Parameters.AddWithValue("@base", row.BaseScore); command.Parameters.AddWithValue("@residual", row.ResidualCorrection); command.Parameters.AddWithValue("@consensus", row.ConsensusScore); command.Parameters.AddWithValue("@risk", row.JointFailureRisk); command.Parameters.AddWithValue("@confidence", row.Confidence); command.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
