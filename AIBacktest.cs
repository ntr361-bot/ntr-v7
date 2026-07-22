using System;
using System.Collections.Generic;
using System.Linq;

namespace 六合分析软件
{
    /// <summary>
    /// AI预测回测模块
    /// 模拟历史预测，验证模型准确率
    /// </summary>
    public static class AIBacktest
    {
        /// <summary>
        /// 回测结果
        /// </summary>
        public class BacktestReport
        {
            public int TotalTests { get; set; }
            public int TrainPeriods { get; set; }
            public int Top1Hits { get; set; }
            public int Top3Hits { get; set; }
            public int Top6Hits { get; set; }
            public double Top1HitRate => TotalTests > 0 ? (double)Top1Hits / TotalTests * 100 : 0;
            public double Top3HitRate => TotalTests > 0 ? (double)Top3Hits / TotalTests * 100 : 0;
            public double Top6HitRate => TotalTests > 0 ? (double)Top6Hits / TotalTests * 100 : 0;
            public List<BacktestRecord> Records { get; set; } = new List<BacktestRecord>();

            // 各生肖命中统计
            public Dictionary<string, int> ZodiacHitCount { get; set; } = new Dictionary<string, int>();
        }

        public class BacktestRecord
        {
            public int TestIndex { get; set; }
            public string Period { get; set; } = "";
            public string ActualZodiac { get; set; } = "";
            public List<string> PredictedTop3 { get; set; } = new List<string>();
            public List<string> PredictedTop6 { get; set; } = new List<string>();
            public bool Top1Hit { get; set; }
            public bool Top3Hit { get; set; }
            public bool Top6Hit { get; set; }
        }

        /// <summary>
        /// 执行回测
        /// </summary>
        /// <param name="trainPeriods">每次训练使用的期数</param>
        /// <param name="testCount">测试次数</param>
        public static BacktestReport Run(int trainPeriods = 100, int testCount = 50)
        {
            var report = new BacktestReport
            {
                TrainPeriods = trainPeriods
            };

            // 获取足够的数据
            var allHistory = DatabaseHelper.GetLatestHistory(trainPeriods + testCount + 50);
            var validHistory = allHistory
                .Where(r => !string.IsNullOrEmpty(r.SpecialZodiac))
                .Reverse()
                .ToList();
            var zodiacData = validHistory.Select(r => r.SpecialZodiac).ToList();
            var periodData = validHistory.Select(r => r.Period).ToList();

            int totalTests = Math.Min(testCount, zodiacData.Count - trainPeriods);
            if (totalTests <= 0)
                return report;

            report.TotalTests = totalTests;
            var engine = new ZodiacPredictEngine();

            // 初始化生肖命中计数
            string[] allZodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
            foreach (var z in allZodiacs)
                report.ZodiacHitCount[z] = 0;

            for (int i = 0; i < totalTests; i++)
            {
                int testPos = trainPeriods + i;
                string actualZodiac = zodiacData[testPos];
                string period = testPos < periodData.Count ? periodData[testPos] : "";

                // 使用当前窗口数据训练
                var trainData = zodiacData.Take(trainPeriods + i).Reverse().ToList();

                // 计算评分
                var scores = new List<(string zodiac, double score)>();
                foreach (var zodiac in allZodiacs)
                {
                    double score = engine.CalculateZodiacScoreFromData(zodiac, trainData);
                    scores.Add((zodiac, score));
                }

                var sorted = scores.OrderByDescending(s => s.score).ToList();
                var top1 = sorted[0].zodiac;
                var top3 = sorted.Take(3).Select(s => s.zodiac).ToList();
                var top6 = sorted.Take(6).Select(s => s.zodiac).ToList();

                bool top1Hit = top1 == actualZodiac;
                bool top3Hit = top3.Contains(actualZodiac);
                bool top6Hit = top6.Contains(actualZodiac);

                if (top1Hit) report.Top1Hits++;
                if (top3Hit) report.Top3Hits++;
                if (top6Hit) report.Top6Hits++;

                if (top6Hit && report.ZodiacHitCount.ContainsKey(actualZodiac))
                    report.ZodiacHitCount[actualZodiac]++;

                report.Records.Add(new BacktestRecord
                {
                    TestIndex = i + 1,
                    Period = period,
                    ActualZodiac = actualZodiac,
                    PredictedTop3 = top3,
                    PredictedTop6 = top6,
                    Top1Hit = top1Hit,
                    Top3Hit = top3Hit,
                    Top6Hit = top6Hit
                });
            }

            return report;
        }

        /// <summary>
        /// 生成回测报告文本
        /// </summary>
        public static string GenerateReportText(BacktestReport report)
        {
            var sb = new System.Text.StringBuilder();

            sb.AppendLine("═══════════════════════════════════════════");
            sb.AppendLine("       AI特码生肖预测 · 回测报告");
            sb.AppendLine("═══════════════════════════════════════════");
            sb.AppendLine();
            sb.AppendLine($"训练期数：{report.TrainPeriods} 期");
            sb.AppendLine($"测试次数：{report.TotalTests} 次");
            sb.AppendLine();
            sb.AppendLine("─────────── 命中率统计 ───────────");
            sb.AppendLine($"  第1名命中：{report.Top1HitRate:F1}%  ({report.Top1Hits}/{report.TotalTests})");
            sb.AppendLine($"  前3名命中：{report.Top3HitRate:F1}%  ({report.Top3Hits}/{report.TotalTests})");
            sb.AppendLine($"  前6名命中：{report.Top6HitRate:F1}%  ({report.Top6Hits}/{report.TotalTests})");
            sb.AppendLine();
            sb.AppendLine("─────────── 各生肖命中次数 ───────────");

            string[] allZodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
            foreach (var z in allZodiacs)
            {
                int count = report.ZodiacHitCount.ContainsKey(z) ? report.ZodiacHitCount[z] : 0;
                string bar = new string('█', count);
                sb.AppendLine($"  {z} {count:D2}次 {bar}");
            }

            sb.AppendLine();
            sb.AppendLine("─────────── 最近10次测试详情 ───────────");
            var recent = report.Records.Take(10).ToList();
            foreach (var r in recent)
            {
                string mark1 = r.Top1Hit ? "✅" : "❌";
                string mark3 = r.Top3Hit ? "✅" : "❌";
                string mark6 = r.Top6Hit ? "✅" : "❌";
                sb.AppendLine($"  #{r.TestIndex:D2} 实际:{r.ActualZodiac}  第1名:{mark1}  前3:{mark3}  前6:{mark6}  预测:[{string.Join(",", r.PredictedTop3)}]");
            }

            sb.AppendLine();
            sb.AppendLine("═══════════════════════════════════════════");

            return sb.ToString();
        }
    }
}
