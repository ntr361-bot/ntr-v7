using System.Data.SQLite;

namespace 六合分析软件;

/// <summary>
/// Creates an isolated, writable SQLite backup from a source database opened read-only.
/// It is intended for export/replay tooling and never modifies the source database.
/// </summary>
public static class ReadOnlyDatabaseSnapshot
{
    public static string Create(string sourceDatabasePath, string destinationDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceDatabasePath))
            throw new ArgumentException("必须提供源数据库路径", nameof(sourceDatabasePath));
        if (!File.Exists(sourceDatabasePath))
            throw new FileNotFoundException("源数据库不存在", sourceDatabasePath);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
            throw new ArgumentException("必须提供快照目录", nameof(destinationDirectory));

        EnsureEmptyDestination(destinationDirectory);
        try
        {
            return CreateViaSqliteBackup(sourceDatabasePath, destinationDirectory);
        }
        catch (SQLiteException)
        {
            ClearPartialDestination(destinationDirectory);
            return CreateFromValidatedFileCopy(sourceDatabasePath, destinationDirectory);
        }
    }

    /// <summary>
    /// Fallback for source folders that permit read access but reject SQLite's online-backup lock.
    /// It refuses WAL/journal sources and checks the source did not change during copying.
    /// </summary>
    public static string CreateFromValidatedFileCopy(string sourceDatabasePath, string destinationDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceDatabasePath))
            throw new ArgumentException("必须提供源数据库路径", nameof(sourceDatabasePath));
        if (!File.Exists(sourceDatabasePath))
            throw new FileNotFoundException("源数据库不存在", sourceDatabasePath);
        EnsureEmptyDestination(destinationDirectory);
        foreach (string sidecar in new[] { sourceDatabasePath + "-wal", sourceDatabasePath + "-shm", sourceDatabasePath + "-journal" })
        {
            if (File.Exists(sidecar))
                throw new InvalidOperationException("源数据库存在未合并日志，拒绝不安全的文件复制快照");
        }

        FileInfo before = new(sourceDatabasePath);
        long beforeLength = before.Length;
        DateTime beforeWriteUtc = before.LastWriteTimeUtc;
        string destinationPath = Path.Combine(destinationDirectory, "history.db");
        Directory.CreateDirectory(destinationDirectory);
        File.Copy(sourceDatabasePath, destinationPath);
        FileInfo after = new(sourceDatabasePath);
        if (after.Length != beforeLength || after.LastWriteTimeUtc != beforeWriteUtc)
        {
            File.Delete(destinationPath);
            throw new InvalidOperationException("源数据库在复制期间发生变化，拒绝导出不稳定快照");
        }

        using var verify = new SQLiteConnection($"Data Source={destinationPath};Version=3;Read Only=True;");
        verify.Open();
        using var check = new SQLiteCommand("PRAGMA quick_check", verify);
        if (!string.Equals(Convert.ToString(check.ExecuteScalar()), "ok", StringComparison.OrdinalIgnoreCase))
        {
            verify.Close();
            File.Delete(destinationPath);
            throw new InvalidDataException("复制快照完整性校验失败");
        }
        return destinationPath;
    }

    private static string CreateViaSqliteBackup(string sourceDatabasePath, string destinationDirectory)
    {
        string destinationPath = Path.Combine(destinationDirectory, "history.db");
        using var source = new SQLiteConnection($"Data Source={sourceDatabasePath};Version=3;Read Only=True;");
        using var destination = new SQLiteConnection($"Data Source={destinationPath};Version=3;");
        source.Open();
        destination.Open();
        source.BackupDatabase(destination, "main", "main", -1, null, 100);
        return destinationPath;
    }

    private static void EnsureEmptyDestination(string destinationDirectory)
    {
        if (string.IsNullOrWhiteSpace(destinationDirectory))
            throw new ArgumentException("必须提供快照目录", nameof(destinationDirectory));
        Directory.CreateDirectory(destinationDirectory);
        string destinationPath = Path.Combine(destinationDirectory, "history.db");
        if (File.Exists(destinationPath) || File.Exists(destinationPath + "-wal") || File.Exists(destinationPath + "-shm"))
            throw new InvalidOperationException("目标快照已存在，拒绝覆盖");
    }

    private static void ClearPartialDestination(string destinationDirectory)
    {
        foreach (string path in new[]
        {
            Path.Combine(destinationDirectory, "history.db"),
            Path.Combine(destinationDirectory, "history.db-wal"),
            Path.Combine(destinationDirectory, "history.db-shm")
        })
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
