using System;
using System.Collections.Generic;
using System.Linq;

namespace 六合分析软件
{
    /// <summary>
    /// 集成学习预测服务 - 立竿见影版本
    /// 融合多个子模型 + 动态权重调整
    /// </summary>
    public static class EnsemblePredictionService
    {
        public class EnsembleResult
        {
            public string Zodiac { get; set; } = "";
            public double FinalScore { get; set; }
            public double FrequencyModel { get; set; }
            public double TrendModel { get; set; }
            public double MissingModel { get; set; }
            public double PatternModel { get; set; }
            public double MomentumModel { get; set; }
            public string Confidence { get; set; } = "";
            public string Reason { get; set; } = "";
        }

        public class PredictionReport
        {
            public List<EnsembleResult> Predictions { get; set; } = new List<EnsembleResult>();
            public DateTime PredictTime { get; set; }
            public Dictionary<string, double> DynamicWeights { get; set; } = new Dictionary<string, double>();
            public EnsembleResult? Top1 => Predictions.Count > 0 ? Predictions[0] : null;
            public List<string> Top3 => Predictions.Take(3).Select(p => p.Zodiac).ToList();
        }

        /// <summary>
        /// 集成预测（核心方法）
        /// </summary>
        public static PredictionReport Predict(int periodRange = 500)
        {
            var report = new PredictionReport { PredictTime = DateTime.Now };

            // 获取历史数据
            var history = DatabaseHelper.GetLatestHistory(periodRange);
            if (history.Count < 50) return report;

            string[] allZodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };

            // 计算动态权重（基于最近50期各模型表现）
            var weights = CalculateDynamicWeights(history);
            report.DynamicWeights = weights;

            // 对每个生肖进行集成预测
            foreach (var zodiac in allZodiacs)
            {
                var result = new EnsembleResult { Zodiac = zodiac };

                // 子模型1：频率模型
                result.FrequencyModel = FrequencyModel(history, zodiac);

                // 子模型2：趋势模型
                result.TrendModel = TrendModel(history, zodiac);

                // 子模型3：遗漏模型
                result.MissingModel = MissingModel(history, zodiac);

                // 子模型4：模式识别模型（新增）
                result.PatternModel = PatternModel(history, zodiac);

                // 子模型5：动量模型（新增）
                result.MomentumModel = MomentumModel(history, zodiac);

                // 集成融合（加权平均）
                result.FinalScore = 
                    result.FrequencyModel * weights["frequency"] +
                    result.TrendModel * weights["trend"] +
                    result.MissingModel * weights["missing"] +
                    result.PatternModel * weights["pattern"] +
                    result.MomentumModel * weights["momentum"];

                // 置信度
                if (result.FinalScore >= 0.75) result.Confidence = "高";
                else if (result.FinalScore >= 0.60) result.Confidence = "中";
                else result.Confidence = "低";

                // 理由
                result.Reason = $"频率{result.FrequencyModel:F2} | 趋势{result.TrendModel:F2} | " +
                               $"遗漏{result.MissingModel:F2} | 模式{result.PatternModel:F2} | 动量{result.MomentumModel:F2}";

                report.Predictions.Add(result);
            }

            // 按最终得分排序
            report.Predictions = report.Predictions.OrderByDescending(p => p.FinalScore).ToList();

            return report;
        }

        /// <summary>
        /// 动态权重计算（基于最近50期各模型表现）
        /// </summary>
        private static Dictionary<string, double> CalculateDynamicWeights(List<DatabaseHelper.HistoryRecord> history)
        {
            var weights = new Dictionary<string, double>
            {
                { "frequency", 0.20 },
                { "trend", 0.25 },
                { "missing", 0.25 },
                { "pattern", 0.15 },
                { "momentum", 0.15 }
            };

            // 基于最近50期表现调整权重
            var recentHistory = history.Take(50).ToList();
            if (recentHistory.Count < 20) return weights;

            // 计算各模型在最近50期的预测准确率
            double freqAccuracy = EvaluateModelAccuracy(recentHistory, "frequency");
            double trendAccuracy = EvaluateModelAccuracy(recentHistory, "trend");
            double missingAccuracy = EvaluateModelAccuracy(recentHistory, "missing");
            double patternAccuracy = EvaluateModelAccuracy(recentHistory, "pattern");
            double momentumAccuracy = EvaluateModelAccuracy(recentHistory, "momentum");

            // 保留一半基础权重，另一半按严格滚动回测的准确率分配。
            double totalAccuracy = freqAccuracy + trendAccuracy + missingAccuracy + patternAccuracy + momentumAccuracy;
            if (totalAccuracy > 0)
            {
                var baseline = new Dictionary<string, double>(weights);
                var accuracy = new Dictionary<string, double> {
                    ["frequency"] = freqAccuracy, ["trend"] = trendAccuracy,
                    ["missing"] = missingAccuracy, ["pattern"] = patternAccuracy,
                    ["momentum"] = momentumAccuracy
                };
                foreach (var key in weights.Keys.ToList())
                    weights[key] = baseline[key] * 0.5 + accuracy[key] / totalAccuracy * 0.5;
            }

            return weights;
        }

        /// <summary>
        /// 评估模型准确率（简化版）
        /// </summary>
        private static double EvaluateModelAccuracy(List<DatabaseHelper.HistoryRecord> history, string modelName)
        {
            int correct = 0;
            int total = Math.Min(20, history.Count - 1);

            for (int i = 0; i < total; i++)
            {
                var actual = history[i].SpecialZodiac;
                // history 为新到旧；只用被测期之前的更旧记录。
                var predicted = GetModelPrediction(history.Skip(i + 1).ToList(), modelName);
                if (predicted == actual) correct++;
            }

            return total > 0 ? (double)correct / total : 0.5;
        }

        private static string GetModelPrediction(List<DatabaseHelper.HistoryRecord> history, string modelName)
        {
            string[] zodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
            if (history.Count == 0) return "";
            return zodiacs.Select(z => (zodiac: z, score: modelName switch
            {
                "frequency" => FrequencyModel(history, z),
                "trend" => TrendModel(history, z),
                "missing" => MissingModel(history, z),
                "pattern" => PatternModel(history, z),
                "momentum" => MomentumModel(history, z),
                _ => 0.0
            })).OrderByDescending(x => x.score).ThenBy(x => x.zodiac).First().zodiac;
        }

        /// <summary>
        /// 子模型1：频率模型
        /// </summary>
        private static double FrequencyModel(List<DatabaseHelper.HistoryRecord> history, string zodiac)
        {
            var zodiacs = history.Where(h => h.SpecialZodiac == zodiac).Count();
            double frequency = history.Count > 0 ? (double)zodiacs / history.Count : 0;
            
            // 归一化到0-1
            return Math.Min(1.0, frequency * 12); // 12个生肖，平均频率约0.083
        }

        /// <summary>
        /// 子模型2：趋势模型
        /// </summary>
        private static double TrendModel(List<DatabaseHelper.HistoryRecord> history, string zodiac)
        {
            var recent30 = history.Take(30).Where(h => h.SpecialZodiac == zodiac).Count();
            var previous30 = history.Skip(30).Take(30).Where(h => h.SpecialZodiac == zodiac).Count();

            double trend = (recent30 - previous30) / 30.0;
            
            // 归一化到0-1
            return Math.Max(0, Math.Min(1.0, 0.5 + trend * 2));
        }

        /// <summary>
        /// 子模型3：遗漏模型
        /// </summary>
        private static double MissingModel(List<DatabaseHelper.HistoryRecord> history, string zodiac)
        {
            int missing = 0;
            foreach (var h in history)
            {
                if (h.SpecialZodiac == zodiac) break;
                missing++;
            }

            // 计算平均遗漏
            var allMissings = new List<int>();
            int currentMissing = 0;
            foreach (var h in history)
            {
                if (h.SpecialZodiac == zodiac)
                {
                    if (currentMissing > 0) allMissings.Add(currentMissing);
                    currentMissing = 0;
                }
                else currentMissing++;
            }

            double avgMissing = allMissings.Count > 0 ? allMissings.Average() : 10;
            double ratio = missing / avgMissing;

            // 遗漏接近平均值时得分最高
            if (ratio >= 0.8 && ratio <= 1.5) return 0.9;
            else if (ratio >= 0.5 && ratio <= 2.0) return 0.7;
            else if (ratio < 0.3) return 0.3; // 刚出过
            else return 0.4; // 遗漏太久
        }

        /// <summary>
        /// 子模型4：模式识别模型（新增）
        /// 检测周期性模式
        /// </summary>
        private static double PatternModel(List<DatabaseHelper.HistoryRecord> history, string zodiac)
        {
            // 检测最近出现间隔
            var intervals = new List<int>();
            int lastSeen = -1;
            
            for (int i = 0; i < history.Count; i++)
            {
                if (history[i].SpecialZodiac == zodiac)
                {
                    if (lastSeen >= 0) intervals.Add(i - lastSeen);
                    lastSeen = i;
                }
            }

            if (intervals.Count < 3) return 0.5; // 数据不足

            // 计算间隔的标准差（越小越规律）
            double avgInterval = intervals.Average();
            double variance = intervals.Select(x => Math.Pow(x - avgInterval, 2)).Average();
            double stdDev = Math.Sqrt(variance);

            // 规律性得分（标准差越小得分越高）
            double regularity = 1.0 - Math.Min(1.0, stdDev / 10.0);
            
            // 当前遗漏与平均间隔的匹配度
            int currentMissing = 0;
            foreach (var h in history)
            {
                if (h.SpecialZodiac == zodiac) break;
                currentMissing++;
            }

            double matchScore = 1.0 - Math.Min(1.0, Math.Abs(currentMissing - avgInterval) / avgInterval);

            return (regularity + matchScore) / 2.0;
        }

        /// <summary>
        /// 子模型5：动量模型（新增）
        /// 检测短期动量
        /// </summary>
        private static double MomentumModel(List<DatabaseHelper.HistoryRecord> history, string zodiac)
        {
            // 最近10期出现次数
            var recent10 = history.Take(10).Where(h => h.SpecialZodiac == zodiac).Count();
            
            // 最近20期出现次数
            var recent20 = history.Take(20).Where(h => h.SpecialZodiac == zodiac).Count();

            // 动量 = 近期表现 / 长期表现
            double momentum = recent20 > 0 ? (double)recent10 / recent20 : 0.5;

            // 归一化到0-1
            return Math.Max(0, Math.Min(1.0, momentum));
        }

        /// <summary>
        /// 生成预测报告文本
        /// </summary>
        public static string GenerateReport(PredictionReport report)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("═══════════════════════════════════════════");
            sb.AppendLine("     集成学习预测报告 V2.0");
            sb.AppendLine("═══════════════════════════════════════════");
            sb.AppendLine($"预测时间：{report.PredictTime:yyyy-MM-dd HH:mm}");
            sb.AppendLine();

            sb.AppendLine("── 动态权重 ──");
            foreach (var w in report.DynamicWeights)
            {
                sb.AppendLine($"  {w.Key}: {w.Value:F2}");
            }
            sb.AppendLine();

            sb.AppendLine("── TOP 6 推荐 ──");
            for (int i = 0; i < Math.Min(6, report.Predictions.Count); i++)
            {
                var p = report.Predictions[i];
                string badge = i < 3 ? "⭐" : "  ";
                sb.AppendLine($"{badge} #{i + 1} {p.Zodiac} 综合:{p.FinalScore:F2} " +
                             $"[频{p.FrequencyModel:F2}+势{p.TrendModel:F2}+漏{p.MissingModel:F2}+模{p.PatternModel:F2}+动{p.MomentumModel:F2}] " +
                             $"置信度:{p.Confidence}");
            }

            sb.AppendLine();
            sb.AppendLine("── 评分详情 ──");
            foreach (var p in report.Predictions)
            {
                sb.AppendLine($"  {p.Zodiac} {p.FinalScore:F2} → {p.Reason}");
            }

            sb.AppendLine();
            sb.AppendLine("═══════════════════════════════════════════");
            return sb.ToString();
        }
    }
}
