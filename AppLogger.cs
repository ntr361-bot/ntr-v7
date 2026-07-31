using System;
using System.IO;

namespace 六合分析软件
{
    internal static class AppLogger
    {
        private static readonly object SyncRoot = new object();

        public static void Info(string operation, string message) => Write("INFO", operation, message);

        public static void Error(string operation, Exception exception) =>
            Write("ERROR", operation, $"{exception.GetType().Name}: {exception.Message}{Environment.NewLine}{exception.StackTrace}");

        private static void Write(string level, string operation, string message)
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {operation}: {message}";
            Console.WriteLine(line);

            try
            {
                string logPath = Path.Combine(AppPaths.LogDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");
                lock (SyncRoot)
                {
                    File.AppendAllText(logPath, line + Environment.NewLine);
                }
            }
            catch
            {
                // 日志失败不能影响主程序。
            }
        }
    }
}
