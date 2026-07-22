using System;
using System.Collections.Generic;
using System.Linq;

namespace 六合分析软件
{
    public static class WeightOptimizationService
    {
        private static readonly string[] Zodiacs =
        {
            "鼠", "牛", "虎", "兔", "龙", "蛇",
            "马", "羊", "猴", "鸡", "狗", "猪"
        };

        public static OptimizedWeightResult FindBestWeights(int trainPeriods = 300, int testPeriods = 50)
        {
            var history = GetValidHistoryOldToNew(trainPeriods + testPeriods);
            return FindBestWeights(history, trainPeriods, testPeriods);
        }

        public static OptimizedWeightResult FindBestWeights(
            List<DatabaseHelper.HistoryRecord> oldToNewHistory,
            int trainPeriods,
            int testPeriods)
        {
            var best = new OptimizedWeightResult
            {
                ModelName = "V6自动权重模型",
                TotalTests = 0,
                StabilityGrade = "数据不足"
            };

            if (oldToNewHistory.Count < trainPeriods + 1)
                return best;

            int availableTests = Math.Min(testPeriods, oldToNewHistory.Count - trainPeriods);
            int tested = 0;

            foreach (var weights in GenerateWeightCombinations())
            {
                tested++;
                var score = EvaluateWeights(oldToNewHistory, trainPeriods, availableTests, weights);
                if (score.CombinedScore > best.CombinedScore || best.TotalTests == 0)
                {
                    best = new OptimizedWeightResult
                    {
                        ModelName = "V6自动权重模型",
                        Weights = weights,
                        TestedCombinations = tested,
                        TotalTests = score.TotalTests,
                        Top3Hits = score.Top3Hits,
                        Top6Hits = score.Top6Hits,
                        MaxConsecutiveHits = score.MaxConsecutiveHits,
                        MaxConsecutiveMisses = score.MaxConsecutiveMisses,
                        StabilityScore = score.StabilityScore,
                        CombinedScore = score.CombinedScore,
                        StabilityGrade = score.StabilityGrade,
                        Records = score.Records
                    };
                }
            }

            best.TestedCombinations = tested;
            return best;
        }

        public static ModelScoreResult EvaluateWeights(
            List<DatabaseHelper.HistoryRecord> oldToNewHistory,
            int trainPeriods,
            int testPeriods,
            ZodiacPredictEngineV2.WeightConfig weights)
        {
            return EvaluatePredictor(
                oldToNewHistory,
                trainPeriods,
                testPeriods,
                "V6自动权重模型",
                training => RankByWeights(training, weights));
        }

        public static ModelScoreResult EvaluatePredictor(
            List<DatabaseHelper.HistoryRecord> oldToNewHistory,
            int trainPeriods,
            int testPeriods,
            string modelName,
            Func<List<DatabaseHelper.HistoryRecord>, List<(string zodiac, double score)>> predictor)
        {
            var result = new ModelScoreResult { ModelName = modelName };
            if (oldToNewHistory.Count < trainPeriods + 1) return FinalizeScore(result);

            int end = Math.Min(oldToNewHistory.Count, trainPeriods + testPeriods);
            int consecutiveHit = 0;
            int consecutiveMiss = 0;

            for (int i = trainPeriods; i < end; i++)
            {
                var training = oldToNewHistory.Take(i).Reverse().ToList();
                var actual = oldToNewHistory[i].SpecialZodiac;
                var ranked = predictor(training);
                var top3 = ranked.Take(3).Select(x => x.zodiac).ToList();
                var top6 = ranked.Take(6).Select(x => x.zodiac).ToList();
                bool top3Hit = top3.Contains(actual);
                bool top6Hit = top6.Contains(actual);

                result.TotalTests++;
                if (top3Hit)
                {
                    result.Top3Hits++;
                    consecutiveHit++;
                    consecutiveMiss = 0;
                }
                else
                {
                    consecutiveMiss++;
                    consecutiveHit = 0;
                }

                if (top6Hit) result.Top6Hits++;
                result.MaxConsecutiveHits = Math.Max(result.MaxConsecutiveHits, consecutiveHit);
                result.MaxConsecutiveMisses = Math.Max(result.MaxConsecutiveMisses, consecutiveMiss);
                result.Records.Add(new BacktestPredictionRecord
                {
                    Period = oldToNewHistory[i].Period,
                    ActualZodiac = actual,
                    Top3 = top3,
                    Top6 = top6,
                    Top3Hit = top3Hit,
                    Top6Hit = top6Hit
                });
            }

            return FinalizeScore(result);
        }

        public static List<(string zodiac, double score)> RankByWeights(
            List<DatabaseHelper.HistoryRecord> newestFirstHistory,
            ZodiacPredictEngineV2.WeightConfig weights)
        {
            return Zodiacs
                .Select(z => (zodiac: z, score: CalculateWeightedScore(newestFirstHistory, z, weights)))
                .OrderByDescending(x => x.score)
                .ThenBy(x => x.zodiac)
                .ToList();
        }

        public static List<ZodiacPredictEngineV2.WeightConfig> GenerateWeightCombinations()
        {
            var values = new[] { 0.1, 0.2, 0.3, 0.4, 0.5 };
            var list = new List<ZodiacPredictEngineV2.WeightConfig>();

            foreach (double frequency in values)
            foreach (double trend in values)
            foreach (double omission in values)
            foreach (double pattern in values)
            {
                double sum = frequency + trend + omission + pattern;
                if (Math.Abs(sum - 1.0) > 0.0001) continue;

                list.Add(new ZodiacPredictEngineV2.WeightConfig
                {
                    FrequencyWeight = frequency,
                    RecentTrendWeight = trend,
                    OmissionWeight = omission,
                    HotColdWeight = 0,
                    PeriodPatternWeight = pattern * 0.65,
                    ConsecutiveWeight = pattern * 0.35
                });
            }

            return list;
        }

        internal static List<DatabaseHelper.HistoryRecord> GetValidHistoryOldToNew(int periods)
        {
            return DatabaseHelper.GetLatestHistory(periods)
                .Where(r => !string.IsNullOrWhiteSpace(r.SpecialZodiac))
                .Reverse()
                .ToList();
        }

        internal static ModelScoreResult FinalizeScore(ModelScoreResult result)
        {
            result.Top3HitRate = result.TotalTests > 0 ? (double)result.Top3Hits / result.TotalTests * 100 : 0;
            result.Top6HitRate = result.TotalTests > 0 ? (double)result.Top6Hits / result.TotalTests * 100 : 0;
            double missPenalty = Math.Min(30, result.MaxConsecutiveMisses * 3);
            result.StabilityScore = Math.Max(0, result.Top3HitRate * 0.55 + result.Top6HitRate * 0.35 + 10 - missPenalty);
            result.CombinedScore = result.Top3HitRate * 0.50 + result.Top6HitRate * 0.25 + result.StabilityScore * 0.25;
            result.StabilityGrade = ToGrade(result.StabilityScore);
            return result;
        }

        internal static string ToGrade(double score)
        {
            if (score >= 80) return "A";
            if (score >= 70) return "B";
            if (score >= 60) return "C";
            return "D";
        }

        private static double CalculateWeightedScore(
            List<DatabaseHelper.HistoryRecord> newestFirstHistory,
            string zodiac,
            ZodiacPredictEngineV2.WeightConfig weights)
        {
            int total = newestFirstHistory.Count;
            if (total == 0) return 0;

            int appear = newestFirstHistory.Count(h => h.SpecialZodiac == zodiac);
            double frequencyScore = Math.Min(100, (double)appear / total * 12 * 100);

            int recent10 = newestFirstHistory.Take(Math.Min(10, total)).Count(h => h.SpecialZodiac == zodiac);
            int recent30 = newestFirstHistory.Take(Math.Min(30, total)).Count(h => h.SpecialZodiac == zodiac);
            int previous30 = newestFirstHistory.Skip(30).Take(30).Count(h => h.SpecialZodiac == zodiac);
            double recentRate = Math.Min(100, ((double)recent10 / Math.Min(10, total) * 0.55 +
                (double)recent30 / Math.Min(30, total) * 0.30 +
                Math.Max(0, recent30 - previous30) / 30.0 * 0.15) * 12 * 100);

            int currentOmission = newestFirstHistory.TakeWhile(h => h.SpecialZodiac != zodiac).Count();
            var intervals = GetIntervals(newestFirstHistory, zodiac);
            double avgInterval = intervals.Count > 0 ? intervals.Average() : 12;
            double omissionRatio = currentOmission / Math.Max(1, avgInterval);
            double omissionScore = omissionRatio >= 0.8 && omissionRatio <= 1.6
                ? 90
                : Math.Max(10, 90 - Math.Abs(omissionRatio - 1.2) * 40);

            double patternScore = 45;
            if (intervals.Count >= 3)
            {
                double avg = intervals.Average();
                double variance = intervals.Select(i => Math.Pow(i - avg, 2)).Average();
                double regularity = Math.Max(0, 100 - Math.Sqrt(variance) / Math.Max(1, avg) * 100);
                double match = Math.Max(0, 100 - Math.Abs(currentOmission - avg) / Math.Max(1, avg) * 100);
                patternScore = regularity * 0.45 + match * 0.55;
            }

            return frequencyScore * weights.FrequencyWeight +
                recentRate * weights.RecentTrendWeight +
                omissionScore * weights.OmissionWeight +
                patternScore * (weights.PeriodPatternWeight + weights.ConsecutiveWeight);
        }

        private static List<int> GetIntervals(List<DatabaseHelper.HistoryRecord> newestFirstHistory, string zodiac)
        {
            var intervals = new List<int>();
            int last = -1;
            for (int i = 0; i < newestFirstHistory.Count; i++)
            {
                if (newestFirstHistory[i].SpecialZodiac != zodiac) continue;
                if (last >= 0) intervals.Add(i - last);
                last = i;
            }
            return intervals;
        }
    }
}
