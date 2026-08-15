using System.Data.SQLite;

namespace 六合分析软件;

public static class AutoLearningV2ExperimentService
{
    public static void SaveRun(AutoLearningV2ExperimentRun run)
    {
        DatabaseHelper.InitializeDatabase();
        using SQLiteConnection connection = DatabaseHelper.GetConnection();
        using var command = new SQLiteCommand(@"INSERT OR IGNORE INTO AutoLearningV2ExperimentRun
            (RunId, ModelKey, CodeVersion, Lambda, Decay, TrainingStartIssue, TrainingEndIssue,
             ValidationEndIssue, HoldoutEndIssue, CreatedAt)
            VALUES (@id,@model,@code,@lambda,@decay,@trainStart,@trainEnd,@validation,@holdout,@created)", connection);
        command.Parameters.AddWithValue("@id", run.RunId);
        command.Parameters.AddWithValue("@model", run.ModelKey);
        command.Parameters.AddWithValue("@code", run.CodeVersion);
        command.Parameters.AddWithValue("@lambda", run.Lambda);
        command.Parameters.AddWithValue("@decay", run.Decay);
        command.Parameters.AddWithValue("@trainStart", run.TrainingStartIssue);
        command.Parameters.AddWithValue("@trainEnd", run.TrainingEndIssue);
        command.Parameters.AddWithValue("@validation", run.ValidationEndIssue);
        command.Parameters.AddWithValue("@holdout", run.HoldoutEndIssue);
        command.Parameters.AddWithValue("@created", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public static void SavePrediction(string runId, AutoLearningV2ExperimentPrediction prediction)
    {
        DatabaseHelper.InitializeDatabase();
        using SQLiteConnection connection = DatabaseHelper.GetConnection();
        using var command = new SQLiteCommand(@"INSERT OR REPLACE INTO AutoLearningV2ExperimentPrediction
            (RunId, Issue, Top6, ActualRank, BaseScore, ResidualCorrection, FinalScore, Confidence)
            VALUES (@run,@issue,@top6,@rank,@base,@residual,@final,@confidence)", connection);
        command.Parameters.AddWithValue("@run", runId);
        command.Parameters.AddWithValue("@issue", prediction.Issue);
        command.Parameters.AddWithValue("@top6", prediction.Top6);
        command.Parameters.AddWithValue("@rank", prediction.ActualRank);
        command.Parameters.AddWithValue("@base", prediction.BaseScore);
        command.Parameters.AddWithValue("@residual", prediction.ResidualCorrection);
        command.Parameters.AddWithValue("@final", prediction.FinalScore);
        command.Parameters.AddWithValue("@confidence", prediction.Confidence);
        command.ExecuteNonQuery();
    }

    public static AutoLearningV2ExperimentRun? GetRun(string runId)
    {
        DatabaseHelper.InitializeDatabase();
        using SQLiteConnection connection = DatabaseHelper.GetConnection();
        using var command = new SQLiteCommand("SELECT ModelKey, CodeVersion, Lambda, Decay, TrainingStartIssue, TrainingEndIssue, ValidationEndIssue, HoldoutEndIssue FROM AutoLearningV2ExperimentRun WHERE RunId=@id", connection);
        command.Parameters.AddWithValue("@id", runId);
        using SQLiteDataReader reader = command.ExecuteReader();
        return !reader.Read() ? null : new AutoLearningV2ExperimentRun(runId, reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7));
    }

    public static int GetPredictionCount(string runId)
    {
        DatabaseHelper.InitializeDatabase();
        using SQLiteConnection connection = DatabaseHelper.GetConnection();
        using var command = new SQLiteCommand("SELECT COUNT(*) FROM AutoLearningV2ExperimentPrediction WHERE RunId=@id", connection);
        command.Parameters.AddWithValue("@id", runId);
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
