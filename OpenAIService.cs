using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace 六合分析软件
{
    /// <summary>
    /// GPT API 调用服务
    /// 支持配置 API Key 和模型名称
    /// API 失败时自动降级为本地统计预测
    /// </summary>
    public static class OpenAIService
    {
        // ===== 配置项 =====
        public static string ApiKey { get; set; } = "";
        public static string ApiBaseUrl { get; set; } = "https://api.openai.com/v1";
        public static string Model { get; set; } = "gpt-5.6-sol";

        private static readonly HttpClient _httpClient = new HttpClient();

        static OpenAIService()
        {
            _httpClient.Timeout = TimeSpan.FromSeconds(60);
        }

        /// <summary>
        /// GPT 分析结果
        /// </summary>
        public class GptAnalysisResult
        {
            public bool Success { get; set; }
            public string ErrorMessage { get; set; } = "";
            public string AnalysisText { get; set; } = "";       // GPT 分析文本
            public List<string> Recommended6 { get; set; } = new List<string>();  // 推荐6生肖
            public List<string> Focus3 { get; set; } = new List<string>();        // 重点关注3生肖
            public List<string> RiskZodiacs { get; set; } = new List<string>();   // 风险生肖
            public bool UsedFallback { get; set; } = false;       // 是否使用了本地降级
        }

        /// <summary>
        /// 构建发送给 GPT 的分析报告
        /// </summary>
        public static string BuildAnalysisPrompt(ZodiacPredictEngine.PredictResult predictResult,
            List<(string Zodiac, int Count, double Rate)> hotZodiacs,
            List<(string Zodiac, int Count, double Rate)> coldZodiacs,
            List<string> recentZodiacs)
        {
            var prompt = new System.Text.StringBuilder();

            prompt.AppendLine("你是六合彩特码生肖分析专家。请根据以下历史数据统计数据，预测下一期最可能出现的特码生肖。");
            prompt.AppendLine();
            prompt.AppendLine("=== 历史数据统计 ===");
            prompt.AppendLine($"分析周期：最近 {predictResult.AnalysisPeriods} 期");
            prompt.AppendLine();

            // 12生肖评分排行
            prompt.AppendLine("--- 12生肖综合评分排行 ---");
            foreach (var s in predictResult.AllScores.OrderByDescending(x => x.TotalScore))
            {
                prompt.AppendLine($"{s.Zodiac}：总分{s.TotalScore:F1}（频率{s.FrequencyScore:F1} 遗漏{s.OmissionScore:F1} 趋势{s.TrendScore:F1} 走势{s.RecentTrendScore:F1} 规律{s.PatternScore:F1}）出现{s.AppearCount}次 上次遗漏{s.LastAppearIndex}期");
            }
            prompt.AppendLine();

            // 热门生肖
            prompt.AppendLine("--- 热门生肖（按出现频率） ---");
            foreach (var z in hotZodiacs)
            {
                prompt.AppendLine($"{z.Zodiac}：{z.Count}次（{z.Rate:F1}%）");
            }
            prompt.AppendLine();

            // 冷门生肖
            prompt.AppendLine("--- 冷门生肖（按出现频率） ---");
            foreach (var z in coldZodiacs)
            {
                prompt.AppendLine($"{z.Zodiac}：{z.Count}次（{z.Rate:F1}%）");
            }
            prompt.AppendLine();

            // 最近走势
            prompt.AppendLine("--- 最近10期生肖走势 ---");
            for (int i = 0; i < Math.Min(10, recentZodiacs.Count); i++)
            {
                prompt.AppendLine($"第{i + 1}期：{recentZodiacs[i]}");
            }
            prompt.AppendLine();

            prompt.AppendLine("=== 请返回以下格式的分析结果 ===");
            prompt.AppendLine("【推荐6生肖】按可能性排序，列出6个生肖");
            prompt.AppendLine("【重点关注】从6个中选出最可能的3个");
            prompt.AppendLine("【风险生肖】列出3个最不可能出现的生肖");
            prompt.AppendLine("【分析理由】简要说明分析逻辑和依据（100字以内）");
            prompt.AppendLine();
            prompt.AppendLine("请直接返回结果，不要使用JSON格式。");

            return prompt.ToString();
        }

        /// <summary>
        /// 调用 GPT API 分析
        /// 如果 API 失败，自动降级为本地统计预测
        /// </summary>
        public static async Task<GptAnalysisResult> AnalyzeAsync(string prompt,
            ZodiacPredictEngine.PredictResult? localResult)
        {
            var result = new GptAnalysisResult();

            // 检查 API Key
            if (string.IsNullOrEmpty(ApiKey))
            {
                result.Success = true;
                result.UsedFallback = true;
                result.AnalysisText = "⚠️ 未配置 GPT API Key，使用本地统计预测。\n\n请在设置中配置 API Key 以启用 GPT 智能分析。\n\n本地预测结果：\n重点关注：" + (localResult?.FocusZodiacs ?? "数据不足");
                result.Recommended6 = localResult?.Top6 ?? new List<string>();
                result.Focus3 = localResult?.Top3 ?? new List<string>();
                return result;
            }

            try
            {
                var response = await CallGptApiAsync(prompt);

                if (response == null)
                {
                    throw new Exception("API 返回为空");
                }

                result.Success = true;
                result.AnalysisText = response;
                result.UsedFallback = false;

                // 尝试从 GPT 回复中提取推荐生肖
                ParseGptResponse(response, result, localResult);
            }
            catch (Exception ex)
            {
                result.Success = true; // 不报错，降级
                result.UsedFallback = true;
                result.ErrorMessage = ex.Message;
                result.AnalysisText = $"⚠️ GPT API 调用失败（{ex.Message}），已自动切换为本地统计预测。\n\n本地预测结果：\n重点关注：" + (localResult?.FocusZodiacs ?? "数据不足");
                result.Recommended6 = localResult?.Top6 ?? new List<string>();
                result.Focus3 = localResult?.Top3 ?? new List<string>();
            }

            return result;
        }

        /// <summary>
        /// 调用 GPT API
        /// </summary>
        private static async Task<string> CallGptApiAsync(string prompt)
        {
            var url = $"{ApiBaseUrl.TrimEnd('/')}/chat/completions";

            var body = new
            {
                model = Model,
                messages = new[]
                {
                    new { role = "system", content = "你是六合彩特码生肖分析专家，擅长根据历史数据统计规律进行预测分析。" },
                    new { role = "user", content = prompt }
                },
                temperature = 0.7,
                max_tokens = 1000
            };

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {ApiKey}");

            var response = await _httpClient.PostAsJsonAsync(url, body);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"HTTP {(int)response.StatusCode} {response.StatusCode}: {json.Substring(0, Math.Min(200, json.Length))}");
            }

            // 解析返回
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("message", out var message))
                {
                    if (message.TryGetProperty("content", out var content))
                    {
                        return content.GetString() ?? "";
                    }
                }
            }

            throw new Exception("无法解析 GPT 返回内容");
        }

        /// <summary>
        /// 解析 GPT 回复，提取推荐生肖
        /// </summary>
        private static void ParseGptResponse(string response, GptAnalysisResult result,
            ZodiacPredictEngine.PredictResult? localResult)
        {
            string[] allZodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };

            // 尝试提取【推荐6生肖】
            int idx6 = response.IndexOf("【推荐6生肖】");
            int idx3 = response.IndexOf("【重点关注】");
            int idxRisk = response.IndexOf("【风险生肖】");
            int idxReason = response.IndexOf("【分析理由】");

            if (idx6 >= 0)
            {
                string section = idx3 >= 0 ? response.Substring(idx6, idx3 - idx6) : response.Substring(idx6, Math.Min(200, response.Length - idx6));
                foreach (var z in allZodiacs)
                {
                    if (section.Contains(z) && result.Recommended6.Count < 6)
                        result.Recommended6.Add(z);
                }
            }

            if (idx3 >= 0)
            {
                string section = idxRisk >= 0 ? response.Substring(idx3, idxRisk - idx3) :
                    idxReason >= 0 ? response.Substring(idx3, idxReason - idx3) :
                    response.Substring(idx3, Math.Min(200, response.Length - idx3));
                foreach (var z in allZodiacs)
                {
                    if (section.Contains(z) && result.Focus3.Count < 3)
                        result.Focus3.Add(z);
                }
            }

            if (idxRisk >= 0)
            {
                string section = idxReason >= 0 ? response.Substring(idxRisk, idxReason - idxRisk) : response.Substring(idxRisk, Math.Min(200, response.Length - idxRisk));
                foreach (var z in allZodiacs)
                {
                    if (section.Contains(z) && result.RiskZodiacs.Count < 3)
                        result.RiskZodiacs.Add(z);
                }
            }

            // 如果解析不足，用本地结果补充
            if (result.Recommended6.Count < 6 && localResult != null)
            {
                foreach (var z in localResult.Top6)
                {
                    if (!result.Recommended6.Contains(z))
                        result.Recommended6.Add(z);
                    if (result.Recommended6.Count >= 6) break;
                }
            }

            if (result.Focus3.Count < 3 && localResult != null)
            {
                foreach (var z in localResult.Top3)
                {
                    if (!result.Focus3.Contains(z))
                        result.Focus3.Add(z);
                    if (result.Focus3.Count >= 3) break;
                }
            }

            // 提取分析理由
            if (idxReason >= 0)
            {
                string reasonSection = response.Substring(idxReason);
                // 保留完整的分析文本
            }
        }

        /// <summary>
        /// 同步版本（用于 UI 线程）
        /// </summary>
        public static GptAnalysisResult Analyze(string prompt, ZodiacPredictEngine.PredictResult? localResult)
        {
            return AnalyzeAsync(prompt, localResult).GetAwaiter().GetResult();
        }
    }
}
