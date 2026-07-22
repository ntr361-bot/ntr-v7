using System;
using System.Collections.Generic;
using System.Linq;

namespace 六合分析软件
{
    /// <summary>
    /// 遗漏分析服务
    /// 统计每个号码和生肖的当前遗漏、最大遗漏、平均遗漏
    /// </summary>
    public static class MissingNumberService
    {
        public class MissingRecord
        {
            public string Item { get; set; } = "";         // 号码或生肖
            public int CurrentMissing { get; set; }        // 当前遗漏
            public int MaxMissing { get; set; }            // 历史最大遗漏
            public double AvgMissing { get; set; }         // 平均遗漏
            public int TotalAppear { get; set; }           // 总出现次数
            public int TotalMissingRuns { get; set; }      // 遗漏段数
            public string HotStatus { get; set; } = "";    // 冷热状态
        }

        public class MissingReport
        {
            public int PeriodRange { get; set; }
            public int TotalPeriods { get; set; }
            public List<MissingRecord> NumberMissings { get; set; } = new List<MissingRecord>();
            public List<MissingRecord> ZodiacMissings { get; set; } = new List<MissingRecord>();

            // 异常标记
            public List<string> Anomalies { get; set; } = new List<string>();
        }

        /// <summary>
        /// 计算遗漏报告（号码+生肖）
        /// </summary>
        public static MissingReport Calculate(int periodRange = 500)
        {
            var report = new MissingReport { PeriodRange = periodRange };
            var allHistory = DatabaseHelper.GetLatestHistory(periodRange + 100);
            var history = allHistory.Take(periodRange).ToList();

            if (history.Count == 0) return report;

            report.TotalPeriods = history.Count;

            // 号码遗漏
            report.NumberMissings = CalculateNumberMissings(history);

            // 生肖遗漏
            report.ZodiacMissings = CalculateZodiacMissings(history);

            // 异常标记
            DetectAnomalies(report);

            return report;
        }

        private static List<MissingRecord> CalculateNumberMissings(List<DatabaseHelper.HistoryRecord> history)
        {
            var results = new List<MissingRecord>();

            for (int n = 1; n <= 49; n++)
            {
                var record = new MissingRecord { Item = n.ToString("D2") };

                var misses = new List<int>();
                int currentRun = 0;

                // 数据按最新期到最旧期排列，开头连续未命中数就是当前遗漏。
                record.CurrentMissing = history.TakeWhile(h =>
                    !int.TryParse(h.SpecialNumber, out int special) || special != n).Count();

                foreach (var h in history)
                {
                    // 号码遗漏只按每期特码计算，平码不参与。
                    if (int.TryParse(h.SpecialNumber, out int special) && special == n)
                    {
                        if (currentRun > 0)
                        {
                            misses.Add(currentRun);
                            currentRun = 0;
                        }
                        record.TotalAppear++;
                    }
                    else
                    {
                        currentRun++;
                    }
                }

                if (currentRun > 0) misses.Add(currentRun);

                // 最大遗漏
                record.MaxMissing = misses.Count > 0 ? misses.Max() : 0;

                // 平均遗漏
                record.TotalMissingRuns = misses.Count;
                record.AvgMissing = misses.Count > 0 ? misses.Average() : 0;

                // 冷热状态
                if (record.CurrentMissing > record.MaxMissing * 0.7)
                    record.HotStatus = "极冷⚠️";
                else if (record.CurrentMissing > record.AvgMissing * 2)
                    record.HotStatus = "偏冷";
                else if (record.CurrentMissing < record.AvgMissing * 0.5)
                    record.HotStatus = "偏热";
                else
                    record.HotStatus = "正常";

                results.Add(record);
            }

            return results.OrderBy(r => r.Item).ToList();
        }

        private static List<MissingRecord> CalculateZodiacMissings(List<DatabaseHelper.HistoryRecord> history)
        {
            var results = new List<MissingRecord>();
            string[] allZodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };

            var zodiacSequence = history
                .Where(r => !string.IsNullOrEmpty(r.SpecialZodiac))
                .Select(r => r.SpecialZodiac)
                .ToList();

            foreach (var z in allZodiacs)
            {
                var record = new MissingRecord { Item = z };
                var misses = new List<int>();
                int currentRun = 0;

                record.CurrentMissing = zodiacSequence.TakeWhile(actual => actual != z).Count();

                foreach (var actual in zodiacSequence)
                {
                    if (actual == z)
                    {
                        if (currentRun > 0)
                        {
                            misses.Add(currentRun);
                            currentRun = 0;
                        }
                        record.TotalAppear++;
                    }
                    else
                    {
                        currentRun++;
                    }
                }

                if (currentRun > 0) misses.Add(currentRun);

                record.MaxMissing = misses.Count > 0 ? misses.Max() : 0;
                record.TotalMissingRuns = misses.Count;
                record.AvgMissing = misses.Count > 0 ? misses.Average() : 0;

                if (record.CurrentMissing > record.MaxMissing * 0.7)
                    record.HotStatus = "极冷⚠️";
                else if (record.CurrentMissing > record.AvgMissing * 2)
                    record.HotStatus = "偏冷";
                else if (record.CurrentMissing < record.AvgMissing * 0.5)
                    record.HotStatus = "偏热";
                else
                    record.HotStatus = "正常";

                results.Add(record);
            }

            return results.OrderBy(r => r.Item).ToList();
        }

        private static void DetectAnomalies(MissingReport report)
        {
            // 检查号码：当前遗漏超过30期的标记异常
            foreach (var n in report.NumberMissings)
            {
                if (n.CurrentMissing > 30 && n.CurrentMissing >= n.MaxMissing * 0.9)
                    report.Anomalies.Add($"⚠️ 号码 {n.Item} 当前遗漏 {n.CurrentMissing} 期，接近历史最大 {n.MaxMissing} 期");
            }

            // 检查生肖：当前遗漏超过15期的标记异常
            foreach (var z in report.ZodiacMissings)
            {
                if (z.CurrentMissing > 15)
                    report.Anomalies.Add($"⚠️ 生肖 {z.Item} 当前遗漏 {z.CurrentMissing} 期");
            }
        }
    }
}
