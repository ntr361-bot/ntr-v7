using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace 六合分析软件
{
    /// <summary>
    /// Uses verified predictions only to explain misses and make a small, bounded calibration.
    /// </summary>
    public static class PredictionLearningService
    {
        private const int MinimumSamples = 12;
        private const int MaximumSamples = 60;
        private const double MaximumScoreAdjustment = 3.0;
        // 旧版曾有独立的固定500期模型；全部历史记录从其实际样本数（>500）归为稳定桶。
        private const int AllHistoryThreshold = 500;

        private static readonly (string Key, string Name, Func<ZodiacPredictEngineV2.ZodiacScoreV2, double> Value)[] Factors =
        {
            ("频", "频率", score => score.FrequencyScore),
            ("势", "趋势", score => score.RecentTrendScore),
            ("漏", "遗漏", score => score.OmissionScore),
            ("冷", "冷热", score => score.HotColdScore),
            ("周", "周期", score => score.PeriodPatternScore)
        };

        public static string ApplyCalibration(ZodiacPredictEngineV2.PredictResultV2 result, int analysisPeriods)
        {
            var samples = DatabaseHelper.GetPredictionHistory(500)
                .Where(record => IsSameAnalysisBucket(record.AnalysisPeriods, analysisPeriods))
                .Where(record => record.HitResult is "命中" or "未命中")
                .Where(record => !string.IsNullOrWhiteSpace(record.ActualZodiac))
                .Select(record => (Record: record, Scores: ParseScores(record.ScoreDetails)))
                .Where(sample => sample.Scores.Count >= 10 && sample.Scores.ContainsKey(sample.Record.ActualZodiac))
                .Take(MaximumSamples)
                .ToList();

            if (samples.Count < MinimumSamples)
                return $"错因学习：已有{samples.Count}条有效复盘，满{MinimumSamples}条后启用小幅校正";

            var coefficients = new Dictionary<string, double>();
            foreach (var factor in Factors)
            {
                double weightedPercentiles = 0;
                double totalWeight = 0;
                for (int index = 0; index < samples.Count; index++)
                {
                    var sample = samples[index];
                    var actual = sample.Scores[sample.Record.ActualZodiac];
                    double actualValue = actual.Components.GetValueOrDefault(factor.Key);
                    double percentile = sample.Scores.Values.Count(score =>
                        score.Components.GetValueOrDefault(factor.Key) <= actualValue) / (double)sample.Scores.Count;
                    double recencyWeight = Math.Pow(0.97, index);
                    weightedPercentiles += percentile * recencyWeight;
                    totalWeight += recencyWeight;
                }

                double averagePercentile = totalWeight > 0 ? weightedPercentiles / totalWeight : 0.5;
                coefficients[factor.Key] = Math.Clamp((averagePercentile - 0.5) * 0.12, -0.06, 0.06);
            }

            foreach (var score in result.AllScores)
            {
                double adjustment = 0;
                foreach (var factor in Factors)
                {
                    double mean = result.AllScores.Average(factor.Value);
                    adjustment += coefficients[factor.Key] * (factor.Value(score) - mean);
                }
                score.TotalScore += Math.Clamp(adjustment, -MaximumScoreAdjustment, MaximumScoreAdjustment);
            }

            RefreshRanking(result);
            string strongest = Factors.OrderByDescending(factor => coefficients[factor.Key]).First().Name;
            string weakest = Factors.OrderBy(factor => coefficients[factor.Key]).First().Name;
            return $"错因学习：依据最近{samples.Count}条已开奖复盘，小幅校正不超过±{MaximumScoreAdjustment:F0}分；近期较有效={strongest}，较弱={weakest}";
        }

        public static bool IsSameAnalysisBucket(int storedPeriods, int requestedPeriods)
        {
            bool storedIsAllHistory = storedPeriods > AllHistoryThreshold;
            bool requestedIsAllHistory = requestedPeriods > AllHistoryThreshold;
            return storedIsAllHistory && requestedIsAllHistory || storedPeriods == requestedPeriods;
        }

        public static string BuildReview(string scoreDetails, string predictedTop3, string actualZodiac)
        {
            var scores = ParseScores(scoreDetails);
            if (scores.Count == 0 || !scores.TryGetValue(actualZodiac, out ParsedScore? actual))
                return "错因复盘：历史记录缺少完整分项评分，无法定位原因";

            var ranking = scores.Values.OrderByDescending(score => score.Total).ToList();
            int rank = ranking.FindIndex(score => score.Zodiac == actualZodiac) + 1;
            double cutoff = ranking.Count >= 3 ? ranking[2].Total : ranking.Last().Total;
            bool hit = predictedTop3.Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(actualZodiac);
            if (hit)
                return $"复盘：实际{actualZodiac}排名第{rank}，Top3命中；保留本期分项作为后续校准样本";

            ParsedScore cutoffScore = ranking[Math.Min(2, ranking.Count - 1)];
            var weights = new Dictionary<string, double>
            {
                ["频"] = 0.10, ["势"] = 0.10, ["漏"] = 0.50, ["冷"] = 0.10, ["周"] = 0.20, ["联"] = 0
            };
            var deficits = Factors
                .Select(factor => new
                {
                    factor.Name,
                    Deficit = (cutoffScore.Components.GetValueOrDefault(factor.Key) -
                               actual.Components.GetValueOrDefault(factor.Key)) * weights.GetValueOrDefault(factor.Key)
                })
                .OrderByDescending(item => item.Deficit)
                .ToList();
            string mainReason = deficits[0].Deficit > 0.05 ? deficits[0].Name + "项偏低" : "综合分接近但排序落后";
            double gap = Math.Max(0, cutoff - actual.Total);
            return $"错因复盘：实际{actualZodiac}排名第{rank}，距Top3门槛{gap:F1}分；主要原因={mainReason}。本期仅加入已开奖学习样本，不追改历史结果";
        }

        private static void RefreshRanking(ZodiacPredictEngineV2.PredictResultV2 result)
        {
            var sorted = result.AllScores.OrderByDescending(score => score.TotalScore).ToList();
            result.Top3 = sorted.Take(3).Select(score => score.Zodiac).ToList();
            result.Top6 = sorted.Take(6).Select(score => score.Zodiac).ToList();
            result.Bottom3 = sorted.Skip(9).Select(score => score.Zodiac).ToList();
            result.FirstTier = string.Join("  ", sorted.Take(3).Select(score => $"{score.Zodiac} {score.TotalScore:F0}分"));
            result.SecondTier = string.Join("  ", sorted.Skip(3).Take(3).Select(score => $"{score.Zodiac} {score.TotalScore:F0}分"));
            result.Eliminated = string.Join("  ", sorted.Skip(9).Take(3).Select(score => score.Zodiac));
        }

        private static Dictionary<string, ParsedScore> ParseScores(string details)
        {
            var result = new Dictionary<string, ParsedScore>();
            string scorePart = (details ?? "").Split('#')[0];
            foreach (string item in scorePart.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = item.Split('|', StringSplitOptions.RemoveEmptyEntries);
                string[] header = parts[0].Split(':', 2);
                if (header.Length != 2 || !double.TryParse(header[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double total))
                    continue;
                var components = new Dictionary<string, double>();
                foreach (string part in parts.Skip(1))
                {
                    if (part.Length < 2 || !double.TryParse(part[1..], NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                        continue;
                    components[part[..1]] = value;
                }
                result[header[0]] = new ParsedScore(header[0], total, components);
            }
            return result;
        }

        private sealed record ParsedScore(string Zodiac, double Total, Dictionary<string, double> Components);
    }
}
