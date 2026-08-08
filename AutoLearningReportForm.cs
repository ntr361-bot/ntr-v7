using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace 六合分析软件;

public sealed record AutoLearningReportData(
    string Status,
    int SampleCount,
    double Top3Rate,
    double Top6Rate,
    double Mrr,
    int MaximumTop6MissStreak,
    ModelWeights Weights,
    IReadOnlyList<KeyValuePair<string,double>> TopFeatures,
    IReadOnlyList<LearningAdjustmentRecord> RecentAdjustments)
{
    public static AutoLearningReportData Load()
    {
        ModelMemoryState memory = new ModelMemory().LoadOrCreate();
        bool[] top3 = memory.RecentTop3.TakeLast(100).ToArray();
        bool[] top6 = memory.RecentTop6.TakeLast(100).ToArray();
        double[] ranks = memory.RecentReciprocalRanks.TakeLast(100).ToArray();
        int current = 0, maximum = 0;
        foreach (bool hit in top6)
        {
            current = hit ? 0 : current+1;
            maximum = Math.Max(maximum, current);
        }
        string status = memory.LearnedSamples < 100 ? "样本不足，继续使用原预测排序"
            : memory.ConsecutiveTop3Misses >= 5 || memory.ConsecutiveTop6Misses >= 3 ? "已执行连续未命中失效分析"
            : "自动学习正常";
        return new AutoLearningReportData(status, memory.LearnedSamples,
            top3.Length == 0 ? 0 : top3.Count(value => value)/(double)top3.Length,
            top6.Length == 0 ? 0 : top6.Count(value => value)/(double)top6.Length,
            ranks.Length == 0 ? 0 : ranks.Average(), maximum, memory.Weights,
            memory.FeatureContributions.OrderByDescending(item => item.Value).Take(3).ToArray(),
            memory.RecentAdjustments.TakeLast(10).Reverse().ToArray());
    }
}

public sealed class AutoLearningReportForm : Form
{
    public AutoLearningReportData ReportData { get; }

    public AutoLearningReportForm() : this(AutoLearningReportData.Load()) { }

    public AutoLearningReportForm(AutoLearningReportData data)
    {
        ReportData = data;
        Text = "自动学习报告";
        Size = new Size(820, 650);
        MinimumSize = new Size(720, 520);
        StartPosition = FormStartPosition.CenterParent;
        var title = new Label
        {
            Dock = DockStyle.Top,
            Height = 58,
            Padding = new Padding(18, 0, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.FromArgb(30, 30, 46),
            ForeColor = Color.White,
            Font = new Font("微软雅黑", 16, FontStyle.Bold),
            Text = "自动学习报告（只读）"
        };
        var content = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.White,
            BorderStyle = BorderStyle.None,
            Font = new Font("微软雅黑", 11),
            Text = Format(data)
        };
        Controls.Add(content);
        Controls.Add(title);
    }

    private static string Format(AutoLearningReportData data)
    {
        var text = new StringBuilder();
        text.AppendLine($"当前状态：{data.Status}");
        text.AppendLine($"已学习样本：{data.SampleCount}");
        text.AppendLine();
        text.AppendLine("最近100期表现");
        text.AppendLine($"TOP3：{data.Top3Rate:P1}");
        text.AppendLine($"TOP6：{data.Top6Rate:P1}");
        text.AppendLine($"MRR：{data.Mrr:F3}");
        text.AppendLine($"TOP6最大连续未命中：{data.MaximumTop6MissStreak}期");
        text.AppendLine();
        text.AppendLine("当前基础模型权重");
        text.AppendLine($"AI：{data.Weights.AI:P1}    ML：{data.Weights.ML:P1}    状态：{data.Weights.State:P1}    规则：{data.Weights.Rule:P1}");
        text.AppendLine();
        text.AppendLine("最高贡献特征");
        if (data.TopFeatures.Count == 0) text.AppendLine("样本不足，尚无可靠特征贡献排名");
        foreach (var (feature, contribution) in data.TopFeatures)
            text.AppendLine($"{feature}：{contribution:P1}");
        text.AppendLine();
        text.AppendLine("最近调整");
        if (data.RecentAdjustments.Count == 0) text.AppendLine("暂无自动降权记录");
        foreach (LearningAdjustmentRecord adjustment in data.RecentAdjustments)
            text.AppendLine($"{adjustment.AdjustedAt:yyyy-MM-dd HH:mm}  第{adjustment.Issue}期  {adjustment.Reason}");
        return text.ToString();
    }
}
