using System;
using System.Collections.Generic;
using System.Linq;

namespace 六合分析软件
{
    /// <summary>
    /// 号码冷热统计分析服务
    /// 统计号码出现次数、当前遗漏、热度分类
    /// </summary>
    public static class NumberStatisticsService
    {
        public enum HotLevel { 热, 中, 冷 }

        public class NumberStat
        {
            public int Number { get; set; }
            public int AppearCount { get; set; }
            public int CurrentMissing { get; set; }     // 当前遗漏期数
            public int MaxMissing { get; set; }          // 历史最大遗漏
            public double AvgMissing { get; set; }       // 平均遗漏
            public HotLevel Level { get; set; }          // 热度分类
            public double Frequency { get; set; }        // 出现频率百分比
        }

        public class StatisticsResult
        {
            public int PeriodRange { get; set; }
            public int TotalPeriods { get; set; }
            public List<NumberStat> Stats { get; set; } = new List<NumberStat>();
            public int HotCount => Stats.Count(s => s.Level == HotLevel.热);
            public int MediumCount => Stats.Count(s => s.Level == HotLevel.中);
            public int ColdCount => Stats.Count(s => s.Level == HotLevel.冷);
        }

        /// <summary>
        /// 计算指定期数范围内的号码统计
        /// </summary>
        public static StatisticsResult Calculate(int periodRange = 500)
        {
            var result = new StatisticsResult { PeriodRange = periodRange };
            var allHistory = DatabaseHelper.GetLatestHistory(periodRange);

            if (allHistory.Count == 0) return result;

            var history = allHistory.Take(periodRange).ToList();
            result.TotalPeriods = history.Count;

            // 每期只取一个特码；平码不参与号码统计。
            var specialNumbers = history
                .Select(r => int.TryParse(r.SpecialNumber, out int special) ? (int?)special : null)
                .ToList();

            // 统计每个号码（1-49）
            var stats = new Dictionary<int, NumberStat>();
            for (int n = 1; n <= 49; n++)
            {
                stats[n] = new NumberStat { Number = n };
            }

            foreach (var special in specialNumbers)
            {
                if (special.HasValue && stats.ContainsKey(special.Value))
                    stats[special.Value].AppearCount++;
            }

            // 计算当前遗漏（从最近一期往前数）
            for (int n = 1; n <= 49; n++)
            {
                int missing = 0;
                bool found = false;

                foreach (var special in specialNumbers)
                {
                    if (special == n)
                    {
                        found = true;
                        break;
                    }
                    missing++;
                }

                stats[n].CurrentMissing = found ? missing : result.TotalPeriods;
                // 特码频率 = 该特码出现次数 / 实际统计期数。
                stats[n].Frequency = result.TotalPeriods > 0
                    ? (double)stats[n].AppearCount / result.TotalPeriods * 100
                    : 0;
            }

            // 计算最大遗漏和平均遗漏
            for (int n = 1; n <= 49; n++)
            {
                int maxMissing = 0;
                int currentRun = 0;
                int totalMissingRuns = 0;
                int missingRunCount = 0;

                foreach (var special in specialNumbers)
                {
                    if (special == n)
                    {
                        if (currentRun > 0)
                        {
                            if (currentRun > maxMissing) maxMissing = currentRun;
                            totalMissingRuns += currentRun;
                            missingRunCount++;
                        }
                        currentRun = 0;
                    }
                    else
                    {
                        currentRun++;
                    }
                }

                if (currentRun > 0)
                {
                    if (currentRun > maxMissing) maxMissing = currentRun;
                    totalMissingRuns += currentRun;
                    missingRunCount++;
                }

                stats[n].MaxMissing = maxMissing;
                stats[n].AvgMissing = missingRunCount > 0 ? (double)totalMissingRuns / missingRunCount : 0;
            }

            // 热度分类：按出现次数排名
            var sorted = stats.Values.OrderByDescending(s => s.AppearCount).ToList();
            for (int i = 0; i < sorted.Count; i++)
            {
                if (i < 16) sorted[i].Level = HotLevel.热;          // 前1/3
                else if (i < 33) sorted[i].Level = HotLevel.中;     // 中1/3
                else sorted[i].Level = HotLevel.冷;                  // 后1/3
            }

            result.Stats = sorted;
            return result;
        }

        /// <summary>
        /// 获取多个周期的统计结果
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
