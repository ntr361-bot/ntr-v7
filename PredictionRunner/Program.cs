using System.Text.Json;
using 六合分析软件;

try
{
    Dictionary<string, string?> arguments = ParseArguments(args);
    if (arguments.ContainsKey("help"))
    {
        PrintUsage();
        return 0;
    }

    string repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    string dataDirectory = Environment.GetEnvironmentVariable("LIUHE_DATA_DIR")
        ?? Path.Combine(repositoryRoot, "data");
    string outputDirectory = Environment.GetEnvironmentVariable("PREDICTION_OUTPUT_DIR")
        ?? Path.Combine(repositoryRoot, "site", "data", "predictions");
    string dailyOutputDirectory = Environment.GetEnvironmentVariable("DAILY_PREDICTION_OUTPUT_DIR")
        ?? Path.Combine(repositoryRoot, "site", "data", "daily-records");
    Environment.SetEnvironmentVariable("LIUHE_DATA_DIR", dataDirectory);

    if (arguments.ContainsKey("rebuild-db"))
    {
        string historyJson = Path.Combine(repositoryRoot, "site", "data", "history.json");
        Console.WriteLine($"[INFO] 从提交的 JSON 重建开奖数据库：{historyJson}");
        DatabaseHelper.InitializeDatabase();
        int saved = CloudPredictionSyncService.ImportLocalHistoryArchive(historyJson);
        Console.WriteLine($"[SUCCESS] 开奖数据库重建完成：写入 {saved} 条记录，最新期号 {DatabaseHelper.GetLatestPeriod()}");

        string runtimeStateJson = Path.Combine(repositoryRoot, "site", "data", "runtime-state.json");
        if (File.Exists(runtimeStateJson))
        {
            int merged = CloudPredictionSyncService.ImportLocalRuntimeState(runtimeStateJson);
            Console.WriteLine($"[SUCCESS] 运行状态恢复完成：合并 {merged} 条预测记录与模型记忆");
        }
        else
        {
            Console.WriteLine("[WARNING] 未找到 runtime-state.json，本次仅恢复开奖记录");
        }
        if (arguments.ContainsKey("rebuild-only")) return 0;
    }

    if (arguments.ContainsKey("export-history"))
    {
        CloudHistoryAutomation.Export(Path.Combine(repositoryRoot, "site", "data", "history.json"));
        return 0;
    }

    if (arguments.ContainsKey("export-state"))
    {
        WriteRuntimeState(repositoryRoot);
        Console.WriteLine("[SUCCESS] 运行状态已导出");
        return 0;
    }

    if (arguments.ContainsKey("refresh-data"))
    {
        LotteryRefreshResult refresh = await LotteryDataRefresh.RefreshAsync(arguments.ContainsKey("dry-run"));
        if (arguments.ContainsKey("require-advance")) LotteryDataRefresh.RequireAdvance(refresh);
        if (arguments.ContainsKey("refresh-only")) return 0;
    }

    long? issue = ParseIssue(arguments, "issue");
    long? startIssue = ParseIssue(arguments, "start-issue");

    if (arguments.ContainsKey("generate-all"))
    {
        DailyPredictionAutomation.GenerateMissing(outputDirectory, dailyOutputDirectory,
            issue, startIssue, arguments.ContainsKey("force"), arguments.ContainsKey("dry-run"));
        if (!arguments.ContainsKey("dry-run"))
        {
            CloudHistoryAutomation.Export(Path.Combine(repositoryRoot, "site", "data", "history.json"));
            WriteRuntimeState(repositoryRoot);
        }
        return 0;
    }

    PredictionAutomation.Run(new PredictionRunOptions
    {
        Issue = issue,
        Force = arguments.ContainsKey("force"),
        DryRun = arguments.ContainsKey("dry-run"),
        OutputDirectory = outputDirectory
    });
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[ERROR] {ex.Message}");
    if (Environment.GetEnvironmentVariable("PREDICTION_DEBUG") == "1") Console.Error.WriteLine(ex);
    return 1;
}

static long? ParseIssue(Dictionary<string, string?> arguments, string key)
{
    if (!arguments.TryGetValue(key, out string? text)) return null;
    if (!long.TryParse(text, out long issue) || issue <= 0)
        throw new ArgumentException($"--{key} 必须是正整数");
    return issue;
}

static Dictionary<string, string?> ParseArguments(string[] values)
{
    var parsed = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < values.Length; i++)
    {
        switch (values[i])
        {
            case "--issue":
                if (++i >= values.Length) throw new ArgumentException("--issue 缺少期号");
                parsed["issue"] = values[i];
                break;
            case "--start-issue":
                if (++i >= values.Length) throw new ArgumentException("--start-issue 缺少期号");
                parsed["start-issue"] = values[i];
                break;
            case "--force": parsed["force"] = null; break;
            case "--dry-run": parsed["dry-run"] = null; break;
            case "--refresh-data": parsed["refresh-data"] = null; break;
            case "--refresh-only": parsed["refresh-only"] = null; break;
            case "--require-advance": parsed["require-advance"] = null; break;
            case "--generate-all": parsed["generate-all"] = null; break;
            case "--rebuild-db": parsed["rebuild-db"] = null; break;
            case "--rebuild-only": parsed["rebuild-only"] = null; break;
            case "--export-history": parsed["export-history"] = null; break;
            case "--export-state": parsed["export-state"] = null; break;
            case "--help":
            case "-h": parsed["help"] = null; break;
            default: throw new ArgumentException($"未知参数：{values[i]}");
        }
    }
    return parsed;
}

static void PrintUsage() => Console.WriteLine(
    "用法：dotnet run --project PredictionRunner -- [--issue 2026203] [--start-issue 2026197] [--force] [--dry-run] [--refresh-data] [--refresh-only] [--require-advance] [--generate-all] [--rebuild-db] [--rebuild-only] [--export-history] [--export-state]");

static void WriteRuntimeState(string repositoryRoot)
{
    string output = Path.Combine(repositoryRoot, "site", "data", "runtime-state.json");
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    SymmetricRuntimeStateSnapshot snapshot = SymmetricRuntimeStateSync.Export("V6.5");
    string temporary = output + $".{Guid.NewGuid():N}.tmp";
    try
    {
        File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        }));
        using JsonDocument _ = JsonDocument.Parse(File.ReadAllBytes(temporary));
        File.Move(temporary, output, true);
    }
    finally
    {
        if (File.Exists(temporary)) File.Delete(temporary);
    }
}
