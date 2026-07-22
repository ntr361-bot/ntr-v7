using System.Collections.Generic;
using System.Linq;

namespace 六合分析软件
{
    public static class RollingBacktestService
    {
        public static RollingBacktestResult Run(int totalPeriods = 500)
        {
            var history = WeightOptimizationService.GetValidHistoryOldToNew(totalPeriods);
            var result = new RollingBacktestResult();

            int[] trainWindows = { 300, 350, 400, 450 };
            int windowIndex = 1;
            foreach (int trainPeriods in trainWindows)
            {
                if (history.Count < trainPeriods + 1) continue;

                int testPeriods = System.Math.Min(50, history.Count - trainPeriods);
                var bestWeights = WeightOptimizationService.FindBestWeights(history.Take(trainPeriods).ToList(), System.Math.Min(300, trainPeriods - 1), System.Math.Min(50, trainPeriods - System.Math.Min(300, trainPeriods - 1)));
                var score = WeightOptimizationService.EvaluateWeights(history, trainPeriods, testPeriods, bestWeights.Weights);

                result.Windows.Add(new RollingBacktestWindow
                {
                    WindowIndex = windowIndex++,
                    TrainPeriods = trainPeriods,
                    TestStartIndex = trainPeriods + 1,
                    TestEndIndex = trainPeriods + testPeriods,
                    BestWeights = bestWeights,
                    Score = score
                });
            }

            result.TotalTests = result.Windows.Sum(w => w.Score.TotalTests);
            result.AverageTop3HitRate = result.Windows.Count > 0 ? result.Windows.Average(w => w.Score.Top3HitRate) : 0;
            result.AverageTop6HitRate = result.Windows.Count > 0 ? result.Windows.Average(w => w.Score.Top6HitRate) : 0;
            double variance = result.Windows.Count > 0
                ? result.Windows.Select(w => System.Math.Pow(w.Score.Top3HitRate - result.AverageTop3HitRate, 2)).Average()
                : 0;
            result.StabilityScore = System.Math.Max(0, result.AverageTop3HitRate * 0.60 + result.AverageTop6HitRate * 0.30 + 10 - System.Math.Sqrt(variance));
            result.StabilityGrade = WeightOptimizationService.ToGrade(result.StabilityScore);
            return result;
        }
    }
}
