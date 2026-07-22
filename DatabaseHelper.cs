using System;
using System.Data.SQLite;
using System.Collections.Generic;
using System.Linq;
using System.IO;

namespace 六合分析软件
{
    public class DatabaseHelper
    {
        private static readonly AsyncLocal<long?> HistoryIssueUpperBound = new();

        public static IDisposable UseHistoryThroughIssue(long issue)
        {
            if (issue <= 0) throw new ArgumentOutOfRangeException(nameof(issue));
            long? previous = HistoryIssueUpperBound.Value;
            HistoryIssueUpperBound.Value = issue;
            return new HistoryIssueScope(previous);
        }

        private sealed class HistoryIssueScope(long? previous) : IDisposable
        {
            private bool disposed;

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                HistoryIssueUpperBound.Value = previous;
            }
        }

        public static string DatabasePath { get; } = ResolveDatabasePath();

        private static readonly string connString =
            $"Data Source={DatabasePath};Version=3;";

        private static string ResolveDatabasePath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string databasePath = Path.Combine(AppPaths.DataDirectory, "history.db");
            string projectDir = FindProjectDirectory(baseDir);
            string[] legacyPaths =
            {
                Path.Combine(baseDir, "history.db"),
                Path.Combine(projectDir, "history.db"),
                Path.Combine(Directory.GetCurrentDirectory(), "history.db")
            };

            foreach (string legacyPath in legacyPaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Path.GetFullPath(databasePath).Equals(Path.GetFullPath(legacyPath), StringComparison.OrdinalIgnoreCase))
                    TryPromoteLegacyDatabase(databasePath, legacyPath);
            }

            return databasePath;
        }

        private static string FindProjectDirectory(string startDir)
        {
            var dir = new DirectoryInfo(startDir);
            while (dir != null)
            {
                if (dir.GetFiles("*.csproj").Any())
                    return dir.FullName;
                dir = dir.Parent;
            }

            return Directory.GetCurrentDirectory();
        }

        private static void TryPromoteLegacyDatabase(string rootDb, string legacyRuntimeDb)
        {
            try
            {
                if (!File.Exists(legacyRuntimeDb)) return;

                int rootCount = CountHistoryRows(rootDb);
                int legacyCount = CountHistoryRows(legacyRuntimeDb);
                if (legacyCount <= rootCount) return;

                Directory.CreateDirectory(Path.GetDirectoryName(rootDb) ?? ".");
                if (File.Exists(rootDb))
                {
                    string backupPath = Path.Combine(
                        Path.GetDirectoryName(rootDb) ?? ".",
                        $"history.root-before-v6.1.{DateTime.Now:yyyyMMddHHmmss}.db");
                    File.Copy(rootDb, backupPath, overwrite: true);
                }

                File.Copy(legacyRuntimeDb, rootDb, overwrite: true);
            }
            catch (Exception ex)
            {
                AppLogger.Error("迁移旧数据库", ex);
            }
        }

        private static int CountHistoryRows(string dbPath)
        {
            try
            {
                if (!File.Exists(dbPath)) return 0;
                using var conn = new SQLiteConnection($"Data Source={dbPath};Version=3;Read Only=True;");
                conn.Open();
                using var cmd = new SQLiteCommand("SELECT COUNT(*) FROM History", conn);
                object? result = cmd.ExecuteScalar();
                return Convert.ToInt32(result);
            }
            catch (Exception ex)
            {
                AppLogger.Error($"读取数据库记录数 ({dbPath})", ex);
                return 0;
            }
        }

        // 历史记录数据结构
        public class HistoryRecord
        {
            public int Id { get; set; }
            public string Period { get; set; } = "";       // 期号
            public string Numbers { get; set; } = "";      // 前6个开奖号码
            public string SpecialNumber { get; set; } = "";// 特码（第7个号码）
            public string SpecialZodiac { get; set; } = "";// 特码生肖（网站提供，权威来源）
            public string OpenTime { get; set; } = "";     // 开奖时间
            public string Date { get; set; } = "";         // 日期（兼容旧字段）
            public string ShengXiao { get; set; } = "";    // 生肖（兼容旧字段，同SpecialZodiac）
            // 校验字段：用于对比网站数据与本地计算结果
            public string WebZodiac { get; set; } = "";    // 网站原始生肖
            public string CalcZodiac { get; set; } = "";   // 系统计算生肖
            public string ZodiacCheck { get; set; } = "";  // 校验结果：正确/错误/无网站数据
        }

        // 获取数据库连接
        public static SQLiteConnection GetConnection()
        {
            SQLiteConnection conn =
                new SQLiteConnection(connString);

            conn.Open();

            return conn;
        }

        // 初始化数据库
        public static void InitializeDatabase()
        {
            using (SQLiteConnection conn = GetConnection())
            {
                string sql = @"
                CREATE TABLE IF NOT EXISTS History
                (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Period TEXT DEFAULT '',
                    Numbers TEXT DEFAULT '',
                    SpecialNumber TEXT DEFAULT '',
                    SpecialZodiac TEXT DEFAULT '',
                    OpenTime TEXT DEFAULT '',
                    Date TEXT,
                    ShengXiao TEXT DEFAULT '',
                    WebZodiac TEXT DEFAULT '',
                    CalcZodiac TEXT DEFAULT '',
                    ZodiacCheck TEXT DEFAULT ''
                )";
                new SQLiteCommand(sql, conn).ExecuteNonQuery();

                // 兼容旧表结构：添加缺失的列
                EnsureColumns(conn, "History",
                    "Period TEXT DEFAULT ''",
                    "Numbers TEXT DEFAULT ''",
                    "ShengXiao TEXT DEFAULT ''",
                    "SpecialNumber TEXT DEFAULT ''",
                    "SpecialZodiac TEXT DEFAULT ''",
                    "OpenTime TEXT DEFAULT ''",
                    "WebZodiac TEXT DEFAULT ''",
                    "CalcZodiac TEXT DEFAULT ''",
                    "ZodiacCheck TEXT DEFAULT ''");

                // 旧版本可能产生重复期号；保留最新一条后再建立数据库级唯一约束。
                new SQLiteCommand(@"DELETE FROM History
                    WHERE Period != '' AND Id NOT IN
                    (SELECT MAX(Id) FROM History WHERE Period != '' GROUP BY Period)", conn).ExecuteNonQuery();
                new SQLiteCommand(@"CREATE UNIQUE INDEX IF NOT EXISTS idx_history_period
                    ON History(Period) WHERE Period != ''", conn).ExecuteNonQuery();

                // 创建 AI 预测历史表
                string aiSql = @"
                CREATE TABLE IF NOT EXISTS AIPredictHistory
                (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    PredictPeriod TEXT DEFAULT '',
                    PredictDate TEXT DEFAULT '',
                    ModelVersion TEXT DEFAULT '',
                    AnalysisPeriods INTEGER DEFAULT 500,
                    Recommended6 TEXT DEFAULT '',
                    Focus3 TEXT DEFAULT '',
                    GptAnalysis TEXT DEFAULT '',
                    ActualZodiac TEXT DEFAULT '',
                    Top3Hit INTEGER DEFAULT -1,
                    Top6Hit INTEGER DEFAULT -1
                )";
                new SQLiteCommand(aiSql, conn).ExecuteNonQuery();

                // 兼容旧表结构：添加缺失的列
                EnsureColumns(conn, "AIPredictHistory",
                    "PredictPeriod TEXT DEFAULT ''",
                    "ModelVersion TEXT DEFAULT ''",
                    "ActualZodiac TEXT DEFAULT ''",
                    "Top3Hit INTEGER DEFAULT -1",
                    "Top6Hit INTEGER DEFAULT -1");

                // 创建新版预测历史表（每期唯一一条记录）
                string predSql = @"
                CREATE TABLE IF NOT EXISTS PredictionHistory
                (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Issue TEXT NOT NULL DEFAULT '',
                    PredictTime TEXT DEFAULT '',
                    PredictNumber TEXT DEFAULT '',
                    PredictZodiac TEXT DEFAULT '',
                    Top6Zodiac TEXT DEFAULT '',
                    PredictionGroupId TEXT DEFAULT '',
                    AnalysisPeriods INTEGER DEFAULT 0,
                    ScoreDetails TEXT DEFAULT '',
                    ModelVersion TEXT DEFAULT '',
                    ActualNumber TEXT DEFAULT '',
                    ActualZodiac TEXT DEFAULT '',
                    HitResult TEXT DEFAULT '',
                    Top6HitResult TEXT DEFAULT ''
                )";
                SQLiteCommand predCmd = new SQLiteCommand(predSql, conn);
                predCmd.ExecuteNonQuery();

                // 兼容旧表结构：添加缺失的列
                EnsureColumns(conn, "PredictionHistory",
                    "PredictNumber TEXT DEFAULT ''",
                    "PredictZodiac TEXT DEFAULT ''",
                    "Top6Zodiac TEXT DEFAULT ''",
                    "PredictionGroupId TEXT DEFAULT ''",
                    "AnalysisPeriods INTEGER DEFAULT 0",
                    "ScoreDetails TEXT DEFAULT ''",
                    "ActualNumber TEXT DEFAULT ''",
                    "ActualZodiac TEXT DEFAULT ''",
                    "Top6HitResult TEXT DEFAULT ''");

                // 每个期号固定保留50/100/200/500期四套结果，同期同周期只保留一条。
                new SQLiteCommand("DROP INDEX IF EXISTS idx_prediction_issue", conn).ExecuteNonQuery();
                new SQLiteCommand(@"DELETE FROM PredictionHistory
                    WHERE Id NOT IN (
                        SELECT Id FROM PredictionHistory AS current
                        WHERE Id = (
                            SELECT Id FROM PredictionHistory AS candidate
                            WHERE candidate.Issue = current.Issue
                              AND candidate.AnalysisPeriods = current.AnalysisPeriods
                            ORDER BY candidate.Id DESC
                            LIMIT 1
                        )
                    )", conn).ExecuteNonQuery();
                new SQLiteCommand(@"CREATE UNIQUE INDEX IF NOT EXISTS idx_prediction_issue_periods
                    ON PredictionHistory(Issue, AnalysisPeriods)", conn).ExecuteNonQuery();

                new SQLiteCommand(@"UPDATE PredictionHistory
                    SET PredictionGroupId = 'PRED-' || Issue
                    WHERE (PredictionGroupId IS NULL OR PredictionGroupId = '') AND Issue != ''", conn).ExecuteNonQuery();
                new SQLiteCommand(@"CREATE INDEX IF NOT EXISTS idx_prediction_group
                    ON PredictionHistory(PredictionGroupId)", conn).ExecuteNonQuery();

                // 创建 AI 模型版本管理表
                string modelSql = @"
                CREATE TABLE IF NOT EXISTS AIModels
                (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ModelVersion TEXT NOT NULL DEFAULT '',
                    ModelName TEXT DEFAULT '',
                    CreateTime TEXT DEFAULT '',
                    Description TEXT DEFAULT '',
                    Accuracy REAL DEFAULT 0
                )";
                SQLiteCommand modelCmd = new SQLiteCommand(modelSql, conn);
                modelCmd.ExecuteNonQuery();

                // 兼容旧表结构
                EnsureColumns(conn, "AIModels", "Accuracy REAL DEFAULT 0");

                // 种子数据：默认模型版本
                SeedAIModels(conn);

                // 从旧 AIPredictHistory 迁移数据到 PredictionHistory（按 Issue 去重）
                MigrateOldPredictions();
            }
        }

        private static void EnsureColumns(SQLiteConnection connection, string tableName, params string[] definitions)
        {
            var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var command = new SQLiteCommand($"PRAGMA table_info([{tableName}])", connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                    existingColumns.Add(reader.GetString(1));
            }

            foreach (string definition in definitions)
            {
                string columnName = definition.Split(' ', 2)[0];
                if (existingColumns.Contains(columnName))
                    continue;

                new SQLiteCommand($"ALTER TABLE [{tableName}] ADD COLUMN {definition}", connection).ExecuteNonQuery();
                AppLogger.Info("数据库迁移", $"{tableName} 新增列 {columnName}");
            }
        }

        // ===== 兼容旧接口 =====

        // 保存历史记录（旧接口：number=期号, date=日期）
        public static void InsertHistory(string number, string date)
        {
            InsertHistory(number, "", date, "");
        }

        // 保存历史记录（旧接口：含生肖）
        public static void InsertHistory(string number, string date, string shengxiao)
        {
            InsertHistory(number, "", date, shengxiao);
        }

        // ===== 新接口 =====

        // 保存历史记录（完整字段）
        public static void InsertHistory(
            string period,
            string numbers,
            string date,
            string shengxiao)
        {
            InsertHistory(period, numbers, "", shengxiao, date, date);
        }

        // 保存历史记录（完整字段 - 新版）
        public static void InsertHistory(
            string period,
            string numbers,
            string specialNumber,
            string specialZodiac,
            string openTime,
            string date)
        {
            // 计算本地生肖（与 DataCrawler 使用相同算法）
            // 注意：API 的 pet 字段是年份生肖，不是特码生肖
            // 特码生肖 = GetShengXiaoByTeMa(特码, 年份生肖)
            string calcZodiac = "";
            string zodiacCheck = "";
            string webZodiac = specialZodiac; // DataCrawler 已用年肖+特码算出的正确生肖

            if (!string.IsNullOrEmpty(specialNumber) && !string.IsNullOrEmpty(date) && date.Length >= 4)
            {
                string yearPet = GetYearPet(date.Substring(0, 4));
                if (!string.IsNullOrEmpty(yearPet))
                {
                    calcZodiac = DataCrawler.GetShengXiaoByTeMa(specialNumber, yearPet);
                }
            }

            // 校验：网站生肖 vs 系统计算
            if (!string.IsNullOrEmpty(webZodiac) && !string.IsNullOrEmpty(calcZodiac))
            {
                zodiacCheck = (webZodiac == calcZodiac) ? "正确" : "错误";
            }
            else if (string.IsNullOrEmpty(webZodiac))
            {
                zodiacCheck = "无网站数据";
            }

            using (SQLiteConnection conn = GetConnection())
            {
                string sql = @"
                INSERT INTO History (Period, Numbers, SpecialNumber, SpecialZodiac, OpenTime, Date, ShengXiao, WebZodiac, CalcZodiac, ZodiacCheck)
                VALUES (@period, @numbers, @specialNum, @specialZodiac, @openTime, @date, @shengxiao, @webZodiac, @calcZodiac, @zodiacCheck)";

                SQLiteCommand cmd = new SQLiteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@period", period);
                cmd.Parameters.AddWithValue("@numbers", numbers);
                cmd.Parameters.AddWithValue("@specialNum", specialNumber);
                cmd.Parameters.AddWithValue("@specialZodiac", specialZodiac);
                cmd.Parameters.AddWithValue("@openTime", openTime);
                cmd.Parameters.AddWithValue("@date", date);
                cmd.Parameters.AddWithValue("@shengxiao", specialZodiac); // ShengXiao 始终与网站数据一致
                cmd.Parameters.AddWithValue("@webZodiac", webZodiac);
                cmd.Parameters.AddWithValue("@calcZodiac", calcZodiac);
                cmd.Parameters.AddWithValue("@zodiacCheck", zodiacCheck);
                cmd.ExecuteNonQuery();
            }
        }

        // 获取历史记录（返回 List，避免连接泄漏）
        public static List<HistoryRecord> GetHistory()
        {
            return GetLatestHistory(int.MaxValue);
        }

        public static int GetHistoryCount()
        {
            using SQLiteConnection conn = GetConnection();
            using SQLiteCommand cmd = new SQLiteCommand("SELECT COUNT(*) FROM History", conn);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }

        // 获取最近 N 条记录（按期号降序，确保最大期号在最前）
        public static List<HistoryRecord> GetLatestHistory(int limit)
        {
            List<HistoryRecord> records = new List<HistoryRecord>();
            limit = Math.Max(0, limit);

            using (SQLiteConnection conn = GetConnection())
            {
                string issueFilter = HistoryIssueUpperBound.Value.HasValue
                    ? "WHERE CAST(Period AS INTEGER) <= @maxIssue"
                    : "";
                string sql = $@"
                SELECT Id, Period, Numbers, SpecialNumber, SpecialZodiac, OpenTime, Date, ShengXiao, WebZodiac, CalcZodiac, ZodiacCheck
                FROM History
                {issueFilter}
                ORDER BY CAST(Period AS INTEGER) DESC, Id DESC
                LIMIT @limit";

                SQLiteCommand cmd = new SQLiteCommand(sql, conn);
                cmd.Parameters.Add("@limit", System.Data.DbType.Int32).Value = limit;
                if (HistoryIssueUpperBound.Value is long maxIssue)
                    cmd.Parameters.AddWithValue("@maxIssue", maxIssue);

                using (SQLiteDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        records.Add(new HistoryRecord
                        {
                            Id = reader.GetInt32(0),
                            Period = reader.IsDBNull(1) ? "" : reader.GetString(1),
                            Numbers = reader.IsDBNull(2) ? "" : reader.GetString(2),
                            SpecialNumber = reader.IsDBNull(3) ? "" : reader.GetString(3),
                            SpecialZodiac = reader.IsDBNull(4) ? "" : reader.GetString(4),
                            OpenTime = reader.IsDBNull(5) ? "" : reader.GetString(5),
                            Date = reader.IsDBNull(6) ? "" : reader.GetString(6),
                            ShengXiao = reader.IsDBNull(7) ? "" : reader.GetString(7),
                            WebZodiac = reader.IsDBNull(8) ? "" : reader.GetString(8),
                            CalcZodiac = reader.IsDBNull(9) ? "" : reader.GetString(9),
                            ZodiacCheck = reader.IsDBNull(10) ? "" : reader.GetString(10)
                        });
                    }
                }
            }

            return records;
        }

        // 删除全部历史
        public static void ClearHistory()
        {
            using (SQLiteConnection conn = GetConnection())
            {
                string sql = "DELETE FROM History";
                SQLiteCommand cmd = new SQLiteCommand(sql, conn);
                cmd.ExecuteNonQuery();
            }
        }

        // 删除单条记录
        public static void DeleteHistory(int id)
        {
            using (SQLiteConnection conn = GetConnection())
            {
                string sql = "DELETE FROM History WHERE Id=@id";
                SQLiteCommand cmd = new SQLiteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@id", id);
                cmd.ExecuteNonQuery();
            }
        }

        // 获取数据库中期号最大的记录（最新开奖）
        public static HistoryRecord GetLatestRecord()
        {
            var list = GetLatestHistory(1);
            return list.Count > 0 ? list[0] : new HistoryRecord();
        }

        // 获取数据库中最晚的期号（按Period数值排序）
        public static string GetLatestPeriod()
        {
            try
            {
                using (SQLiteConnection conn = GetConnection())
                {
                    string sql = HistoryIssueUpperBound.Value.HasValue
                        ? "SELECT Period FROM History WHERE Period != '' AND CAST(Period AS INTEGER) <= @maxIssue ORDER BY CAST(Period AS INTEGER) DESC LIMIT 1"
                        : "SELECT Period FROM History WHERE Period != '' ORDER BY CAST(Period AS INTEGER) DESC LIMIT 1";
                    SQLiteCommand cmd = new SQLiteCommand(sql, conn);
                    if (HistoryIssueUpperBound.Value is long maxIssue)
                        cmd.Parameters.AddWithValue("@maxIssue", maxIssue);
                    var result = cmd.ExecuteScalar();
                    return result != null ? result.ToString() : "";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[数据库] 获取最新期号失败: {ex.Message}");
                return "";
            }
        }

        // 获取数据库中最新的日期
        public static string GetLatestDate()
        {
            try
            {
                using (SQLiteConnection conn = GetConnection())
                {
                    string sql = "SELECT OpenTime FROM History WHERE OpenTime != '' ORDER BY CAST(Period AS INTEGER) DESC LIMIT 1";
                    SQLiteCommand cmd = new SQLiteCommand(sql, conn);
                    var result = cmd.ExecuteScalar();
                    return result != null ? result.ToString() : "";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[数据库] 获取最新日期失败: {ex.Message}");
                return "";
            }
        }

        // 检查期号是否已存在
        public static bool PeriodExists(string period)
        {
            if (string.IsNullOrEmpty(period))
                return false;

            try
            {
                using (SQLiteConnection conn = GetConnection())
                {
                    string sql = "SELECT COUNT(*) FROM History WHERE Period=@period";
                    SQLiteCommand cmd = new SQLiteCommand(sql, conn);
                    cmd.Parameters.AddWithValue("@period", period);
                    long count = (long)cmd.ExecuteScalar();
                    return count > 0;
                }
            }
            catch
            {
                return false;
            }
        }

        // 保存爬虫数据（批量，自动去重）
        public static int SaveCrawlerData(List<DataCrawler.CrawlRecord> records)
        {
            int saved = 0;
            using (SQLiteConnection conn = GetConnection())
            using (SQLiteTransaction transaction = conn.BeginTransaction())
            {
                foreach (var r in records)
                {
                    string calcZodiac = "";
                    if (!string.IsNullOrEmpty(r.SpecialNumber) && r.Date.Length >= 4)
                    {
                        string yearPet = GetYearPet(r.Date.Substring(0, 4));
                        calcZodiac = DataCrawler.GetShengXiaoByTeMa(r.SpecialNumber, yearPet);
                    }
                    string check = string.IsNullOrEmpty(r.SpecialZodiac) ? "无网站数据" :
                        r.SpecialZodiac == calcZodiac ? "正确" : "错误";
                    using var cmd = new SQLiteCommand(@"
                        INSERT OR IGNORE INTO History
                        (Period, Numbers, SpecialNumber, SpecialZodiac, OpenTime, Date, ShengXiao,
                         WebZodiac, CalcZodiac, ZodiacCheck)
                        VALUES (@period, @numbers, @specialNum, @zodiac, @date, @date, @zodiac,
                                @zodiac, @calcZodiac, @check)", conn, transaction);
                    cmd.Parameters.AddWithValue("@period", r.Period);
                    cmd.Parameters.AddWithValue("@numbers", r.Numbers);
                    cmd.Parameters.AddWithValue("@specialNum", r.SpecialNumber);
                    cmd.Parameters.AddWithValue("@zodiac", r.SpecialZodiac);
                    cmd.Parameters.AddWithValue("@date", r.Date);
                    cmd.Parameters.AddWithValue("@calcZodiac", calcZodiac);
                    cmd.Parameters.AddWithValue("@check", check);
                    saved += cmd.ExecuteNonQuery();
                }
                transaction.Commit();
            }

            // 保存后自动校验
            if (saved > 0)
                DataCheckService.CheckRecentData();

            return saved;
        }

        // 更新所有记录的校验字段（网站生肖 vs 系统计算）
        // 不再覆盖 SpecialZodiac/ShengXiao，只填充 CalcZodiac 和 ZodiacCheck
        public static int UpdateAllShengXiao()
        {
            int updated = 0;

            try
            {
                // 获取所有记录
                var records = GetHistory();

                foreach (var r in records)
                {
                    try
                    {
                        // 获取特码
                        string teMa = r.SpecialNumber;
                        if (string.IsNullOrEmpty(teMa))
                            continue;

                        // 获取年份
                        string year = "";
                        if (!string.IsNullOrEmpty(r.OpenTime) && r.OpenTime.Length >= 4)
                            year = r.OpenTime.Substring(0, 4);
                        else if (!string.IsNullOrEmpty(r.Date) && r.Date.Length >= 4)
                            year = r.Date.Substring(0, 4);
                        if (string.IsNullOrEmpty(year))
                            continue;

                        // 获取该年份的生肖
                        string yearPet = GetYearPet(year);
                        if (string.IsNullOrEmpty(yearPet))
                            continue;

                        // 计算本地生肖（仅用于校验）
                        string calcZodiac = DataCrawler.GetShengXiaoByTeMa(teMa, yearPet);
                        if (string.IsNullOrEmpty(calcZodiac))
                            continue;

                        // 网站生肖
                        string webZodiac = r.SpecialZodiac;
                        if (string.IsNullOrEmpty(webZodiac))
                            webZodiac = r.ShengXiao;

                        // 校验结果
                        string zodiacCheck = "";
                        if (!string.IsNullOrEmpty(webZodiac))
                        {
                            zodiacCheck = (webZodiac == calcZodiac) ? "正确" : "错误";
                        }
                        else
                        {
                            zodiacCheck = "无网站数据";
                        }

                        // 更新校验字段，不修改 SpecialZodiac/ShengXiao
                        using (SQLiteConnection conn = GetConnection())
                        {
                            string sql = @"UPDATE History
                            SET WebZodiac=@web, CalcZodiac=@calc, ZodiacCheck=@check
                            WHERE Id=@id";
                            SQLiteCommand cmd = new SQLiteCommand(sql, conn);
                            cmd.Parameters.AddWithValue("@web", webZodiac);
                            cmd.Parameters.AddWithValue("@calc", calcZodiac);
                            cmd.Parameters.AddWithValue("@check", zodiacCheck);
                            cmd.Parameters.AddWithValue("@id", r.Id);
                            cmd.ExecuteNonQuery();
                            updated++;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[数据库] 校验更新失败(ID:{r.Id}): {ex.Message}");
                    }
                }

                Console.WriteLine($"[数据库] 校验字段更新完成，共更新 {updated} 条记录");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[数据库] 更新校验字段失败: {ex.Message}");
            }

            return updated;
        }

        // 获取年份对应的生肖
        public static string GetYearPetPublic(string year)
        {
            return GetYearPet(year);
        }

        // 获取年份对应的生肖（内部实现）
        private static string GetYearPet(string year)
        {
            // 生肖顺序
            string[] shengxiao = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };

            try
            {
                int y = int.Parse(year);
                // 2020年是鼠年，作为基准
                int offset = (y - 2020) % 12;
                if (offset < 0) offset += 12;
                return shengxiao[offset];
            }
            catch
            {
                return "";
            }
        }

        // ===== AI 预测历史 =====

        /// <summary>
        /// 迁移旧数据：为没有 SpecialNumber 的记录补全
        /// 注意：不覆盖已有的 SpecialZodiac（网站数据优先）
        /// </summary>
        public static int MigrateOldData()
        {
            int migrated = 0;
            try
            {
                var records = GetHistory();
                foreach (var r in records)
                {
                    if (!string.IsNullOrEmpty(r.SpecialNumber)) continue;
                    if (string.IsNullOrEmpty(r.Numbers)) continue;
                    try
                    {
                        string specialNum = DataCrawler.GetTeMaNumber(r.Numbers);
                        if (string.IsNullOrEmpty(specialNum)) continue;
                        string numbers6 = DataCrawler.GetFirst6Numbers(r.Numbers);

                        // 仅当 SpecialZodiac 为空时才用本地计算填充
                        bool hasWebZodiac = !string.IsNullOrEmpty(r.SpecialZodiac);
                        string specialZodiac = r.SpecialZodiac;
                        string calcZodiac = "";
                        string zodiacCheck = "";
                        string yearPet = "";
                        if (!string.IsNullOrEmpty(r.Date) && r.Date.Length >= 4)
                            yearPet = GetYearPet(r.Date.Substring(0, 4));

                        if (!string.IsNullOrEmpty(specialNum) && !string.IsNullOrEmpty(yearPet))
                            calcZodiac = DataCrawler.GetShengXiaoByTeMa(specialNum, yearPet);

                        if (!hasWebZodiac)
                        {
                            // 无网站数据时，用本地计算填充（标记为计算值）
                            specialZodiac = calcZodiac;
                            zodiacCheck = "无网站数据";
                        }
                        else
                        {
                            // 有网站数据时，只做校验不覆盖
                            zodiacCheck = (!string.IsNullOrEmpty(calcZodiac) && specialZodiac == calcZodiac)
                                ? "正确" : "错误";
                        }

                        using (SQLiteConnection conn = GetConnection())
                        {
                            string sql = @"UPDATE History
                            SET SpecialNumber=@sn, SpecialZodiac=@sz, ShengXiao=@sz,
                                Numbers=@n6, WebZodiac=@web, CalcZodiac=@calc, ZodiacCheck=@check
                            WHERE Id=@id";
                            SQLiteCommand cmd = new SQLiteCommand(sql, conn);
                            cmd.Parameters.AddWithValue("@sn", specialNum);
                            cmd.Parameters.AddWithValue("@sz", specialZodiac);
                            cmd.Parameters.AddWithValue("@n6", numbers6);
                            cmd.Parameters.AddWithValue("@web", r.SpecialZodiac);
                            cmd.Parameters.AddWithValue("@calc", calcZodiac);
                            cmd.Parameters.AddWithValue("@check", zodiacCheck);
                            cmd.Parameters.AddWithValue("@id", r.Id);
                            cmd.ExecuteNonQuery();
                            migrated++;
                        }
                    }
                    catch (Exception ex) { AppLogger.Error($"迁移旧开奖记录（Id={r.Id}）", ex); }
                }
            }
            catch (Exception ex) { AppLogger.Error("迁移旧开奖记录", ex); }
            return migrated;
        }

        // ===== 新版预测历史（每期唯一一条记录）=====

        /// <summary>
        /// 预测历史记录数据结构 — 每期唯一
        /// </summary>
        public class PredictionRecord
        {
            public int Id { get; set; }
            public string Issue { get; set; } = "";          // 开奖期号
            public string PredictTime { get; set; } = "";    // 预测时间
            public string PredictionGroupId { get; set; } = "";
            public string PredictNumber { get; set; } = "";  // 推荐号码
            public string PredictZodiac { get; set; } = "";  // 推荐生肖
            public string Top6Zodiac { get; set; } = "";     // 推荐前6生肖
            public int AnalysisPeriods { get; set; }          // 分析期数
            public string ScoreDetails { get; set; } = "";   // 完整评分摘要
            public string ModelVersion { get; set; } = "";   // 模型版本
            public string ActualNumber { get; set; } = "";   // 实际开奖特码
            public string ActualZodiac { get; set; } = "";   // 实际生肖
            public string HitResult { get; set; } = "";      // 命中结果：未开奖/命中/未命中
            public string Top6HitResult { get; set; } = "";  // 前6命中结果
        }

        /// <summary>
        /// 从旧 AIPredictHistory 迁移数据到 PredictionHistory（按 Issue 去重）
        /// </summary>
        private static void MigrateOldPredictions()
        {
            try
            {
                using (SQLiteConnection conn = GetConnection())
                {
                    string checkSql = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='AIPredictHistory'";
                    SQLiteCommand checkCmd = new SQLiteCommand(checkSql, conn);
                    long tableExists = (long)checkCmd.ExecuteScalar();
                    if (tableExists == 0) return;

                    string newCountSql = "SELECT COUNT(*) FROM PredictionHistory";
                    SQLiteCommand newCountCmd = new SQLiteCommand(newCountSql, conn);
                    long newCount = (long)newCountCmd.ExecuteScalar();
                    if (newCount > 0) return;

                    string migrateSql = @"
                    INSERT OR IGNORE INTO PredictionHistory (Issue, PredictionGroupId, PredictTime, PredictZodiac, ModelVersion, ActualZodiac, HitResult)
                    SELECT PredictPeriod, 'PRED-' || PredictPeriod, PredictDate, Focus3, ModelVersion, ActualZodiac,
                        CASE
                            WHEN Top3Hit = 1 THEN '命中'
                            WHEN Top3Hit = 0 THEN '未命中'
                            ELSE '未开奖'
                        END
                    FROM AIPredictHistory
                    WHERE PredictPeriod != ''
                    GROUP BY PredictPeriod
                    ORDER BY Id DESC";

                    SQLiteCommand migrateCmd = new SQLiteCommand(migrateSql, conn);
                    int migrated = migrateCmd.ExecuteNonQuery();
                    if (migrated > 0)
                        Console.WriteLine($"[数据库] 旧预测数据迁移完成：{migrated} 条（已按期号去重）");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[数据库] 预测数据迁移失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 保存预测记录（每期唯一，已存在则更新）
        /// </summary>
        private static string GetPredictionGroupId(string issue)
        {
            return string.IsNullOrWhiteSpace(issue) ? "" : $"PRED-{issue.Trim()}";
        }

        public static void SavePrediction(string issue, string predictZodiac, string top6Zodiac,
            string predictNumber, string modelVersion, int analysisPeriods, string scoreDetails)
        {
            using (SQLiteConnection conn = GetConnection())
            {
                string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                string predictionGroupId = GetPredictionGroupId(issue);

                string checkSql = "SELECT COUNT(*) FROM PredictionHistory WHERE Issue=@issue AND AnalysisPeriods=@periods";
                SQLiteCommand checkCmd = new SQLiteCommand(checkSql, conn);
                checkCmd.Parameters.AddWithValue("@issue", issue);
                checkCmd.Parameters.AddWithValue("@periods", analysisPeriods);
                long count = (long)checkCmd.ExecuteScalar();

                if (count > 0)
                {
                    string sql = @"UPDATE PredictionHistory
                    SET PredictTime=@time, PredictNumber=@num, PredictZodiac=@zodiac, Top6Zodiac=@top6,
                        ModelVersion=@model, ScoreDetails=@scores, HitResult='未开奖', Top6HitResult='未开奖',
                        ActualNumber='', ActualZodiac='', PredictionGroupId=@groupId
                    WHERE Issue=@issue AND AnalysisPeriods=@periods";
                    SQLiteCommand cmd = new SQLiteCommand(sql, conn);
                    cmd.Parameters.AddWithValue("@time", now);
                    cmd.Parameters.AddWithValue("@num", predictNumber);
                    cmd.Parameters.AddWithValue("@zodiac", predictZodiac);
                    cmd.Parameters.AddWithValue("@top6", top6Zodiac);
                    cmd.Parameters.AddWithValue("@model", modelVersion);
                    cmd.Parameters.AddWithValue("@scores", scoreDetails);
                    cmd.Parameters.AddWithValue("@groupId", predictionGroupId);
                    cmd.Parameters.AddWithValue("@issue", issue);
                    cmd.Parameters.AddWithValue("@periods", analysisPeriods);
                    cmd.ExecuteNonQuery();
                    Console.WriteLine($"[数据库] 更新预测记录（期号:{issue}）");
                }
                else
                {
                    string sql = @"INSERT INTO PredictionHistory
                    (Issue, PredictionGroupId, PredictTime, PredictNumber, PredictZodiac, Top6Zodiac, AnalysisPeriods, ScoreDetails,
                     ModelVersion, HitResult, Top6HitResult)
                    VALUES (@issue, @groupId, @time, @num, @zodiac, @top6, @periods, @scores, @model, '未开奖', '未开奖')";
                    SQLiteCommand cmd = new SQLiteCommand(sql, conn);
                    cmd.Parameters.AddWithValue("@issue", issue);
                    cmd.Parameters.AddWithValue("@groupId", predictionGroupId);
                    cmd.Parameters.AddWithValue("@time", now);
                    cmd.Parameters.AddWithValue("@num", predictNumber);
                    cmd.Parameters.AddWithValue("@zodiac", predictZodiac);
                    cmd.Parameters.AddWithValue("@top6", top6Zodiac);
                    cmd.Parameters.AddWithValue("@periods", analysisPeriods);
                    cmd.Parameters.AddWithValue("@scores", scoreDetails);
                    cmd.Parameters.AddWithValue("@model", modelVersion);
                    cmd.ExecuteNonQuery();
                    Console.WriteLine($"[数据库] 新建预测记录（期号:{issue}）");
                }
            }
        }

        public static void SaveCloudPrediction(string issue, string predictTime, string predictZodiac,
            string top6Zodiac, string predictNumber, string modelVersion, int analysisPeriods,
            string scoreDetails)
        {
            using SQLiteConnection conn = GetConnection();
            string predictionGroupId = GetPredictionGroupId(issue);
            using SQLiteCommand cmd = new SQLiteCommand(@"
                INSERT INTO PredictionHistory
                (Issue, PredictionGroupId, PredictTime, PredictNumber, PredictZodiac, Top6Zodiac,
                 AnalysisPeriods, ScoreDetails, ModelVersion, HitResult, Top6HitResult)
                VALUES (@issue, @groupId, @time, @num, @zodiac, @top6, @periods, @scores, @model,
                        '未开奖', '未开奖')
                ON CONFLICT(Issue, AnalysisPeriods) DO UPDATE SET
                    PredictionGroupId=excluded.PredictionGroupId,
                    PredictTime=excluded.PredictTime,
                    PredictNumber=excluded.PredictNumber,
                    PredictZodiac=excluded.PredictZodiac,
                    Top6Zodiac=excluded.Top6Zodiac,
                    ScoreDetails=excluded.ScoreDetails,
                    ModelVersion=excluded.ModelVersion", conn);
            cmd.Parameters.AddWithValue("@issue", issue);
            cmd.Parameters.AddWithValue("@groupId", predictionGroupId);
            cmd.Parameters.AddWithValue("@time", predictTime);
            cmd.Parameters.AddWithValue("@num", predictNumber);
            cmd.Parameters.AddWithValue("@zodiac", predictZodiac);
            cmd.Parameters.AddWithValue("@top6", top6Zodiac);
            cmd.Parameters.AddWithValue("@periods", analysisPeriods);
            cmd.Parameters.AddWithValue("@scores", scoreDetails);
            cmd.Parameters.AddWithValue("@model", modelVersion);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// 验证预测结果（开奖后自动匹配）
        /// </summary>
        public static void VerifyPrediction(string issue, string actualNumber, string actualZodiac)
        {
            using (SQLiteConnection conn = GetConnection())
            {
                string findSql = "SELECT Id, PredictZodiac, Top6Zodiac FROM PredictionHistory WHERE Issue=@issue AND (HitResult='未开奖' OR HitResult='')";
                SQLiteCommand findCmd = new SQLiteCommand(findSql, conn);
                findCmd.Parameters.AddWithValue("@issue", issue);

                var pending = new List<(int id, string top3, string top6)>();
                using (SQLiteDataReader reader = findCmd.ExecuteReader())
                {
                    while (reader.Read())
                        pending.Add((reader.GetInt32(0), reader.IsDBNull(1) ? "" : reader.GetString(1),
                            reader.IsDBNull(2) ? "" : reader.GetString(2)));
                }

                foreach (var item in pending)
                {
                    bool hit = !string.IsNullOrEmpty(item.top3) &&
                               !string.IsNullOrEmpty(actualZodiac) &&
                               item.top3.Split(',').Contains(actualZodiac);
                    bool top6Hit = !string.IsNullOrEmpty(item.top6) &&
                                   !string.IsNullOrEmpty(actualZodiac) &&
                                   item.top6.Split(',').Contains(actualZodiac);

                    string updateSql = @"UPDATE PredictionHistory
                    SET ActualNumber=@num, ActualZodiac=@zodiac, HitResult=@result, Top6HitResult=@top6Result
                    WHERE Id=@id";
                    SQLiteCommand updateCmd = new SQLiteCommand(updateSql, conn);
                    updateCmd.Parameters.AddWithValue("@num", actualNumber);
                    updateCmd.Parameters.AddWithValue("@zodiac", actualZodiac);
                    updateCmd.Parameters.AddWithValue("@result", hit ? "命中" : "未命中");
                    updateCmd.Parameters.AddWithValue("@top6Result", top6Hit ? "命中" : "未命中");
                    updateCmd.Parameters.AddWithValue("@id", item.id);
                    updateCmd.ExecuteNonQuery();

                    Console.WriteLine($"[预测验证] 期号:{issue} 实际:{actualNumber} {actualZodiac} {(hit ? "命中" : "未命中")}");
                }
            }
        }

        public static void VerifyAIPredict(string actualZodiac)
        {
            var unverified = GetPredictionHistory(1)
                .Where(r => r.HitResult == "未开奖" || string.IsNullOrEmpty(r.HitResult))
                .ToList();
            if (unverified.Count > 0 && !string.IsNullOrEmpty(unverified[0].Issue))
                VerifyPrediction(unverified[0].Issue, "", actualZodiac);
        }

        public static int BatchVerifyAIPredicts()
        {
            int verified = 0;
            try
            {
                var unverified = GetPredictionHistory(int.MaxValue)
                    .Where(r => r.HitResult == "未开奖" || string.IsNullOrEmpty(r.HitResult))
                    .Where(r => !string.IsNullOrEmpty(r.Issue))
                    .ToList();

                var history = GetHistory();
                var periodMap = new Dictionary<string, (string number, string zodiac)>();
                foreach (var h in history)
                {
                    if (!string.IsNullOrEmpty(h.Period) && !periodMap.ContainsKey(h.Period))
                        periodMap[h.Period] = (h.SpecialNumber, h.SpecialZodiac);
                }

                foreach (var issue in unverified.Select(r => r.Issue).Distinct())
                {
                    if (!periodMap.ContainsKey(issue)) continue;
                    var (actualNum, actualZodiac) = periodMap[issue];
                    if (string.IsNullOrEmpty(actualZodiac)) continue;
                    VerifyPrediction(issue, actualNum, actualZodiac);
                    verified += unverified.Count(r => r.Issue == issue);
                }

                if (verified > 0)
                    Console.WriteLine($"[预测验证] 批量验证完成：{verified} 条记录");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[预测验证] 批量验证失败: {ex.Message}");
            }
            return verified;
        }

        /// <summary>
        /// 获取预测历史（按期号降序，每期唯一一条）
        /// </summary>
        public static List<PredictionRecord> GetPredictionHistory(int limit = 50)
        {
            var records = new List<PredictionRecord>();
            using (SQLiteConnection conn = GetConnection())
            {
                string sql = $@"
                SELECT Id, Issue, PredictionGroupId, PredictTime, PredictNumber, PredictZodiac, Top6Zodiac, AnalysisPeriods,
                       ScoreDetails, ModelVersion, ActualNumber, ActualZodiac, HitResult, Top6HitResult
                FROM PredictionHistory
                ORDER BY CAST(Issue AS INTEGER) DESC, AnalysisPeriods ASC
                LIMIT {limit}";

                SQLiteCommand cmd = new SQLiteCommand(sql, conn);
                using (SQLiteDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        records.Add(new PredictionRecord
                        {
                            Id = reader.GetInt32(0),
                            Issue = reader.IsDBNull(1) ? "" : reader.GetString(1),
                            PredictionGroupId = reader.IsDBNull(2) ? "" : reader.GetString(2),
                            PredictTime = reader.IsDBNull(3) ? "" : reader.GetString(3),
                            PredictNumber = reader.IsDBNull(4) ? "" : reader.GetString(4),
                            PredictZodiac = reader.IsDBNull(5) ? "" : reader.GetString(5),
                            Top6Zodiac = reader.IsDBNull(6) ? "" : reader.GetString(6),
                            AnalysisPeriods = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                            ScoreDetails = reader.IsDBNull(8) ? "" : reader.GetString(8),
                            ModelVersion = reader.IsDBNull(9) ? "" : reader.GetString(9),
                            ActualNumber = reader.IsDBNull(10) ? "" : reader.GetString(10),
                            ActualZodiac = reader.IsDBNull(11) ? "" : reader.GetString(11),
                            HitResult = reader.IsDBNull(12) ? "" : reader.GetString(12)
                            ,Top6HitResult = reader.IsDBNull(13) ? "" : reader.GetString(13)
                        });
                    }
                }
            }
            return records;
        }

        // 兼容旧接口
        public static List<AIPredictRecord> GetAIPredictHistory(int limit = 50)
        {
            return GetPredictionHistory(limit).Select(r => new AIPredictRecord
            {
                Id = r.Id,
                PredictPeriod = r.Issue,
                PredictDate = r.PredictTime,
                ModelVersion = r.ModelVersion,
                Focus3 = r.PredictZodiac,
                ActualZodiac = r.ActualZodiac,
                Top3Hit = r.HitResult == "命中" ? 1 : (r.HitResult == "未命中" ? 0 : -1),
                Top6Hit = r.HitResult == "命中" ? 1 : (r.HitResult == "未命中" ? 0 : -1)
            }).ToList();
        }

        public static PredictionRecord? GetLatestPredictionRecord()
        {
            var records = GetPredictionHistory(1);
            return records.Count > 0 ? records[0] : null;
        }

        public static AIPredictRecord? GetLatestAIPredictRecord()
        {
            var records = GetAIPredictHistory(1);
            return records.Count > 0 ? records[0] : null;
        }

        /// <summary>
        /// 检查指定期号是否已有预测记录（基于 Issue 主键）
        /// </summary>
        public static bool HasPredictionForIssue(string issue)
        {
            if (string.IsNullOrEmpty(issue)) return false;
            try
            {
                using (SQLiteConnection conn = GetConnection())
                {
                    string sql = "SELECT COUNT(*) FROM PredictionHistory WHERE Issue=@issue";
                    SQLiteCommand cmd = new SQLiteCommand(sql, conn);
                    cmd.Parameters.AddWithValue("@issue", issue);
                    long count = (long)cmd.ExecuteScalar();
                    return count > 0;
                }
            }
            catch (Exception ex) { AppLogger.Error("检查预测记录", ex); return false; }
        }

        // 兼容旧接口（改为基于 Issue）
        public static bool HasAIPredictToday()
        {
            string latestPeriod = GetLatestPeriod();
            if (string.IsNullOrEmpty(latestPeriod)) return false;
            try
            {
                int nextPeriod = int.Parse(latestPeriod) + 1;
                return HasPredictionForIssue(nextPeriod.ToString());
            }
            catch (Exception ex) { AppLogger.Error("检查今日 AI 预测", ex); return false; }
        }

        public static (int Total, int Top3Hits, int Top6Hits, double Top3Rate, double Top6Rate) GetAIPredictStats()
        {
            try
            {
                int total = 0, hits = 0, top6Hits = 0;
                using (SQLiteConnection conn = GetConnection())
                {
                    string sql = @"
                    WITH Ranked AS (
                        SELECT Issue, HitResult, Top6HitResult,
                               ROW_NUMBER() OVER (
                                   PARTITION BY Issue
                                   ORDER BY CASE WHEN AnalysisPeriods=500 THEN 0 ELSE 1 END,
                                            AnalysisPeriods DESC,
                                            Id DESC
                               ) AS rn
                        FROM PredictionHistory
                        WHERE Issue != '' AND HitResult IN ('命中','未命中')
                    )
                    SELECT COUNT(*),
                           SUM(CASE WHEN HitResult='命中' THEN 1 ELSE 0 END),
                           SUM(CASE WHEN Top6HitResult='命中' THEN 1 ELSE 0 END)
                    FROM Ranked
                    WHERE rn = 1";
                    SQLiteCommand cmd = new SQLiteCommand(sql, conn);
                    using (SQLiteDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            total = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                            hits = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                            top6Hits = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                        }
                    }
                }
                double rate = total > 0 ? (double)hits / total * 100 : 0;
                double top6Rate = total > 0 ? (double)top6Hits / total * 100 : 0;
                return (total, hits, top6Hits, rate, top6Rate);
            }
            catch (Exception ex) { AppLogger.Error("读取 AI 预测统计", ex); return (0, 0, 0, 0, 0); }
        }

        // 保留旧类定义以兼容
        public class AIPredictRecord
        {
            public int Id { get; set; }
            public string PredictPeriod { get; set; } = "";
            public string PredictDate { get; set; } = "";
            public string ModelVersion { get; set; } = "";
            public int AnalysisPeriods { get; set; }
            public string Recommended6 { get; set; } = "";
            public string Focus3 { get; set; } = "";
            public string GptAnalysis { get; set; } = "";
            public string ActualZodiac { get; set; } = "";
            public int Top3Hit { get; set; } = -1;
            public int Top6Hit { get; set; } = -1;
        }

        // ===== AI 模型版本管理 =====

        public class AIModelRecord
        {
            public int Id { get; set; }
            public string ModelVersion { get; set; } = "";
            public string ModelName { get; set; } = "";
            public string CreateTime { get; set; } = "";
            public string Description { get; set; } = "";
            public double Accuracy { get; set; }
        }

        private static void SeedAIModels(SQLiteConnection conn)
        {
            string checkSql = "SELECT COUNT(*) FROM AIModels";
            SQLiteCommand checkCmd = new SQLiteCommand(checkSql, conn);
            long count = (long)checkCmd.ExecuteScalar();
            if (count > 0) return;

            var seedData = new (string version, string name, string desc)[]
            {
                ("V1", "冷热模型", "基于生肖冷热统计的基础预测模型"),
                ("V2", "遗漏模型", "结合遗漏分析和周期规律的预测模型"),
                ("V3", "综合模型", "多维度综合评分模型（频率+趋势+遗漏+生肖+周期）")
            };

            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            foreach (var (version, name, desc) in seedData)
            {
                string sql = "INSERT INTO AIModels (ModelVersion, ModelName, CreateTime, Description, Accuracy) VALUES (@v, @n, @t, @d, 0)";
                SQLiteCommand cmd = new SQLiteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@v", version);
                cmd.Parameters.AddWithValue("@n", name);
                cmd.Parameters.AddWithValue("@t", now);
                cmd.Parameters.AddWithValue("@d", desc);
                cmd.ExecuteNonQuery();
            }
        }

        public static List<AIModelRecord> GetAIModels()
        {
            var models = new List<AIModelRecord>();
            using (SQLiteConnection conn = GetConnection())
            {
                string sql = "SELECT Id, ModelVersion, ModelName, CreateTime, Description, Accuracy FROM AIModels ORDER BY Id";
                SQLiteCommand cmd = new SQLiteCommand(sql, conn);
                using (SQLiteDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        models.Add(new AIModelRecord
                        {
                            Id = reader.GetInt32(0),
                            ModelVersion = reader.IsDBNull(1) ? "" : reader.GetString(1),
                            ModelName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                            CreateTime = reader.IsDBNull(3) ? "" : reader.GetString(3),
                            Description = reader.IsDBNull(4) ? "" : reader.GetString(4),
                            Accuracy = reader.IsDBNull(5) ? 0 : reader.GetDouble(5)
                        });
                    }
                }
            }
            return models;
        }

        public static void UpdateModelAccuracy(string modelVersion, double accuracy)
        {
            using (SQLiteConnection conn = GetConnection())
            {
                string sql = "UPDATE AIModels SET Accuracy=@acc WHERE ModelVersion=@v";
                SQLiteCommand cmd = new SQLiteCommand(sql, conn);
                cmd.Parameters.AddWithValue("@acc", accuracy);
                cmd.Parameters.AddWithValue("@v", modelVersion);
                cmd.ExecuteNonQuery();
            }
        }

        public static string GetCurrentModelVersion()
        {
            return "V3";
        }

    }
}
