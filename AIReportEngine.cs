using System.Text;

namespace 六合分析软件;

public sealed class AIAnalysisReport
{
    public DateTime GeneratedAt { get; init; } = DateTime.Now;
    public bool IsPrediction => false;
    public List<string> Items { get; init; } = new();
    public string Text { get; init; } = "";
}

/// <summary>
/// V7 AI解释层。它只解释已经产生的特征、模型和波色结果，不重新预测。
/// </summary>
public static class AIReportEngine
{
    public static AIAnalysisReport Generate(
        IReadOnlyList<DatabaseHelper.HistoryRecord> history,
        IReadOnlyList<V7PredictionResult> engines,
        ColorPredictionResult color,
        MLPredictionOutput? ml = null)
    {
        var features = FeatureEngine.BuildFeatures(history);
        var items = new List<string>();
        int active = features.Count(x => x.ShortCycleRepeatCount >= 2);
        if (active >= 2) items.Add("当前进入短周期频率窗口：多个生肖出现隔期重复信号。");
        else items.Add("当前短周期频率窗口不明显，重复信号处于普通水平。");

        var forbidden = features.Where(x => x.ShortForbidden).Select(x => x.Zodiac).ToList();
        items.Add(forbidden.Count == 0
            ? "本期没有生肖触发最近5期排除条件。"
            : $"以下生肖触发最近5期排除条件：{string.Join("、", forbidden)}。预测时应移出候选池。");

        string colorState = color.Probabilities[color.Main] - color.Probabilities[color.Defense] < 0.1
            ? "波色处于转换阶段，主波色与防波色差距较小。"
            : $"波色信号相对集中：{color.Main}为主波色，{color.Defense}为防波色，{color.Excluded}为排除波色。";
        items.Add(colorState);

        var builder = new StringBuilder("本期分析：");
        foreach (var item in items) builder.AppendLine().Append(items.IndexOf(item) + 1).Append(". ").Append(item);
        return new AIAnalysisReport { Items = items, Text = builder.ToString() };
    }
}
