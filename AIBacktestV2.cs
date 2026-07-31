using System;
using System.Collections.Generic;
using System.Linq;

namespace 六合分析软件
{
    /// <summary>
    /// AI预测回测模块 V2
    /// 支持权重自动优化 + 完整统计
    /// </summary>
    public static class AIBacktestV2
    {
        public class BacktestReportV2
        {
            public int TotalTests { get; set; }
            public int TrainPeriods { get; set; }
            public int Top3Hits { get; set; }
            public int Top6Hits { get; set; }
            public double Top3HitRate => TotalTests > 0 ? (double)Top3Hits / TotalTests * 100 : 0;
            public double Top6HitRate => TotalTests > 0 ? (double)Top6Hits / TotalTests * 100 : 0;
            public int MaxConsecutiveHits { get; set; }
            public int MaxConsecutiveMiss { get; set; }
            public string BestModel { get; set; } = "";
            public List<BacktestRecordV2> Records { get; set; } = new List<BacktestRecordV2>();
        }

        public class BacktestRecordV2
        {
            public int TestIndex { get; set; }
            public string Period { get; set; } = "";
            public string ActualZodiac { get; set; } = "";
            public List<string> PredictedTop3 { get; set; } = new List<string>();
            public List<string> PredictedTop6 { get; set; } = new List<string>();
            public bool Top3Hit { get; set; }
            public bool Top6Hit { get; set; }
        }

        public static BacktestReportV2 Run(int trainPeriods = 100, int testCount = 100)
        {
            var engine = new ZodiacPredictEngineV2();
            var result = engine.Backtest(trainPeriods, testCount);

            var report = new BacktestReportV2
            {
                TotalTests = result.TotalTests,
                TrainPeriods = result.TrainPeriods,
                Top3Hits = result.Top3Hits,
                Top6Hits = result.Top6Hits,
                MaxConsecutiveHits = result.MaxConsecutiveHits,
                MaxConsecutiveMiss = result.MaxConsecutiveMiss,
                BestModel = result.BestModel
            };

            foreach (var r in result.Records)
            {
                report.Records.Add(new BacktestRecordV2
                {
                    TestIndex = r.TestIndex,
                    Period = r.Period,
                    ActualZodiac = r.ActualZodiac,
                    PredictedTop3 = r.PredictedTop3,
                    PredictedTop6 = r.PredictedTop6,
                    Top3Hit = r.Top3Hit,
                    Top6Hit = r.Top6Hit
                });
            }

            return report;
        }

        public static string GenerateReportText(BacktestReportV2 report)
        {
            var sb = new System.Text.StringBuilder();

            sb.AppendLine("═══════════════════════════════════════════");
            sb.AppendLine("     AI特码生肖预测 V2 · 回测报告");
            sb.AppendLine("═══════════════════════════════════════════");
            sb.AppendLine();
            sb.AppendLine($"训练期数：{report.TrainPeriods} 期");
            sb.AppendLine($"测试次数：{report.TotalTests} 次");
            sb.AppendLine($"最佳模型：{report.BestModel}");
            sb.AppendLine();
            sb.AppendLine("─────────── 命中率统计 ───────────");
            sb.AppendLine($"  前3名命中：{report.Top3HitRate:F1}%  ({report.Top3Hits}/{report.TotalTests})");
            sb.AppendLine($"  前6名命中：{report.Top6HitRate:F1}%  ({report.Top6Hits}/{report.TotalTests})");
            sb.AppendLine($"  最大连续命中：{report.MaxConsecutiveHits} 次");
            sb.AppendLine($"  最大连续未中：{report.MaxConsecutiveMiss} 次");
            sb.AppendLine();
            sb.AppendLine("─────────── 最近10次测试详情 ───────────");

            var recent = report.Records.Take(10).ToList();
            foreach (var r in recent)
            {
                string mark3 = r.Top3Hit ? "✅" : "❌";
                string mark6 = r.Top6Hit ? "✅" : "❌";
                sb.AppendLine($"  #{r.TestIndex:D2} 实际:{r.ActualZodiac}  前3:{mark3}  前6:{mark6}  预测:[{string.Join(",", r.PredictedTop3)}]");
            }

            sb.AppendLine();
            sb.AppendLine("═══════════════════════════════════════════");
            return sb.ToString();
        }
    }
}
