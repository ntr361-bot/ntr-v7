using System;
using System.Data.SQLite;
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
        private static readonly string BackupDir = AppPaths.BackupDirectory;

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

                // 每个进程写自己的临时库，再无覆盖发布；失败进程绝不能删除另一进程的成功备份。
                string temporaryPath = Path.Combine(BackupDir, $".{today}.{Guid.NewGuid():N}.tmp");
                try
                {
                    // SQLite 使用 WAL 时直接复制主文件可能漏掉最新记录，必须使用在线备份 API。
                    CreateConsistentBackup(DbPath, temporaryPath);
                    VerifyBackup(temporaryPath);
                    try
                    {
                        File.Move(temporaryPath, backupPath);
                    }
                    catch (IOException) when (File.Exists(backupPath))
                    {
                        // 另一实例已先发布；接受其经过同一流程生成的快照。
                        VerifyBackup(backupPath);
                    }
                }
                finally
                {
                    DeleteTemporaryDatabaseFiles(temporaryPath);
                }
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

        private static void CreateConsistentBackup(string sourcePath, string backupPath)
        {
            using var source = new SQLiteConnection(
                $"Data Source={sourcePath};Version=3;Read Only=True;");
            using var destination = new SQLiteConnection(
                $"Data Source={backupPath};Version=3;");
            source.Open();
            destination.Open();
            source.BackupDatabase(destination, "main", "main", -1, null, 100);
        }

        private static void VerifyBackup(string backupPath)
        {
            using var connection = new SQLiteConnection(
                $"Data Source={backupPath};Version=3;Read Only=True;");
            connection.Open();
            using var command = new SQLiteCommand("PRAGMA quick_check", connection);
            if (!string.Equals(Convert.ToString(command.ExecuteScalar()), "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("备份数据库完整性校验失败");
        }

        private static void DeleteTemporaryDatabaseFiles(string temporaryPath)
        {
            foreach (string path in new[] { temporaryPath, temporaryPath + "-wal", temporaryPath + "-shm" })
            {
                if (File.Exists(path)) File.Delete(path);
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
