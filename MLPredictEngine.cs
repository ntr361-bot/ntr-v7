namespace 六合分析软件;

public sealed class MLPredictionOutput
{
    public MlModelKind Model { get; init; }
    public Dictionary<string, double> Probabilities { get; init; } = new();
    public List<string> Top3 { get; init; } = new();
    public List<string> Top6 { get; init; } = new();
}

public static class MLPredictEngine
{
    public static MLPredictionOutput Predict(IReadOnlyList<DatabaseHelper.HistoryRecord> history,
        MlModelKind model = MlModelKind.LightGbmStyle)
    {
        var probabilities = MachineLearningPredictionService.Predict(history, 30, model)
            .ToDictionary(x => x.Zodiac, x => x.Probability);
        var ranked = probabilities.OrderByDescending(x => x.Value).ThenBy(x => x.Key).Select(x => x.Key).ToList();
        return new MLPredictionOutput { Model = model, Probabilities = probabilities, Top3 = ranked.Take(3).ToList(), Top6 = ranked.Take(6).ToList() };
    }
}
