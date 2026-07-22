using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace 六合分析软件
{
    /// <summary>
    /// 六合彩历史数据爬虫
    /// 数据源：https://api.00853lhc.com/api/HistoryOpenInfo
    /// </summary>
    public class DataCrawler
    {
        // API 基础地址
        private static readonly string ApiBaseUrl = "https://api.00853lhc.com/api/";
        private static readonly int LotteryId = 2032;
        private static readonly HttpClient HttpClient = CreateHttpClient();

        // 生肖缓存
        private static Dictionary<string, Dictionary<string, List<string>>> _shengxiaoCache = new Dictionary<string, Dictionary<string, List<string>>>();

        /// <summary>
        /// 抓取结果
        /// </summary>
        public class CrawlResult
        {
            public bool Success { get; set; }
            public string Message { get; set; } = "";
            public int NewCount { get; set; }
            public int TotalCount { get; set; }
            public int DbTotal { get; set; }
            public string RemoteLatestPeriod { get; set; } = "";
        }

        /// <summary>
        /// 解析出的单条记录
        /// </summary>
        public class CrawlRecord
        {
            public string Period { get; set; } = "";      // 期号
            public string Date { get; set; } = "";         // 开奖时间
            public string Numbers { get; set; } = "";      // 前6个开奖号码
            public string SpecialNumber { get; set; } = "";// 特码（第7个号码）
            public string SpecialZodiac { get; set; } = "";// 特码生肖（网站直接提供）
            public string ShengXiao { get; set; } = "";    // 兼容旧字段
        }

        /// <summary>
        /// API 响应模型
        /// </summary>
        private class ApiResponse
        {
            public int code { get; set; }
            public string message { get; set; } = "";
            public List<ApiRecord> data { get; set; } = new List<ApiRecord>();
        }

        private class ApiRecord
        {
            public string issue { get; set; } = "";
            public string openCode { get; set; } = "";
            public string openTime { get; set; } = "";
            public string pet { get; set; } = "";
        }

        /// <summary>
        /// 获取 API 最新期号（不保存，仅用于检测）
        /// </summary>
        public static async Task<string> GetLatestPeriodAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                string json = await FetchApiAsync(DateTime.Now.ToString("yyyy-M-d"), cancellationToken);
                if (string.IsNullOrEmpty(json))
                    return "";

                var records = ParseJson(json);
                if (records.Count > 0)
                    return records[0].Period;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { AppLogger.Error("获取最新期号", ex); }
            return "";
        }

        /// <summary>
        /// 执行抓取并保存
        /// </summary>
        public static async Task<CrawlResult> FetchAndSaveAsync()
        {
            return await FetchAndSaveAsync(500, false);
        }

        public static async Task<CrawlResult> FetchAndSaveAsync(int periods)
        {
            return await FetchAndSaveAsync(periods, false);
        }

        /// <summary>
        /// 执行抓取并保存
        /// </summary>
        /// <param name="isTest">是否为测试模式（不保存数据）</param>
        public static async Task<CrawlResult> FetchAndSaveAsync(bool isTest)
        {
            return await FetchAndSaveAsync(500, isTest);
        }

        public static async Task<CrawlResult> FetchAndSaveAsync(int periods, bool isTest, CancellationToken cancellationToken = default)
        {
            var result = new CrawlResult();
            if (periods <= 0) periods = 500;

            try
            {
                // 1. 获取数据库最新期号
                string latestPeriod = DatabaseHelper.GetLatestPeriod();
                Console.WriteLine($"[爬虫] 数据库最新期号: {latestPeriod}");

                // 2. 按年份向前抓取，直到凑够所选期数
                var records = await FetchRecentRecordsAsync(periods, cancellationToken);
                if (records.Count == 0)
                {
                    result.Message = "未解析到开奖数据";
                    return result;
                }

                result.TotalCount = records.Count;
                ValidateCrawlRecords(records);
                result.RemoteLatestPeriod = records
                    .OrderByDescending(r => long.TryParse(r.Period, out long issue) ? issue : 0)
                    .First().Period;

                // 测试模式：只返回解析结果，不保存
                if (isTest)
                {
                    result.Success = true;
                    result.NewCount = records.Count;
                    result.Message = $"测试成功！连接正常，解析到 {records.Count} 条记录";
                    return result;
                }

                // 4. HashSet 去重
                var existingPeriods = new HashSet<string>();
                foreach (var r in DatabaseHelper.GetHistory())
                {
                    if (!string.IsNullOrEmpty(r.Period))
                        existingPeriods.Add(r.Period);
                    else if (!string.IsNullOrEmpty(r.Numbers))
                        existingPeriods.Add(r.Numbers); // 旧数据兼容
                }

                List<CrawlRecord> newRecords = new List<CrawlRecord>();
                foreach (var r in records)
                {
                    if (!existingPeriods.Contains(r.Period))
                    {
                        newRecords.Add(r);
                    }
                }

                // 5. 在单个事务中批量保存；数据库唯一索引负责最终去重。
                int savedCount = DatabaseHelper.SaveCrawlerData(newRecords);

                // 6. 结果
                result.Success = true;
                result.NewCount = savedCount;
                result.DbTotal = existingPeriods.Count + savedCount;

                if (savedCount > 0)
                {
                    result.Message = $"网站抓取：{records.Count} 条\r\n新增：{savedCount} 条\r\n数据库总数：{result.DbTotal} 条";
                }
                else
                {
                    result.Message = $"网站抓取：{records.Count} 条\r\n新增：0 条\r\n数据库总数：{result.DbTotal} 条";
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Error("抓取开奖记录", ex);
                result.Message = $"抓取失败：{ex.Message}";
            }

            return result;
        }

        /// <summary>
        /// 保存前严格校验第三方数据，避免损坏或不完整响应进入正式历史库。
        /// </summary>
        public static void ValidateCrawlRecords(IReadOnlyCollection<CrawlRecord> records)
        {
            if (records.Count == 0)
                throw new InvalidDataException("开奖 API 没有返回任何记录");

            string[] validZodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
            var issues = new HashSet<long>();
            foreach (CrawlRecord record in records)
            {
                if (!long.TryParse(record.Period, out long issue) || issue <= 0)
                    throw new InvalidDataException($"开奖 API 返回非法期号：{record.Period}");
                if (!issues.Add(issue))
                    throw new InvalidDataException($"开奖 API 返回重复期号：{issue}");
                if (record.Numbers.Length != 12 || !record.Numbers.All(char.IsDigit))
                    throw new InvalidDataException($"第{issue}期前六个号码格式无效：{record.Numbers}");
                int[] regularNumbers = Enumerable.Range(0, 6)
                    .Select(index => int.Parse(record.Numbers.Substring(index * 2, 2)))
                    .ToArray();
                if (regularNumbers.Any(number => number is < 1 or > 49) || regularNumbers.Distinct().Count() != 6)
                    throw new InvalidDataException($"第{issue}期前六个号码范围或重复校验失败");
                if (!int.TryParse(record.SpecialNumber, out int specialNumber) || specialNumber is < 1 or > 49)
                    throw new InvalidDataException($"第{issue}期特码无效：{record.SpecialNumber}");
                if (regularNumbers.Contains(specialNumber))
                    throw new InvalidDataException($"第{issue}期特码与前六个号码重复");
                if (!validZodiacs.Contains(record.SpecialZodiac))
                    throw new InvalidDataException($"第{issue}期特码生肖无效：{record.SpecialZodiac}");
                if (!DateTimeOffset.TryParse(record.Date, out _))
                    throw new InvalidDataException($"第{issue}期开奖时间无效：{record.Date}");
            }
        }

        private static async Task<List<CrawlRecord>> FetchRecentRecordsAsync(int periods, CancellationToken cancellationToken)
        {
            var allRecords = new Dictionary<string, CrawlRecord>();
            int year = DateTime.Now.Year;

            for (int i = 0; i < 8 && allRecords.Count < periods; i++)
            {
                string issueDate = i == 0
                    ? DateTime.Now.ToString("yyyy-M-d")
                    : new DateTime(year - i, 12, 31).ToString("yyyy-M-d");

                cancellationToken.ThrowIfCancellationRequested();
                string json = await FetchApiAsync(issueDate, cancellationToken);
                if (string.IsNullOrEmpty(json))
                    continue;

                foreach (var record in ParseJson(json))
                {
                    if (!string.IsNullOrEmpty(record.Period))
                        allRecords[record.Period] = record;
                }
            }

            return allRecords.Values
                .OrderByDescending(r => int.TryParse(r.Period, out int period) ? period : 0)
                .Take(periods)
                .ToList();
        }

        /// <summary>
        /// 从 API 获取 JSON 数据
        /// </summary>
        private static async Task<string> FetchApiAsync(string issueNum, CancellationToken cancellationToken)
        {
            // 最多重试3次，使用短退避，避免瞬时故障造成重复快速请求。
            for (int retry = 0; retry < 3; retry++)
            {
                try
                {
                    if (retry > 0)
                    {
                        Console.WriteLine($"[爬虫] API 请求重试 {retry}/3...");
                        await Task.Delay(TimeSpan.FromSeconds(retry), cancellationToken);
                    }

                    string url = $"{ApiBaseUrl}HistoryOpenInfo?issueNum={issueNum}&lotteryId={LotteryId}";
                    using HttpResponseMessage response = await HttpClient.GetAsync(url, cancellationToken);
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsStringAsync(cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AppLogger.Error($"API 请求失败（第 {retry + 1} 次）", ex);
                    if (retry == 2) return ""; // 最后一次也失败则放弃
                }
            }
            return "";
        }

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                UseProxy = true,
                Proxy = WebRequest.DefaultWebProxy
            };
            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36");
            return client;
        }

        /// <summary>
        /// 解析 JSON 数据
        /// </summary>
        public static List<CrawlRecord> ParseJson(string json)
        {
            List<CrawlRecord> records = new List<CrawlRecord>();

            try
            {
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };

                var response = JsonSerializer.Deserialize<ApiResponse>(json, options);

                if (response == null || response.code != 0)
                {
                    Console.WriteLine($"[爬虫] API 返回错误: code={response?.code}, msg={response?.message}");
                    return records;
                }

                if (response.data == null || response.data.Count == 0)
                {
                    Console.WriteLine("[爬虫] API 返回数据为空");
                    return records;
                }

                foreach (var item in response.data)
                {
                    // 完整开奖号码
                    string fullCode = item.openCode?.Replace(" ", "") ?? "";

                    // 获取特码（第7个号码）
                    string specialNum = GetTeMaNumber(item.openCode ?? "");

                    // 获取前6个号码
                    string numbers6 = GetFirst6Numbers(item.openCode ?? "");

                    // 特码生肖：item.pet 是年份生肖（2026=馬），不是特码生肖！
                    // 需要根据年份生肖 + 特码号码 计算出真正的特码生肖
                    // 例如：2026年马，特码39 → 特码生肖=龙
                    string yearPet = item.pet?.Trim() ?? "";
                    string specialZodiac = "";
                    if (!string.IsNullOrEmpty(yearPet) && !string.IsNullOrEmpty(specialNum))
                    {
                        specialZodiac = GetShengXiaoByTeMa(specialNum, yearPet);
                    }

                    var record = new CrawlRecord
                    {
                        Period = item.issue?.Trim() ?? "",
                        Numbers = numbers6,
                        SpecialNumber = specialNum,
                        SpecialZodiac = specialZodiac,
                        Date = item.openTime?.Trim() ?? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                        ShengXiao = specialZodiac  // 兼容旧字段
                    };

                    if (!string.IsNullOrEmpty(record.Period) && !string.IsNullOrEmpty(fullCode))
                    {
                        records.Add(record);
                    }
                }

                Console.WriteLine($"[爬虫] 解析完成，共找到 {records.Count} 条记录");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[爬虫] 解析 JSON 失败: {ex.Message}");
            }

            return records;
        }

        /// <summary>
        /// 获取前6个号码
        /// </summary>
        public static string GetFirst6Numbers(string openCode)
        {
            if (string.IsNullOrEmpty(openCode))
                return "";

            try
            {
                string[] nums = openCode.Split(new[] { ',', '，', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (nums.Length >= 6)
                {
                    return string.Join("", nums.Take(6));
                }
            }
            catch (Exception ex) { AppLogger.Error("解析前六个号码", ex); }

            return "";
        }

        /// <summary>
        /// 获取特码（第7个号码）
        /// </summary>
        public static string GetTeMaNumber(string openCode)
        {
            if (string.IsNullOrEmpty(openCode))
                return "";

            try
            {
                string[] nums = openCode.Split(new[] { ',', '，', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (nums.Length >= 7)
                {
                    return nums[6].Trim();
                }
            }
            catch (Exception ex) { AppLogger.Error("解析特码", ex); }

            return "";
        }

        /// <summary>
        /// 根据特码和年份生肖计算特码生肖
        /// API 的 pet 字段是年份生肖（如2026=馬），需配合特码计算出真正的特码生肖
        /// 例：2026年馬，特码39 → 计算得 龍
        /// </summary>
        public static string GetShengXiaoByTeMa(string teMa, string yearPet)
        {
            if (string.IsNullOrEmpty(teMa) || string.IsNullOrEmpty(yearPet))
                return "";

            try
            {
                int num = int.Parse(teMa);
                Dictionary<string, List<string>> map = BuildShengXiaoMap(yearPet);

                foreach (var kvp in map)
                {
                    if (kvp.Value.Contains(num.ToString("D2")))
                    {
                        return ConvertShengXiao(kvp.Key);
                    }
                }
            }
            catch (Exception ex) { AppLogger.Error("计算特码生肖", ex); }

            return "";
        }

        /// <summary>
        /// 构建生肖映射表（本地算法，与网站JS逻辑一致）
        /// ⚠️ 仅供校验参考，不得用于覆盖网站原始数据
        /// </summary>
        private static Dictionary<string, List<string>> BuildShengXiaoMap(string yearPet)
        {
            // 检查缓存
            if (_shengxiaoCache.ContainsKey(yearPet))
                return _shengxiaoCache[yearPet];

            // 生肖顺序（与网站JS一致）
            string[] shengxiaoOrder = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };

            // 繁转简
            string simplifiedPet = ConvertShengXiao(yearPet);

            int yearIndex = -1;
            for (int i = 0; i < shengxiaoOrder.Length; i++)
            {
                if (shengxiaoOrder[i] == simplifiedPet)
                {
                    yearIndex = i;
                    break;
                }
            }

            if (yearIndex < 0)
            {
                // 如果找不到，尝试用繁体
                for (int i = 0; i < shengxiaoOrder.Length; i++)
                {
                    if (ConvertShengXiao(shengxiaoOrder[i]) == simplifiedPet)
                    {
                        yearIndex = i;
                        break;
                    }
                }
            }

            if (yearIndex < 0)
                return new Dictionary<string, List<string>>();

            // 构建反转顺序（与JS逻辑一致）
            // s = [yearPet, yearPet-1, yearPet-2, ..., yearPet+1]
            List<string> reversedOrder = new List<string>();

            // 从当前生肖开始，逆时针排列
            for (int i = yearIndex; i >= 0; i--)
                reversedOrder.Add(shengxiaoOrder[i]);
            for (int i = shengxiaoOrder.Length - 1; i > yearIndex; i--)
                reversedOrder.Add(shengxiaoOrder[i]);

            // 分配号码
            Dictionary<string, List<string>> map = new Dictionary<string, List<string>>();
            foreach (var pet in shengxiaoOrder)
                map[pet] = new List<string>();

            for (int i = 0; i < 4; i++)
            {
                for (int j = 0; j < 12; j++)
                {
                    int num = 12 * i + (j + 1);
                    if (num <= 48)
                    {
                        map[reversedOrder[j]].Add(num.ToString("D2"));
                    }
                }
            }

            // 49号分配给年份生肖
            map[simplifiedPet].Add("49");

            _shengxiaoCache[yearPet] = map;
            return map;
        }

        /// <summary>
        /// 繁转简生肖
        /// </summary>
        private static string ConvertShengXiao(string pet)
        {
            if (string.IsNullOrEmpty(pet))
                return "";

            Dictionary<string, string> map = new Dictionary<string, string>
            {
                { "鼠", "鼠" }, { "牛", "牛" }, { "虎", "虎" }, { "兔", "兔" },
                { "龍", "龙" }, { "蛇", "蛇" }, { "馬", "马" }, { "羊", "羊" },
                { "猴", "猴" }, { "雞", "鸡" }, { "狗", "狗" }, { "豬", "猪" }
            };

            if (map.ContainsKey(pet))
                return map[pet];

            return pet;
        }

        /// <summary>
        /// 构建生肖映射表（公开版本，供外部调用）
        /// </summary>
        public static Dictionary<string, List<string>> BuildShengXiaoMapPublic(string yearPet)
        {
            return BuildShengXiaoMap(yearPet);
        }
    }
}
