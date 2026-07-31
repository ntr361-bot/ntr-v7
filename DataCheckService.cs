using System;
using System.Collections.Generic;
using System.Linq;

namespace 六合分析软件
{
    /// <summary>
    /// 数据异常检测服务
    /// 比较网站返回的生肖与本地计算的生肖，检测数据不一致
    /// 数据优先级：网站官方数据 > 程序计算数据 > AI分析数据
    /// </summary>
    public static class DataCheckService
    {
        public class SelectionQuality
        {
            public int RequestedPeriods { get; set; }
            public int LoadedPeriods { get; set; }
            public int ValidSpecialNumbers { get; set; }
            public int ValidSpecialZodiacs { get; set; }
            public string LatestPeriod { get; set; } = "";
            public bool IsComplete => LoadedPeriods == RequestedPeriods &&
                ValidSpecialNumbers == LoadedPeriods && ValidSpecialZodiacs == LoadedPeriods;
            public string Summary => IsComplete
                ? $"数据完整：{LoadedPeriods}/{RequestedPeriods}期，特码及特码生肖全部有效"
                : $"数据不完整：记录{LoadedPeriods}/{RequestedPeriods}，有效特码{ValidSpecialNumbers}，有效特码生肖{ValidSpecialZodiacs}";
        }

        public static SelectionQuality CheckSelection(int periods)
        {
            int historyLimit = AISettings.ResolveHistoryLimit(periods);
            var history = DatabaseHelper.GetLatestHistory(historyLimit);
            string[] zodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
            return new SelectionQuality
            {
                RequestedPeriods = periods == AISettings.AllHistoryModeValue ? history.Count : periods,
                LoadedPeriods = history.Count,
                ValidSpecialNumbers = history.Count(h => int.TryParse(h.SpecialNumber, out int n) && n >= 1 && n <= 49),
                ValidSpecialZodiacs = history.Count(h => zodiacs.Contains(h.SpecialZodiac)),
                LatestPeriod = history.FirstOrDefault()?.Period ?? ""
            };
        }

        public class CheckResult
        {
            public int TotalChecked { get; set; }
            public int MatchCount { get; set; }
            public int MismatchCount { get; set; }
            public int NoWebDataCount { get; set; }
            public List<MismatchDetail> Mismatches { get; set; } = new List<MismatchDetail>();
        }

        public class MismatchDetail
        {
            public string Period { get; set; } = "";
            public string SpecialNumber { get; set; } = "";
            public string WebZodiac { get; set; } = "";
            public string CalcZodiac { get; set; } = "";
        }

        public class PeriodCheckResult
        {
            public bool IsNormal { get; set; } = true;
            public int TotalPeriods { get; set; }
            public List<string> MissingPeriods { get; set; } = new List<string>();
            public List<string> DuplicatePeriods { get; set; } = new List<string>();
            public string FirstPeriod { get; set; } = "";
            public string LastPeriod { get; set; } = "";
        }

        public class NumberCheckResult
        {
            public bool IsValid { get; set; } = true;
            public int TotalChecked { get; set; }
            public int InvalidCount { get; set; }
            public List<string> Issues { get; set; } = new List<string>();
        }

        public static CheckResult CheckRecentData(int limit = 50)
        {
            var result = new CheckResult();
            try
            {
                var records = DatabaseHelper.GetLatestHistory(limit);
                foreach (var r in records)
                {
                    result.TotalChecked++;
                    if (string.IsNullOrEmpty(r.SpecialZodiac)) { result.NoWebDataCount++; continue; }
                    if (!string.IsNullOrEmpty(r.ZodiacCheck))
                    {
                        if (r.ZodiacCheck == "错误") { result.MismatchCount++; result.Mismatches.Add(new MismatchDetail { Period = r.Period, SpecialNumber = r.SpecialNumber, WebZodiac = r.SpecialZodiac, CalcZodiac = r.CalcZodiac }); }
                        else if (r.ZodiacCheck == "正确") { result.MatchCount++; }
                        continue;
                    }
                    string calcZodiac = GetCalcZodiacForRecord(r);
                    if (string.IsNullOrEmpty(calcZodiac)) continue;
                    if (r.SpecialZodiac == calcZodiac) result.MatchCount++;
                    else { result.MismatchCount++; result.Mismatches.Add(new MismatchDetail { Period = r.Period, SpecialNumber = r.SpecialNumber, WebZodiac = r.SpecialZodiac, CalcZodiac = calcZodiac }); }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[数据校验] 校验失败: {ex.Message}"); }
            return result;
        }

        public static CheckResult CheckAllData()
        {
            var result = new CheckResult();
            try
            {
                var records = DatabaseHelper.GetHistory();
                foreach (var r in records)
                {
                    result.TotalChecked++;
                    if (string.IsNullOrEmpty(r.SpecialZodiac)) { result.NoWebDataCount++; continue; }
                    string calcZodiac = GetCalcZodiacForRecord(r);
                    if (string.IsNullOrEmpty(calcZodiac)) continue;
                    if (r.SpecialZodiac == calcZodiac) result.MatchCount++;
                    else { result.MismatchCount++; result.Mismatches.Add(new MismatchDetail { Period = r.Period, SpecialNumber = r.SpecialNumber, WebZodiac = r.SpecialZodiac, CalcZodiac = calcZodiac }); }
                }
            }
            catch (Exception ex) { Console.WriteLine($"[数据校验] 全量校验失败: {ex.Message}"); }
            return result;
        }

        // ===== 期号连续性检查 =====
        public static PeriodCheckResult CheckPeriodContinuity(int limit = 200)
        {
            var result = new PeriodCheckResult();
            try
            {
                var history = DatabaseHelper.GetLatestHistory(limit);
                if (history.Count == 0) return result;
                var periods = history.Where(h => !string.IsNullOrEmpty(h.Period)).Select(h => h.Period).OrderBy(p => p).ToList();
                result.TotalPeriods = periods.Count;
                result.FirstPeriod = periods.FirstOrDefault() ?? "";
                result.LastPeriod = periods.LastOrDefault() ?? "";
                var dupGroups = periods.GroupBy(p => p).Where(g => g.Count() > 1);
                result.DuplicatePeriods = dupGroups.Select(g => g.Key).ToList();
                var numericPeriods = periods.Distinct().Select(p => int.TryParse(p, out int n) ? n : 0).Where(n => n > 0).OrderBy(n => n).ToList();
                for (int i = 1; i < numericPeriods.Count; i++)
                {
                    int expected = numericPeriods[i - 1] + 1;
                    while (expected < numericPeriods[i]) { result.MissingPeriods.Add(expected.ToString()); expected++; }
                }
                result.IsNormal = result.MissingPeriods.Count == 0 && result.DuplicatePeriods.Count == 0;
            }
            catch { }
            return result;
        }

        // ===== 特码有效性检查 =====
        public static NumberCheckResult CheckNumberValidity(int limit = 500)
        {
            var result = new NumberCheckResult();
            try
            {
                var history = DatabaseHelper.GetLatestHistory(limit);
                result.TotalChecked = history.Count;
                foreach (var h in history)
                {
                    if (!int.TryParse(h.SpecialNumber, out int special))
                    {
                        result.InvalidCount++;
                        result.Issues.Add($"期号{h.Period}：特码为空或格式无效");
                        result.IsValid = false;
                    }
                    else if (special < 1 || special > 49)
                    {
                        result.InvalidCount++;
                        result.Issues.Add($"期号{h.Period}：特码超出范围 [{special}]");
                        result.IsValid = false;
                    }
                }
            }
            catch { }
            return result;
        }

        /// <summary>
        /// 综合数据质量报告
        /// </summary>
        public static string GetQualityReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("══════ 数据质量检测报告 ══════");
            sb.AppendLine();
            var periodCheck = CheckPeriodContinuity();
            sb.AppendLine(periodCheck.IsNormal ? "✅ 期号连续性：正常" : "⚠️ 期号连续性：异常");
            sb.AppendLine($"   总期数：{periodCheck.TotalPeriods}  首期：{periodCheck.FirstPeriod}  末期：{periodCheck.LastPeriod}");
            if (periodCheck.MissingPeriods.Count > 0)
                sb.AppendLine($"   缺失期号：{string.Join(", ", periodCheck.MissingPeriods.Take(10))}{(periodCheck.MissingPeriods.Count > 10 ? "..." : "")}");
            if (periodCheck.DuplicatePeriods.Count > 0)
                sb.AppendLine($"   重复期号：{string.Join(", ", periodCheck.DuplicatePeriods)}");
            sb.AppendLine();
            var numberCheck = CheckNumberValidity();
            sb.AppendLine(numberCheck.IsValid ? "✅ 特码有效性：正常" : "⚠️ 特码有效性：异常");
            sb.AppendLine($"   检查期数：{numberCheck.TotalChecked}  异常：{numberCheck.InvalidCount}");
            foreach (var issue in numberCheck.Issues.Take(10)) sb.AppendLine($"   - {issue}");
            sb.AppendLine();
            var zodiacCheck = CheckRecentData(100);
            sb.AppendLine(zodiacCheck.MismatchCount == 0 ? "✅ 生肖校验：正常" : "⚠️ 生肖校验：异常");
            sb.AppendLine($"   匹配：{zodiacCheck.MatchCount}  不匹配：{zodiacCheck.MismatchCount}");
            sb.AppendLine();
            sb.AppendLine("═══════════════════════════════");
            return sb.ToString();
        }

        public static string GetAnomalyReport(CheckResult result)
        {
            if (result.MismatchCount == 0) return $"数据校验通过 ✅\n总计 {result.TotalChecked} 条，匹配 {result.MatchCount} 条";
            var report = new System.Text.StringBuilder();
            report.AppendLine($"⚠️ 发现 {result.MismatchCount} 条生肖异常");
            foreach (var m in result.Mismatches.Take(20))
                report.AppendLine($"  期号 {m.Period} | 特码 {m.SpecialNumber} | 网站: {m.WebZodiac} | 计算: {m.CalcZodiac}");
            return report.ToString();
        }

        private static string GetCalcZodiacForRecord(DatabaseHelper.HistoryRecord record)
        {
            try
            {
                string teMa = record.SpecialNumber;
                if (string.IsNullOrEmpty(teMa)) return "";
                string year = "";
                if (!string.IsNullOrEmpty(record.OpenTime) && record.OpenTime.Length >= 4) year = record.OpenTime.Substring(0, 4);
                else if (!string.IsNullOrEmpty(record.Date) && record.Date.Length >= 4) year = record.Date.Substring(0, 4);
                if (string.IsNullOrEmpty(year)) return "";
                string yearPet = DatabaseHelper.GetYearPetPublic(year);
                if (string.IsNullOrEmpty(yearPet)) return "";
                return DataCrawler.GetShengXiaoByTeMa(teMa, yearPet);
            }
            catch { return ""; }
        }
    }
}
