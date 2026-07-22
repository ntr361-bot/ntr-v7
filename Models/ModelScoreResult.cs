using System;
using System.Collections.Generic;

namespace 六合分析软件
{
    public class ModelScoreResult
    {
        public string ModelName { get; set; } = "";
        public double Top3HitRate { get; set; }
        public double Top6HitRate { get; set; }
        public int TotalTests { get; set; }
        public int Top3Hits { get; set; }
        public int Top6Hits { get; set; }
        public int MaxConsecutiveHits { get; set; }
        public int MaxConsecutiveMisses { get; set; }
        public double StabilityScore { get; set; }
        public double CombinedScore { get; set; }
        public string StabilityGrade { get; set; } = "";
        public List<BacktestPredictionRecord> Records { get; set; } = new List<BacktestPredictionRecord>();
    }

    public class BacktestPredictionRecord
    {
        public string Period { get; set; } = "";
        public string ActualZodiac { get; set; } = "";
        public List<string> Top3 { get; set; } = new List<string>();
        public List<string> Top6 { get; set; } = new List<string>();
        public bool Top3Hit { get; set; }
        public bool Top6Hit { get; set; }
    }

    public class OptimizedWeightResult : ModelScoreResult
    {
        public ZodiacPredictEngineV2.WeightConfig Weights { get; set; } = new ZodiacPredictEngineV2.WeightConfig();
        public int TestedCombinations { get; set; }
        public DateTime TrainTime { get; set; } = DateTime.Now;
    }

    public class RollingBacktestResult
    {
        public List<RollingBacktestWindow> Windows { get; set; } = new List<RollingBacktestWindow>();
        public double AverageTop3HitRate { get; set; }
        public double AverageTop6HitRate { get; set; }
        public double StabilityScore { get; set; }
        public string StabilityGrade { get; set; } = "";
        public int TotalTests { get; set; }
    }

    public class RollingBacktestWindow
    {
        public int WindowIndex { get; set; }
        public int TrainPeriods { get; set; }
        public int TestStartIndex { get; set; }
        public int TestEndIndex { get; set; }
        public ModelScoreResult Score { get; set; } = new ModelScoreResult();
        public OptimizedWeightResult? BestWeights { get; set; }
    }

    public class PredictionExplanation
    {
        public string Zodiac { get; set; } = "";
        public double Confidence { get; set; }
        public List<string> SupportFactors { get; set; } = new List<string>();
        public List<string> Risks { get; set; } = new List<string>();
    }
}
