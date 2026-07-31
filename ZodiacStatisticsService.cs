using System;
using System.Collections.Generic;
using System.Linq;

namespace 六合分析软件
{
    /// <summary>
    /// 生肖冷热统计分析服务
    /// 统计12生肖出现情况、遗漏、趋势
    /// </summary>
    public static class ZodiacStatisticsService
    {
        public enum TrendType { 上升, 下降, 平稳, 冷 }

        public class ZodiacStat
        {
            public string Zodiac { get; set; } = "";
            public int AppearCount { get; set; }
            public int CurrentMissing { get; set; }     // 当前遗漏期数
            public int MaxMissing { get; set; }          // 历史最大遗漏
            public double Frequency { get; set; }        // 出现频率百分比
            public TrendType Trend { get; set; }         // 趋势
            public int Recent10Count { get; set; }       // 最近10期出现次数
            public int Recent30Count { get; set; }       // 最近30期出现次数
            public string TrendLabel => Trend switch
            {
                TrendType.上升 => "↑ 上升",
                TrendType.下降 => "↓ 下降",
                TrendType.平稳 => "→ 平稳",
                TrendType.冷 => "❄ 冷",
                _ => ""
            };
        }

        public class StatisticsResult
        {
            public int PeriodRange { get; set; }
            public int TotalPeriods { get; set; }
            public List<ZodiacStat> Stats { get; set; } = new List<ZodiacStat>();
        }

        /// <summary>
        /// 计算生肖统计
        /// </summary>
        public static StatisticsResult Calculate(int periodRange = 500)
        {
            var result = new StatisticsResult { PeriodRange = periodRange };

            var history = DatabaseHelper.GetLatestHistory(periodRange);
            if (history.Count == 0) return result;

            result.TotalPeriods = history.Count;

            string[] allZodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
            var zodiacs = history
                .Where(r => !string.IsNullOrEmpty(r.SpecialZodiac))
                .Select(r => r.SpecialZodiac)
                .ToList();

            // 统计出现次数
            var stats = new Dictionary<string, ZodiacStat>();
            foreach (var z in allZodiacs)
            {
                stats[z] = new ZodiacStat { Zodiac = z };
                stats[z].AppearCount = zodiacs.Count(x => x == z);
                stats[z].Frequency = zodiacs.Count > 0 ? (double)stats[z].AppearCount / zodiacs.Count * 100 : 0;
            }

            // 计算当前遗漏
            foreach (var z in allZodiacs)
            {
                int missing = 0;
                foreach (var actual in zodiacs)
                {
                    if (actual == z) break;
                    missing++;
                }
                stats[z].CurrentMissing = missing >= zodiacs.Count ? zodiacs.Count : missing;
            }

            // 计算最大遗漏
            foreach (var z in allZodiacs)
            {
                int maxMissing = 0, currentRun = 0;
                foreach (var actual in zodiacs)
                {
                    if (actual == z)
                    {
                        if (currentRun > maxMissing) maxMissing = currentRun;
                        currentRun = 0;
                    }
                    else currentRun++;
                }
                if (currentRun > maxMissing) maxMissing = currentRun;
                stats[z].MaxMissing = maxMissing;
            }

            // 最近10期和30期统计
            var recent10 = zodiacs.Take(10).ToList();
            var recent30 = zodiacs.Take(30).ToList();
            foreach (var z in allZodiacs)
            {
                stats[z].Recent10Count = recent10.Count(x => x == z);
                stats[z].Recent30Count = recent30.Count(x => x == z);
            }

            // 趋势分析：比较最近30期和30-60期的出现次数
            var previous30 = zodiacs.Skip(30).Take(30).ToList();
            foreach (var z in allZodiacs)
            {
                int recent = stats[z].Recent30Count;
                int previous = previous30.Count(x => x == z);

                if (recent == 0 && previous == 0)
                    stats[z].Trend = TrendType.冷;
                else if (recent > previous + 1)
                    stats[z].Trend = TrendType.上升;
                else if (recent < previous - 1)
                    stats[z].Trend = TrendType.下降;
                else
                    stats[z].Trend = TrendType.平稳;
            }

            // 按出现次数降序
            result.Stats = stats.Values.OrderByDescending(s => s.AppearCount).ToList();
            return result;
        }

        /// <summary>
        /// 多周期统计
        /// </summary>
        public static Dictionary<int, StatisticsResult> CalculateMultiPeriod(params int[] periods)
        {
            var results = new Dictionary<int, StatisticsResult>();
            foreach (var p in periods)
                results[p] = Calculate(p);
            return results;
        }
    }
}
