using System;
using System.Collections.Generic;
using System.Linq;

namespace 六合分析软件
{
    /// <summary>
    /// AI预测综合评分服务
    /// 多维度评分：历史频率20分 + 冷热趋势18分 + 遗漏25分 + 生肖13分 + 周期10分 = 86分满分
    /// </summary>
    public static class PredictionScoreService
    {
        public class ScoredPrediction
        {
            public string Number { get; set; } = "";
            public string Zodiac { get; set; } = "";
            public int TotalScore { get; set; }
            public int FrequencyScore { get; set; }
            public int TrendScore { get; set; }
            public int MissingScore { get; set; }
            public int ZodiacScore { get; set; }
            public int PeriodScore { get; set; }
            public string Reason { get; set; } = "";
            public string Confidence { get; set; } = "";   // 高/中/低
        }

        public class ScoreResult
        {
            public int AnalysisPeriods { get; set; }
            public DateTime PredictTime { get; set; }
            public List<ScoredPrediction> Predictions { get; set; } = new List<ScoredPrediction>();
            public ScoredPrediction? Top1 => Predictions.Count > 0 ? Predictions[0] : null;
            public List<string> TopZodiacs => Predictions.Take(3).Select(p => p.Zodiac).ToList();
        }

        /// <summary>
        /// 综合评分预测
        /// </summary>
        public static ScoreResult Predict(int periodRange = 500)
        {
            var result = new ScoreResult
            {
                AnalysisPeriods = periodRange,
                PredictTime = DateTime.Now
            };

            // 获取基础统计数据
            var zodiacStats = ZodiacStatisticsService.Calculate(periodRange);
            var numberStats = NumberStatisticsService.Calculate(periodRange);
            var missingReport = MissingNumberService.Calculate(periodRange);

            string[] allZodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };

            foreach (var zodiac in allZodiacs)
            {
                var prediction = new ScoredPrediction { Zodiac = zodiac };

                var zStat = zodiacStats.Stats.FirstOrDefault(s => s.Zodiac == zodiac);
                if (zStat == null) continue;

                // 1. 历史频率评分（满分20）
                // 频率越高分数越高，12生肖平均频率约8.3%
                double freq = zStat.Frequency;
                if (freq >= 12) prediction.FrequencyScore = 20;
                else if (freq >= 10) prediction.FrequencyScore = 17;
                else if (freq >= 8.3) prediction.FrequencyScore = 14;
                else if (freq >= 7) prediction.FrequencyScore = 10;
                else if (freq >= 5) prediction.FrequencyScore = 6;
                else prediction.FrequencyScore = 3;

                // 2. 冷热趋势评分（满分18）
                switch (zStat.Trend)
                {
                    case ZodiacStatisticsService.TrendType.上升:
                        prediction.TrendScore = 18; break;
                    case ZodiacStatisticsService.TrendType.平稳:
                        prediction.TrendScore = 12; break;
                    case ZodiacStatisticsService.TrendType.下降:
                        prediction.TrendScore = 6; break;
                    case ZodiacStatisticsService.TrendType.冷:
                        prediction.TrendScore = 2; break;
                }
                // 近10期表现加分
                if (zStat.Recent10Count >= 3) prediction.TrendScore = Math.Min(18, prediction.TrendScore + 5);
                else if (zStat.Recent10Count >= 2) prediction.TrendScore = Math.Min(18, prediction.TrendScore + 2);

                // 3. 遗漏评分（满分25）
                var zMissing = missingReport.ZodiacMissings.FirstOrDefault(m => m.Item == zodiac);
                if (zMissing != null)
                {
                    // 遗漏接近平均值 = 最佳时机
                    double ratio = zMissing.AvgMissing > 0 ? zMissing.CurrentMissing / zMissing.AvgMissing : 1;
                    if (ratio >= 0.8 && ratio <= 1.5) prediction.MissingScore = 25;
                    else if (ratio >= 0.5 && ratio <= 2.0) prediction.MissingScore = 18;
                    else if (ratio >= 0.3 && ratio <= 3.0) prediction.MissingScore = 12;
                    else if (ratio < 0.3) prediction.MissingScore = 8;   // 刚出过，短期再出概率低
                    else prediction.MissingScore = 6;                     // 遗漏太久，冷号
                }
                else
                {
                    prediction.MissingScore = 10;
                }

                // 4. 生肖关联评分（满分13）
                // 基于历史频率的稳定性
                int appearCount = zStat.AppearCount;
                if (appearCount >= periodRange / 10) prediction.ZodiacScore = 13;
                else if (appearCount >= periodRange / 15) prediction.ZodiacScore = 9;
                else if (appearCount >= periodRange / 20) prediction.ZodiacScore = 6;
                else prediction.ZodiacScore = 3;

                // 5. 周期规律评分（满分10）
                // 基于遗漏周期性
                if (zMissing != null)
                {
                    double avg = zMissing.AvgMissing;
                    if (avg >= 5 && avg <= 12) prediction.PeriodScore = 10;     // 规律性好
                    else if (avg >= 3 && avg <= 15) prediction.PeriodScore = 7;
                    else if (avg >= 2 && avg <= 20) prediction.PeriodScore = 4;
                    else prediction.PeriodScore = 2;
                }
                else
                {
                    prediction.PeriodScore = 5;
                }

                // 总分
                prediction.TotalScore = prediction.FrequencyScore + prediction.TrendScore +
                    prediction.MissingScore + prediction.ZodiacScore + prediction.PeriodScore;

                // 置信度
                if (prediction.TotalScore >= 70) prediction.Confidence = "高";
                else if (prediction.TotalScore >= 50) prediction.Confidence = "中";
                else prediction.Confidence = "低";

                // 评分理由
                prediction.Reason = $"频率{freq:F1}% | 趋势:{zStat.TrendLabel} | " +
                    $"遗漏{zStat.CurrentMissing}期(均{zMissing?.AvgMissing ?? 0:F1}) | 近10期{zStat.Recent10Count}次";

                // 号码映射：每个生肖对应4个号码
                prediction.Number = GetZodiacNumbers(zodiac);

                result.Predictions.Add(prediction);
            }

            // 按总分降序排列
            result.Predictions = result.Predictions.OrderByDescending(p => p.TotalScore).ToList();

            return result;
        }

        /// <summary>
        /// 生成评分分析文本
        /// </summary>
        public static string GenerateAnalysisText(ScoreResult result)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("═══════════════════════════════════════════");
            sb.AppendLine("     AI 综合评分预测报告");
            sb.AppendLine("═══════════════════════════════════════════");
            sb.AppendLine($"分析周期：{result.AnalysisPeriods} 期");
            sb.AppendLine($"预测时间：{result.PredictTime:yyyy-MM-dd HH:mm}");
            sb.AppendLine();

            sb.AppendLine("── 评分维度 ──");
            sb.AppendLine("  历史频率(20) + 冷热趋势(18) + 遗漏(25) + 生肖(13) + 周期(10) = 满分86");
            sb.AppendLine();

            sb.AppendLine("── TOP 6 推荐 ──");
            for (int i = 0; i < Math.Min(6, result.Predictions.Count); i++)
            {
                var p = result.Predictions[i];
                string badge = i < 3 ? "⭐" : "  ";
                sb.AppendLine($"{badge} #{i + 1} {p.Zodiac} ({p.Number}) 综合:{p.TotalScore}分 " +
                    $"[频{p.FrequencyScore}+势{p.TrendScore}+漏{p.MissingScore}+肖{p.ZodiacScore}+周{p.PeriodScore}] " +
                    $"置信度:{p.Confidence}");
            }

            sb.AppendLine();
            sb.AppendLine("── 评分详情 ──");
            foreach (var p in result.Predictions)
            {
                sb.AppendLine($"  {p.Zodiac} {p.TotalScore}分 → {p.Reason}");
            }

            sb.AppendLine();
            sb.AppendLine("═══════════════════════════════════════════");
            return sb.ToString();
        }

        /// <summary>
        /// 获取生肖对应的号码范围
        /// </summary>
        private static string GetZodiacNumbers(string zodiac)
        {
            // 简化的生肖-号码映射
            var map = new Dictionary<string, string>
            {
                { "鼠", "01,13,25,37,49" },
                { "牛", "02,14,26,38" },
                { "虎", "03,15,27,39" },
                { "兔", "04,16,28,40" },
                { "龙", "05,17,29,41" },
                { "蛇", "06,18,30,42" },
                { "马", "07,19,31,43" },
                { "羊", "08,20,32,44" },
                { "猴", "09,21,33,45" },
                { "鸡", "10,22,34,46" },
                { "狗", "11,23,35,47" },
                { "猪", "12,24,36,48" }
            };
            return map.ContainsKey(zodiac) ? map[zodiac] : "";
        }
    }
}
