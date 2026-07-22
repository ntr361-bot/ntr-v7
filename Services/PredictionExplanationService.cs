using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace 六合分析软件
{
    public static class PredictionExplanationService
    {
        public static List<PredictionExplanation> Explain(ZodiacPredictEngineV2.PredictResultV2 result)
        {
            double maxScore = result.AllScores.Count > 0 ? result.AllScores.Max(s => s.TotalScore) : 1;
            return result.AllScores
                .OrderByDescending(s => s.TotalScore)
                .Take(6)
                .Select(s => BuildExplanation(s, maxScore))
                .ToList();
        }

        public static string BuildReport(ZodiacPredictEngineV2.PredictResultV2 result, OptimizedWeightResult? weights, RollingBacktestResult? rolling, List<ModelScoreResult>? competition)
        {
            var sb = new StringBuilder();
            sb.AppendLine("V6自动优化预测说明");
            sb.AppendLine($"分析周期：{result.AnalysisPeriods}期");
            sb.AppendLine($"预测Top3：{string.Join("、", result.Top3)}");
            sb.AppendLine($"预测Top6：{string.Join("、", result.Top6)}");
            sb.AppendLine();

            if (weights != null && weights.TotalTests > 0)
            {
                sb.AppendLine("最佳模型参数：");
                sb.AppendLine($"频率：{weights.Weights.FrequencyWeight:P0}");
                sb.AppendLine($"趋势：{weights.Weights.RecentTrendWeight:P0}");
                sb.AppendLine($"遗漏：{weights.Weights.OmissionWeight:P0}");
                sb.AppendLine($"模式：{(weights.Weights.PeriodPatternWeight + weights.Weights.ConsecutiveWeight):P0}");
                sb.AppendLine($"综合评分：{weights.CombinedScore:F1}");
                sb.AppendLine();
            }

            if (rolling != null && rolling.TotalTests > 0)
            {
                sb.AppendLine("滚动验证结果：");
                foreach (var w in rolling.Windows)
                    sb.AppendLine($"窗口{w.WindowIndex}：Top3 {w.Score.Top3HitRate:F2}% Top6 {w.Score.Top6HitRate:F2}%");
                sb.AppendLine($"平均：Top3 {rolling.AverageTop3HitRate:F2}% Top6 {rolling.AverageTop6HitRate:F2}%");
                sb.AppendLine($"稳定性：{rolling.StabilityGrade}级");
                sb.AppendLine();
            }

            if (competition != null && competition.Count > 0)
            {
                sb.AppendLine("模型排名：");
                int rank = 1;
                foreach (var model in competition.Take(6))
                    sb.AppendLine($"{rank++}. {model.ModelName} 综合评分 {model.CombinedScore:F1} Top3 {model.Top3HitRate:F2}%");
                sb.AppendLine();
            }

            sb.AppendLine("预测解释：");
            foreach (var item in Explain(result).Take(3))
            {
                sb.AppendLine($"{item.Zodiac} 可信度：{item.Confidence:F0}%");
                sb.AppendLine("支持因素：" + string.Join("；", item.SupportFactors));
                if (item.Risks.Count > 0) sb.AppendLine("风险：" + string.Join("；", item.Risks));
            }

            return sb.ToString();
        }

        private static PredictionExplanation BuildExplanation(ZodiacPredictEngineV2.ZodiacScoreV2 score, double maxScore)
        {
            var explanation = new PredictionExplanation
            {
                Zodiac = score.Zodiac,
                Confidence = maxScore > 0 ? Math.Min(95, Math.Max(35, score.TotalScore / maxScore * 88)) : 50
            };

            if (score.RecentTrendScore >= 65) explanation.SupportFactors.Add("最近走势增强");
            if (score.OmissionScore >= 70) explanation.SupportFactors.Add("遗漏周期接近历史均值");
            if (score.FrequencyScore >= 70) explanation.SupportFactors.Add("历史频率高于平均");
            if (score.PeriodPatternScore >= 60) explanation.SupportFactors.Add("历史周期规律较明显");
            if (score.EightZodiacScore > 0) explanation.SupportFactors.Add("八肖关联规则加分");
            if (explanation.SupportFactors.Count == 0) explanation.SupportFactors.Add("综合评分进入候选区间");

            if (score.Appear5 >= 2) explanation.Risks.Add("短期连续出现概率偏高");
            if (score.CurrentOmission > score.AvgOmission * 2 && score.AvgOmission > 0) explanation.Risks.Add("当前遗漏已明显偏长");
            if (score.HotColdTrend.Contains("下降")) explanation.Risks.Add("近期热度下降");
            return explanation;
        }
    }
}
