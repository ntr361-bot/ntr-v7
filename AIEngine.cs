using System;
using System.Collections.Generic;
using System.Linq;

namespace 六合分析软件
{
    /// <summary>
    /// 统一 AI 特码生肖预测引擎 V3.0
    /// 所有预测调用同一个 Predict 方法
    /// 结果自动保存数据库，首页和预测窗口读取同一结果
    /// </summary>
    public static class AIEngine
    {
        public const string Version = "AI生肖预测 V6.0";
        private const int DefaultPeriods = 500; // 默认分析500期

        /// <summary>
        /// 统一预测结果
        /// </summary>
        public class PredictResult
        {
            public string Version { get; set; } = AIEngine.Version;
            public string PredictPeriod { get; set; } = "";  // 预测期号
            public int AnalysisPeriods { get; set; }
            public DateTime PredictTime { get; set; }
            public List<ZodiacPredictEngineV2.ZodiacScoreV2> AllScores { get; set; } = new List<ZodiacPredictEngineV2.ZodiacScoreV2>();
            public List<string> Top3 { get; set; } = new List<string>();
            public List<string> Top6 { get; set; } = new List<string>();
            public List<string> Bottom3 { get; set; } = new List<string>();
            public List<int> RecommendedNumbers { get; set; } = new List<int>();
            public string NumberScoreDetails { get; set; } = "";
            public string FirstTier { get; set; } = "";
            public string SecondTier { get; set; } = "";
            public string Eliminated { get; set; } = "";
            public string Confidence { get; set; } = "";
            public string BestModel { get; set; } = "";
            public string AnalysisText { get; set; } = ""; // GPT 分析文本
            public bool UsedGpt { get; set; } = false;
            public Dictionary<string, ZodiacPredictEngineV2.PredictResultV2> WindowResults { get; set; } = new Dictionary<string, ZodiacPredictEngineV2.PredictResultV2>();
        }

        // ===== 内存缓存 =====
        private static PredictResult? _memoryCache;
        private static string _memoryCacheKey = "";

        /// <summary>
        /// 统一预测入口
        /// </summary>
        /// <param name="periodCount">使用真实特码生肖数据的分析期数</param>
        /// <param name="forceRefresh">是否强制重新预测（忽略缓存）</param>
        public static PredictResult Predict(int periodCount = 0, bool forceRefresh = false)
        {
            int periods = periodCount > 0 ? periodCount : AISettings.AnalysisPeriods;
            string cacheKey = $"{periods}_{DatabaseHelper.GetLatestPeriod()}";

            // 如果缓存有效且未强制刷新，直接返回
            if (!forceRefresh && _memoryCache != null && _memoryCacheKey == cacheKey)
            {
                return _memoryCache;
            }

            // 尝试从数据库加载缓存
            if (!forceRefresh && periodCount == 0)
            {
                var dbCache = LoadFromDatabase();
                if (dbCache != null && dbCache.AnalysisPeriods == AISettings.AnalysisPeriods)
                {
                    // 检查数据库最新期号是否变化
                    if (_memoryCache == null || _memoryCacheKey != cacheKey)
                    {
                        _memoryCache = dbCache;
                        _memoryCacheKey = cacheKey;
                        return dbCache;
                    }
                }
            }

            // 执行预测
            var result = RunPrediction(periods, includeExternalAnalysis: true, targetPeriod: null);

            // 每次预测都按目标期号和分析周期留档，开奖后自动验证。
            SaveToDatabase(result);

            // 更新内存缓存
            _memoryCache = result;
            _memoryCacheKey = cacheKey;

            return result;
        }

        /// <summary>
        /// 执行实际预测
        /// </summary>
        public static PredictResult GenerateForAutomation(int periodCount = 0, string? targetPeriod = null)
        {
            int periods = periodCount > 0 ? periodCount : AISettings.AnalysisPeriods;
            PredictResult result = RunPrediction(periods, includeExternalAnalysis: false, targetPeriod);
            if (!string.IsNullOrWhiteSpace(targetPeriod))
                result.PredictPeriod = targetPeriod.Trim();
            return result;
        }

        public static void SavePredictionHistory(PredictResult result) => SaveToDatabase(result);

        private static PredictResult RunPrediction(int periods, bool includeExternalAnalysis, string? targetPeriod)
        {
            var engine = new ZodiacPredictEngineV2();
            var optimizedWeights = WeightOptimizationService.FindBestWeights(
                Math.Min(300, Math.Max(50, periods - 100)),
                Math.Min(50, Math.Max(20, periods - Math.Min(300, Math.Max(50, periods - 100)))));
            var v2Result = optimizedWeights.TotalTests > 0
                ? engine.Predict(periods, optimizedWeights.Weights)
                : engine.Predict(periods);
            var rollingBacktest = includeExternalAnalysis
                ? RollingBacktestService.Run(500)
                : new RollingBacktestResult();
            var modelCompetition = includeExternalAnalysis
                ? ModelCompetitionService.RunCompetition(500)
                : new List<ModelScoreResult>();

            // 计算预测期号（最新期号 + 1）
            string nextPeriod = "";
            try
            {
                var latest = DatabaseHelper.GetLatestHistory(1);
                if (latest.Count > 0 && !string.IsNullOrEmpty(latest[0].Period))
                {
                    int latestNum = int.Parse(latest[0].Period);
                    nextPeriod = (latestNum + 1).ToString();
                }
            }
            catch { }

            var result = new PredictResult
            {
                PredictPeriod = nextPeriod,
                AnalysisPeriods = v2Result.AnalysisPeriods,
                PredictTime = DateTime.Now,
                AllScores = v2Result.AllScores,
                Top3 = v2Result.Top3,
                Top6 = v2Result.Top6,
                Bottom3 = v2Result.Bottom3,
                FirstTier = v2Result.FirstTier,
                SecondTier = v2Result.SecondTier,
                Eliminated = v2Result.Eliminated,
                Confidence = v2Result.Confidence,
                BestModel = v2Result.BestModel,
                WindowResults = v2Result.WindowResults
            };

            BuildRecommendedNumbers(result, periods, targetPeriod: targetPeriod);

            // 构建 GPT 分析提示词
            var hotZodiacs = engine.GetHotZodiacs(periods);
            var coldZodiacs = engine.GetColdZodiacs(periods);
            var recentZodiacs = DatabaseHelper.GetLatestHistory(periods)
                .Where(r => !string.IsNullOrEmpty(r.SpecialZodiac))
                .Select(r => r.SpecialZodiac)
                .Take(10)
                .ToList();

            var prompt = new System.Text.StringBuilder();
            prompt.AppendLine("你是六合彩特码生肖分析专家。请根据以下 V6 自动权重优化、多模型竞争和滚动回测数据，预测下一期最可能出现的特码生肖。");
            prompt.AppendLine();
            prompt.AppendLine($"分析周期：{v2Result.AnalysisPeriods} 期");
            prompt.AppendLine($"可信度：{v2Result.Confidence}");
            prompt.AppendLine($"最佳模型：{v2Result.BestModel}");
            if (optimizedWeights.TotalTests > 0)
            {
                prompt.AppendLine($"自动权重：频率{optimizedWeights.Weights.FrequencyWeight:P0} 趋势{optimizedWeights.Weights.RecentTrendWeight:P0} 遗漏{optimizedWeights.Weights.OmissionWeight:P0} 模式{(optimizedWeights.Weights.PeriodPatternWeight + optimizedWeights.Weights.ConsecutiveWeight):P0}");
                prompt.AppendLine($"权重训练回测：Top3 {optimizedWeights.Top3HitRate:F2}% Top6 {optimizedWeights.Top6HitRate:F2}% 综合评分{optimizedWeights.CombinedScore:F1}");
            }
            if (rollingBacktest.TotalTests > 0)
                prompt.AppendLine($"滚动回测：平均Top3 {rollingBacktest.AverageTop3HitRate:F2}% 平均Top6 {rollingBacktest.AverageTop6HitRate:F2}% 稳定性{rollingBacktest.StabilityGrade}级");
            if (modelCompetition.Count > 0)
                prompt.AppendLine($"模型竞争第一名：{modelCompetition[0].ModelName} 综合评分{modelCompetition[0].CombinedScore:F1}");
            prompt.AppendLine();
            prompt.AppendLine("--- 12生肖综合评分排行 ---");
            foreach (var s in v2Result.AllScores.OrderByDescending(x => x.TotalScore))
            {
                prompt.AppendLine($"{s.Zodiac}：总分{s.TotalScore:F1}（频率{s.FrequencyScore:F1} 走势{s.RecentTrendScore:F1} 遗漏{s.OmissionScore:F1} 冷热{s.HotColdScore:F1} 周期{s.PeriodPatternScore:F1} 关联{s.ConsecutiveScore:F1} 八肖加分{s.EightZodiacScore:F1}）出现{s.TotalAppear}次 趋势{s.HotColdTrend}");
            }
            prompt.AppendLine();
            prompt.AppendLine("--- 热门生肖 ---");
            foreach (var z in hotZodiacs)
                prompt.AppendLine($"{z.Zodiac}：{z.Count}次（{z.Rate:F1}%）");
            prompt.AppendLine();
            prompt.AppendLine("--- 最近10期走势 ---");
            for (int i = 0; i < Math.Min(10, recentZodiacs.Count); i++)
                prompt.AppendLine($"第{i + 1}期：{recentZodiacs[i]}");
            prompt.AppendLine();
            prompt.AppendLine("【推荐6生肖】【重点关注3个】【风险生肖】【分析理由】");

            // 调用 GPT（可能降级为本地）
            string v6LocalReport = PredictionExplanationService.BuildReport(v2Result, optimizedWeights, rollingBacktest, modelCompetition);
            if (includeExternalAnalysis)
            {
                var gptResult = OpenAIService.Analyze(prompt.ToString(), null);
                result.AnalysisText = v6LocalReport + Environment.NewLine + gptResult.AnalysisText;
                result.UsedGpt = !gptResult.UsedFallback;
            }
            else
            {
                result.AnalysisText = v6LocalReport;
                result.UsedGpt = false;
            }

            return result;
        }

        /// <summary>
        /// 从数据库加载缓存
        /// </summary>
        private static PredictResult? LoadFromDatabase()
        {
            try
            {
                var records = DatabaseHelper.GetLatestAIPredictRecord();
                if (records == null) return null;

                var result = new PredictResult
                {
                    PredictPeriod = records.PredictPeriod,
                    AnalysisPeriods = records.AnalysisPeriods,
                    PredictTime = DateTime.TryParse(records.PredictDate, out var dt) ? dt : DateTime.Now,
                    AnalysisText = records.GptAnalysis,
                    FirstTier = records.Focus3 ?? "",
                    Top3 = records.Focus3?.Split(',').Where(s => !string.IsNullOrEmpty(s)).ToList() ?? new List<string>(),
                    Top6 = records.Recommended6?.Split(',').Where(s => !string.IsNullOrEmpty(s)).ToList() ?? new List<string>(),
                };

                return result;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 保存预测结果到数据库（每期唯一，已存在则更新）
        /// </summary>
        private static void SaveToDatabase(PredictResult result)
        {
            try
            {
                string nextPeriod = "";
                string yearPet = "";
                try
                {
                    var latest = DatabaseHelper.GetLatestHistory(1);
                    if (latest.Count > 0 && !string.IsNullOrEmpty(latest[0].Period))
                    {
                        int latestNum = int.Parse(latest[0].Period);
                        nextPeriod = (latestNum + 1).ToString();
                        
                        // 获取年份生肖
                        string year = latest[0].OpenTime.Length >= 4 ? latest[0].OpenTime.Substring(0, 4) : "";
                        if (!string.IsNullOrEmpty(year))
                        {
                            yearPet = DatabaseHelper.GetYearPetPublic(year);
                        }
                    }
                }
                catch { }

                if (string.IsNullOrEmpty(nextPeriod))
                    return;

                string predictNumbers = string.Join(",", result.RecommendedNumbers.Select(n => n.ToString("D2")));

                string scoreDetails = string.Join(";", result.AllScores
                    .OrderByDescending(s => s.TotalScore)
                    .Select(s => $"{s.Zodiac}:{s.TotalScore:F1}|频{s.FrequencyScore:F1}|势{s.RecentTrendScore:F1}|漏{s.OmissionScore:F1}|冷{s.HotColdScore:F1}|周{s.PeriodPatternScore:F1}|联{s.ConsecutiveScore:F1}|八{s.EightZodiacScore:F1}"));

                DatabaseHelper.SavePrediction(
                    nextPeriod,                                    // Issue 开奖期号
                    string.Join(",", result.Top3),                 // PredictZodiac 推荐生肖
                    string.Join(",", result.Top6),                 // Top6Zodiac
                    predictNumbers,                                // PredictNumber 推荐号码
                    DatabaseHelper.GetCurrentModelVersion(),       // ModelVersion（来自AIModels表）
                    result.AnalysisPeriods,
                    scoreDetails + "#重点号码:" + result.NumberScoreDetails);
            }
            catch { }
        }

        /// <summary>
        /// 清除缓存（用于数据更新后重新训练）
        /// </summary>
        public static void InvalidateCache()
        {
            _memoryCache = null;
            _memoryCacheKey = "";
        }

        /// <summary>
        /// 在重点生肖对应号码中，根据真实特码历史做二次筛选。
        /// </summary>
        private static void BuildRecommendedNumbers(
            PredictResult result, int periods, int takeCount = 8, string? targetPeriod = null)
        {
            try
            {
                var latest = DatabaseHelper.GetLatestHistory(1);
                string year = targetPeriod?.Length >= 4
                    ? targetPeriod.Substring(0, 4)
                    : latest.FirstOrDefault()?.OpenTime?.Length >= 4
                    ? latest[0].OpenTime.Substring(0, 4)
                    : latest.FirstOrDefault()?.Date?.Length >= 4 ? latest[0].Date.Substring(0, 4) : "";
                string yearPet = string.IsNullOrEmpty(year) ? "" : DatabaseHelper.GetYearPetPublic(year);
                if (string.IsNullOrEmpty(yearPet) || result.Top3.Count == 0) return;

                var map = DataCrawler.BuildShengXiaoMapPublic(yearPet);
                var history = DatabaseHelper.GetLatestHistory(periods)
                    .Select(h => int.TryParse(h.SpecialNumber, out int n) && n >= 1 && n <= 49 ? (int?)n : null)
                    .ToList();
                var zodiacScores = result.AllScores.ToDictionary(s => s.Zodiac, s => s.TotalScore);
                var candidates = new List<(int number, string zodiac, double score, string detail)>();

                foreach (string zodiac in result.Top3)
                {
                    if (!map.TryGetValue(zodiac, out var zodiacNumbers)) continue;
                    foreach (string text in zodiacNumbers)
                    {
                        if (!int.TryParse(text, out int number)) continue;
                        int total = history.Count(n => n == number);
                        int recent10 = history.Take(10).Count(n => n == number);
                        int recent30 = history.Take(30).Count(n => n == number);
                        int missing = history.TakeWhile(n => n != number).Count();
                        double expectedAppear = Math.Max(1.0, history.Count / 49.0);
                        double frequencyScore = Math.Min(100, total / expectedAppear * 50);
                        double recentScore = Math.Min(100, recent10 * 35 + recent30 * 8);
                        double avgInterval = total > 0 ? (double)history.Count / total : 49;
                        double omissionScore = Math.Max(0, 100 - Math.Abs(missing - avgInterval) / Math.Max(1, avgInterval) * 60);
                        double zodiacScore = zodiacScores.GetValueOrDefault(zodiac);
                        double score = zodiacScore * 0.50 + recentScore * 0.20 +
                            frequencyScore * 0.15 + omissionScore * 0.15;
                        string detail = $"{number:D2}({zodiac})={score:F1}[肖{zodiacScore:F1},近10:{recent10},近30:{recent30},总:{total},漏:{missing}]";
                        candidates.Add((number, zodiac, score, detail));
                    }
                }

                var selected = new List<(int number, string zodiac, double score, string detail)>();
                foreach (string zodiac in result.Top3)
                {
                    var best = candidates.Where(c => c.zodiac == zodiac).OrderByDescending(c => c.score).FirstOrDefault();
                    if (best.number > 0) selected.Add(best);
                }
                selected.AddRange(candidates.OrderByDescending(c => c.score)
                    .Where(c => selected.All(s => s.number != c.number))
                    .Take(Math.Max(0, takeCount - selected.Count)));
                selected = selected.OrderByDescending(c => c.score).ToList();
                result.RecommendedNumbers = selected.Select(c => c.number).ToList();
                result.NumberScoreDetails = string.Join(";", selected.Select(c => c.detail));
            }
            catch
            {
                result.RecommendedNumbers.Clear();
                result.NumberScoreDetails = "";
            }
        }

        /// <summary>
        /// 数据更新后重新训练
        /// </summary>
        public static PredictResult Retrain()
        {
            InvalidateCache();
            return Predict(AISettings.AnalysisPeriods, forceRefresh: true);
        }
    }
}
