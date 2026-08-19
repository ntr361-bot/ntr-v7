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
        // Keep the public model identifier aligned with the V6.5 formal release.
        // The external reasoning model is tracked separately by OpenAIService.
        public const string Version = "AI生肖预测 V6.5";
        private const int DefaultPeriods = AISettings.AllHistoryModeValue;

        /// <summary>
        /// 统一预测结果
        /// </summary>
        public class PredictResult
        {
            public string Version { get; set; } = AIEngine.Version;
            public string PredictPeriod { get; set; } = "";  // 预测期号
            public int AnalysisPeriods { get; set; }
            public DateTime PredictTime { get; set; }
            public List<V65RuleScoringEngine.ZodiacScoreV2> AllScores { get; set; } = new List<V65RuleScoringEngine.ZodiacScoreV2>();
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
            public Dictionary<string, V65RuleScoringEngine.PredictResultV2> WindowResults { get; set; } = new Dictionary<string, V65RuleScoringEngine.PredictResultV2>();
        }

        // ===== 内存缓存 =====
        private static PredictResult? _memoryCache;
        private static string _memoryCacheKey = "";

        /// <summary>
        /// 统一预测入口
        /// </summary>
        /// <param name="periodCount">使用真实特码生肖数据的分析期数</param>
        /// <param name="forceRefresh">是否强制重新预测（忽略缓存）</param>
        public static PredictResult Predict(int periodCount = -1, bool forceRefresh = false)
        {
            int periods = ResolveRequestedPeriods(periodCount);
            string cacheKey = $"prediction-v2|{Version}|{periods}|{DatabaseHelper.GetLatestPeriod()}";
            string cacheName = $"ai-prediction-{periods}";

            // 如果缓存有效且未强制刷新，直接返回
            if (!forceRefresh && _memoryCache != null && _memoryCacheKey == cacheKey)
            {
                return _memoryCache;
            }

            // 跨进程缓存保留完整评分、号码和分析文本。
            if (!forceRefresh && JsonFileCache.TryLoad<PredictResult>(cacheName, cacheKey, out var persistedCache))
            {
                _memoryCache = persistedCache;
                _memoryCacheKey = cacheKey;
                return persistedCache!;
            }

            // 执行预测
            var result = RunPrediction(periods, includeExternalAnalysis: true);

            // 每次预测都按目标期号和分析周期留档，开奖后自动验证。
            SaveToDatabase(result);

            // 更新内存缓存
            _memoryCache = result;
            _memoryCacheKey = cacheKey;
            JsonFileCache.Save(cacheName, cacheKey, result);

            return result;
        }

        /// <summary>
        /// 强制刷新正式版的全部分析周期，并将每个周期的结果分别保存到预测历史。
        /// 字典键使用配置周期值，其中 0 表示全部历史。
        /// </summary>
        public static Dictionary<int, PredictResult> RefreshAllPeriodPredictions()
        {
            // 展示档只有100期；50期/全部历史由每日自动化在后台计算供自动学习学习。
            int[] periods = { 100 };
            var results = new Dictionary<int, PredictResult>();

            foreach (int period in periods)
                results[period] = Predict(period, forceRefresh: true);

            return results;
        }

        /// <summary>
        /// 执行实际预测
        /// </summary>
        public static PredictResult GenerateForAutomation(int periodCount = -1, string? targetPeriod = null)
        {
            int periods = ResolveRequestedPeriods(periodCount);
            PredictResult result = RunPrediction(periods, includeExternalAnalysis: false);
            if (!string.IsNullOrWhiteSpace(targetPeriod))
                result.PredictPeriod = targetPeriod.Trim();
            return result;
        }

        public static void SavePredictionHistory(PredictResult result) => SaveToDatabase(result);

        private static int ResolveRequestedPeriods(int requestedPeriods)
        {
            int periods = requestedPeriods < 0 ? AISettings.AnalysisPeriods : requestedPeriods;
            // 旧设置中的200/500期模型已取消，自动迁移为全部历史。
            return periods is 200 or 500 ? AISettings.AllHistoryModeValue : periods;
        }

        private static PredictResult RunPrediction(int periods, bool includeExternalAnalysis)
        {
            var engine = new V65RuleScoringEngine();
            int historyLimit = AISettings.ResolveHistoryLimit(periods);
            // 正式三条基础模型固定使用各自的 V6.5 权重；旧候选权重搜索只允许在实验回测中运行。
            var v2Result = engine.Predict(periods, V65ExperimentPipeline.GetWeightsForPeriods(periods));
            V65RuleScoringEngine.WeightConfig selectedWeights = v2Result.UsedWeights;
            string learningDetails = PredictionLearningService.ApplyCalibration(v2Result, v2Result.AnalysisPeriods);
            // 回测与模型竞争由 V65ExperimentBacktestService 按需执行，不能伪装为正式预测已经运行。
            RollingBacktestResult? rollingBacktest = null;
            List<ModelScoreResult>? modelCompetition = null;

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

            BuildRecommendedNumbers(result, periods);

            // 构建 GPT 分析提示词
            var hotZodiacs = engine.GetHotZodiacs(periods);
            var coldZodiacs = engine.GetColdZodiacs(periods);
            var recentZodiacs = DatabaseHelper.GetLatestHistory(historyLimit)
                .Where(r => !string.IsNullOrEmpty(r.SpecialZodiac))
                .Select(r => r.SpecialZodiac)
                .Take(10)
                .ToList();

            var prompt = new System.Text.StringBuilder();
            prompt.AppendLine("你是六合彩特码生肖分析专家。请根据以下 V6.5 自适应权重及错因学习数据，预测下一期最可能出现的特码生肖。");
            prompt.AppendLine();
            prompt.AppendLine($"分析周期：{v2Result.AnalysisPeriods} 期");
            prompt.AppendLine($"可信度：{v2Result.Confidence}");
            prompt.AppendLine($"最佳模型：{v2Result.BestModel}");
            prompt.AppendLine($"正式V6.5分周期权重：频率{selectedWeights.FrequencyWeight:P0} 趋势{selectedWeights.RecentTrendWeight:P0} 遗漏{selectedWeights.OmissionWeight:P0} 冷热{selectedWeights.HotColdWeight:P0} 周期{selectedWeights.PeriodPatternWeight + selectedWeights.ConsecutiveWeight:P0}");
            if (rollingBacktest?.TotalTests > 0)
                prompt.AppendLine($"滚动回测：平均Top3 {rollingBacktest.AverageTop3HitRate:F2}% 平均Top6 {rollingBacktest.AverageTop6HitRate:F2}% 稳定性{rollingBacktest.StabilityGrade}级");
            if (modelCompetition?.Count > 0)
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
            string v6LocalReport = PredictionExplanationService.BuildReport(v2Result, null, rollingBacktest, modelCompetition);
            v6LocalReport += Environment.NewLine + learningDetails;
            prompt.AppendLine();
            prompt.AppendLine("以下是程序已计算完成的本地智能报告。你只负责把报告润色成清晰、简洁的中文，");
            prompt.AppendLine("不得改变Top3、Top6、重点号码、评分排序或夸大确定性，并明确提示统计预测不等于保证。");
            prompt.AppendLine("--- 本地智能报告 ---");
            prompt.AppendLine(v6LocalReport);

            // GPT 仅润色；未配置、额度不足、限流或网络失败时完整返回本地智能报告。
            if (includeExternalAnalysis)
            {
                var gptResult = OpenAIService.Analyze(prompt.ToString(), null, v6LocalReport);
                result.AnalysisText = gptResult.UsedFallback
                    ? gptResult.AnalysisText
                    : "【分析来源】GPT润色（预测排序由本地V6.5算法确定）" +
                      Environment.NewLine + Environment.NewLine + gptResult.AnalysisText;
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

                result.PredictPeriod = nextPeriod;
                // 三条基础模型必须保留自己的原始排序；自动学习只能在它们保存完
                // 同一期快照后单独生成第四条预测，不能把基础模型反向覆盖。
                string[] finalRanking = result.AllScores
                    .OrderByDescending(s => s.TotalScore)
                    .Select(s => s.Zodiac)
                    .ToArray();
                string finalRankingJson = System.Text.Json.JsonSerializer.Serialize(finalRanking);
                string baseModelScoresJson = System.Text.Json.JsonSerializer.Serialize(
                    result.AllScores.ToDictionary(s => s.Zodiac, s => s.TotalScore));

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
                    scoreDetails + "#重点号码:" + result.NumberScoreDetails,
                    result.AnalysisText.Split(Environment.NewLine)
                        .LastOrDefault(line => line.StartsWith("错因学习：", StringComparison.Ordinal)) ?? "",
                    finalRankingJson,
                    baseModelScoresJson,
                    string.Empty,
                    string.Empty);
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
            JsonFileCache.RemoveByPrefix("ai-prediction-");
        }

        /// <summary>
        /// 在重点生肖对应号码中，根据真实特码历史做二次筛选。
        /// </summary>
        private static void BuildRecommendedNumbers(PredictResult result, int periods, int takeCount = 8)
        {
            try
            {
                var latest = DatabaseHelper.GetLatestHistory(1);
                string year = latest.FirstOrDefault()?.OpenTime?.Length >= 4
                    ? latest[0].OpenTime.Substring(0, 4)
                    : latest.FirstOrDefault()?.Date?.Length >= 4 ? latest[0].Date.Substring(0, 4) : "";
                string yearPet = string.IsNullOrEmpty(year) ? "" : DatabaseHelper.GetYearPetPublic(year);
                if (string.IsNullOrEmpty(yearPet) || result.Top3.Count == 0) return;

                var map = DataCrawler.BuildShengXiaoMapPublic(yearPet);
                int historyLimit = AISettings.ResolveHistoryLimit(periods);
                var history = DatabaseHelper.GetLatestHistory(historyLimit)
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
