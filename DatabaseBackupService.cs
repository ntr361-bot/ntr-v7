using System;
using System.IO;

namespace 六合分析软件
{
    /// <summary>
    /// 数据库自动备份服务
    /// 每天启动时自动备份 history.db 到 Backup 目录
    /// </summary>
    public static class DatabaseBackupService
    {
        private static readonly string DbPath = DatabaseHelper.DatabasePath;
        private static readonly string BackupDir = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Backup");

        /// <summary>
        /// 检查今天是否已备份
        /// </summary>
        public static bool HasBackupToday()
        {
            string today = DateTime.Now.ToString("yyyyMMdd");
            string backupPath = Path.Combine(BackupDir, $"{today}.db");
            return File.Exists(backupPath);
        }

        /// <summary>
        /// 执行备份（如果今天还没有备份）
        /// </summary>
        public static string Backup()
        {
            try
            {
                if (!File.Exists(DbPath))
                    return "数据库文件不存在，跳过备份";

                // 创建备份目录
                if (!Directory.Exists(BackupDir))
                    Directory.CreateDirectory(BackupDir);

                string today = DateTime.Now.ToString("yyyyMMdd");
                string backupPath = Path.Combine(BackupDir, $"{today}.db");

                // 今天已备份则跳过
                if (File.Exists(backupPath))
                {
                    Console.WriteLine($"[备份] 今日已备份：{backupPath}");
                    return $"已备份（{today}）";
                }

                // 复制数据库
                File.Copy(DbPath, backupPath, overwrite: false);
                Console.WriteLine($"[备份] 数据库备份成功：{backupPath}");

                // 清理30天前的旧备份
                CleanOldBackups(30);

                // 获取文件大小
                var info = new FileInfo(backupPath);
                double sizeMB = info.Length / 1024.0 / 1024.0;
                return $"备份成功（{today}，{sizeMB:F1}MB）";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[备份] 备份失败：{ex.Message}");
                return $"备份失败：{ex.Message}";
            }
        }

        /// <summary>
        /// 获取备份统计信息
        /// </summary>
        public static (int count, string latest, double totalSizeMB) GetBackupStats()
        {
            try
            {
                if (!Directory.Exists(BackupDir))
                    return (0, "无", 0);

                var files = Directory.GetFiles(BackupDir, "*.db");
                double totalSize = 0;
                string latest = "无";
                DateTime latestDate = DateTime.MinValue;

                foreach (var f in files)
                {
                    var info = new FileInfo(f);
                    totalSize += info.Length;
                    if (info.LastWriteTime > latestDate)
                    {
                        latestDate = info.LastWriteTime;
                        latest = Path.GetFileNameWithoutExtension(f);
                    }
                }

                return (files.Length, latest, totalSize / 1024.0 / 1024.0);
            }
            catch
            {
                return (0, "无", 0);
            }
        }

        /// <summary>
        /// 清理旧备份
        /// </summary>
        private static void CleanOldBackups(int keepDays)
        {
            try
            {
                if (!Directory.Exists(BackupDir)) return;

                var cutoff = DateTime.Now.AddDays(-keepDays);
                var files = Directory.GetFiles(BackupDir, "*.db");

                foreach (var f in files)
                {
                    var info = new FileInfo(f);
                    if (info.LastWriteTime < cutoff)
                    {
                        File.Delete(f);
                        Console.WriteLine($"[备份] 清理旧备份：{Path.GetFileName(f)}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[备份] 清理失败：{ex.Message}");
            }
        }
    }
}
