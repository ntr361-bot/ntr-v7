using System.Data.SQLite;
using System.Text.Json;

namespace 六合分析软件;

public static class CandidateStage2Ids
{
    public const string MlLgb = "Candidate-ML-LGB";
    public const string MlXgb = "Candidate-ML-XGB";
    public const string Ranking = "Candidate-Ranking";
    public const string V7S = "Candidate-V7-S";
    public const string V7M = "Candidate-V7-M";
    public const string V7L = "Candidate-V7-L";
}

public sealed class CandidateStage2ReplayEngine
{
    private static readonly string[] Zodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
    public const int FixedWarmup = 100;

    public (IReadOnlyList<CandidateSnapshot> Candidates, IReadOnlyList<ReplayPredictionSnapshot> Controls, string ExperimentId, string StorePath) Run(
        IReadOnlyList<DatabaseHelper.HistoryRecord> source, string storePath, CancellationToken cancellationToken = default)
    {
        string experimentId = "candidate-stage2-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        var history = AutoLearningTrainer.Normalize(source).Where(x => !string.IsNullOrWhiteSpace(x.SpecialZodiac)).ToArray();
        var candidates = new List<CandidateSnapshot>(); var controls = new List<ReplayPredictionSnapshot>();
        foreach (int index in Enumerable.Range(FixedWarmup, Math.Max(0, history.Length - FixedWarmup)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actual = history[index]; var prior = history[index - 1]; var prefix = history.Take(index).ToArray();
            if (long.Parse(prior.Period) >= long.Parse(actual.Period)) throw new InvalidDataException("Candidate replay cutoff invalid");
            using (DatabaseHelper.UseHistoryThroughIssue(long.Parse(prior.Period)))
            {
                var bases = V65ExperimentPipeline.RunBaseModels(prefix, actual.Period);
                foreach (var b in bases) controls.Add(Control(experimentId, actual, prior, prefix.Length, ModelId(b.AnalysisPeriods), b.Result.AllScores));
                controls.Add(BaseAverage(experimentId, actual, prior, prefix.Length, bases));
                var state = MarketStateEngine.Detect(prefix);
                AddMl(candidates, experimentId, actual, prior, prefix, state, MachineLearningPredictionService.Predict(prefix, 30, MlModelKind.LightGbmStyle), CandidateStage2Ids.MlLgb);
                AddMl(candidates, experimentId, actual, prior, prefix, state, MachineLearningPredictionService.Predict(prefix, 30, MlModelKind.XgBoostStyle), CandidateStage2Ids.MlXgb);
                AddRanking(candidates, experimentId, actual, prior, prefix, state, ZodiacRankingEngine.Predict(prefix, int.MaxValue, 30));
                AddV7(candidates, experimentId, actual, prior, prefix, state, ShortTermEngine.Predict(prefix), CandidateStage2Ids.V7S);
                AddV7(candidates, experimentId, actual, prior, prefix, state, MediumTermEngine.Predict(prefix), CandidateStage2Ids.V7M);
                AddV7(candidates, experimentId, actual, prior, prefix, state, LongTermEngine.Predict(prefix), CandidateStage2Ids.V7L);
            }
        }
        CandidateStage2Store.Save(storePath, experimentId, candidates, controls);
        return (candidates, controls, experimentId, storePath);
    }

    private static void AddMl(List<CandidateSnapshot> output, string run, DatabaseHelper.HistoryRecord actual, DatabaseHelper.HistoryRecord prior, IReadOnlyList<DatabaseHelper.HistoryRecord> prefix, MarketStateResult state, IReadOnlyList<MlZodiacProbability> rows, string id)
        => Add(output, run, actual, prior, prefix, state, id, rows.OrderByDescending(x => x.Probability).ThenBy(x => x.Zodiac).Select(x => x.Zodiac).ToArray(), rows.ToDictionary(x => x.Zodiac, x => x.Probability), false, 30);
    private static void AddRanking(List<CandidateSnapshot> output, string run, DatabaseHelper.HistoryRecord actual, DatabaseHelper.HistoryRecord prior, IReadOnlyList<DatabaseHelper.HistoryRecord> prefix, MarketStateResult state, ZodiacRankingResult result)
        => Add(output, run, actual, prior, prefix, state, CandidateStage2Ids.Ranking, result.Items.OrderBy(x => x.Rank).Select(x => x.Zodiac).ToArray(), result.Items.ToDictionary(x => x.Zodiac, x => x.Score), false, result.TrainingTargets);
    private static void AddV7(List<CandidateSnapshot> output, string run, DatabaseHelper.HistoryRecord actual, DatabaseHelper.HistoryRecord prior, IReadOnlyList<DatabaseHelper.HistoryRecord> prefix, MarketStateResult state, V7PredictionResult result, string id)
        => Add(output, run, actual, prior, prefix, state, id, result.Probabilities.OrderByDescending(x => x.Value).ThenBy(x => x.Key).Select(x => x.Key).ToArray(), result.Probabilities, result.Probabilities.Count != 12, null);
    private static void Add(List<CandidateSnapshot> output, string run, DatabaseHelper.HistoryRecord actual, DatabaseHelper.HistoryRecord prior, IReadOnlyList<DatabaseHelper.HistoryRecord> prefix, MarketStateResult state, string id, IReadOnlyList<string> ranking, IReadOnlyDictionary<string, double> scores, bool incomplete, int? training)
    {
        int rank = ranking.IndexOf(actual.SpecialZodiac) + 1; bool valid = rank > 0;
        output.Add(new(run, id, actual.Period, prior.Period, prefix.Count, ranking, scores, incomplete, actual.SpecialZodiac, valid ? rank : null, rank == 1, rank is > 0 and <= 3, rank is > 0 and <= 6, state.PrimaryState.ToString(), state.Confidence, JsonSerializer.Serialize(state), true, prior.Period, prior.Period, training));
    }
    private static ReplayPredictionSnapshot Control(string run, DatabaseHelper.HistoryRecord actual, DatabaseHelper.HistoryRecord prior, int count, string id, IReadOnlyList<V65RuleScoringEngine.ZodiacScoreV2> scores) { var r = scores.OrderByDescending(x => x.TotalScore).ThenBy(x => x.Zodiac).ToArray(); return Scored(run, actual, prior, count, id, r.Select(x => x.Zodiac).ToArray(), r.Select(x => x.TotalScore).ToArray()); }
    private static ReplayPredictionSnapshot BaseAverage(string run, DatabaseHelper.HistoryRecord actual, DatabaseHelper.HistoryRecord prior, int count, IReadOnlyList<V65ExperimentPipeline.BaseModelPrediction> models) { var maps = models.Select(x => x.Result.AllScores.ToDictionary(y => y.Zodiac, y => y.TotalScore)).ToArray(); var r = maps[0].Keys.Select(z => (z, s: maps.Select(m => Normalize(m[z], m.Values)).Average())).OrderByDescending(x => x.s).ThenBy(x => x.z).ToArray(); return Scored(run, actual, prior, count, HistoricalReplayModelIds.BaseAverage, r.Select(x => x.z).ToArray(), r.Select(x => x.s).ToArray()); }
    private static ReplayPredictionSnapshot Scored(string run, DatabaseHelper.HistoryRecord actual, DatabaseHelper.HistoryRecord prior, int count, string id, IReadOnlyList<string> ranking, IReadOnlyList<double> scores) { int rank = ranking.IndexOf(actual.SpecialZodiac) + 1; return new(run, actual.Period, id, id, prior.Period, count, ranking, scores, actual.SpecialZodiac, rank, rank == 1, rank <= 3, rank <= 6, rank > 0 ? 1d / rank : null); }
    private static string ModelId(int p) => p switch { 50 => HistoricalReplayModelIds.Period50, 100 => HistoricalReplayModelIds.Period100, _ => HistoricalReplayModelIds.AllHistory };
    private static double Normalize(double v, IEnumerable<double> all) { double min = all.Min(), max = all.Max(); return max - min < 1e-12 ? .5 : (v - min) / (max - min); }
}

internal static class CandidateListExtensions { public static int IndexOf(this IReadOnlyList<string> list, string value) => Array.IndexOf(list.ToArray(), value); }

public static class CandidateStage2Store
{
    public static void Save(string path, string run, IReadOnlyList<CandidateSnapshot> candidates, IReadOnlyList<ReplayPredictionSnapshot> controls)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); using var c = new SQLiteConnection($"Data Source={path};Version=3;"); c.Open();
        using var schema = new SQLiteCommand("CREATE TABLE IF NOT EXISTS CandidateExperimentRuns (RunId TEXT PRIMARY KEY, CreatedAt TEXT); CREATE TABLE IF NOT EXISTS CandidateSnapshots (RunId TEXT, CandidateId TEXT, TargetIssue TEXT, Cutoff TEXT, SampleCount INTEGER, RankingJson TEXT, ScoresJson TEXT, Incomplete INTEGER, ActualZodiac TEXT, ActualRank INTEGER, Top1 INTEGER, Top3 INTEGER, Top6 INTEGER, State TEXT, StateConfidence REAL, StateJson TEXT, LeakageSafe INTEGER); CREATE TABLE IF NOT EXISTS CandidateControls (RunId TEXT, ModelId TEXT, TargetIssue TEXT, RankingJson TEXT, ActualRank INTEGER, Top3 INTEGER, Top6 INTEGER);", c); schema.ExecuteNonQuery();
        using var tx = c.BeginTransaction(); using var runCmd = new SQLiteCommand("INSERT OR REPLACE INTO CandidateExperimentRuns VALUES (@r,@t)", c, tx); runCmd.Parameters.AddWithValue("@r", run); runCmd.Parameters.AddWithValue("@t", DateTimeOffset.UtcNow.ToString("O")); runCmd.ExecuteNonQuery();
        foreach (var x in candidates) { using var q = new SQLiteCommand("INSERT INTO CandidateSnapshots VALUES (@r,@c,@i,@cut,@n,@rank,@score,@inc,@a,@ar,@t1,@t3,@t6,@s,@sc,@sj,@l)", c, tx); q.Parameters.AddWithValue("@r",run);q.Parameters.AddWithValue("@c",x.CandidateId);q.Parameters.AddWithValue("@i",x.TargetIssue);q.Parameters.AddWithValue("@cut",x.HistoryCutoffIssue);q.Parameters.AddWithValue("@n",x.HistorySampleCount);q.Parameters.AddWithValue("@rank",x.RankingJson);q.Parameters.AddWithValue("@score",x.ScoresJson);q.Parameters.AddWithValue("@inc",x.IncompleteRanking?1:0);q.Parameters.AddWithValue("@a",x.ActualZodiac);q.Parameters.AddWithValue("@ar",x.ActualRank);q.Parameters.AddWithValue("@t1",x.Top1Hit?1:0);q.Parameters.AddWithValue("@t3",x.Top3Hit?1:0);q.Parameters.AddWithValue("@t6",x.Top6Hit?1:0);q.Parameters.AddWithValue("@s",x.MarketState);q.Parameters.AddWithValue("@sc",x.StateConfidence);q.Parameters.AddWithValue("@sj",x.StateJson);q.Parameters.AddWithValue("@l",x.LeakageAuditPassed?1:0);q.ExecuteNonQuery(); }
        foreach (var x in controls) { using var q = new SQLiteCommand("INSERT INTO CandidateControls VALUES (@r,@m,@i,@j,@a,@t3,@t6)", c, tx); q.Parameters.AddWithValue("@r",run);q.Parameters.AddWithValue("@m",x.ModelId);q.Parameters.AddWithValue("@i",x.TargetIssue);q.Parameters.AddWithValue("@j",x.RankingJson);q.Parameters.AddWithValue("@a",x.ActualRank);q.Parameters.AddWithValue("@t3",x.Top3Hit);q.Parameters.AddWithValue("@t6",x.Top6Hit);q.ExecuteNonQuery(); }
        tx.Commit();
    }
}
