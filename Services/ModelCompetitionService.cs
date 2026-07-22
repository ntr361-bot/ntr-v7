using System;
using System.Collections.Generic;
using System.Linq;

namespace 六合分析软件
{
    public static class ModelCompetitionService
    {
        private static readonly string[] Zodiacs =
        {
            "鼠", "牛", "虎", "兔", "龙", "蛇",
            "马", "羊", "猴", "鸡", "狗", "猪"
        };

        public static List<ModelScoreResult> RunCompetition(int totalPeriods = 500)
        {
            var history = WeightOptimizationService.GetValidHistoryOldToNew(totalPeriods);
            if (history.Count < 350) return new List<ModelScoreResult>();

            var best = WeightOptimizationService.FindBestWeights(history, 300, Math.Min(50, history.Count - 300));
            var models = new Dictionary<string, Func<List<DatabaseHelper.HistoryRecord>, List<(string zodiac, double score)>>>
            {
                ["FrequencyModel"] = h => Rank(h, FrequencyScore),
                ["TrendModel"] = h => Rank(h, TrendScore),
                ["MissingModel"] = h => Rank(h, MissingScore),
                ["PatternModel"] = h => Rank(h, PatternScore),
                ["MomentumModel"] = h => Rank(h, MomentumScore),
                ["EnsembleModel"] = h => WeightOptimizationService.RankByWeights(h, best.Weights)
            };

            return models
                .Select(m => WeightOptimizationService.EvaluatePredictor(history, 300, Math.Min(200, history.Count - 300), m.Key, m.Value))
                .OrderByDescending(r => r.CombinedScore)
                .ThenByDescending(r => r.Top3HitRate)
                .ToList();
        }

        private static List<(string zodiac, double score)> Rank(
            List<DatabaseHelper.HistoryRecord> history,
            Func<List<DatabaseHelper.HistoryRecord>, string, double> scorer)
        {
            return Zodiacs.Select(z => (zodiac: z, score: scorer(history, z)))
                .OrderByDescending(x => x.score)
                .ThenBy(x => x.zodiac)
                .ToList();
        }

        private static double FrequencyScore(List<DatabaseHelper.HistoryRecord> history, string zodiac)
        {
            return history.Count == 0 ? 0 : (double)history.Count(h => h.SpecialZodiac == zodiac) / history.Count * 12;
        }

        private static double TrendScore(List<DatabaseHelper.HistoryRecord> history, string zodiac)
        {
            double recent = history.Take(30).Count(h => h.SpecialZodiac == zodiac) / 30.0;
            double previous = history.Skip(30).Take(30).Count(h => h.SpecialZodiac == zodiac) / 30.0;
            return Math.Max(0, 0.5 + (recent - previous) * 6);
        }

        private static double MissingScore(List<DatabaseHelper.HistoryRecord> history, string zodiac)
        {
            int missing = history.TakeWhile(h => h.SpecialZodiac != zodiac).Count();
            var intervals = GetIntervals(history, zodiac);
            double avg = intervals.Count > 0 ? intervals.Average() : 12;
            double ratio = missing / Math.Max(1, avg);
            return ratio >= 0.8 && ratio <= 1.6 ? 1 : Math.Max(0.1, 1 - Math.Abs(ratio - 1.2) / 2);
        }

        private static double PatternScore(List<DatabaseHelper.HistoryRecord> history, string zodiac)
        {
            var intervals = GetIntervals(history, zodiac);
            if (intervals.Count < 3) return 0.5;
            double avg = intervals.Average();
            double variance = intervals.Select(i => Math.Pow(i - avg, 2)).Average();
            return Math.Max(0, 1 - Math.Sqrt(variance) / Math.Max(1, avg));
        }

        private static double MomentumScore(List<DatabaseHelper.HistoryRecord> history, string zodiac)
        {
            int recent10 = history.Take(10).Count(h => h.SpecialZodiac == zodiac);
            int recent20 = history.Take(20).Count(h => h.SpecialZodiac == zodiac);
            return recent20 > 0 ? (double)recent10 / recent20 : 0.5;
        }

        private static List<int> GetIntervals(List<DatabaseHelper.HistoryRecord> history, string zodiac)
        {
            var intervals = new List<int>();
            int last = -1;
            for (int i = 0; i < history.Count; i++)
            {
                if (history[i].SpecialZodiac != zodiac) continue;
                if (last >= 0) intervals.Add(i - last);
                last = i;
            }
            return intervals;
        }
    }
}
