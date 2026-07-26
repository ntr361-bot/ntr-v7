using System.Text.Json;
using 六合分析软件;

string testData = Path.Combine(AppContext.BaseDirectory, "TestData");
if (Directory.Exists(testData)) Directory.Delete(testData, recursive: true);
Environment.SetEnvironmentVariable("LIUHE_DATA_DIR", testData);
DatabaseHelper.InitializeDatabase();
SeedHistory();

var tests = new (string Name, Action Run)[]
{
    ("自动化预测写入历史记录", AutomationRecordsPredictionHistory),
    ("解析有效开奖 JSON", ParseValidJson),
    ("拒绝失败 API 响应", RejectFailedResponse),
    ("号码统计正确", CountNumbers),
    ("2026马年生肖号码映射正确", ZodiacNumberMapFor2026IsCorrect),
    ("综合评分使用目标年份生肖映射", PredictionScoreUsesTargetYearMap),
    ("自动识别下一期", AutoDetectNextIssue),
    ("指定期号运行", ExplicitIssue),
    ("已存在文件时跳过", ExistingFileSkips),
    ("强制覆盖", ForceOverwrite),
    ("历史数据为空", EmptyHistoryFails),
    ("历史数据格式错误", InvalidHistoryFails),
    ("输出 JSON 校验", OutputJsonIsValid),
    ("latest.json 更新", LatestJsonUpdates),
    ("重复期号检测", DuplicateIssueFails),
    ("dry-run 不修改文件", DryRunDoesNotWrite),
    ("历史数据截止期生效", HistoryCutoffWorks),
    ("特码规律生成六肖", ZodiacRuleGeneratesSix),
    ("全功能dry-run不写文件", DailyDryRunDoesNotWrite),
    ("有效抓取数据校验", ValidCrawlDataPasses),
    ("损坏抓取数据拒绝", InvalidCrawlDataFails)
    ,("预测清单包含全部期号", PredictionManifestContainsAllIssues)
    ,("云端预测导入四周期历史", CloudPredictionImportsFourPeriods)
    ,("云端开奖档案导出", CloudHistoryExportIsValid)
    ,("历史预测逐项写入命中结果", PublishedPredictionVerificationIsRecorded)
};

int failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}");
    }
}

return failures == 0 ? 0 : 1;

void SeedHistory()
{
    DatabaseHelper.ClearHistory();
    DatabaseHelper.InsertHistory("100", "010203040506", "07", "马", "2026-01-01 21:30:00", "2026-01-01");
    DatabaseHelper.InsertHistory("101", "010203040506", "08", "蛇", "2026-01-03 21:30:00", "2026-01-03");
    DatabaseHelper.InsertHistory("102", "010203040506", "09", "龙", "2026-01-05 21:30:00", "2026-01-05");
}

void ParseValidJson()
{
    const string json = """
        {"code":0,"message":"ok","data":[{"issue":"2026001","openCode":"01,02,03,04,05,06,07","openTime":"2026-01-01 21:30:00","pet":"马"}]}
        """;
    var records = DataCrawler.ParseJson(json);
    Assert(records.Count == 1, "应解析出一条记录");
    Assert(records[0].Period == "2026001", "期号不正确");
    Assert(records[0].SpecialNumber == "07", "特码不正确");
}

void RejectFailedResponse() => Assert(
    DataCrawler.ParseJson("{\"code\":500,\"message\":\"failed\",\"data\":[]}").Count == 0,
    "失败响应不应产生记录");

void CountNumbers()
{
    var counts = AnalysisEngine.CountNumbers(new List<string> { "01", "02", "03", "02", "03", "04" });
    Assert(counts["02"] == 2 && counts["04"] == 1, "号码频次计算不正确");
}

void ZodiacNumberMapFor2026IsCorrect()
{
    var map = DataCrawler.BuildShengXiaoMapPublic("马");
    Assert(string.Join(",", map["马"]) == "01,13,25,37,49", "马肖号码错误");
    Assert(string.Join(",", map["蛇"]) == "02,14,26,38", "蛇肖号码错误");
    Assert(string.Join(",", map["龙"]) == "03,15,27,39", "龙肖号码错误");
    Assert(string.Join(",", map["兔"]) == "04,16,28,40", "兔肖号码错误");
    Assert(string.Join(",", map["虎"]) == "05,17,29,41", "虎肖号码错误");
    Assert(string.Join(",", map["牛"]) == "06,18,30,42", "牛肖号码错误");
    Assert(string.Join(",", map["鼠"]) == "07,19,31,43", "鼠肖号码错误");
    Assert(string.Join(",", map["猪"]) == "08,20,32,44", "猪肖号码错误");
    Assert(string.Join(",", map["狗"]) == "09,21,33,45", "狗肖号码错误");
    Assert(string.Join(",", map["鸡"]) == "10,22,34,46", "鸡肖号码错误");
    Assert(string.Join(",", map["猴"]) == "11,23,35,47", "猴肖号码错误");
    Assert(string.Join(",", map["羊"]) == "12,24,36,48", "羊肖号码错误");
    Assert(map.Values.SelectMany(numbers => numbers).Distinct().Count() == 49,
        "生肖映射必须完整覆盖01至49且不能重复");
}

void PredictionScoreUsesTargetYearMap()
{
    var result = PredictionScoreService.Predict(500, 2026);
    Assert(result.Predictions.Single(item => item.Zodiac == "马").Number == "01,13,25,37,49",
        "综合评分仍在使用旧年份静态映射");
    Assert(result.Predictions.Single(item => item.Zodiac == "虎").Number == "05,17,29,41",
        "综合评分虎肖号码未按2026马年轮转");
}

void AutoDetectNextIssue()
{
    string output = FreshDirectory();
    PredictionRunResult result = PredictionAutomation.Run(new() { DryRun = true, OutputDirectory = output });
    Assert(result.Issue == 103, "下一期应为 103");
}

void ExplicitIssue()
{
    PredictionRunResult result = PredictionAutomation.Run(new() { Issue = 110, DryRun = true, OutputDirectory = FreshDirectory() });
    Assert(result.Issue == 110, "应使用指定期号");
}

void AutomationRecordsPredictionHistory()
{
    string output = FreshDirectory();
    PredictionAutomation.Run(new() { OutputDirectory = output, Force = true }, FakePrediction);
    bool recorded = DatabaseHelper.GetPredictionHistory(int.MaxValue).Any(record =>
        record.Issue == "103" &&
        record.AnalysisPeriods == 3 &&
        record.PredictZodiac == "马,蛇,龙" &&
        record.Top6Zodiac == "马,蛇,龙,兔,虎,牛");
    Assert(recorded, "自动化预测应写入 PredictionHistory");
}

void ExistingFileSkips()
{
    string output = FreshDirectory();
    File.WriteAllText(Path.Combine(output, "103.json"), "{}");
    PredictionRunResult result = PredictionAutomation.Run(new() { OutputDirectory = output }, _ => throw new Exception("不应调用预测器"));
    Assert(result.Status == "skipped" && !result.Changed, "已存在文件应跳过");
}

void ForceOverwrite()
{
    string output = FreshDirectory();
    File.WriteAllText(Path.Combine(output, "103.json"), "{}");
    PredictionRunResult result = PredictionAutomation.Run(new() { OutputDirectory = output, Force = true }, FakePrediction);
    Assert(result.Changed, "强制模式应覆盖文件");
    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "103.json")));
    Assert(document.RootElement.GetProperty("status").GetString() == "success", "覆盖后的状态无效");
}

void EmptyHistoryFails() => AssertThrows<InvalidDataException>(
    () => PredictionAutomation.ValidateHistory(Array.Empty<DatabaseHelper.HistoryRecord>()), "空历史应失败");

void InvalidHistoryFails() => AssertThrows<InvalidDataException>(() => PredictionAutomation.ValidateHistory(new[]
{
    History("100", "not-a-number", "马")
}), "非法号码应失败");

void OutputJsonIsValid()
{
    string output = FreshDirectory();
    PredictionAutomation.Run(new() { OutputDirectory = output }, FakePrediction);
    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "103.json")));
    JsonElement prediction = document.RootElement.GetProperty("prediction");
    Assert(prediction.GetProperty("zodiacs").GetArrayLength() == 3, "生肖输出为空");
    Assert(prediction.GetProperty("numbers").GetArrayLength() == 3, "号码输出为空");
}

void LatestJsonUpdates()
{
    string output = FreshDirectory();
    PredictionAutomation.Run(new() { OutputDirectory = output }, FakePrediction);
    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "latest.json")));
    Assert(document.RootElement.GetProperty("latest_issue").GetInt64() == 103, "latest.json 期号错误");
    Assert(document.RootElement.GetProperty("prediction_file").GetString() == "103.json", "latest.json 文件名错误");
}

void DuplicateIssueFails() => AssertThrows<InvalidDataException>(() => PredictionAutomation.ValidateHistory(new[]
{
    History("100", "07", "马"), History("100", "08", "蛇")
}), "重复期号应失败");

void DryRunDoesNotWrite()
{
    string output = FreshDirectory();
    PredictionAutomation.Run(new() { OutputDirectory = output, DryRun = true });
    Assert(!Directory.EnumerateFileSystemEntries(output).Any(), "dry-run 不应写文件");
}

void HistoryCutoffWorks()
{
    using (DatabaseHelper.UseHistoryThroughIssue(101))
    {
        Assert(DatabaseHelper.GetLatestPeriod() == "101", "截止期后最新期号应为101");
        Assert(DatabaseHelper.GetLatestHistory(50).Count == 2, "截止期不应包含未来数据");
    }
    Assert(DatabaseHelper.GetLatestPeriod() == "102", "离开截止范围后应恢复全部数据");
}

void ZodiacRuleGeneratesSix()
{
    ZodiacRulePrediction result = ZodiacRulePredictionService.Predict(103);
    Assert(result.SourceIssue == "102", "特码规律应使用最新已开奖期");
    Assert(result.Zodiacs.Count == 6, "特码规律应生成6个不重复生肖");
}

void DailyDryRunDoesNotWrite()
{
    string output = FreshDirectory();
    string file = DailyPredictionAutomation.Generate(103, output, dryRun: true);
    Assert(!File.Exists(file), "全功能dry-run不应写入记录");
}

void ValidCrawlDataPasses()
{
    DataCrawler.ValidateCrawlRecords(new[]
    {
        new DataCrawler.CrawlRecord
        {
            Period = "2026103",
            Numbers = "010203040506",
            SpecialNumber = "07",
            SpecialZodiac = "马",
            Date = "2026-07-19 21:30:00"
        }
    });
}

void InvalidCrawlDataFails() => AssertThrows<InvalidDataException>(() =>
    DataCrawler.ValidateCrawlRecords(new[]
    {
        new DataCrawler.CrawlRecord
        {
            Period = "2026103",
            Numbers = "010203040506",
            SpecialNumber = "06",
            SpecialZodiac = "马",
            Date = "2026-07-19 21:30:00"
        }
    }), "与前六个号码重复的特码应被拒绝");

void PredictionManifestContainsAllIssues()
{
    string output = FreshDirectory();
    File.WriteAllText(Path.Combine(output, "2026201.json"), "{}");
    File.WriteAllText(Path.Combine(output, "2026203.json"), "{}");
    string manifest = DailyPredictionAutomation.UpdateManifest(output);
    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest));
    JsonElement records = document.RootElement.GetProperty("records");
    Assert(records.GetArrayLength() == 2, "清单应包含所有已保存预测期号");
    Assert(document.RootElement.GetProperty("latest_issue").GetInt64() == 2026203, "清单最新期号错误");
    using JsonDocument history = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "history.json")));
    Assert(history.RootElement.GetProperty("predictions").GetArrayLength() == 2, "预测历史页面档案应包含全部期号");
}

void CloudPredictionImportsFourPeriods()
{
    var prediction = new CloudDailyPrediction
    {
        Issue = 2026203,
        GeneratedAt = "2026-07-22T22:00:00+08:00",
        Status = "success",
        AiZodiac = new Dictionary<string, CloudAiPrediction>()
    };
    foreach (string period in new[] { "50", "100", "200", "all" })
    {
        prediction.AiZodiac[period] = new CloudAiPrediction
        {
            Top3 = new() { "马", "蛇", "龙" },
            Top6 = new() { "马", "蛇", "龙", "兔", "虎", "牛" },
            Numbers = new() { 1, 2, 3 },
            Confidence = "中",
            BestModel = "测试模型"
        };
    }
    Assert(CloudPredictionSyncService.ImportPrediction(prediction) == 4, "应导入四个分析周期");
    int count = DatabaseHelper.GetPredictionHistory(int.MaxValue)
        .Count(record => record.Issue == "2026203");
    Assert(count == 4, "本地每期应保存四条固定周期记录");
}

void CloudHistoryExportIsValid()
{
    string output = Path.Combine(FreshDirectory(), "history.json");
    CloudHistoryAutomation.Export(output);
    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(output));
    Assert(document.RootElement.GetProperty("status").GetString() == "success", "开奖档案状态错误");
    Assert(document.RootElement.GetProperty("records").GetArrayLength() >= 3, "开奖档案记录不完整");
}

void PublishedPredictionVerificationIsRecorded()
{
    string output = FreshDirectory();
    File.WriteAllText(Path.Combine(output, "102.json"), """
    {
      "issue": 102,
      "status": "success",
      "ai_zodiac": {
        "100": { "top3": ["龙", "马", "蛇"], "top6": ["龙", "马", "蛇", "兔", "虎", "牛"] }
      },
      "special_rule": { "zodiacs": ["龙", "马", "蛇", "兔", "虎", "牛"] },
      "comprehensive_score": [{ "zodiac": "龙" }, { "zodiac": "马" }],
      "ensemble": [{ "zodiac": "马" }, { "zodiac": "龙" }],
      "verification": { "status": "pending" }
    }
    """);

    Assert(DailyPredictionAutomation.VerifyPublishedPredictions(output) == 1, "应验算一期预测");
    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "102.json")));
    JsonElement root = document.RootElement;
    Assert(root.GetProperty("verification").GetProperty("actual_zodiac").GetString() == "龙", "应记录实际生肖");
    Assert(root.GetProperty("ai_zodiac").GetProperty("100").GetProperty("top3_hit").GetBoolean(), "应记录前三命中");
    Assert(root.GetProperty("comprehensive_score")[0].GetProperty("result").GetString() == "命中", "应逐项记录综合评分命中");
    Assert(root.GetProperty("ensemble")[0].GetProperty("result").GetString() == "未命中", "应逐项记录集成模型未命中");
}

AIEngine.PredictResult FakePrediction(long issue) => new()
{
    PredictPeriod = issue.ToString(),
    PredictTime = DateTime.Now,
    AnalysisPeriods = 3,
    Top3 = new() { "马", "蛇", "龙" },
    Top6 = new() { "马", "蛇", "龙", "兔", "虎", "牛" },
    RecommendedNumbers = new() { 7, 8, 9 },
    Confidence = "测试",
    BestModel = "fake"
};

DatabaseHelper.HistoryRecord History(string issue, string number, string zodiac) => new()
{
    Period = issue,
    SpecialNumber = number,
    SpecialZodiac = zodiac
};

string FreshDirectory()
{
    string path = Path.Combine(testData, "output", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(path);
    return path;
}

void AssertThrows<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException(message);
}

void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
