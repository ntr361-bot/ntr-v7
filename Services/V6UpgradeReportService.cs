using System;
using System.Linq;
using System.Text;

namespace 六合分析软件
{
    public static class V6UpgradeReportService
    {
        public static string GenerateReport(int totalPeriods = 500)
        {
            var history = WeightOptimizationService.GetValidHistoryOldToNew(totalPeriods);
            var sb = new StringBuilder();

            sb.AppendLine("六合分析软件 v5 -> v6 真实回测报告");
            sb.AppendLine($"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("数据来源：history.db 真实历史特码生肖");
            sb.AppendLine($"读取期数：{history.Count}");
            sb.AppendLine("说明：本报告只读取历史数据，不修改开奖记录。");
            sb.AppendLine();

            if (history.Count < 350)
            {
                sb.AppendLine("历史数据不足：至少需要350期才能完成300期训练 + 50期测试。");
                return sb.ToString();
            }

            var v5Weights = new V65RuleScoringEngine.WeightConfig
            {
                FrequencyWeight = 0.20,
                RecentTrendWeight = 0.20,
                OmissionWeight = 0.20,
                HotColdWeight = 0,
                PeriodPatternWeight = 0.26,
                ConsecutiveWeight = 0.14
            };

            int testPeriods = Math.Min(200, history.Count - 300);
            var v5 = WeightOptimizationService.EvaluateWeights(history, 300, testPeriods, v5Weights);
            var best = WeightOptimizationService.FindBestWeights(history, 300, Math.Min(50, history.Count - 300));
            var v6 = WeightOptimizationService.EvaluateWeights(history, 300, testPeriods, best.Weights);
            var rolling = RollingBacktestService.Run(totalPeriods);
            var competition = ModelCompetitionService.RunCompetition(totalPeriods);

            sb.AppendLine("最佳模型参数：");
            sb.AppendLine($"频率：{best.Weights.FrequencyWeight:P0}");
            sb.AppendLine($"趋势：{best.Weights.RecentTrendWeight:P0}");
            sb.AppendLine($"遗漏：{best.Weights.OmissionWeight:P0}");
            sb.AppendLine($"模式：{(best.Weights.PeriodPatternWeight + best.Weights.ConsecutiveWeight):P0}");
            sb.AppendLine($"组合数量：{best.TestedCombinations}");
            sb.AppendLine($"训练综合评分：{best.CombinedScore:F2}");
            sb.AppendLine();

            sb.AppendLine("v5 vs v6 对比：");
            AppendScore(sb, "升级前 v5", v5);
            AppendScore(sb, "升级后 v6", v6);
            double top3Lift = v5.Top3HitRate > 0 ? (v6.Top3HitRate - v5.Top3HitRate) / v5.Top3HitRate * 100 : 0;
            double top6Lift = v5.Top6HitRate > 0 ? (v6.Top6HitRate - v5.Top6HitRate) / v5.Top6HitRate * 100 : 0;
            sb.AppendLine($"Top3提升比例：{top3Lift:F2}%");
            sb.AppendLine($"Top6提升比例：{top6Lift:F2}%");
            sb.AppendLine($"测试周期：第301期到第{300 + testPeriods}期滚动验证样本");
            sb.AppendLine();

            sb.AppendLine("滚动验证结果：");
            foreach (var window in rolling.Windows)
                sb.AppendLine($"窗口{window.WindowIndex}：训练1-{window.TrainPeriods}期，测试{window.TestStartIndex}-{window.TestEndIndex}期，Top3 {window.Score.Top3HitRate:F2}%，Top6 {window.Score.Top6HitRate:F2}%");
            sb.AppendLine($"平均Top3：{rolling.AverageTop3HitRate:F2}%");
            sb.AppendLine($"平均Top6：{rolling.AverageTop6HitRate:F2}%");
            sb.AppendLine($"稳定性：{rolling.StabilityGrade}级");
            sb.AppendLine();

            sb.AppendLine("模型竞争排名：");
            int rank = 1;
            foreach (var model in competition.Take(6))
                sb.AppendLine($"{rank++}. {model.ModelName} 综合评分 {model.CombinedScore:F2}，Top3 {model.Top3HitRate:F2}%，Top6 {model.Top6HitRate:F2}%");

            return sb.ToString();
        }

        private static void AppendScore(StringBuilder sb, string title, ModelScoreResult score)
        {
            sb.AppendLine($"{title}：Top3 {score.Top3HitRate:F2}% ({score.Top3Hits}/{score.TotalTests})，Top6 {score.Top6HitRate:F2}% ({score.Top6Hits}/{score.TotalTests})，连续命中{score.MaxConsecutiveHits}，连续遗漏{score.MaxConsecutiveMisses}，稳定性{score.StabilityScore:F2}，综合评分{score.CombinedScore:F2}");
        }
    }
}
