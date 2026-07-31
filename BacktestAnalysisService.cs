using System;
using System.Collections.Generic;
using System.Linq;

namespace 六合分析软件
{
    /// <summary>
    /// 升级版AI回测分析服务
    /// 支持100/300/500期统计，追踪最大连续失败
    /// </summary>
    public static class BacktestAnalysisService
    {
        public class BacktestResult
        {
            public string ModelVersion { get; set; } = "";
            public string ModelName { get; set; } = "";
            public int TestPeriods { get; set; }
            public int TotalPredictions { get; set; }
            public int ZodiacHits { get; set; }           // 生肖命中
            public int NumberHits { get; set; }            // 号码命中
            public double Accuracy { get; set; }           // 准确率
            public int MaxConsecutiveFail { get; set; }    // 最大连续失败
            public int MaxConsecutiveHit { get; set; }     // 最大连续命中
            public int CurrentStreak { get; set; }         // 当前连中/连败
            public bool CurrentStreakIsHit { get; set; }   // 当前连中是命中还是失败
            public List<BacktestRecord> Records { get; set; } = new List<BacktestRecord>();
            public Dictionary<string, int> ZodiacHitDetail { get; set; } = new Dictionary<string, int>();
        }

        public class BacktestRecord
        {
            public int Index { get; set; }
            public string Period { get; set; } = "";
            public string PredictedZodiac { get; set; } = "";
            public string ActualZodiac { get; set; } = "";
            public int Score { get; set; }
            public bool Hit { get; set; }
            public string Status => Hit ? "✅" : "❌";
        }

        /// <summary>
        /// 执行回测（指定期数）
        /// </summary>
        public static BacktestResult Run(int testPeriods = 100, int trainPeriods = 50)
        {
            var result = new BacktestResult
            {
                ModelVersion = DatabaseHelper.GetCurrentModelVersion(),
                TestPeriods = testPeriods
            };

            // 获取模型信息
            var models = DatabaseHelper.GetAIModels();
            var currentModel = models.FirstOrDefault(m => m.ModelVersion == result.ModelVersion);
            if (currentModel != null)
                result.ModelName = currentModel.ModelName;

            // 获取足够的历史数据
            int needed = testPeriods + trainPeriods + 10;
            var history = DatabaseHelper.GetLatestHistory(needed);
            if (history.Count < testPeriods + trainPeriods)
                return result;

            // 倒序排列（最新在前），测试时从 trainPeriods 位置开始向前预测
            var zodiacs = history
                .Where(r => !string.IsNullOrEmpty(r.SpecialZodiac))
                .Select(r => (period: r.Period, zodiac: r.SpecialZodiac))
                .ToList();

            if (zodiacs.Count < testPeriods + trainPeriods)
                return result;

            result.TotalPredictions = testPeriods;

            int hits = 0, numberHits = 0;
            int currentConsecutiveFail = 0, currentConsecutiveHit = 0;
            result.MaxConsecutiveFail = 0;
            result.MaxConsecutiveHit = 0;
            string[] allZodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
            foreach (var z in allZodiacs) result.ZodiacHitDetail[z] = 0;

            for (int i = trainPeriods; i < trainPeriods + testPeriods; i++)
            {
                // 训练数据：i 之前的所有数据
                var trainData = zodiacs.Take(i).ToList();
                var actual = zodiacs[i];

                // 用训练数据做评分预测
                var prediction = ScoreZodiac(trainData, allZodiacs);

                bool hit = prediction == actual.zodiac;
                if (hit)
                {
                    hits++;
                    result.ZodiacHitDetail[actual.zodiac] = result.ZodiacHitDetail.GetValueOrDefault(actual.zodiac) + 1;
                    currentConsecutiveHit++;
                    currentConsecutiveFail = 0;
                    if (currentConsecutiveHit > result.MaxConsecutiveHit)
                        result.MaxConsecutiveHit = currentConsecutiveHit;
                }
                else
                {
                    currentConsecutiveFail++;
                    currentConsecutiveHit = 0;
                    if (currentConsecutiveFail > result.MaxConsecutiveFail)
                        result.MaxConsecutiveFail = currentConsecutiveFail;
                }

                result.Records.Add(new BacktestRecord
                {
                    Index = i - trainPeriods + 1,
                    Period = actual.period,
                    PredictedZodiac = prediction,
                    ActualZodiac = actual.zodiac,
                    Score = 0,
                    Hit = hit
                });
            }

            result.ZodiacHits = hits;
            result.Accuracy = testPeriods > 0 ? (double)hits / testPeriods * 100 : 0;
            result.CurrentStreak = currentConsecutiveFail > 0 ? currentConsecutiveFail : currentConsecutiveHit;
            result.CurrentStreakIsHit = currentConsecutiveHit > 0;

            // 更新模型准确率
            DatabaseHelper.UpdateModelAccuracy(result.ModelVersion, result.Accuracy);

            return result;
        }

        /// <summary>
        /// 多周期回测
        /// </summary>
        public static Dictionary<int, BacktestResult> RunMultiPeriod(params int[] periods)
        {
            var results = new Dictionary<int, BacktestResult>();
            foreach (var p in periods)
                results[p] = Run(p);
            return results;
        }

        /// <summary>
        /// 基于历史数据做生肖预测（简化版评分）
        /// </summary>
        private static string ScoreZodiac(List<(string period, string zodiac)> data, string[] allZodiacs)
        {
            if (data.Count < 10) return "";

            var scores = new Dictionary<string, double>();

            foreach (var z in allZodiacs)
            {
                double totalScore = 0;
                int appearCount = data.Count(d => d.zodiac == z);
                int recent10Count = data.Take(10).Count(d => d.zodiac == z);
                int recent30Count = data.Take(30).Count(d => d.zodiac == z);
                int prev30Count = data.Skip(30).Take(30).Count(d => d.zodiac == z);

                // 频率分
                double freqRate = (double)appearCount / data.Count * 100;
                totalScore += Math.Min(20, freqRate * 2);

                // 趋势分
                if (recent30Count > prev30Count + 1) totalScore += 18;
                else if (recent30Count >= prev30Count - 1) totalScore += 12;
                else totalScore += 6;
                if (recent10Count >= 3) totalScore += 5;

                // 遗漏分
                int missing = 0;
                foreach (var d in data)
                {
                    if (d.zodiac == z) break;
                    missing++;
                }
                totalScore += Math.Min(25, missing * 2);

                // 基础分
                totalScore += appearCount > 0 ? 10 : 0;

                scores[z] = totalScore;
            }

            return scores.OrderByDescending(kv => kv.Value).First().Key;
        }

        /// <summary>
        /// 生成回测报告文本
        /// </summary>
        public static string GenerateReport(BacktestResult result)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("═══════════════════════════════════════════");
            sb.AppendLine("       AI 回测分析报告 V2.0");
            sb.AppendLine("═══════════════════════════════════════════");
            sb.AppendLine();
            sb.AppendLine($"模型：{result.ModelVersion} {result.ModelName}");
            sb.AppendLine($"测试周期：{result.TestPeriods} 期");
            sb.AppendLine($"总预测：{result.TotalPredictions} 次");
            sb.AppendLine();
            sb.AppendLine("── 命中统计 ──");
            sb.AppendLine($"  生肖命中：{result.ZodiacHits} 次");
            sb.AppendLine($"  准确率：{result.Accuracy:F1}%");
            sb.AppendLine($"  最大连续命中：{result.MaxConsecutiveHit} 次");
            sb.AppendLine($"  最大连续失败：{result.MaxConsecutiveFail} 次");
            string streakText = result.CurrentStreakIsHit ? $"连续命中 {result.CurrentStreak} 次" : $"连续失败 {result.CurrentStreak} 次";
            sb.AppendLine($"  当前：{streakText}");
            sb.AppendLine();
            sb.AppendLine("── 各生肖命中分布 ──");
            foreach (var kv in result.ZodiacHitDetail.OrderByDescending(kv => kv.Value))
            {
                string bar = new string('█', kv.Value);
                sb.AppendLine($"  {kv.Key} {kv.Value:D2}次 {bar}");
            }
            sb.AppendLine();
            sb.AppendLine("── 最近10次 ──");
            var recent = result.Records.TakeLast(10).ToList();
            foreach (var r in recent)
            {
                sb.AppendLine($"  #{r.Index:D3} 期号:{r.Period} 预测:{r.PredictedZodiac} 实际:{r.ActualZodiac} {r.Status}");
            }
            sb.AppendLine();
            sb.AppendLine("═══════════════════════════════════════════");
            return sb.ToString();
        }
    }
}
