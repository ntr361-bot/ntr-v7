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
            $"Data Source={DatabasePath};Version=3;Default Timeout=30;Journal Mode=WAL;";

        private static string ResolveDatabasePath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string databasePath = Path.Combine(AppPaths.DataDirectory, "history.db");
            string projectDir = FindProjectDirectory(baseDir);
            string[] legacyPaths =
            {
                Path.Combine(baseDir, "history.db"),
                Path.Combine(projectDir, "history.db"),
                Path.Combine(projectDir, "data", "history.db"),
                Path.Combine(Directory.GetCurrentDirectory(), "history.db")
            };

            PromoteLegacyDatabaseOrThrow(databasePath, legacyPaths);

            return databasePath;
        }

        private static void PromoteLegacyDatabaseOrThrow(string databasePath, IEnumerable<string> legacyPaths)
        {
            if (File.Exists(databasePath) ||
                File.Exists(databasePath + "-wal") ||
                File.Exists(databasePath + "-shm"))
                return;

            var candidates = legacyPaths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(path => !Path.GetFullPath(databasePath)
                    .Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
                .Where(File.Exists)
                .Select(path => (Path: path, Rows: CountUserRows(path)))
                .Where(candidate => candidate.Rows.History > 0 ||
                                    candidate.Rows.Predictions > 0 ||
                                    candidate.Rows.LegacyPredictions > 0)
                .ToList();
            if (candidates.Count == 0) return;

            var dominant = candidates.Where(candidate => candidates.All(other =>
                    candidate.Rows.History >= other.Rows.History &&
                    candidate.Rows.Predictions >= other.Rows.Predictions &&
                    candidate.Rows.LegacyPredictions >= other.Rows.LegacyPredictions))
                .ToList();
            if (dominant.Count != 1)
                throw new InvalidOperationException(
                    "发现多份互不包含的旧数据库；为防止历史丢失，已中止创建空稳定库，请保留原文件人工合并");

            if (!TryPromoteLegacyDatabase(databasePath, dominant[0].Path))
                throw new InvalidOperationException(
                    $"旧数据库迁移未完成；为防止历史丢失，已中止创建空稳定库：{dominant[0].Path}");
        }

        private static string FindProjectDirectory(string startDir)
        {
            var dir = new DirectoryInfo(startDir);
            while (dir != null)
            {
                try
                {
                    if (dir.GetFiles("*.csproj").Any())
                        return dir.FullName;
                }
                catch (UnauthorizedAccessException)
                {
                    break;
                }
                catch (IOException)
                {
                    break;
                }
                dir = dir.Parent;
            }

            return Directory.GetCurrentDirectory();
        }

        private static bool TryPromoteLegacyDatabase(string rootDb, string legacyRuntimeDb)
        {
            try
            {
                if (!File.Exists(legacyRuntimeDb)) return false;
                // 稳定库一旦存在即视为权威来源，禁止用任何行数启发式整库覆盖。
                if (File.Exists(rootDb) || File.Exists(rootDb + "-wal") || File.Exists(rootDb + "-shm"))
                    return false;

                var legacyRows = CountUserRows(legacyRuntimeDb);
                if (legacyRows.History == 0 && legacyRows.Predictions == 0 && legacyRows.LegacyPredictions == 0)
                    return false;

                Directory.CreateDirectory(Path.GetDirectoryName(rootDb) ?? ".");
                string temporaryPath = rootDb + $".migrating-{Guid.NewGuid():N}.tmp";
                try
                {
                    CreateConsistentDatabaseCopy(legacyRuntimeDb, temporaryPath);
                    var copiedRows = CountUserRows(temporaryPath);
                    if (copiedRows != legacyRows)
                        throw new InvalidDataException("旧数据库在线备份行数校验失败");
                    using (var verify = new SQLiteConnection($"Data Source={temporaryPath};Version=3;Read Only=True;"))
                    {
                        verify.Open();
                        using var check = new SQLiteCommand("PRAGMA quick_check", verify);
                        if (!string.Equals(Convert.ToString(check.ExecuteScalar()), "ok", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("旧数据库在线备份完整性校验失败");
                    }

                    // 无覆盖移动：若另一进程已先创建稳定库，本次迁移会失败并保留先创建的库。
                    File.Move(temporaryPath, rootDb);
                    return true;
                }
                finally
                {
                    DeleteTemporaryDatabaseFiles(temporaryPath);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("迁移旧数据库", ex);
                return false;
            }
        }

        private static (int History, int Predictions, int LegacyPredictions) CountUserRows(string dbPath)
        {
            try
            {
                if (!File.Exists(dbPath)) return (0, 0, 0);
                using var conn = new SQLiteConnection($"Data Source={dbPath};Version=3;Read Only=True;");
                conn.Open();
                return (CountTableRows(conn, "History"), CountTableRows(conn, "PredictionHistory"),
                    CountTableRows(conn, "AIPredictHistory"));
            }
            catch (Exception ex)
            {
                AppLogger.Error($"读取数据库记录数 ({dbPath})", ex);
                throw new InvalidDataException($"无法读取旧数据库：{dbPath}", ex);
            }
        }

        private static void CreateConsistentDatabaseCopy(string sourcePath, string destinationPath)
        {
            using var source = new SQLiteConnection($"Data Source={sourcePath};Version=3;Read Only=True;");
            using var destination = new SQLiteConnection($"Data Source={destinationPath};Version=3;");
            source.Open();
            destination.Open();
            source.BackupDatabase(destination, "main", "main", -1, null, 100);
        }

        private static void DeleteTemporaryDatabaseFiles(string temporaryPath)
        {
            foreach (string path in new[] { temporaryPath, temporaryPath + "-wal", temporaryPath + "-shm" })
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static int CountTableRows(SQLiteConnection conn, string tableName)
        {
            using var exists = new SQLiteCommand(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name", conn);
            exists.Parameters.AddWithValue("@name", tableName);
            if (Convert.ToInt32(exists.ExecuteScalar()) == 0) return 0;

            using var count = new SQLiteCommand($"SELECT COUNT(*) FROM [{tableName}]", conn);
            return Convert.ToInt32(count.ExecuteScalar());
        }

        // 历史记录数据结构
        public class HistoryRecord
        {
            public int Id { get; set; }
            public string Period { get; set; } = "";       // 期号
            public string Numbers { get; set; } = "";      // 前6个开奖号码
            public string SpecialNumber { get; set; } = "";// 特码（第7个号码）
            public string SpecialZodiac { get; set; } = "";// 特码生肖（网站提供，权威来源）
            public string SpecialWaveColor { get; set; } = "";
            public string WaveColorSource { get; set; } = "";
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
            using (var busyTimeout = new SQLiteCommand("PRAGMA busy_timeout=30000", conn))
                busyTimeout.ExecuteNonQuery();

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
                    SpecialWaveColor TEXT DEFAULT '',
                    WaveColorSource TEXT DEFAULT '',
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
                    "SpecialWaveColor TEXT DEFAULT ''",
                    "WaveColorSource TEXT DEFAULT ''",
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
                    Top6HitResult TEXT DEFAULT '',
                    ReviewDetails TEXT DEFAULT '',
                    LearningDetails TEXT DEFAULT ''
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
                    "Top6HitResult TEXT DEFAULT ''",
                    "ReviewDetails TEXT DEFAULT ''",
                    "LearningDetails TEXT DEFAULT ''",
                    "FinalRankingJson TEXT DEFAULT ''",
                    "BaseModelScoresJson TEXT DEFAULT ''",
                    "FeatureSnapshotJson TEXT DEFAULT ''",
                    "WeightSnapshotJson TEXT DEFAULT ''",
                    "MappingSnapshotJson TEXT DEFAULT ''",
                    "ActualRank INTEGER DEFAULT 0",
                    "LearningStatus TEXT DEFAULT 'Pending'",
                    "LearnedAt TEXT DEFAULT ''");
                EnsureAutoLearningSchema(conn);

                // 历史快照不得在初始化时物理去重；旧版本的重复行也要完整保留以便审计。
                new SQLiteCommand("DROP INDEX IF EXISTS idx_prediction_issue", conn).ExecuteNonQuery();
                new SQLiteCommand("DROP INDEX IF EXISTS idx_prediction_issue_periods", conn).ExecuteNonQuery();
                new SQLiteCommand(@"CREATE INDEX IF NOT EXISTS idx_prediction_identity
                    ON PredictionHistory(Issue, AnalysisPeriods, ModelVersion, Id)", conn).ExecuteNonQuery();

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

                // 已停用模型只从当前预测、学习及界面入口中过滤；历史记录必须保留用于审计。
                // 旧 AIPredictHistory 表保留为只读归档，不能在升级初始化时物理删除。
            }

            TryRestoreSiblingLegacyPredictionHistory();
        }

        private static void TryRestoreSiblingLegacyPredictionHistory()
        {
            try
            {
                string? activeDirectory = Path.GetDirectoryName(DatabasePath);
                if (activeDirectory is null ||
                    !string.Equals(Path.GetFileName(activeDirectory), "V7History", StringComparison.OrdinalIgnoreCase))
                    return;

                string projectDirectory = FindProjectDirectory(activeDirectory);
                string[] sources =
                {
                    Path.GetFullPath(Path.Combine(activeDirectory, "..", "Debug", "net10.0-windows", "history.db")),
                    Path.Combine(projectDirectory, "data", "history.db")
                };
                foreach (string source in sources.Distinct(StringComparer.OrdinalIgnoreCase))
                    ImportLegacyPredictionHistory(source);
            }
            catch (Exception ex)
            {
                AppLogger.Error("恢复旧预测历史", ex);
            }
        }

        public static int ImportLegacyPredictionHistory(string sourceDatabasePath)
        {
            if (string.IsNullOrWhiteSpace(sourceDatabasePath) || !File.Exists(sourceDatabasePath)) return 0;
            if (Path.GetFullPath(sourceDatabasePath).Equals(Path.GetFullPath(DatabasePath), StringComparison.OrdinalIgnoreCase)) return 0;

            int imported = 0;
            using var source = new SQLiteConnection($"Data Source={sourceDatabasePath};Version=3;Read Only=True;");
            source.Open();
            using var exists = new SQLiteCommand("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='PredictionHistory'", source);
            if (Convert.ToInt32(exists.ExecuteScalar()) == 0) return 0;

            const string columns = "Issue,PredictTime,PredictNumber,PredictZodiac,Top6Zodiac,PredictionGroupId," +
                                   "AnalysisPeriods,ScoreDetails,ModelVersion,ActualNumber,ActualZodiac,HitResult," +
                                   "Top6HitResult,ReviewDetails,LearningDetails";
            using var read = new SQLiteCommand($@"SELECT {columns} FROM PredictionHistory
                WHERE AnalysisPeriods NOT IN (0,200)
                  AND ModelVersion NOT LIKE 'V7%'
                  AND ModelVersion <> '云端 V6.3'
                ORDER BY Id", source);
            using SQLiteDataReader reader = read.ExecuteReader();
            using SQLiteConnection target = GetConnection();
            using SQLiteTransaction transaction = target.BeginTransaction();
            while (reader.Read())
            {
                using var insert = new SQLiteCommand($@"INSERT INTO PredictionHistory ({columns})
                    SELECT @issue,@time,@number,@zodiac,@top6,@group,@periods,@scores,@model,
                           @actualNumber,@actualZodiac,@hit,@top6Hit,@review,@learning
                    WHERE NOT EXISTS (
                        SELECT 1 FROM PredictionHistory
                        WHERE Issue=@issue AND AnalysisPeriods=@periods AND ModelVersion=@model
                    )", target, transaction);
                string issue = Convert.ToString(reader["Issue"]) ?? "";
                insert.Parameters.AddWithValue("@issue", issue);
                insert.Parameters.AddWithValue("@time", Convert.ToString(reader["PredictTime"]) ?? "");
                insert.Parameters.AddWithValue("@number", Convert.ToString(reader["PredictNumber"]) ?? "");
                insert.Parameters.AddWithValue("@zodiac", Convert.ToString(reader["PredictZodiac"]) ?? "");
                insert.Parameters.AddWithValue("@top6", Convert.ToString(reader["Top6Zodiac"]) ?? "");
                insert.Parameters.AddWithValue("@group", string.IsNullOrWhiteSpace(Convert.ToString(reader["PredictionGroupId"])) ? GetPredictionGroupId(issue) : Convert.ToString(reader["PredictionGroupId"]));
                insert.Parameters.AddWithValue("@periods", Convert.ToInt32(reader["AnalysisPeriods"]));
                insert.Parameters.AddWithValue("@scores", Convert.ToString(reader["ScoreDetails"]) ?? "");
                insert.Parameters.AddWithValue("@model", Convert.ToString(reader["ModelVersion"]) ?? "");
                insert.Parameters.AddWithValue("@actualNumber", Convert.ToString(reader["ActualNumber"]) ?? "");
                insert.Parameters.AddWithValue("@actualZodiac", Convert.ToString(reader["ActualZodiac"]) ?? "");
                insert.Parameters.AddWithValue("@hit", Convert.ToString(reader["HitResult"]) ?? "");
                insert.Parameters.AddWithValue("@top6Hit", Convert.ToString(reader["Top6HitResult"]) ?? "");
                insert.Parameters.AddWithValue("@review", Convert.ToString(reader["ReviewDetails"]) ?? "");
                insert.Parameters.AddWithValue("@learning", Convert.ToString(reader["LearningDetails"]) ?? "");
                imported += insert.ExecuteNonQuery();
            }
            transaction.Commit();
            return imported;
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

        public static void EnsureAutoLearningSchema(SQLiteConnection connection)
        {
            EnsureColumns(connection, "PredictionHistory",
                "FinalRankingJson TEXT DEFAULT ''",
                "BaseModelScoresJson TEXT DEFAULT ''",
                "FeatureSnapshotJson TEXT DEFAULT ''",
                "WeightSnapshotJson TEXT DEFAULT ''",
                "ActualRank INTEGER DEFAULT 0",
                "LearningStatus TEXT DEFAULT 'Pending'",
                "LearnedAt TEXT DEFAULT ''");
            new SQLiteCommand(@"CREATE TABLE IF NOT EXISTS ModelMemory
                (MemoryKey TEXT PRIMARY KEY, MemoryJson TEXT NOT NULL DEFAULT '', UpdatedAt TEXT NOT NULL DEFAULT '')", connection)
                .ExecuteNonQuery();
            new SQLiteCommand(@"CREATE TABLE IF NOT EXISTS LearningAdjustmentHistory
                (Id INTEGER PRIMARY KEY AUTOINCREMENT, Issue TEXT NOT NULL DEFAULT '', AdjustedAt TEXT NOT NULL DEFAULT '',
                 OldWeightsJson TEXT NOT NULL DEFAULT '', NewWeightsJson TEXT NOT NULL DEFAULT '',
                 FeatureContributionJson TEXT NOT NULL DEFAULT '', Reason TEXT NOT NULL DEFAULT '')", connection)
                .ExecuteNonQuery();
            new SQLiteCommand(@"CREATE INDEX IF NOT EXISTS idx_prediction_learning_status
                ON PredictionHistory(LearningStatus, Issue)", connection).ExecuteNonQuery();
            new SQLiteCommand(@"CREATE INDEX IF NOT EXISTS idx_learning_adjustment_issue
                ON LearningAdjustmentHistory(Issue)", connection).ExecuteNonQuery();
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
                SELECT Id, Period, Numbers, SpecialNumber, SpecialZodiac, SpecialWaveColor, WaveColorSource, OpenTime, Date, ShengXiao, WebZodiac, CalcZodiac, ZodiacCheck
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
                            SpecialWaveColor = reader.IsDBNull(5) ? "" : reader.GetString(5),
                            WaveColorSource = reader.IsDBNull(6) ? "" : reader.GetString(6),
                            OpenTime = reader.IsDBNull(7) ? "" : reader.GetString(7),
                            Date = reader.IsDBNull(8) ? "" : reader.GetString(8),
                            ShengXiao = reader.IsDBNull(9) ? "" : reader.GetString(9),
                            WebZodiac = reader.IsDBNull(10) ? "" : reader.GetString(10),
                            CalcZodiac = reader.IsDBNull(11) ? "" : reader.GetString(11),
                            ZodiacCheck = reader.IsDBNull(12) ? "" : reader.GetString(12)
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
                        (Period, Numbers, SpecialNumber, SpecialZodiac, SpecialWaveColor, WaveColorSource, OpenTime, Date, ShengXiao,
                         WebZodiac, CalcZodiac, ZodiacCheck)
                        VALUES (@period, @numbers, @specialNum, @zodiac, @waveColor, @waveSource, @date, @date, @zodiac,
                                @zodiac, @calcZodiac, @check)", conn, transaction);
                    cmd.Parameters.AddWithValue("@period", r.Period);
                    cmd.Parameters.AddWithValue("@numbers", r.Numbers);
                    cmd.Parameters.AddWithValue("@specialNum", r.SpecialNumber);
                    cmd.Parameters.AddWithValue("@zodiac", r.SpecialZodiac);
                    cmd.Parameters.AddWithValue("@waveColor", r.SpecialWaveColor);
                    cmd.Parameters.AddWithValue("@waveSource", r.WaveColorSource);
                    cmd.Parameters.AddWithValue("@date", r.Date);
                    cmd.Parameters.AddWithValue("@calcZodiac", calcZodiac);
                    cmd.Parameters.AddWithValue("@check", check);
                    saved += cmd.ExecuteNonQuery();

                    using var waveUpdate = new SQLiteCommand(@"
                        UPDATE History
                        SET SpecialWaveColor = @waveColor,
                            WaveColorSource = @waveSource
                        WHERE Period = @period
                          AND @waveColor <> ''
                          AND (
                              SpecialWaveColor IS NULL OR SpecialWaveColor = ''
                              OR (WaveColorSource NOT LIKE 'WebPage%' AND @waveSource LIKE 'WebPage%')
                          )", conn, transaction);
                    waveUpdate.Parameters.AddWithValue("@period", r.Period);
                    waveUpdate.Parameters.AddWithValue("@waveColor", r.SpecialWaveColor);
                    waveUpdate.Parameters.AddWithValue("@waveSource", r.WaveColorSource);
                    waveUpdate.ExecuteNonQuery();
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
            return int.TryParse(year, out int value) ? V65MappingService.GetYearZodiac(value) : "";
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
            public string ReviewDetails { get; set; } = ""; // 开奖后的错因复盘
            public string LearningDetails { get; set; } = ""; // 预测时采用的学习状态
            public string FinalRankingJson { get; set; } = "";
            public string BaseModelScoresJson { get; set; } = "";
            public string FeatureSnapshotJson { get; set; } = "";
            public string WeightSnapshotJson { get; set; } = "";
            public string MappingSnapshotJson { get; set; } = "";
            public int ActualRank { get; set; }
            public string LearningStatus { get; set; } = "Pending";
            public string LearnedAt { get; set; } = "";
        }

        /// <summary>
        /// 保存模型的首次正式预测快照；同一期、同周期、同模型的重复保存保持幂等。
        /// </summary>
        private static string GetPredictionGroupId(string issue)
        {
            return string.IsNullOrWhiteSpace(issue) ? "" : $"PRED-{issue.Trim()}";
        }

        private static DateTime ResolveMappingSnapshotDate(string issue)
        {
            if (DateTime.TryParseExact(issue, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out DateTime datedIssue))
                return datedIssue;
            HistoryRecord? latest = GetLatestHistory(1).FirstOrDefault();
            if (latest != null && DateTime.TryParse(latest.OpenTime, out DateTime openTime)) return openTime;
            if (latest != null && DateTime.TryParse(latest.Date, out DateTime date)) return date;
            return DateTime.Today;
        }

        public static void SavePrediction(string issue, string predictZodiac, string top6Zodiac,
            string predictNumber, string modelVersion, int analysisPeriods, string scoreDetails,
            string learningDetails = "", string finalRankingJson = "", string baseModelScoresJson = "",
            string featureSnapshotJson = "", string weightSnapshotJson = "", string mappingSnapshotJson = "")
        {
            mappingSnapshotJson = string.IsNullOrWhiteSpace(mappingSnapshotJson)
                ? V65MappingService.CreateSnapshot(issue, ResolveMappingSnapshotDate(issue))
                : mappingSnapshotJson;
            using (SQLiteConnection conn = GetConnection())
            {
                string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                string predictionGroupId = GetPredictionGroupId(issue);
                bool transactionStarted = false;
                try
                {
                    new SQLiteCommand("BEGIN IMMEDIATE", conn).ExecuteNonQuery();
                    transactionStarted = true;
                    string sql = @"INSERT INTO PredictionHistory
                    (Issue, PredictionGroupId, PredictTime, PredictNumber, PredictZodiac, Top6Zodiac, AnalysisPeriods, ScoreDetails,
                     ModelVersion, LearningDetails, HitResult, Top6HitResult, FinalRankingJson,
                     BaseModelScoresJson, FeatureSnapshotJson, WeightSnapshotJson, MappingSnapshotJson, LearningStatus)
                    SELECT @issue, @groupId, @time, @num, @zodiac, @top6, @periods, @scores, @model, @learning,
                           '未开奖', '未开奖', @ranking, @baseScores, @features, @weights, @mapping, 'Pending'
                    WHERE NOT EXISTS (
                        SELECT 1 FROM PredictionHistory
                        WHERE Issue=@issue AND AnalysisPeriods=@periods AND ModelVersion=@model
                    )";
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
                    cmd.Parameters.AddWithValue("@learning", learningDetails);
                    cmd.Parameters.AddWithValue("@ranking", finalRankingJson);
                    cmd.Parameters.AddWithValue("@baseScores", baseModelScoresJson);
                    cmd.Parameters.AddWithValue("@features", featureSnapshotJson);
                    cmd.Parameters.AddWithValue("@weights", weightSnapshotJson);
                    cmd.Parameters.AddWithValue("@mapping", mappingSnapshotJson);
                    int inserted = cmd.ExecuteNonQuery();
                    new SQLiteCommand("COMMIT", conn).ExecuteNonQuery();
                    transactionStarted = false;
                    Console.WriteLine(inserted > 0
                        ? $"[数据库] 新建预测记录（期号:{issue}，周期:{analysisPeriods}，模型:{modelVersion}）"
                        : $"[数据库] 预测记录已存在，保留首次快照（期号:{issue}，周期:{analysisPeriods}，模型:{modelVersion}）");
                }
                catch
                {
                    if (transactionStarted)
                    {
                        try { new SQLiteCommand("ROLLBACK", conn).ExecuteNonQuery(); }
                        catch { }
                    }
                    throw;
                }
            }
        }

        public static void SaveCloudPrediction(string issue, string predictTime, string predictZodiac,
            string top6Zodiac, string predictNumber, string modelVersion, int analysisPeriods,
            string scoreDetails)
        {
            using SQLiteConnection conn = GetConnection();
            string predictionGroupId = GetPredictionGroupId(issue);
            new SQLiteCommand("BEGIN IMMEDIATE", conn).ExecuteNonQuery();
            bool committed = false;
            try
            {
            using SQLiteCommand cmd = new SQLiteCommand(@"
                INSERT INTO PredictionHistory
                (Issue, PredictionGroupId, PredictTime, PredictNumber, PredictZodiac, Top6Zodiac,
                 AnalysisPeriods, ScoreDetails, ModelVersion, HitResult, Top6HitResult)
                SELECT @issue, @groupId, @time, @num, @zodiac, @top6, @periods, @scores, @model,
                       '未开奖', '未开奖'
                WHERE NOT EXISTS (
                    SELECT 1 FROM PredictionHistory
                    WHERE Issue=@issue AND AnalysisPeriods=@periods AND ModelVersion=@model
                )", conn);
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
            new SQLiteCommand("COMMIT", conn).ExecuteNonQuery();
            committed = true;
            }
            finally
            {
                if (!committed)
                {
                    try { new SQLiteCommand("ROLLBACK", conn).ExecuteNonQuery(); }
                    catch { }
                }
            }
            RecalculateVerifiedPredictionResults();
        }

        public static void SaveVerifiedValidationPrediction(string issue, string top3Zodiac, string top6Zodiac,
            string actualZodiac, string actualNumber, int actualRank, int analysisPeriods, string modelVersion,
            string scoreDetails, bool mainColorHit, bool dualColorHit, string actualColor)
        {
            using (SQLiteConnection schema = GetConnection()) EnsureAutoLearningSchema(schema);
            string[] ranking = top6Zodiac.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            SavePrediction(issue, top3Zodiac, top6Zodiac, "", modelVersion, analysisPeriods, scoreDetails,
                "严格滚动验证记录，不参与在线重复学习", System.Text.Json.JsonSerializer.Serialize(ranking));
            bool top3Hit = top3Zodiac.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(actualZodiac);
            bool top6Hit = ranking.Contains(actualZodiac);
            using SQLiteConnection conn = GetConnection();
            using var cmd = new SQLiteCommand(@"UPDATE PredictionHistory SET ActualNumber=@number, ActualZodiac=@actual,
                HitResult=@top3, Top6HitResult=@top6, ReviewDetails=@review, ActualRank=@rank,
                LearningStatus='Backtest', LearnedAt=@time
                WHERE Issue=@issue AND AnalysisPeriods=@periods AND ModelVersion=@model", conn);
            cmd.Parameters.AddWithValue("@actual", actualZodiac);
            cmd.Parameters.AddWithValue("@number", actualNumber);
            cmd.Parameters.AddWithValue("@top3", top3Hit ? "命中" : "未命中");
            cmd.Parameters.AddWithValue("@top6", top6Hit ? "命中" : "未命中");
            cmd.Parameters.AddWithValue("@review", $"2026严格滚动验证：实际排名{actualRank}，TOP3{(top3Hit ? "命中" : "未命中")}，" +
                $"TOP6{(top6Hit ? "命中" : "未命中")}；实际波色{actualColor}，" +
                $"主波{(mainColorHit ? "命中" : "未命中")}，双波{(dualColorHit ? "命中" : "未命中")}");
            cmd.Parameters.AddWithValue("@rank", actualRank);
            cmd.Parameters.AddWithValue("@time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.Parameters.AddWithValue("@issue", issue);
            cmd.Parameters.AddWithValue("@periods", analysisPeriods);
            cmd.Parameters.AddWithValue("@model", modelVersion);
            cmd.ExecuteNonQuery();
        }

        /// <summary>
        /// 验证预测结果（开奖后自动匹配）
        /// </summary>
        public static void VerifyPrediction(string issue, string actualNumber, string actualZodiac)
        {
            VerifyPrediction(issue, null, actualNumber, actualZodiac);
        }

        private static void VerifyPrediction(string issue, int? analysisPeriods, string actualNumber, string actualZodiac)
        {
            using (SQLiteConnection conn = GetConnection())
            {
                string findSql = "SELECT Id, PredictZodiac, Top6Zodiac, ScoreDetails, ModelVersion FROM PredictionHistory WHERE Issue=@issue" +
                    (analysisPeriods.HasValue ? " AND AnalysisPeriods=@periods" : "") +
                    " AND (HitResult='未开奖' OR HitResult='')";
                SQLiteCommand findCmd = new SQLiteCommand(findSql, conn);
                findCmd.Parameters.AddWithValue("@issue", issue);
                if (analysisPeriods.HasValue)
                    findCmd.Parameters.AddWithValue("@periods", analysisPeriods.Value);

                var pending = new List<(int id, string top3, string top6, string scores, string modelVersion)>();
                using (SQLiteDataReader reader = findCmd.ExecuteReader())
                {
                    while (reader.Read())
                        pending.Add((reader.GetInt32(0), reader.IsDBNull(1) ? "" : reader.GetString(1),
                            reader.IsDBNull(2) ? "" : reader.GetString(2), reader.IsDBNull(3) ? "" : reader.GetString(3),
                            reader.IsDBNull(4) ? "" : reader.GetString(4)));
                }

                foreach (var item in pending)
                {
                    bool hit = !string.IsNullOrEmpty(item.top3) &&
                               !string.IsNullOrEmpty(actualZodiac) &&
                               item.top3.Split(',').Contains(actualZodiac);
                    bool top6Hit = !string.IsNullOrEmpty(item.top6) &&
                                   !string.IsNullOrEmpty(actualZodiac) &&
                               item.top6.Split(',').Contains(actualZodiac);
                    string review = PredictionLearningService.BuildReview(item.scores, item.top3, actualZodiac);

                    string updateSql = @"UPDATE PredictionHistory
                    SET ActualNumber=@num, ActualZodiac=@zodiac, HitResult=@result, Top6HitResult=@top6Result, ReviewDetails=@review
                    WHERE Id=@id";
                    SQLiteCommand updateCmd = new SQLiteCommand(updateSql, conn);
                    updateCmd.Parameters.AddWithValue("@num", actualNumber);
                    updateCmd.Parameters.AddWithValue("@zodiac", actualZodiac);
                    updateCmd.Parameters.AddWithValue("@result", hit ? "命中" : "未命中");
                    updateCmd.Parameters.AddWithValue("@top6Result", top6Hit ? "命中" : "未命中");
                    updateCmd.Parameters.AddWithValue("@review", review);
                    updateCmd.Parameters.AddWithValue("@id", item.id);
                    updateCmd.ExecuteNonQuery();

                    // 仅第四条“自动学习”预测在开奖后更新其在线记忆。
                    // 基础三模型使用各自独立的滚动校准，不消费元模型快照。
                    if (string.Equals(item.modelVersion, "V6.5 AutoLearning", StringComparison.OrdinalIgnoreCase))
                    {
                        ApplyAutomaticLearningForPrediction(item.id, actualZodiac);
                        ApplyColorLearningForPrediction(item.id, actualNumber);
                    }

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
                    foreach (var prediction in unverified.Where(r => r.Issue == issue))
                    {
                        VerifyPrediction(issue, prediction.AnalysisPeriods, actualNum, actualZodiac);
                        verified++;
                    }
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
        /// Recomputes stored outcomes from the current prediction lists and the
        /// recorded actual zodiac, preventing stale results after a prediction update.
        /// </summary>
        public static int RecalculateVerifiedPredictionResults()
        {
            int changed = 0;
            using (SQLiteConnection conn = GetConnection())
            {
                string selectSql = @"SELECT Id, PredictZodiac, Top6Zodiac, ActualZodiac
                                     FROM PredictionHistory
                                     WHERE ActualZodiac IS NOT NULL AND ActualZodiac <> ''";
                using SQLiteCommand selectCmd = new SQLiteCommand(selectSql, conn);
                using SQLiteDataReader reader = selectCmd.ExecuteReader();
                var rows = new List<(int Id, string Top3, string Top6, string Actual)>();
                while (reader.Read())
                {
                    rows.Add((reader.GetInt32(0),
                        reader.IsDBNull(1) ? "" : reader.GetString(1),
                        reader.IsDBNull(2) ? "" : reader.GetString(2),
                        reader.IsDBNull(3) ? "" : reader.GetString(3)));
                }

                foreach (var row in rows)
                {
                    bool top3Hit = ContainsZodiac(row.Top3, row.Actual);
                    bool top6Hit = ContainsZodiac(row.Top6, row.Actual);
                    string result = top3Hit ? "命中" : "未命中";
                    string top6Result = top6Hit ? "命中" : "未命中";
                    using SQLiteCommand updateCmd = new SQLiteCommand(@"
                        UPDATE PredictionHistory
                        SET HitResult=@result, Top6HitResult=@top6Result
                        WHERE Id=@id AND (HitResult<>@result OR Top6HitResult<>@top6Result)", conn);
                    updateCmd.Parameters.AddWithValue("@result", result);
                    updateCmd.Parameters.AddWithValue("@top6Result", top6Result);
                    updateCmd.Parameters.AddWithValue("@id", row.Id);
                    changed += updateCmd.ExecuteNonQuery();
                }
            }
            return changed;
        }

        private static bool ContainsZodiac(string candidates, string actual)
        {
            if (string.IsNullOrWhiteSpace(candidates) || string.IsNullOrWhiteSpace(actual)) return false;
            return candidates.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Contains(actual.Trim(), StringComparer.Ordinal);
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
                       ScoreDetails, ModelVersion, ActualNumber, ActualZodiac, HitResult, Top6HitResult, ReviewDetails, LearningDetails,
                       FinalRankingJson, BaseModelScoresJson, FeatureSnapshotJson, WeightSnapshotJson, MappingSnapshotJson,
                       ActualRank, LearningStatus, LearnedAt
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
                            ,ReviewDetails = reader.IsDBNull(14) ? "" : reader.GetString(14)
                            ,LearningDetails = reader.IsDBNull(15) ? "" : reader.GetString(15)
                            ,FinalRankingJson = reader.IsDBNull(16) ? "" : reader.GetString(16)
                            ,BaseModelScoresJson = reader.IsDBNull(17) ? "" : reader.GetString(17)
                            ,FeatureSnapshotJson = reader.IsDBNull(18) ? "" : reader.GetString(18)
                            ,WeightSnapshotJson = reader.IsDBNull(19) ? "" : reader.GetString(19)
                            ,MappingSnapshotJson = reader.IsDBNull(20) ? "" : reader.GetString(20)
                            ,ActualRank = reader.IsDBNull(21) ? 0 : reader.GetInt32(21)
                            ,LearningStatus = reader.IsDBNull(22) ? "Pending" : reader.GetString(22)
                            ,LearnedAt = reader.IsDBNull(23) ? "" : reader.GetString(23)
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

        public static (int Total, int Top3Hits, int Top6Hits, double Top3Rate, double Top6Rate) GetAIPredictStats(
            int? analysisPeriods = null)
        {
            try
            {
                int total = 0, hits = 0, top6Hits = 0;
                using (SQLiteConnection conn = GetConnection())
                {
                    string periodFilter = analysisPeriods.HasValue
                        ? " AND AnalysisPeriods=@analysisPeriods"
                        : "";
                    string sql = @"
                    WITH Ranked AS (
                        SELECT Issue, HitResult, Top6HitResult,
                               ROW_NUMBER() OVER (
                                   PARTITION BY Issue
                                   ORDER BY AnalysisPeriods DESC,
                                            Id DESC
                               ) AS rn
                        FROM PredictionHistory
                        WHERE Issue != '' AND HitResult IN ('命中','未命中')" + periodFilter + @"
                    )
                    SELECT COUNT(*),
                           SUM(CASE WHEN HitResult='命中' THEN 1 ELSE 0 END),
                           SUM(CASE WHEN Top6HitResult='命中' THEN 1 ELSE 0 END)
                    FROM Ranked
                    WHERE rn = 1";
                    SQLiteCommand cmd = new SQLiteCommand(sql, conn);
                    if (analysisPeriods.HasValue)
                        cmd.Parameters.AddWithValue("@analysisPeriods", analysisPeriods.Value);
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

        public static string? LoadModelMemoryJson(string key)
        {
            using SQLiteConnection conn = GetConnection();
            EnsureAutoLearningSchema(conn);
            using var cmd = new SQLiteCommand("SELECT MemoryJson FROM ModelMemory WHERE MemoryKey=@key", conn);
            cmd.Parameters.AddWithValue("@key", key);
            object? value = cmd.ExecuteScalar();
            return value is null or DBNull ? null : Convert.ToString(value);
        }

        public static LearningOutcome ApplyAutomaticLearningForPrediction(int predictionId, string actualZodiac)
        {
            if (predictionId <= 0 || string.IsNullOrWhiteSpace(actualZodiac))
                return new LearningOutcome(false, false, 0, ModelWeights.Default, "实际开奖无效");
            try
            {
                string snapshotJson;
                string finalRankingJson;
                string experimentKey;
                using (SQLiteConnection claim = GetConnection())
                {
                    EnsureAutoLearningSchema(claim);
                    using var claimCmd = new SQLiteCommand(@"UPDATE PredictionHistory SET LearningStatus='Processing'
                        WHERE Id=@id AND (LearningStatus='Pending' OR LearningStatus='' OR LearningStatus IS NULL)", claim);
                    claimCmd.Parameters.AddWithValue("@id", predictionId);
                    if (claimCmd.ExecuteNonQuery() != 1)
                        return new LearningOutcome(false, false, 0, new ModelMemory().LoadOrCreate().Weights, "该期已经学习");
                    using var read = new SQLiteCommand("SELECT FeatureSnapshotJson, FinalRankingJson, AnalysisPeriods, ModelVersion FROM PredictionHistory WHERE Id=@id", claim);
                    read.Parameters.AddWithValue("@id", predictionId);
                    using var reader = read.ExecuteReader();
                    if (!reader.Read()) throw new InvalidDataException("找不到预测记录");
                    snapshotJson = reader.IsDBNull(0) ? "" : reader.GetString(0);
                    finalRankingJson = reader.IsDBNull(1) ? "" : reader.GetString(1);
                    int periods = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                    string modelVersion = reader.IsDBNull(3) ? "" : reader.GetString(3);
                    if (!string.Equals(modelVersion, "V6.5", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(modelVersion, "V6.5 AutoLearning", StringComparison.OrdinalIgnoreCase))
                    {
                        SetPredictionLearningState(predictionId, "SkippedExperiment", 0);
                        return new LearningOutcome(false, false, 0, ModelWeights.Default, "淘汰模型不参与四模型实验学习");
                    }
                    experimentKey = string.Equals(modelVersion, "V6.5 AutoLearning", StringComparison.OrdinalIgnoreCase)
                        ? ExperimentModels.AutoLearning : ExperimentModels.ForPeriods(periods);
                }

                if (string.IsNullOrWhiteSpace(snapshotJson))
                {
                    SetPredictionLearningState(predictionId, "SkippedLegacy", 0);
                    return new LearningOutcome(false, false, 0, new ModelMemory().LoadOrCreate().Weights, "旧记录没有完整评分，跳过学习");
                }

                MetaPredictionInput? input = System.Text.Json.JsonSerializer.Deserialize<MetaPredictionInput>(snapshotJson);
                if (input is null || input.Zodiacs.Count != 12)
                    throw new InvalidDataException("自动学习快照不完整");
                var memoryStore = new ModelMemory(experimentKey);
                ModelMemoryState memory = memoryStore.LoadOrCreate();
                string[] savedRanking = System.Text.Json.JsonSerializer.Deserialize<string[]>(finalRankingJson) ?? Array.Empty<string>();
                int actualRank = Array.FindIndex(savedRanking, item => item == actualZodiac) + 1;
                ZodiacMetaFeatures? actual = input.Zodiacs.FirstOrDefault(item => item.Zodiac == actualZodiac);
                if (actualRank == 0 || actual is null) throw new InvalidDataException("实际生肖不在12生肖快照中");

                string[] sources = { "AI", "ML", "State", "Rule" };
                var baseRanks = sources.ToDictionary(source => source, source =>
                    input.Zodiacs.OrderByDescending(item => item.BaseScores.GetValueOrDefault(source))
                        .Select((item, index) => (item.Zodiac, Rank: index+1))
                        .First(item => item.Zodiac == actualZodiac).Rank,
                    StringComparer.OrdinalIgnoreCase);
                new MetaPredictionEngine().Learn(input, actualZodiac, memory);
                LearningOutcome outcome = new AutoLearningEngine().ApplyFeedback(memory,
                    new PredictionFeedback(input.Issue, actualRank, baseRanks, actual.FeatureGroups));
                memoryStore.Save(memory);
                if (outcome.FailureAnalysisTriggered && memory.RecentAdjustments.Count > 0)
                    SaveLearningAdjustment(memory.RecentAdjustments[^1]);
                SetPredictionLearningState(predictionId, "Learned", actualRank);
                return outcome;
            }
            catch (Exception ex)
            {
                SetPredictionLearningState(predictionId, "Error", 0);
                AppLogger.Error("自动学习开奖反馈", ex);
                return new LearningOutcome(false, false, 0, new ModelMemory().LoadOrCreate().Weights, ex.Message);
            }
        }

        public static ColorLearningOutcome ApplyColorLearningForPrediction(int predictionId, string actualNumber)
        {
            var memoryStore = new ModelMemory();
            ModelMemoryState memory = memoryStore.LoadOrCreate();
            string actualColor = ColorEngine.ColorOf(actualNumber);
            if (predictionId <= 0 || string.IsNullOrWhiteSpace(actualColor))
                return new ColorLearningOutcome(false, false, false, false, memory.ColorLearning.Weights,
                    "实际波色无效，跳过学习");
            try
            {
                string scoreDetails;
                using (SQLiteConnection conn = GetConnection())
                {
                    using var cmd = new SQLiteCommand("SELECT ScoreDetails FROM PredictionHistory WHERE Id=@id", conn);
                    cmd.Parameters.AddWithValue("@id", predictionId);
                    scoreDetails = Convert.ToString(cmd.ExecuteScalar()) ?? "";
                }
                if (!ColorPredictionSnapshotCodec.TryDecode(scoreDetails, out ColorPredictionSnapshot snapshot))
                    return new ColorLearningOutcome(false, false, false, false, memory.ColorLearning.Weights,
                        "记录没有波色学习快照");
                var feedback = new ColorPredictionFeedback(snapshot.Issue, actualColor,
                    snapshot.MainColor, snapshot.DefenseColor,
                    snapshot.FeatureSignals.ToDictionary(pair => pair.Key,
                        pair => (IReadOnlyDictionary<string, double>)pair.Value,
                        StringComparer.OrdinalIgnoreCase));
                ColorLearningOutcome outcome = new ColorAutoLearningEngine().ApplyFeedback(memory.ColorLearning, feedback);
                if (outcome.Updated) memoryStore.Save(memory);
                return outcome;
            }
            catch (Exception ex)
            {
                AppLogger.Error("波色自动学习开奖反馈", ex);
                return new ColorLearningOutcome(false, false, false, false, memory.ColorLearning.Weights, ex.Message);
            }
        }

        private static void SetPredictionLearningState(int id, string status, int actualRank)
        {
            using SQLiteConnection conn = GetConnection();
            EnsureAutoLearningSchema(conn);
            using var cmd = new SQLiteCommand(@"UPDATE PredictionHistory
                SET LearningStatus=@status, ActualRank=@rank, LearnedAt=@time WHERE Id=@id", conn);
            cmd.Parameters.AddWithValue("@status", status);
            cmd.Parameters.AddWithValue("@rank", actualRank);
            cmd.Parameters.AddWithValue("@time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }

        public static void SaveModelMemoryJson(string key, string json)
        {
            using SQLiteConnection conn = GetConnection();
            EnsureAutoLearningSchema(conn);
            using var cmd = new SQLiteCommand(@"INSERT INTO ModelMemory(MemoryKey,MemoryJson,UpdatedAt)
                VALUES(@key,@json,@time)
                ON CONFLICT(MemoryKey) DO UPDATE SET MemoryJson=excluded.MemoryJson, UpdatedAt=excluded.UpdatedAt", conn);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@json", json);
            cmd.Parameters.AddWithValue("@time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.ExecuteNonQuery();
        }

        public static void SaveLearningAdjustment(LearningAdjustmentRecord record)
        {
            using SQLiteConnection conn = GetConnection();
            EnsureAutoLearningSchema(conn);
            using var cmd = new SQLiteCommand(@"INSERT INTO LearningAdjustmentHistory
                (Issue,AdjustedAt,OldWeightsJson,NewWeightsJson,FeatureContributionJson,Reason)
                VALUES(@issue,@time,@old,@new,@features,@reason)", conn);
            cmd.Parameters.AddWithValue("@issue", record.Issue);
            cmd.Parameters.AddWithValue("@time", record.AdjustedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.Parameters.AddWithValue("@old", System.Text.Json.JsonSerializer.Serialize(record.OldWeights));
            cmd.Parameters.AddWithValue("@new", System.Text.Json.JsonSerializer.Serialize(record.NewWeights));
            cmd.Parameters.AddWithValue("@features", System.Text.Json.JsonSerializer.Serialize(record.FeatureContribution));
            cmd.Parameters.AddWithValue("@reason", record.Reason);
            cmd.ExecuteNonQuery();
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
                ("V6.5基础评分", "V6.5基础规则模型", "V6.5三条基础模型共用的规则评分核心"),
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
            return "V6.5";
        }

    }
}
