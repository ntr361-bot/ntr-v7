using System;
using System.Collections.Generic;
using System.Linq;

namespace 六合分析软件
{
    /// <summary>
    /// AI特码生肖预测引擎
    /// 基于历史开奖数据，分析特码生肖规律，预测下一期最可能出现的生肖
    /// </summary>
    public class ZodiacPredictEngine
    {
        // 12生肖固定顺序
        private static readonly string[] ZodiacOrder =
        {
            "鼠", "牛", "虎", "兔", "龙", "蛇",
            "马", "羊", "猴", "鸡", "狗", "猪"
        };

        /// <summary>
        /// 单个生肖的预测评分结果
        /// </summary>
        public class ZodiacScore
        {
            public string Zodiac { get; set; } = "";
            public double TotalScore { get; set; }
            public double FrequencyScore { get; set; }   // 出现频率分
            public double OmissionScore { get; set; }     // 遗漏周期分
            public double TrendScore { get; set; }        // 周期趋势分
            public double RecentTrendScore { get; set; }  // 最近走势分
            public double PatternScore { get; set; }      // 连续规律分
            public int AppearCount { get; set; }          // 出现次数
            public int LastAppearIndex { get; set; }      // 上次出现位置（距最新多少期）
            public double AvgInterval { get; set; }       // 平均出现间隔
        }

        /// <summary>
        /// 预测结果
        /// </summary>
        public class PredictResult
        {
            public int AnalysisPeriods { get; set; }       // 分析期数
            public List<ZodiacScore> AllScores { get; set; } = new List<ZodiacScore>();
            public List<string> Top3 { get; set; } = new List<string>();
            public List<string> Top6 { get; set; } = new List<string>();
            public List<string> Top3WithScore { get; set; } = new List<string>();
            public List<string> Top6WithScore { get; set; } = new List<string>();
            public string FirstTier { get; set; } = "";    // 第一梯队（前3）
            public string SecondTier { get; set; } = "";   // 第二梯队（4-6）
            public string FocusZodiacs { get; set; } = ""; // 重点关注
        }

        /// <summary>
        /// 回测结果
        /// </summary>
        public class BacktestResult
        {
            public int TotalTests { get; set; }
            public int Top3Hits { get; set; }
            public int Top6Hits { get; set; }
            public double Top3HitRate => TotalTests > 0 ? (double)Top3Hits / TotalTests * 100 : 0;
            public double Top6HitRate => TotalTests > 0 ? (double)Top6Hits / TotalTests * 100 : 0;
            public List<BacktestDetail> Details { get; set; } = new List<BacktestDetail>();
        }

        public class BacktestDetail
        {
            public int TestIndex { get; set; }
            public string ActualZodiac { get; set; } = "";
            public bool Top3Hit { get; set; }
            public bool Top6Hit { get; set; }
            public List<string> PredictedTop3 { get; set; } = new List<string>();
            public List<string> PredictedTop6 { get; set; } = new List<string>();
        }

        /// <summary>
        /// 执行预测
        /// </summary>
        /// <param name="periodCount">分析期数（50/100/200/0=全部）</param>
        public PredictResult Predict(int periodCount = 200)
        {
            var result = new PredictResult();

            // 1. 获取历史数据
            var history = DatabaseHelper.GetLatestHistory(periodCount > 0 ? periodCount : int.MaxValue);

            // 只分析特码生肖（SpecialZodiac），不分析普通6个号码
            var zodiacData = history
                .Where(r => !string.IsNullOrEmpty(r.SpecialZodiac))
                .Select(r => r.SpecialZodiac)
                .ToList();

            result.AnalysisPeriods = zodiacData.Count;

            if (zodiacData.Count < 10)
            {
                // 数据不足
                foreach (var z in ZodiacOrder)
                {
                    result.AllScores.Add(new ZodiacScore
                    {
                        Zodiac = z,
                        TotalScore = 50,
                        FrequencyScore = 50,
                        OmissionScore = 50,
                        TrendScore = 50,
                        RecentTrendScore = 50,
                        PatternScore = 50,
                        AppearCount = 0,
                        LastAppearIndex = -1,
                        AvgInterval = 0
                    });
                }
                return result;
            }

            // 2. 计算每个生肖的各项评分
            foreach (var zodiac in ZodiacOrder)
            {
                var score = CalculateZodiacScore(zodiac, zodiacData);
                result.AllScores.Add(score);
            }

            // 3. 排序
            var sorted = result.AllScores.OrderByDescending(s => s.TotalScore).ToList();

            result.Top3 = sorted.Take(3).Select(s => s.Zodiac).ToList();
            result.Top6 = sorted.Take(6).Select(s => s.Zodiac).ToList();
            result.Top3WithScore = sorted.Take(3).Select(s => $"{s.Zodiac} {s.TotalScore:F0}分").ToList();
            result.Top6WithScore = sorted.Take(6).Select(s => $"{s.Zodiac} {s.TotalScore:F0}分").ToList();

            // 4. 分梯队
            result.FirstTier = string.Join(" ", sorted.Take(3).Select(s => $"{s.Zodiac}({s.TotalScore:F0})"));
            result.SecondTier = string.Join(" ", sorted.Skip(3).Take(3).Select(s => $"{s.Zodiac}({s.TotalScore:F0})"));
            result.FocusZodiacs = string.Join("、", result.Top3);

            return result;
        }

        /// <summary>
        /// 计算单个生肖的综合评分
        /// </summary>
        private ZodiacScore CalculateZodiacScore(string zodiac, List<string> zodiacData)
        {
            var score = new ZodiacScore
            {
                Zodiac = zodiac
            };

            int total = zodiacData.Count;

            // 1. 出现频率分（30%）
            int appearCount = zodiacData.Count(z => z == zodiac);
            score.AppearCount = appearCount;
            double frequencyRate = (double)appearCount / total;
            // 理论概率约 1/12 ≈ 8.33%，超过理论值得分高
            score.FrequencyScore = Math.Min(frequencyRate * 12 * 100, 100);

            // 2. 遗漏周期分（25%）
            int lastAppearIndex = GetLastAppearIndex(zodiac, zodiacData);
            score.LastAppearIndex = lastAppearIndex;
            // 遗漏越久，分数越高（回补逻辑）
            if (lastAppearIndex < 0)
            {
                // 从未出现，给最高分
                score.OmissionScore = 100;
            }
            else
            {
                // 遗漏期数占总数据的比例
                double omissionRatio = (double)lastAppearIndex / total;
                score.OmissionScore = Math.Min(omissionRatio * 200, 100);
            }

            // 3. 周期趋势分（20%）
            var intervals = GetAppearIntervals(zodiac, zodiacData);
            score.AvgInterval = intervals.Count > 0 ? intervals.Average() : total;
            // 如果平均间隔小于理论值(12)，说明出现频繁，趋势好
            double theoreticalInterval = total / Math.Max(appearCount, 1);
            if (appearCount == 0)
            {
                score.TrendScore = 30; // 从未出现，趋势分低
            }
            else
            {
                // 间隔越短，趋势分越高
                score.TrendScore = Math.Min(theoreticalInterval / 12 * 50 + 50, 100);
            }

            // 4. 最近走势分（15%）
            int recentCount = Math.Min(20, total);
            var recentData = zodiacData.Take(recentCount).ToList();
            int recentAppear = recentData.Count(z => z == zodiac);
            // 最近20期出现越多，走势分越高
            score.RecentTrendScore = Math.Min((double)recentAppear / recentCount * 12 * 100, 100);

            // 5. 连续规律分（10%）
            score.PatternScore = CalculatePatternScore(zodiac, zodiacData);

            // 6. 综合评分
            score.TotalScore =
                score.FrequencyScore * 0.30 +
                score.OmissionScore * 0.25 +
                score.TrendScore * 0.20 +
                score.RecentTrendScore * 0.15 +
                score.PatternScore * 0.10;

            return score;
        }

        /// <summary>
        /// 获取上次出现的位置（从最新往回数，0=最新一期就是）
        /// </summary>
        private int GetLastAppearIndex(string zodiac, List<string> zodiacData)
        {
            for (int i = 0; i < zodiacData.Count; i++)
            {
                if (zodiacData[i] == zodiac)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// 获取每次出现的间隔列表
        /// </summary>
        private List<int> GetAppearIntervals(string zodiac, List<string> zodiacData)
        {
            var intervals = new List<int>();
            int lastPos = -1;

            for (int i = 0; i < zodiacData.Count; i++)
            {
                if (zodiacData[i] == zodiac)
                {
                    if (lastPos >= 0)
                    {
                        intervals.Add(i - lastPos);
                    }
                    lastPos = i;
                }
            }

            return intervals;
        }

        /// <summary>
        /// 计算连续规律分
        /// 分析：连续出现模式和冷热转换
        /// </summary>
        private double CalculatePatternScore(string zodiac, List<string> zodiacData)
        {
            double score = 50; // 基础分

            // 1. 检查是否有连续出现的模式（如连续2期出现）
            int consecutivePairs = 0;
            for (int i = 0; i < zodiacData.Count - 1; i++)
            {
                if (zodiacData[i] == zodiac && zodiacData[i + 1] == zodiac)
                    consecutivePairs++;
            }
            if (consecutivePairs > 0)
            {
                score += 15; // 有连续出现模式加分
            }

            // 2. 冷热转换分析：如果之前冷，最近热，说明有转热趋势
            int half = zodiacData.Count / 2;
            int olderHalf = zodiacData.Skip(half).Count(z => z == zodiac);
            int newerHalf = zodiacData.Take(half).Count(z => z == zodiac);

            if (olderHalf < newerHalf && newerHalf > 0)
            {
                score += 20; // 从冷转热，加分
            }
            else if (olderHalf > newerHalf)
            {
                score -= 10; // 从热转冷，减分
            }

            // 3. 周期性检查：是否呈现规律性出现
            var intervals = GetAppearIntervals(zodiac, zodiacData);
            if (intervals.Count >= 3)
            {
                double avg = intervals.Average();
                double variance = intervals.Select(i => Math.Pow(i - avg, 2)).Average();
                double cv = avg > 0 ? Math.Sqrt(variance) / avg : 1;
                // 变异系数越小，规律性越强
                score += Math.Max(0, (1 - cv) * 30);
            }

            return Math.Max(0, Math.Min(100, score));
        }

        /// <summary>
        /// 回测：用历史数据模拟预测
        /// </summary>
        /// <param name="totalRecords">使用的总记录数</param>
        /// <param name="testCount">测试次数（从后往前推）</param>
        /// <param name="trainPeriods">每次训练使用的期数</param>
        public BacktestResult Backtest(int totalRecords = 300, int testCount = 50, int trainPeriods = 100)
        {
            var result = new BacktestResult();

            // 获取足够的数据
            var allHistory = DatabaseHelper.GetLatestHistory(totalRecords + testCount);
            var zodiacData = allHistory
                .Where(r => !string.IsNullOrEmpty(r.SpecialZodiac))
                .Select(r => r.SpecialZodiac)
                .ToList();

            if (zodiacData.Count < trainPeriods + testCount)
            {
                return result; // 数据不足
            }

            int top3Hits = 0;
            int top6Hits = 0;
            int totalTests = Math.Min(testCount, zodiacData.Count - trainPeriods);

            for (int i = 0; i < totalTests; i++)
            {
                // 用前 trainPeriods 期预测第 trainPeriods+1 期
                int testPosition = trainPeriods + i;
                string actualZodiac = zodiacData[testPosition];

                // 取训练数据
                var trainData = zodiacData.Take(trainPeriods + i).ToList();

                // 对每个生肖计算评分
                var scores = new List<(string zodiac, double score)>();
                foreach (var zodiac in ZodiacOrder)
                {
                    var score = CalculateZodiacScoreFromData(zodiac, trainData);
                    scores.Add((zodiac, score));
                }

                // 排序
                var sorted = scores.OrderByDescending(s => s.score).ToList();
                var predictedTop3 = sorted.Take(3).Select(s => s.zodiac).ToList();
                var predictedTop6 = sorted.Take(6).Select(s => s.zodiac).ToList();

                bool top3Hit = predictedTop3.Contains(actualZodiac);
                bool top6Hit = predictedTop6.Contains(actualZodiac);

                if (top3Hit) top3Hits++;
                if (top6Hit) top6Hits++;

                result.Details.Add(new BacktestDetail
                {
                    TestIndex = i + 1,
                    ActualZodiac = actualZodiac,
                    Top3Hit = top3Hit,
                    Top6Hit = top6Hit,
                    PredictedTop3 = predictedTop3,
                    PredictedTop6 = predictedTop6
                });
            }

            result.TotalTests = totalTests;
            result.Top3Hits = top3Hits;
            result.Top6Hits = top6Hits;

            return result;
        }

        /// <summary>
        /// 从指定数据计算生肖评分（用于回测）
        /// </summary>
        public double CalculateZodiacScoreFromData(string zodiac, List<string> zodiacData)
        {
            int total = zodiacData.Count;
            if (total < 10) return 50;

            int appearCount = zodiacData.Count(z => z == zodiac);

            // 频率分
            double frequencyRate = (double)appearCount / total;
            double frequencyScore = Math.Min(frequencyRate * 12 * 100, 100);

            // 遗漏分
            int lastAppearIndex = GetLastAppearIndex(zodiac, zodiacData);
            double omissionScore = lastAppearIndex < 0 ? 100 : Math.Min((double)lastAppearIndex / total * 200, 100);

            // 趋势分
            var intervals = GetAppearIntervals(zodiac, zodiacData);
            double theoreticalInterval = total / Math.Max(appearCount, 1);
            double trendScore = appearCount == 0 ? 30 : Math.Min(theoreticalInterval / 12 * 50 + 50, 100);

            // 最近走势分
            int recentCount = Math.Min(20, total);
            int recentAppear = zodiacData.Take(recentCount).Count(z => z == zodiac);
            double recentTrendScore = Math.Min((double)recentAppear / recentCount * 12 * 100, 100);

            // 连续规律分
            double patternScore = CalculatePatternScore(zodiac, zodiacData);

            return frequencyScore * 0.30 + omissionScore * 0.25 + trendScore * 0.20 + recentTrendScore * 0.15 + patternScore * 0.10;
        }

        /// <summary>
        /// 获取热门生肖排行（按出现频率）
        /// </summary>
        public List<(string Zodiac, int Count, double Rate)> GetHotZodiacs(int periodCount = 200)
        {
            var history = DatabaseHelper.GetLatestHistory(periodCount > 0 ? periodCount : int.MaxValue);
            var zodiacData = history.Where(r => !string.IsNullOrEmpty(r.SpecialZodiac)).Select(r => r.SpecialZodiac).ToList();
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

        /// <summary>
        /// 获取冷门生肖排行（按出现频率，从低到高）
        /// </summary>
        public List<(string Zodiac, int Count, double Rate)> GetColdZodiacs(int periodCount = 200)
        {
            return GetHotZodiacs(periodCount).OrderBy(x => x.Count).ToList();
        }
    }
}
