using System;
using System.IO;

namespace 六合分析软件
{
    internal static class AppPaths
    {
        public static string DataDirectory { get; } = CreateDirectory(
            Environment.GetEnvironmentVariable("LIUHE_DATA_DIR") ??
            GetDefaultDataDirectory());

        public static string BackupDirectory { get; } = CreateDirectory(Path.Combine(DataDirectory, "Backup"));
        public static string LogDirectory { get; } = CreateDirectory(Path.Combine(DataDirectory, "Logs"));
        public static string CacheDirectory { get; } = CreateDirectory(Path.Combine(DataDirectory, "Cache"));
        public static string CloudPredictionDirectory { get; } = CreateDirectory(
            Path.Combine(DataDirectory, "CloudPredictionsV6"));
        public static bool CloudSyncEnabled => string.Equals(
            Environment.GetEnvironmentVariable("LIUHE_V7_CLOUD_SYNC"), "1", StringComparison.Ordinal);

        private static string GetDefaultDataDirectory()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "六合分析软件-V7");
        }

        private static string CreateDirectory(string path)
        {
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
