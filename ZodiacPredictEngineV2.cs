using System;
using System.Collections.Generic;
using System.Linq;

namespace 六合分析软件
{
    /// <summary>
    /// AI特码生肖预测引擎 V2
    /// 多模型综合评分 + 滑动窗口 + 冷热分析 + 关联分析 + 权重自动优化
    /// </summary>
    public class ZodiacPredictEngineV2
    {
        private static readonly string[] ZodiacOrder =
        {
            "鼠", "牛", "虎", "兔", "龙", "蛇",
            "马", "羊", "猴", "鸡", "狗", "猪"
        };

        // ===== 权重配置（可动态调整）=====
        public class WeightConfig
        {
            public double FrequencyWeight { get; set; } = 0.20;
            public double RecentTrendWeight { get; set; } = 0.20;
            public double OmissionWeight { get; set; } = 0.20;
            public double HotColdWeight { get; set; } = 0.15;
            public double PeriodPatternWeight { get; set; } = 0.15;
            public double ConsecutiveWeight { get; set; } = 0.10;
        }

        // ===== 单个生肖的评分结果 =====
        public class ZodiacScoreV2
        {
            public string Zodiac { get; set; } = "";
            public double TotalScore { get; set; }
            public double FrequencyScore { get; set; }
            public double RecentTrendScore { get; set; }
            public double OmissionScore { get; set; }
            public double HotColdScore { get; set; }
            public double PeriodPatternScore { get; set; }
            public double ConsecutiveScore { get; set; }
            public double EightZodiacScore { get; set; }

            public int TotalAppear { get; set; }
            public int Appear5 { get; set; }
            public int Appear10 { get; set; }
            public int Appear30 { get; set; }
            public int Appear50 { get; set; }
            public int Appear100 { get; set; }
            public int Appear200 { get; set; }
            public int CurrentOmission { get; set; }
            public int MaxOmission { get; set; }
            public double AvgOmission { get; set; }
            public double OmissionPosition { get; set; }
            public string HotColdTrend { get; set; } = "";
        }

        // ===== 预测结果 =====
        public class PredictResultV2
        {
            public int AnalysisPeriods { get; set; }
            public List<ZodiacScoreV2> AllScores { get; set; } = new List<ZodiacScoreV2>();
            public List<string> Top3 { get; set; } = new List<string>();
            public List<string> Top6 { get; set; } = new List<string>();
            public List<string> Bottom3 { get; set; } = new List<string>();
            public string FirstTier { get; set; } = "";     // 第一梯队
            public string SecondTier { get; set; } = "";    // 第二梯队
            public string Eliminated { get; set; } = "";    // 淘汰
            public string Confidence { get; set; } = "";    // 可信度：高/中/低
            public WeightConfig UsedWeights { get; set; } = new WeightConfig();
            public string BestModel { get; set; } = "";     // 最佳模型
            public Dictionary<string, PredictResultV2> WindowResults { get; set; } = new Dictionary<string, PredictResultV2>();
        }

        // ===== 回测结果 =====
        public class BacktestResultV2
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
            public WeightConfig BestWeights { get; set; } = new WeightConfig();
            public List<BacktestRecord> Records { get; set; } = new List<BacktestRecord>();
        }

        public class BacktestRecord
        {
            public int TestIndex { get; set; }
            public string Period { get; set; } = "";
            public string ActualZodiac { get; set; } = "";
            public List<string> PredictedTop3 { get; set; } = new List<string>();
            public List<string> PredictedTop6 { get; set; } = new List<string>();
            public bool Top3Hit { get; set; }
            public bool Top6Hit { get; set; }
        }

        // ===== 主预测方法 =====
        public PredictResultV2 Predict(int periodCount = 200)
        {
            return Predict(periodCount, null);
        }

        public PredictResultV2 Predict(int periodCount, WeightConfig? overrideWeights)
        {
            var result = new PredictResultV2();

            // 获取历史数据（只读特码生肖）
            var history = DatabaseHelper.GetLatestHistory(periodCount > 0 ? periodCount : int.MaxValue);
            var zodiacData = history
                .Where(r => !string.IsNullOrEmpty(r.SpecialZodiac))
                .Select(r => r.SpecialZodiac)
                .ToList();

            result.AnalysisPeriods = zodiacData.Count;

            if (zodiacData.Count < 10)
            {
                foreach (var z in ZodiacOrder)
                    result.AllScores.Add(CreateEmptyScore(z));
                return result;
            }

            // 获取最优权重
            var bestWeights = overrideWeights ?? LoadBestWeights(zodiacData);
            result.UsedWeights = bestWeights;

            // 计算每个生肖的综合评分
            foreach (var zodiac in ZodiacOrder)
            {
                var score = CalculateZodiacScoreV2(zodiac, zodiacData, bestWeights);
                result.AllScores.Add(score);
            }

            ApplyEightZodiacRule(result.AllScores, zodiacData.FirstOrDefault() ?? "");

            // 排序
            var sorted = result.AllScores.OrderByDescending(s => s.TotalScore).ToList();
            result.Top3 = sorted.Take(3).Select(s => s.Zodiac).ToList();
            result.Top6 = sorted.Take(6).Select(s => s.Zodiac).ToList();
            result.Bottom3 = sorted.Skip(9).Select(s => s.Zodiac).ToList();

            // 分梯队
            result.FirstTier = string.Join("  ", sorted.Take(3).Select(s => $"{s.Zodiac} {s.TotalScore:F0}分"));
            result.SecondTier = string.Join("  ", sorted.Skip(3).Take(3).Select(s => $"{s.Zodiac} {s.TotalScore:F0}分"));
            result.Eliminated = string.Join("  ", sorted.Skip(9).Take(3).Select(s => s.Zodiac));

            // 计算可信度
            result.Confidence = CalculateConfidence(sorted);

            // 最佳模型
            result.BestModel = DetermineBestModel(bestWeights);

            // 滑动窗口结果
            result.WindowResults = RunSlidingWindows(zodiacData, bestWeights);

            return result;
        }

        // ===== 多维度评分 =====
        private ZodiacScoreV2 CalculateZodiacScoreV2(string zodiac, List<string> zodiacData, WeightConfig weights)
        {
            var score = new ZodiacScoreV2 { Zodiac = zodiac };
            int total = zodiacData.Count;

            // 1. 历史频率（20%）
            score.TotalAppear = zodiacData.Count(z => z == zodiac);
            double freqRate = (double)score.TotalAppear / total;
            score.FrequencyScore = Math.Min(freqRate * 12 * 100, 100);

            // 2. 近期走势（20%）- 多窗口分析
            score.Appear10 = zodiacData.Take(Math.Min(10, total)).Count(z => z == zodiac);
            score.Appear30 = zodiacData.Take(Math.Min(30, total)).Count(z => z == zodiac);
            score.Appear50 = zodiacData.Take(Math.Min(50, total)).Count(z => z == zodiac);
            score.Appear100 = zodiacData.Take(Math.Min(100, total)).Count(z => z == zodiac);

            // 综合近期走势：最近10期权重最高
            double recent10Rate = Math.Min(10, total) > 0 ? (double)score.Appear10 / Math.Min(10, total) * 12 : 0;
            double recent30Rate = Math.Min(30, total) > 0 ? (double)score.Appear30 / Math.Min(30, total) * 12 : 0;
            double recent50Rate = Math.Min(50, total) > 0 ? (double)score.Appear50 / Math.Min(50, total) * 12 : 0;
            score.RecentTrendScore = Math.Min(
                (recent10Rate * 0.5 + recent30Rate * 0.3 + recent50Rate * 0.2) * 100, 100);

            // 3. 遗漏周期（20%）
            var intervals = GetAppearIntervals(zodiac, zodiacData);
            score.CurrentOmission = GetLastAppearIndex(zodiac, zodiacData);
            score.MaxOmission = intervals.Count > 0 ? intervals.Max() : total;
            score.AvgOmission = intervals.Count > 0 ? intervals.Average() : total;
            score.OmissionPosition = score.AvgOmission > 0 ? (double)score.CurrentOmission / score.AvgOmission : 1;

            // 遗漏评分：考虑当前位置和平均位置的比值
            if (score.CurrentOmission < 0)
            {
                score.OmissionScore = 100; // 从未出现
            }
            else if (score.OmissionPosition >= 1.0)
            {
                // 当前遗漏已超过平均值，即将出现
                score.OmissionScore = Math.Min(50 + score.OmissionPosition * 30, 100);
            }
            else
            {
                // 还未到平均周期
                score.OmissionScore = Math.Min(score.OmissionPosition * 50, 50);
            }

            // 4. 冷热转换（15%）
            int half = total / 2;
            int olderHalf = zodiacData.Skip(half).Count(z => z == zodiac);
            int newerHalf = zodiacData.Take(half).Count(z => z == zodiac);

            if (olderHalf == 0 && newerHalf > 0)
            {
                score.HotColdScore = 100;
                score.HotColdTrend = "上涨";
            }
            else if (olderHalf > 0 && newerHalf == 0)
            {
                score.HotColdScore = 10;
                score.HotColdTrend = "下降";
            }
            else if (olderHalf > 0 && newerHalf > 0)
            {
                double olderRate = (double)olderHalf / half;
                double newerRate = (double)newerHalf / half;
                double changeRatio = newerRate / olderRate;

                if (changeRatio > 1.5)
                {
                    score.HotColdScore = 80 + Math.Min((changeRatio - 1.5) * 20, 20);
                    score.HotColdTrend = "上涨";
                }
                else if (changeRatio > 0.8)
                {
                    score.HotColdScore = 50;
                    score.HotColdTrend = "平稳";
                }
                else
                {
                    score.HotColdScore = Math.Max(changeRatio * 50, 10);
                    score.HotColdTrend = "下降";
                }
            }
            else
            {
                score.HotColdScore = 50;
                score.HotColdTrend = "平稳";
            }

            // 5. 周期规律（15%）
            if (intervals.Count >= 3)
            {
                double avg = intervals.Average();
                double variance = intervals.Select(i => Math.Pow(i - avg, 2)).Average();
                double cv = avg > 0 ? Math.Sqrt(variance) / avg : 1;
                // 变异系数越小，规律性越强
                score.PeriodPatternScore = Math.Max(0, (1 - cv) * 100);
            }
            else if (intervals.Count >= 1)
            {
                score.PeriodPatternScore = 40;
            }
            else
            {
                score.PeriodPatternScore = 20;
            }

            // 6. 连号关联（10%）
            score.ConsecutiveScore = CalculateConsecutiveScore(zodiac, zodiacData);

            // 综合评分
            score.TotalScore =
                score.FrequencyScore * weights.FrequencyWeight +
                score.RecentTrendScore * weights.RecentTrendWeight +
                score.OmissionScore * weights.OmissionWeight +
                score.HotColdScore * weights.HotColdWeight +
                score.PeriodPatternScore * weights.PeriodPatternWeight +
                score.ConsecutiveScore * weights.ConsecutiveWeight;

            return score;
        }

        private void ApplyEightZodiacRule(List<ZodiacScoreV2> scores, string lastZodiac)
        {
            if (string.IsNullOrEmpty(lastZodiac)) return;
            if (!EightZodiacRules.TryGetValue(lastZodiac, out var eightZodiacs)) return;

            double bonus = GetEightZodiacBonus(lastZodiac);
            foreach (var score in scores)
            {
                if (eightZodiacs.Contains(score.Zodiac))
                {
                    score.EightZodiacScore = bonus;
                    score.TotalScore += bonus;
                }
            }
        }

        private double GetEightZodiacBonus(string lastZodiac)
        {
            double rate = EightZodiacHitRates.TryGetValue(lastZodiac, out double value) ? value : 0.7154;

            if (rate >= 0.80) return 8;
            if (rate >= 0.75) return 7;
            if (rate >= 0.70) return 6;
            if (rate >= 0.65) return 5;
            return 4;
        }

        private static readonly Dictionary<string, HashSet<string>> EightZodiacRules = new Dictionary<string, HashSet<string>>
        {
            { "鼠", new HashSet<string> { "羊", "牛", "蛇", "鸡", "猴", "龙", "狗", "虎" } },
            { "牛", new HashSet<string> { "蛇", "鸡", "兔", "猪", "马", "鼠", "龙", "猴" } },
            { "虎", new HashSet<string> { "蛇", "猪", "兔", "羊", "马", "狗", "龙", "虎" } },
            { "兔", new HashSet<string> { "羊", "猪", "牛", "蛇", "龙", "狗", "马", "虎" } },
            { "龙", new HashSet<string> { "兔", "鸡", "牛", "蛇", "猴", "鼠", "虎", "马" } },
            { "蛇", new HashSet<string> { "牛", "鸡", "羊", "兔", "虎", "猴", "龙", "鼠" } },
            { "马", new HashSet<string> { "牛", "羊", "兔", "猪", "虎", "狗", "猴", "龙" } },
            { "羊", new HashSet<string> { "兔", "猪", "鸡", "蛇", "鼠", "马", "虎", "猴" } },
            { "猴", new HashSet<string> { "猪", "蛇", "牛", "鸡", "鼠", "龙", "马", "狗" } },
            { "鸡", new HashSet<string> { "鸡", "兔", "羊", "猪", "马", "虎", "鼠", "猴" } },
            { "狗", new HashSet<string> { "鸡", "牛", "兔", "猪", "鼠", "猴", "虎", "马" } },
            { "猪", new HashSet<string> { "羊", "鸡", "牛", "鼠", "猴", "虎", "马", "狗" } }
        };

        private static readonly Dictionary<string, double> EightZodiacHitRates = new Dictionary<string, double>
        {
            { "鼠", 0.7143 },
            { "牛", 0.8108 },
            { "虎", 0.7708 },
            { "兔", 0.6444 },
            { "龙", 0.6458 },
            { "蛇", 0.7442 },
            { "马", 0.8125 },
            { "羊", 0.6923 },
            { "猴", 0.7429 },
            { "鸡", 0.7027 },
            { "狗", 0.6486 },
            { "猪", 0.6296 }
        };

        // ===== 连号关联分析 =====
        private double CalculateConsecutiveScore(string zodiac, List<string> zodiacData)
        {
            double score = 50;

            // 1. 连续出现模式
            int consecutivePairs = 0;
            for (int i = 0; i < zodiacData.Count - 1; i++)
            {
                if (zodiacData[i] == zodiac && zodiacData[i + 1] == zodiac)
                    consecutivePairs++;
            }
            if (consecutivePairs > 0)
                score += consecutivePairs * 5;

            // 2. 关联分析：该生肖出现后，下一期是什么生肖
            var nextZodiacs = new Dictionary<string, int>();
            for (int i = 0; i < zodiacData.Count - 1; i++)
            {
                if (zodiacData[i] == zodiac)
                {
                    string next = zodiacData[i + 1];
                    if (!nextZodiacs.ContainsKey(next))
                        nextZodiacs[next] = 0;
                    nextZodiacs[next]++;
                }
            }

            // 如果该生肖出现后，某个生肖频繁跟随，说明有关联
            if (nextZodiacs.Count > 0)
            {
                var maxFollow = nextZodiacs.Values.Max();
                int totalAppear = zodiacData.Count(z => z == zodiac);
                if (totalAppear > 0 && maxFollow > totalAppear * 0.2)
                {
                    score += 20; // 有关联加分
                }
            }

            // 3. 间隔出现模式（如每隔2-3期出现一次）
            var intervals = GetAppearIntervals(zodiac, zodiacData);
            if (intervals.Count >= 2)
            {
                // 检查是否有固定间隔
                var modeInterval = intervals.GroupBy(x => x)
                    .OrderByDescending(g => g.Count())
                    .FirstOrDefault();
                if (modeInterval != null && modeInterval.Count() >= 2)
                {
                    score += 15;
                }
            }

            return Math.Max(0, Math.Min(100, score));
        }

        // ===== 滑动窗口预测 =====
        private Dictionary<string, PredictResultV2> RunSlidingWindows(List<string> zodiacData, WeightConfig weights)
        {
            var windows = new Dictionary<string, PredictResultV2>();
            int[] windowSizes = { 20, 50, 100, 200 };

            foreach (var size in windowSizes)
            {
                if (zodiacData.Count >= size)
                {
                    var windowData = zodiacData.Take(size).ToList();
                    var windowResult = new PredictResultV2
                    {
                        AnalysisPeriods = size
                    };

                    foreach (var zodiac in ZodiacOrder)
                    {
                        var score = CalculateZodiacScoreV2(zodiac, windowData, weights);
                        windowResult.AllScores.Add(score);
                    }

                    var sorted = windowResult.AllScores.OrderByDescending(s => s.TotalScore).ToList();
                    windowResult.Top3 = sorted.Take(3).Select(s => s.Zodiac).ToList();
                    windowResult.Top6 = sorted.Take(6).Select(s => s.Zodiac).ToList();

                    windows[$"{size}期"] = windowResult;
                }
            }

            return windows;
        }

        // ===== 可信度计算 =====
        private string CalculateConfidence(List<ZodiacScoreV2> sorted)
        {
            if (sorted.Count < 3) return "低";

            double topScore = sorted[0].TotalScore;
            double secondScore = sorted[1].TotalScore;
            double thirdScore = sorted[2].TotalScore;

            // 第一名和第二名差距越大，可信度越高
            double gap1 = topScore - secondScore;
            double gap2 = secondScore - thirdScore;

            if (gap1 > 10 && gap2 > 5)
                return "高";
            else if (gap1 > 5)
                return "中";
            else
                return "低";
        }

        // ===== 确定最佳模型 =====
        private string DetermineBestModel(WeightConfig weights)
        {
            var modelParts = new List<string>();
            var weightPairs = new List<(string name, double weight)>
            {
                ("频率", weights.FrequencyWeight),
                ("走势", weights.RecentTrendWeight),
                ("遗漏", weights.OmissionWeight),
                ("冷热", weights.HotColdWeight),
                ("周期", weights.PeriodPatternWeight),
                ("关联", weights.ConsecutiveWeight)
            };

            var topWeights = weightPairs.OrderByDescending(w => w.weight).Take(3).ToList();
            return string.Join("+", topWeights.Select(w => w.name)) + "模型";
        }

        // ===== 权重自动优化 =====
        private WeightConfig LoadBestWeights(List<string> zodiacData)
        {
            // 尝试多种权重方案，选择回测命中率最高的
            var candidates = new List<(string name, WeightConfig config)>
            {
                ("均衡", new WeightConfig {
                    FrequencyWeight = 0.20, RecentTrendWeight = 0.20, OmissionWeight = 0.20,
                    HotColdWeight = 0.15, PeriodPatternWeight = 0.15, ConsecutiveWeight = 0.10
                }),
                ("重遗漏", new WeightConfig {
                    FrequencyWeight = 0.15, RecentTrendWeight = 0.15, OmissionWeight = 0.30,
                    HotColdWeight = 0.15, PeriodPatternWeight = 0.15, ConsecutiveWeight = 0.10
                }),
                ("重走势", new WeightConfig {
                    FrequencyWeight = 0.15, RecentTrendWeight = 0.30, OmissionWeight = 0.15,
                    HotColdWeight = 0.15, PeriodPatternWeight = 0.15, ConsecutiveWeight = 0.10
                }),
                ("重冷热", new WeightConfig {
                    FrequencyWeight = 0.15, RecentTrendWeight = 0.15, OmissionWeight = 0.15,
                    HotColdWeight = 0.25, PeriodPatternWeight = 0.20, ConsecutiveWeight = 0.10
                }),
                ("重周期", new WeightConfig {
                    FrequencyWeight = 0.10, RecentTrendWeight = 0.15, OmissionWeight = 0.15,
                    HotColdWeight = 0.15, PeriodPatternWeight = 0.30, ConsecutiveWeight = 0.15
                }),
            };

            // 如果数据不足，使用默认均衡方案
            if (zodiacData.Count < 100)
                return candidates[0].config;

            // 对每种方案进行快速回测
            double bestRate = 0;
            var bestConfig = candidates[0].config;

            foreach (var candidate in candidates)
            {
                double hitRate = QuickBacktest(zodiacData, candidate.config);
                if (hitRate > bestRate)
                {
                    bestRate = hitRate;
                    bestConfig = candidate.config;
                }
            }

            return bestConfig;
        }

        // ===== 快速回测（用于权重优化）=====
        private double QuickBacktest(List<string> zodiacData, WeightConfig weights)
        {
            int trainPeriods = Math.Min(100, zodiacData.Count / 2);
            int testCount = Math.Min(30, zodiacData.Count - trainPeriods);
            if (testCount < 5) return 0;

            int hits = 0;
            for (int i = 0; i < testCount; i++)
            {
                int testPos = trainPeriods + i;
                string actual = zodiacData[testPos];
                var trainData = zodiacData.Take(trainPeriods + i).ToList();

                var scores = ZodiacOrder.Select(z =>
                {
                    var s = CalculateZodiacScoreV2(z, trainData, weights);
                    return (zodiac: z, score: s.TotalScore);
                }).OrderByDescending(s => s.score).Take(3).Select(s => s.zodiac).ToList();

                if (scores.Contains(actual)) hits++;
            }

            return (double)hits / testCount;
        }

        // ===== 完整回测 =====
        public BacktestResultV2 Backtest(int trainPeriods = 100, int testCount = 50)
        {
            var result = new BacktestResultV2 { TrainPeriods = trainPeriods };

            var allHistory = DatabaseHelper.GetLatestHistory(trainPeriods + testCount + 50);
            var validHistory = allHistory
                .Where(r => !string.IsNullOrEmpty(r.SpecialZodiac))
                .Reverse()
                .ToList();
            var zodiacData = validHistory.Select(r => r.SpecialZodiac).ToList();
            var periodData = validHistory.Select(r => r.Period).ToList();

            int totalTests = Math.Min(testCount, zodiacData.Count - trainPeriods);
            if (totalTests <= 0) return result;

            result.TotalTests = totalTests;

            // 获取最优权重
            // 权重只在最初训练区间选择，不能查看后续测试结果。
            var bestWeights = LoadBestWeights(zodiacData.Take(trainPeriods).Reverse().ToList());
            result.BestWeights = bestWeights;
            result.BestModel = DetermineBestModel(bestWeights);

            int consecutiveHits = 0;
            int consecutiveMiss = 0;
            int maxHits = 0;
            int maxMiss = 0;

            for (int i = 0; i < totalTests; i++)
            {
                int testPos = trainPeriods + i;
                string actualZodiac = zodiacData[testPos];
                string period = testPos < periodData.Count ? periodData[testPos] : "";

                var trainData = zodiacData.Take(trainPeriods + i).Reverse().ToList();

                var scores = ZodiacOrder.Select(z =>
                {
                    var s = CalculateZodiacScoreV2(z, trainData, bestWeights);
                    return (zodiac: z, score: s.TotalScore);
                }).OrderByDescending(s => s.score).ToList();

                var top3 = scores.Take(3).Select(s => s.zodiac).ToList();
                var top6 = scores.Take(6).Select(s => s.zodiac).ToList();

                bool top3Hit = top3.Contains(actualZodiac);
                bool top6Hit = top6.Contains(actualZodiac);

                if (top3Hit) { result.Top3Hits++; consecutiveHits++; consecutiveMiss = 0; }
                else { consecutiveMiss++; consecutiveHits = 0; }
                if (top6Hit) result.Top6Hits++;

                if (consecutiveHits > maxHits) maxHits = consecutiveHits;
                if (consecutiveMiss > maxMiss) maxMiss = consecutiveMiss;

                result.Records.Add(new BacktestRecord
                {
                    TestIndex = i + 1,
                    Period = period,
                    ActualZodiac = actualZodiac,
                    PredictedTop3 = top3,
                    PredictedTop6 = top6,
                    Top3Hit = top3Hit,
                    Top6Hit = top6Hit
                });
            }

            result.MaxConsecutiveHits = maxHits;
            result.MaxConsecutiveMiss = maxMiss;

            return result;
        }

        // ===== 辅助方法 =====
        private int GetLastAppearIndex(string zodiac, List<string> zodiacData)
        {
            for (int i = 0; i < zodiacData.Count; i++)
                if (zodiacData[i] == zodiac) return i;
            return -1;
        }

        private List<int> GetAppearIntervals(string zodiac, List<string> zodiacData)
        {
            var intervals = new List<int>();
            int lastPos = -1;
            for (int i = 0; i < zodiacData.Count; i++)
            {
                if (zodiacData[i] == zodiac)
                {
                    if (lastPos >= 0) intervals.Add(i - lastPos);
                    lastPos = i;
                }
            }
            return intervals;
        }

        private ZodiacScoreV2 CreateEmptyScore(string zodiac)
        {
            return new ZodiacScoreV2
            {
                Zodiac = zodiac,
                TotalScore = 50,
                FrequencyScore = 50,
                RecentTrendScore = 50,
                OmissionScore = 50,
                HotColdScore = 50,
                PeriodPatternScore = 50,
                ConsecutiveScore = 50,
                HotColdTrend = "未知"
            };
        }

        // ===== 公开方法：供回测模块调用 =====
        public double CalculateScoreForBacktest(string zodiac, List<string> zodiacData)
        {
            var weights = LoadBestWeights(zodiacData);
            return CalculateZodiacScoreV2(zodiac, zodiacData, weights).TotalScore;
        }

        // ===== 热门/冷门生肖排行 =====
        public List<(string Zodiac, int Count, double Rate)> GetHotZodiacs(int periodCount = 200)
        {
            var history = DatabaseHelper.GetLatestHistory(periodCount > 0 ? periodCount : int.MaxValue);
            var zodiacData = history.Where(r => !string.IsNullOrEmpty(r.SpecialZodiac))
                .Select(r => r.SpecialZodiac).ToList();
            int total = zodiacData.Count;

            var result = new List<(string Zodiac, int Count, double Rate)>();
            foreach (var z in ZodiacOrder)
            {
                int count = zodiacData.Count(zd => zd == z);
                double rate = total > 0 ? (double)count / total * 100 : 0;
                result.Add((z, count, rate));
            }
            return result.OrderByDescending(x => x.Count).ToList();
        }

        public List<(string Zodiac, int Count, double Rate)> GetColdZodiacs(int periodCount = 200)
        {
            return GetHotZodiacs(periodCount).OrderBy(x => x.Count).ToList();
        }
    }
}
