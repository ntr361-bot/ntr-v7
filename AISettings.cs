using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace 六合分析软件
{
    /// <summary>
    /// AI 预测统一设置管理
    /// 所有页面读取同一个设置
    /// </summary>
    public static class AISettings
    {
        private static readonly string SettingsFile = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "ai_settings.json");

        /// <summary>
        /// 分析周期选项
        /// </summary>
        public enum AnalysisPeriodOption
        {
            Period100 = 100,
            Period200 = 200,
            Period500 = 500
        }

        /// <summary>
        /// 当前设置
        /// </summary>
        public class Settings
        {
            public int AnalysisPeriods { get; set; } = 500;  // 默认500期
            public string ModelVersion { get; set; } = "AI生肖预测 V3.5";
            public DateTime LastPredictTime { get; set; }
            public string AnalysisMethod { get; set; } = "热度+遗漏+周期+关联";
        }

        private static Settings _currentSettings;

        /// <summary>
        /// 获取当前设置
        /// </summary>
        public static Settings Current
        {
            get
            {
                if (_currentSettings == null)
                {
                    _currentSettings = Load();
                }
                return _currentSettings;
            }
        }

        /// <summary>
        /// 获取分析周期数
        /// </summary>
        public static int AnalysisPeriods => Current.AnalysisPeriods;

        /// <summary>
        /// 获取模型版本
        /// </summary>
        public static string ModelVersion => Current.ModelVersion;

        /// <summary>
        /// 获取分析方式
        /// </summary>
        public static string AnalysisMethod => Current.AnalysisMethod;

        /// <summary>
        /// 更新分析周期
        /// </summary>
        public static void SetAnalysisPeriods(int periods)
        {
            Current.AnalysisPeriods = periods;
            Save();
        }

        /// <summary>
        /// 更新模型版本
        /// </summary>
        public static void SetModelVersion(string version)
        {
            Current.ModelVersion = version;
            Save();
        }

        /// <summary>
        /// 更新预测时间
        /// </summary>
        public static void UpdatePredictTime()
        {
            Current.LastPredictTime = DateTime.Now;
            Save();
        }

        /// <summary>
        /// 获取分析周期选项列表
        /// </summary>
        public static List<(int Value, string Text)> GetPeriodOptions()
        {
            return new List<(int, string)>
            {
                (100, "最近100期"),
                (200, "最近200期"),
                (500, "最近500期（推荐）")
            };
        }

        /// <summary>
        /// 加载设置
        /// </summary>
        private static Settings Load()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    string json = File.ReadAllText(SettingsFile);
                    var settings = JsonSerializer.Deserialize<Settings>(json);
                    return settings ?? new Settings();
                }
            }
            catch { }

            return new Settings();
        }

        /// <summary>
        /// 保存设置
        /// </summary>
        private static void Save()
        {
            try
            {
                string json = JsonSerializer.Serialize(Current, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(SettingsFile, json);
            }
            catch { }
        }

        /// <summary>
        /// 重置为默认设置
        /// </summary>
        public static void ResetToDefault()
        {
            _currentSettings = new Settings();
            Save();
        }
    }
}
