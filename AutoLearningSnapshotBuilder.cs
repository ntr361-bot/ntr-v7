using System.Text.Json;

namespace 六合分析软件;

public sealed record AutoLearningSnapshot(
    MetaPredictionInput Input,
    IReadOnlyList<string> BaselineRanking,
    MetaPredictionResult Result,
    ModelWeights Weights)
{
    public string FinalRankingJson => JsonSerializer.Serialize(Result.Ranking.Select(item => item.Zodiac));
    public string BaseModelScoresJson => JsonSerializer.Serialize(Input.Zodiacs.ToDictionary(item => item.Zodiac, item => item.BaseScores));
    public string FeatureSnapshotJson => JsonSerializer.Serialize(Input);
    public string WeightSnapshotJson => JsonSerializer.Serialize(Weights);
}

public static class AutoLearningSnapshotBuilder
{
    public static AutoLearningSnapshot BuildFromBasePredictions(string issue,
        IReadOnlyList<DatabaseHelper.PredictionRecord> records, ModelMemoryState memory)
    {
        var baseRows = records.Where(record => record.Issue == issue &&
                record.ModelVersion == "V6.5" &&
                ExperimentModels.AllKeys.Take(3).Contains(ExperimentModels.ForPeriods(record.AnalysisPeriods)))
            .GroupBy(record => ExperimentModels.ForPeriods(record.AnalysisPeriods))
            .ToDictionary(group => group.Key, group => group.Single());
        if (baseRows.Count != 3)
            throw new InvalidDataException("自动学习需要同一期完整的50期、100期和全历史预测快照");

        var rankings = baseRows.ToDictionary(pair => pair.Key, pair =>
            JsonSerializer.Deserialize<string[]>(pair.Value.FinalRankingJson) ?? Array.Empty<string>());
        if (rankings.Values.Any(ranking => ranking.Length != 12 || ranking.Distinct().Count() != 12))
            throw new InvalidDataException("基础模型快照缺少完整12生肖排序");

        string[] baseline = rankings[ExperimentModels.AllHistory];
        var rows = baseline.Select(zodiac =>
        {
            int r50 = Array.IndexOf(rankings[ExperimentModels.Period50], zodiac) + 1;
            int r100 = Array.IndexOf(rankings[ExperimentModels.Period100], zodiac) + 1;
            int rall = Array.IndexOf(rankings[ExperimentModels.AllHistory], zodiac) + 1;
            double consensus = new[] { r50, r100, rall }.Distinct().Count() == 1 ? 1 :
                new[] { r50, r100, rall }.GroupBy(x => x).Max(x => x.Count()) >= 2 ? .5 : 0;
            return new ZodiacMetaFeatures(zodiac, new Dictionary<string, double>
            {
                ["AI"] = (13-r50)/12d,
                ["ML"] = (13-r100)/12d,
                ["State"] = (13-rall)/12d,
                ["Rule"] = consensus
            }, new Dictionary<string, double> { ["model_consensus"] = consensus });
        }).ToArray();
        var input = new MetaPredictionInput(issue, rows);
        var result = new MetaPredictionEngine().Predict(input, memory, baseline);
        return new AutoLearningSnapshot(input, baseline, result, memory.Weights);
    }

    public static AutoLearningSnapshot Build(AIEngine.PredictResult prediction, ModelMemoryState memory)
    {
        var history = DatabaseHelper.GetHistory()
            .Where(item => !string.IsNullOrWhiteSpace(item.SpecialZodiac))
            .OrderBy(item => long.TryParse(item.Period, out long issue) ? issue : long.MaxValue)
            .ToList();
        var features = FeatureEngine.BuildFeatures(history).ToDictionary(item => item.Zodiac);
        var ml = MachineLearningPredictionService.Predict(history).ToDictionary(item => item.Zodiac, item => item.Probability);
        var state = MarketStateEngine.Detect(history);
        var ai = prediction.AllScores.ToDictionary(item => item.Zodiac, item => item.TotalScore);
        var baseline = prediction.AllScores.OrderByDescending(item => item.TotalScore).Select(item => item.Zodiac).ToArray();

        var rows = new List<ZodiacMetaFeatures>(baseline.Length);
        foreach (string zodiac in baseline)
        {
            ZodiacFeature feature = features[zodiac];
            double stateScore = state.PrimaryState switch
            {
                MarketStateKind.ShortCycleRepeat => Scale(feature.ShortCycleRepeatCount + feature.RepeatFrequencyTrend, 0, 8),
                MarketStateKind.HotColdTransition => Scale(feature.Momentum5Vs20 + feature.Momentum10Vs50, -0.3, 0.3),
                MarketStateKind.OmissionRelease => Scale(feature.OmissionRatio, 0, 3),
                _ => Scale(feature.HistoricalRate, 0, 0.2)
            };
            double ruleScore = 0.45*Scale(feature.Recent20Rate, 0, 0.25)
                + 0.30*Scale(feature.OmissionRatio, 0, 3)
                + 0.25*Scale(feature.Momentum10Vs50, -0.2, 0.2);
            var baseScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["AI"] = ai.GetValueOrDefault(zodiac),
                ["ML"] = ml.GetValueOrDefault(zodiac, 1d/12),
                ["State"] = stateScore,
                ["Rule"] = ruleScore
            };
            var groups = BuildGroups(feature, state.Confidence);
            rows.Add(new ZodiacMetaFeatures(zodiac, baseScores, groups));
        }

        AddConsensus(rows);
        var input = new MetaPredictionInput(prediction.PredictPeriod, rows);
        var result = new MetaPredictionEngine().Predict(input, memory, baseline);
        return new AutoLearningSnapshot(input, baseline, result, memory.Weights);
    }

    public static Dictionary<string, double> BuildGroups(ZodiacFeature feature, double stateConfidence)
    {
        return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["frequency"] = Clip(feature.Recent20Rate*6 - 0.5),
            ["omission"] = Clip((feature.OmissionRatio-1)/2),
            ["cycle"] = Clip(feature.LongXShortTrend*4),
            ["momentum"] = Clip((feature.Momentum5Vs20+feature.Momentum10Vs50)*4),
            ["repeat"] = Clip(feature.RepeatFrequencyTrend/3),
            ["trend"] = Clip(feature.Momentum20Vs100*5),
            ["market_state"] = Clip((stateConfidence-0.25)*1.5),
            ["model_consensus"] = 0
        };
    }

    private static void AddConsensus(IReadOnlyList<ZodiacMetaFeatures> rows)
    {
        string[] sources = { "AI", "ML", "State", "Rule" };
        var ranks = sources.ToDictionary(source => source,
            source => rows.OrderByDescending(row => row.BaseScores[source]).Select((row,index) => (row.Zodiac,index))
                .ToDictionary(item => item.Zodiac, item => item.index+1));
        foreach (ZodiacMetaFeatures row in rows)
        {
            double[] values = sources.Select(source => (double)ranks[source][row.Zodiac]).ToArray();
            double variance = values.Select(value => Math.Pow(value-values.Average(),2)).Average();
            if (row.FeatureGroups is Dictionary<string,double> writable)
                writable["model_consensus"] = Clip(1-Math.Sqrt(variance)/6);
        }
    }

    private static double Scale(double value, double min, double max) =>
        max <= min ? 0.5 : Math.Clamp((value-min)/(max-min), 0, 1);
    private static double Clip(double value) => Math.Clamp(double.IsFinite(value) ? value : 0, -1, 1);
}
