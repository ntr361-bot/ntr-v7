using System.Data.SQLite;
using 六合分析软件;

internal static class ReadOnlyDatabaseSnapshotTests
{
    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "liuhe-readonly-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "source.db");
            using (var connection = new SQLiteConnection($"Data Source={source};Version=3;"))
            {
                connection.Open();
                using var command = new SQLiteCommand("CREATE TABLE Probe(Value TEXT); INSERT INTO Probe(Value) VALUES('frozen');", connection);
                command.ExecuteNonQuery();
            }

            string destinationDirectory = Path.Combine(root, "snapshot");
            string copied = ReadOnlyDatabaseSnapshot.Create(source, destinationDirectory);

            Check(File.Exists(copied), "只读快照应创建目标数据库");
            using var verify = new SQLiteConnection($"Data Source={copied};Version=3;Read Only=True;");
            verify.Open();
            using var read = new SQLiteCommand("SELECT Value FROM Probe", verify);
            Check(Convert.ToString(read.ExecuteScalar()) == "frozen", "只读快照应保留源数据库内容");

            string copiedByFile = ReadOnlyDatabaseSnapshot.CreateFromValidatedFileCopy(source, Path.Combine(root, "file-copy"));
            using var copiedVerify = new SQLiteConnection($"Data Source={copiedByFile};Version=3;Read Only=True;");
            copiedVerify.Open();
            using var copiedRead = new SQLiteCommand("SELECT Value FROM Probe", copiedVerify);
            Check(Convert.ToString(copiedRead.ExecuteScalar()) == "frozen", "经校验的文件复制快照应保留源数据库内容");
            Console.WriteLine("READONLY DATABASE SNAPSHOT PASS");
            return 0;
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
