using System.Globalization;
using System.Text;

namespace 六合分析软件;

public static class AutoLearningV2ReportService
{
    public static string Render(AutoLearningV2EvaluationReport report, string codeVersion,
        string lambda, string decay)
    {
        StringBuilder text = new();
        text.AppendLine("# AutoLearning V2 旁路实验报告");
        text.AppendLine();
        text.AppendLine("## 实验边界");
        text.AppendLine("本报告只描述 v65-auto-v2-experiment，正式 V6.5 AutoLearning、PredictionHistory 和 ModelMemory 未被替换或写回。");
        text.AppendLine($"代码版本：{codeVersion}；lambda：{lambda}；decay：{decay}");
        text.AppendLine("结论：不自动替换正式 AutoLearning，需人工确认后才可进入影子运行。");
        text.AppendLine();
        text.AppendLine("## Walk-Forward");
        text.AppendLine($"训练样本：{report.TrainingSamples}；验证/留出样本：{report.TestSamples}；Holdout 起点：{report.HoldoutIssue}");
        text.AppendLine($"未来数据泄漏：{(report.FutureDataLeakageDetected ? "是" : "否")}");
        text.AppendLine();
        text.AppendLine("## 核心指标");
        text.AppendLine($"RescueRate：{report.RescueRate.ToString("P2", CultureInfo.InvariantCulture)}（次数 {report.RescueCount}）");
        text.AppendLine($"HarmRate：{report.HarmRate.ToString("P2", CultureInfo.InvariantCulture)}（次数 {report.HarmCount}）");
        text.AppendLine("Top1/Top3/Top6、MRR、Mean Rank、连败、滚动窗口和模型独立性应在完整实验运行后填入；当前报告不伪造历史结果。");
        return text.ToString();
    }
}
