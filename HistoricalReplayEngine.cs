using System.Text.Json;

namespace 六合分析软件;

public sealed class HistoricalReplayEngine
{
    public const string RequestedFrozenCommit = "2d5a8318cc60ce476dab0d9a067d66b3ee21dcff";

    public HistoricalReplayResult Run(IReadOnlyList<DatabaseHelper.HistoryRecord> source,
        HistoricalReplayOptions? requestedOptions = null,
        CancellationToken cancellationToken = default)
    {
        HistoricalReplayOptions options = (requestedOptions ?? new()).Validate();
        List<DatabaseHelper.HistoryRecord> history = AutoLearningTrainer.Normalize(source)
            .Where(row => !string.IsNullOrWhiteSpace(row.Period) && !string.IsNullOrWhiteSpace(row.SpecialZodiac))
            .ToList();
        if (history.Count <= options.MinimumWarmupSamples)
            throw new InvalidOperationException("真实历史少于统一 warm-up，不能开始实验。");

        var predictions = new List<ReplayPredictionSnapshot>();
        var findings = new List<ReplayLeakageFinding>();
        var state = new AutoLearningV2State();
        var random = new Random(options.RandomSeed);
        string executionCommit = ResolveExecutionCommit();
        string experimentId = options.ExperimentId!;
        foreach (int index in Enumerable.Range(options.MinimumWarmupSamples, history.Count - options.MinimumWarmupSamples))
        {
            cancellationToken.ThrowIfCancellationRequested();
            DatabaseHelper.HistoryRecord actual = history[index];
            DatabaseHelper.HistoryRecord prior = history[index - 1];
            long targetIssue = ParseIssue(actual.Period);
            long priorIssue = ParseIssue(prior.Period);
            var prefix = history.Take(index).ToArray();
            if (prefix.Any(row => ParseIssue(row.Period) >= targetIssue))
            {
                findings.Add(new(actual.Period, "History", prefix.Max(row => row.Period)));
                throw new InvalidDataException($"检测到未来数据泄漏：{actual.Period}");
            }

            using (DatabaseHelper.UseHistoryThroughIssue(priorIssue))
            {
                IReadOnlyList<V65ExperimentPipeline.BaseModelPrediction> baseModels =
                    V65ExperimentPipeline.RunBaseModels(prefix, actual.Period);
                var byId = baseModels.ToDictionary(model => ModelId(model.AnalysisPeriods));
                foreach (V65ExperimentPipeline.BaseModelPrediction model in baseModels)
                    predictions.Add(CreateSnapshot(experimentId, actual, prior, prefix.Length, ModelId(model.AnalysisPeriods), model.Result.AllScores));

                predictions.Add(CreateAverageSnapshot(experimentId, actual, prior, prefix.Length, byId));
                AutoLearningV2Snapshot v2 = BuildV2Snapshot(actual.Period, baseModels, prefix, state);
                string before = JsonSerializer.Serialize(state);
                predictions.Add(CreateV2Snapshot(experimentId, actual, prior, prefix.Length, v2, before));
                state = AutoLearningV2Service.UpdateState(state, v2, actual.SpecialZodiac);
                string after = JsonSerializer.Serialize(state);
                int v2Index = predictions.FindLastIndex(row => row.ModelId == HistoricalReplayModelIds.FrozenV2 && row.TargetIssue == actual.Period);
                predictions[v2Index] = predictions[v2Index] with { StateAfterJson = after };

                predictions.Add(CreateRandomSnapshot(experimentId, actual, prior, prefix.Length, random));
            }
        }

        EvaluationPipeline.AssertTargetSetIsUniform(predictions);
        if (options.PersistSnapshots && !string.IsNullOrWhiteSpace(options.StorePath))
            ReplayExperimentStore.Save(new HistoricalReplayResult(experimentId, RequestedFrozenCommit, executionCommit,
                predictions.Select(row => row.TargetIssue).Distinct().OrderBy(ParseIssue).ToArray(), predictions, findings,
                findings.Count > 0, options.MinimumWarmupSamples), options.StorePath!);
        return new HistoricalReplayResult(experimentId, RequestedFrozenCommit, executionCommit,
            predictions.Select(row => row.TargetIssue).Distinct().OrderBy(ParseIssue).ToArray(), predictions, findings,
            findings.Count > 0, options.MinimumWarmupSamples);
    }

    private static AutoLearningV2Snapshot BuildV2Snapshot(string issue, IReadOnlyList<V65ExperimentPipeline.BaseModelPrediction> models,
        IReadOnlyList<DatabaseHelper.HistoryRecord> prefix, AutoLearningV2State state)
    {
        var traces = models.Select(model => new PredictionTraceBaseModel(
            ExperimentModels.ForPeriods(model.AnalysisPeriods), "V6.5", model.AnalysisPeriods,
            new Dictionary<string, double>(), model.Result.AllScores.OrderByDescending(row => row.TotalScore)
                .Select((row, index) => new PredictionTraceZodiac(row.Zodiac, index + 1, row.TotalScore,
                    new Dictionary<string, PredictionTraceFactor>())).ToArray())).ToArray();
        var trace = new PredictionTraceSnapshot(issue, "Replay", "replay-v1", DateTimeOffset.UtcNow, prefix[^1].Period,
            prefix.Count, "V6.5", "replay", "Complete", traces,
            new PredictionTraceAutoLearning(Array.Empty<PredictionTraceAutoZodiac>(), new Dictionary<string, double>(),
                new Dictionary<string, double>(), false, ""));
        var observed = prefix.TakeLast(Math.Min(20, prefix.Count)).ToArray();
        var history = new AutoLearningV2HistoryFeatures(prefix.Count, observed.Length, 0, 0, 0, 0);
        return AutoLearningV2Service.BuildSnapshot(trace, history);
    }

    private static ReplayPredictionSnapshot CreateSnapshot(string run, DatabaseHelper.HistoryRecord actual,
        DatabaseHelper.HistoryRecord prior, int historySampleCount, string model, IReadOnlyList<V65RuleScoringEngine.ZodiacScoreV2> scores)
    {
        var ranking = scores.OrderByDescending(row => row.TotalScore).ThenBy(row => row.Zodiac).ToArray();
        return CreateScored(run, actual, prior, historySampleCount, model, ranking.Select(row => row.Zodiac).ToArray(), ranking.Select(row => row.TotalScore).ToArray());
    }

    private static ReplayPredictionSnapshot CreateAverageSnapshot(string run, DatabaseHelper.HistoryRecord actual,
        DatabaseHelper.HistoryRecord prior, int historySampleCount, IReadOnlyDictionary<string, V65ExperimentPipeline.BaseModelPrediction> models)
    {
        var maps = models.Values.Select(model => model.Result.AllScores.ToDictionary(row => row.Zodiac, row => row.TotalScore)).ToArray();
        var ranking = maps[0].Keys.Select(zodiac => (zodiac, score: maps.Select(map => Normalize(map[zodiac], map.Values)).Average()))
            .OrderByDescending(row => row.score).ThenBy(row => row.zodiac).ToArray();
        return CreateScored(run, actual, prior, historySampleCount, HistoricalReplayModelIds.BaseAverage, ranking.Select(row => row.zodiac).ToArray(), ranking.Select(row => row.score).ToArray());
    }

    private static ReplayPredictionSnapshot CreateV2Snapshot(string run, DatabaseHelper.HistoryRecord actual,
        DatabaseHelper.HistoryRecord prior, int historySampleCount, AutoLearningV2Snapshot v2, string before)
    {
        AutoLearningV2Zodiac actualRow = v2.Zodiacs.Single(row => row.Zodiac == actual.SpecialZodiac);
        AutoLearningV2Zodiac topRow = v2.Zodiacs.OrderBy(row => row.Rank).First();
        return CreateScored(run, actual, prior, historySampleCount, HistoricalReplayModelIds.FrozenV2,
            v2.Zodiacs.OrderBy(row => row.Rank).Select(row => row.Zodiac).ToArray(),
            v2.Zodiacs.OrderBy(row => row.Rank).Select(row => row.FinalScore).ToArray()) with
        { StateBeforeJson = before, BaseScore = actualRow.BaseScore, ResidualCorrection = actualRow.ResidualCorrection,
          ConsensusScore = topRow.ConsensusScore, JointFailureRisk = v2.JointFailureRisk, Confidence = v2.Confidence };
    }

    private static ReplayPredictionSnapshot CreateRandomSnapshot(string run, DatabaseHelper.HistoryRecord actual,
        DatabaseHelper.HistoryRecord prior, int historySampleCount, Random random)
    {
        string[] ranking = new[] { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" }
            .OrderBy(_ => random.Next()).ToArray();
        return CreateScored(run, actual, prior, historySampleCount, HistoricalReplayModelIds.Random, ranking, Enumerable.Repeat(0d, 12).ToArray());
    }

    private static ReplayPredictionSnapshot CreateScored(string run, DatabaseHelper.HistoryRecord actual,
        DatabaseHelper.HistoryRecord prior, int historySampleCount, string model, IReadOnlyList<string> ranking, IReadOnlyList<double> scores)
    {
        int rank = Array.IndexOf(ranking.ToArray(), actual.SpecialZodiac) + 1;
        return new(run, actual.Period, model, model, prior.Period, historySampleCount, ranking, scores, actual.SpecialZodiac,
            rank, rank == 1, rank <= 3, rank <= 6, rank > 0 ? 1d / rank : null);
    }

    private static double Normalize(double value, IEnumerable<double> values)
    {
        double min = values.Min(), max = values.Max();
        return max - min < 1e-12 ? .5 : (value - min) / (max - min);
    }
    private static string ModelId(int periods) => periods switch { 50 => HistoricalReplayModelIds.Period50, 100 => HistoricalReplayModelIds.Period100, _ => HistoricalReplayModelIds.AllHistory };
    private static long ParseIssue(string issue) => long.TryParse(issue, out long value) ? value : throw new InvalidDataException($"无效期号：{issue}");
    private static string ResolveExecutionCommit() => Environment.GetEnvironmentVariable("GIT_COMMIT") ?? "unknown";
}
