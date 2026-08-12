using System.Text.Json;
using System.Windows.Forms;
using 六合分析软件;

if (args.Contains("--evaluate-auto-learning", StringComparer.OrdinalIgnoreCase))
{
    string dataDirectory = Environment.GetEnvironmentVariable("LIUHE_EVAL_DATA_DIR")
        ?? Path.Combine(Directory.GetCurrentDirectory(), "data");
    Environment.SetEnvironmentVariable("LIUHE_DATA_DIR", dataDirectory);
    var evaluation = AutoLearningEvaluation.Run(DatabaseHelper.GetHistory());
    AutoLearningEvaluation.SaveReports(evaluation, Directory.GetCurrentDirectory());
    if (args.Contains("--persist-latest50", StringComparer.OrdinalIgnoreCase))
        AutoLearningEvaluation.SaveLatest50ToPredictionHistory(evaluation);
    Console.WriteLine(JsonSerializer.Serialize(evaluation, new JsonSerializerOptions { WriteIndented=true }));
    return evaluation.FutureDataLeakageDetected || evaluation.TestSamples == 0 ? 1 : 0;
}

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
    ("V6.5使用GPT-5.6 Sol", V63UsesGpt56Sol),
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
    ,("云端预测不再导入200期模型", CloudPredictionSkipsRemoved200Period)
    ,("云端开奖档案导出", CloudHistoryExportIsValid)
    ,("开奖记录未变化时不重复改写云端档案", UnchangedCloudHistoryIsNotRewritten)
    ,("历史预测逐项写入命中结果", PublishedPredictionVerificationIsRecorded)
    ,("超长遗漏不会继续抬高预测分", ExtremeOmissionDoesNotKeepRising)
    ,("全部历史学习跨期数复用样本", AllHistoryLearningUsesStableBucket)
    ,("V6.5学习只接收同版本同周期样本", V65LearningAcceptsOnlyMatchingSnapshots)
    ,("四模型实验键独立且稳定", ExperimentalModelKeysAreStable)
    ,("自动学习只读取同一期三条基础快照", AutoLearningUsesThreeBaseSnapshots)
    ,("V6.5回测使用正式三模型快照", V65BacktestUsesFormalModelChain)
    ,("V6.5三条基础模型应用各自固定权重", V65BaseModelsUseConfiguredWeights)
    ,("V6.5回测实际比较四条实验模型", V65BacktestComparesFourExperimentModels)
    ,("V6.5实验成绩榜统计四模型与状态", V65ExperimentScoreboardSummarizesModels)
    ,("八肖规则只做小幅校正", EightZodiacBonusIsBounded)
    ,("ML features are leakage safe", MlFeaturesAreLeakageSafe)
    ,("ML models return ranked probabilities", MlModelsReturnRankedProbabilities)
    ,("ML rolling backtest records metrics", MlRollingBacktestRecordsMetrics)
    ,("ML selects the highest-gain split feature", MlSelectsHighestGainFeature)
    ,("FeatureEngine exposes 30+ finite non-five-element features", FeatureEngineExposesThirtyPlusFeatures)
    ,("Five-element signals are removed from prediction", FiveElementSignalsAreRemoved)
    ,("V7 feature engine exposes independent windows", V7FeatureEngineExposesIndependentWindows)
    ,("V7 engines are independent and filter short repeats", V7EnginesAreIndependent)
    ,("V7 ML prediction facade returns probabilities", V7MlPredictionFacadeWorks)
    ,("V7 color engine is independent", V7ColorEngineWorks)
    ,("V7 auto optimizer compares schemes", V7AutoOptimizerWorks)
    ,("V7 AI report explains model state", V7AiReportExplainsState)
    ,("V7 AI report omits repeated implementation notes", V7AiReportOmitsRepeatedImplementationNotes)
    ,("智能预测历史独立保存五条模型记录", V7PredictionsAreSavedToHistory)
    ,("智能预测历史自动学习是独立正式记录", AutoLearningPredictionIsFormalHistoryRow)
    ,("cloud site replaces removed 200 period with automatic learning", CloudSiteUsesAutoLearningSlot)
    ,("cloud workflow runs at 22:00 with one failed-run retry", CloudWorkflowUsesSingleDailyRunAndFailedRetry)
    ,("V7 history uses the V6 history layout", V7HistoryUsesV6Layout)
    ,("verified color hits are visually emphasized", VerifiedColorHitsAreVisuallyEmphasized)
    ,("main menu omits duplicate statistics chart", MainMenuOmitsDuplicateStatisticsChart)
    ,("Legacy prediction history excludes removed and V7 model rows", LegacyPredictionHistoryExcludesRemovedAndV7Rows)
    ,("retired fixed-period prediction model entry points are removed", RemovedFixedPeriodModelHasNoEntryPoints)
    ,("database initialization removes retired compatibility predictions", DatabaseInitializationRemovesRetiredPredictions)
    ,("V8.2 market state probabilities are normalized and leakage safe", V82MarketStateIsNormalizedAndLeakageSafe)
    ,("V8.2 cross features are named finite and leakage safe", V82CrossFeaturesAreNamedFiniteAndLeakageSafe)
    ,("V8.2 pairwise ranker returns one normalized 12-zodiac ranking", V82PairwiseRankerReturnsNormalizedRanking)
    ,("V8.3 state probabilities condition the pairwise ranker without hard routing", V83StateProbabilitiesConditionRanking)
    ,("V8.3 ranking exposes stability metrics", V83RankingExposesStabilityMetrics)
    ,("V8.3 audit can ablate state-missing without changing other features", V83CanAblateStateMissingForAudit)
    ,("V8.2 color backtest is independent from zodiac predictions", V82ColorBacktestIsIndependentFromZodiac)
    ,("automatic learning weights stay bounded", AutomaticLearningWeightsStayBounded)
    ,("meta ranking uses safe fallback and normalized probabilities", MetaRankingIsSafeAndNormalized)
    ,("automatic learning uses top3 five and top6 three miss thresholds", AutomaticLearningUsesDualMissThresholds)
    ,("prediction feedback is persisted exactly once", PredictionFeedbackIsPersistedExactlyOnce)
    ,("automatic learning evaluation is chronological", AutomaticLearningEvaluationIsChronological)
    ,("color learning weights stay bounded", ColorLearningWeightsStayBounded)
    ,("color learning uses independent main and dual miss thresholds", ColorLearningUsesIndependentMissThresholds)
    ,("color feedback is idempotent", ColorFeedbackIsIdempotent)
    ,("color prediction consumes learned weights and exposes features", ColorPredictionConsumesLearnedWeights)
    ,("color prediction history persists one learnable snapshot", ColorPredictionHistoryPersistsSnapshot)
    ,("V6 site, desktop sync, and publisher use one cloud API", V6CloudEndpointsAreConsistent)
    ,("V6.5 mapping service has complete validated maps", V65MappingServiceProvidesCompleteValidatedMaps)
    ,("V6.5 prediction persists target-year mapping snapshot", V65MappingSnapshotIsStoredWithPrediction)
    ,("web wave-color mapping is parsed independently from number API", WebWaveColorMappingIsParsed)
    ,("scoreboard reserves space for the horizontal scroll bar", ScoreboardReservesHorizontalScrollSpace)
    ,("current web wave map is never claimed for a prior lunar year", HistoricalWaveColorDoesNotClaimCurrentWebMap)
    ,("crawler save backfills a missing wave color without counting a new draw", CrawlerSaveBackfillsWaveColor)
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

void V65MappingServiceProvidesCompleteValidatedMaps()
{
    IReadOnlyDictionary<int, string> colors = V65MappingService.NumberToWaveColor;
    Assert(colors.Count == 49 && Enumerable.Range(1, 49).All(number => colors.ContainsKey(number)),
        "1-49 must each have one wave color");
    Assert(colors.Values.All(color => color is "红" or "蓝" or "绿"), "wave color must be red blue or green");
    Assert(V65MappingService.GetWaveColor("26") == "蓝" && V65MappingService.GetWaveColor("05") == "绿",
        "canonical wave colors differ from verified display mapping");

    IReadOnlyDictionary<string, IReadOnlyList<string>> map = V65MappingService.GetZodiacNumberMap(2026);
    string[] allNumbers = map.Values.SelectMany(numbers => numbers).ToArray();
    Assert(map.Count == 12 && allNumbers.Distinct().Count() == 49 && allNumbers.Length == 49,
        "year zodiac mapping must cover 1-49 exactly once");
    Assert(V65MappingService.GetYearZodiac(2026) == "马" && V65MappingService.GetZodiacBySpecialNumber("01", 2026) == "马",
        "known 2026 number zodiac mapping is inconsistent");
}

void V65MappingSnapshotIsStoredWithPrediction()
{
    DatabaseHelper.SavePrediction("20260101", "马", "马,羊,猴,鸡,狗,猪", "01", "V6.5", 50, "test");
    DatabaseHelper.PredictionRecord record = DatabaseHelper.GetPredictionHistory(int.MaxValue)
        .Single(row => row.Issue == "20260101" && row.AnalysisPeriods == 50);
    Assert(record.MappingSnapshotJson.Contains("2026") && record.MappingSnapshotJson.Contains(V65MappingService.ZodiacNumberMappingVersion) &&
        record.MappingSnapshotJson.Contains(V65MappingService.WaveColorMappingVersion),
        "prediction record did not save target-year mapping versions");
}

void WebWaveColorMappingIsParsed()
{
    string script = "const tl={red:[\"01\",\"02\"],blue:[\"03\"],green:[\"04\",\"49\"]};";
    IReadOnlyDictionary<string, string> colors = DataCrawler.ExtractWaveColorMapFromPageScript(script);
    Assert(colors.Count == 5 && colors["01"] == "红" && colors["03"] == "蓝" && colors["49"] == "绿",
        "网页脚本中的波色映射没有独立解析");
}

void ScoreboardReservesHorizontalScrollSpace()
{
    Control scoreboard = V65ExperimentScoreboardView.Create();
    Assert(scoreboard.Height >= 520,
        "scoreboard must have its final height before bottom-anchored controls are created");
    Assert(scoreboard.Controls.OfType<HScrollBar>().Single().Bottom <= scoreboard.ClientSize.Height,
        "horizontal scroll bar must remain inside the visible scoreboard area");
}

void HistoricalWaveColorDoesNotClaimCurrentWebMap()
{
    var historical = new DataCrawler.CrawlRecord
    {
        Period = "2025123",
        SpecialNumber = "01",
        Date = "2025-08-10 21:30:00"
    };
    DataCrawler.ApplyWaveColorSource(historical, new Dictionary<string, string> { ["01"] = "红" }, new DateTime(2026, 8, 12));
    Assert(historical.WaveColorSource == "LocalReference",
        "a current web map must not be labelled as historical web evidence");
}

void CrawlerSaveBackfillsWaveColor()
{
    int saved = DatabaseHelper.SaveCrawlerData(new List<DataCrawler.CrawlRecord>
    {
        new()
        {
            Period = "100",
            Numbers = "010203040506",
            SpecialNumber = "07",
            SpecialZodiac = "马",
            SpecialWaveColor = "红",
            WaveColorSource = "LocalReference",
            Date = "2026-01-01 21:30:00"
        }
    });
    DatabaseHelper.HistoryRecord record = DatabaseHelper.GetHistory().Single(row => row.Period == "100");
    Assert(saved == 0, "backfilling an existing draw must not be reported as a new draw");
    Assert(record.SpecialWaveColor == "红" && record.WaveColorSource == "LocalReference",
        "missing wave color fields were not backfilled");
}

void PredictionScoreUsesTargetYearMap()
{
    var result = PredictionScoreService.Predict(500, 2026);
    Assert(result.Predictions.Single(item => item.Zodiac == "马").Number == "01,13,25,37,49",
        "综合评分仍在使用旧年份静态映射");
    Assert(result.Predictions.Single(item => item.Zodiac == "虎").Number == "05,17,29,41",
        "综合评分虎肖号码未按2026马年轮转");
}

void V63UsesGpt56Sol()
{
    Assert(AIEngine.Version == "AI生肖预测 V6.5", "预测模型不是V6.5");
    Assert(OpenAIService.Model == "gpt-5.6-sol", "V6.5外部分析模型不是GPT-5.6 Sol");
    Assert(OpenAIService.ApiKey == (Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? ""),
        "OpenAI API Key未从云端环境变量读取");
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

void CloudPredictionSkipsRemoved200Period()
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
    Assert(CloudPredictionSyncService.ImportPrediction(prediction) == 0,
        "缺少12生肖完整分项评分的云端预测不应写入学习历史");
    int count = DatabaseHelper.GetPredictionHistory(int.MaxValue)
        .Count(record => record.Issue == "2026203");
    Assert(count == 0, "无效云端预测仍被写入本地历史");
}

void CloudHistoryExportIsValid()
{
    string output = Path.Combine(FreshDirectory(), "history.json");
    CloudHistoryAutomation.Export(output);
    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(output));
    Assert(document.RootElement.GetProperty("status").GetString() == "success", "开奖档案状态错误");
    Assert(document.RootElement.GetProperty("records").GetArrayLength() >= 3, "开奖档案记录不完整");
}

void UnchangedCloudHistoryIsNotRewritten()
{
    string output = Path.Combine(FreshDirectory(), "history.json");
    CloudHistoryAutomation.Export(output);
    string first = File.ReadAllText(output);
    Thread.Sleep(20);
    CloudHistoryAutomation.Export(output);
    Assert(File.ReadAllText(output) == first,
        "开奖记录没有增加时不应只因更新时间变化而制造新的Git提交");
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

void ExtremeOmissionDoesNotKeepRising()
{
    double nearAverage = V65RuleScoringEngine.CalculateOmissionScore(12, 12);
    double extreme = V65RuleScoringEngine.CalculateOmissionScore(36, 12);
    Assert(extreme < nearAverage,
        "遗漏达到平均值数倍后不应继续加分，否则会让同一生肖越不中越霸榜");
}

void AllHistoryLearningUsesStableBucket()
{
    Assert(PredictionLearningService.IsSameAnalysisBucket(1313, 1306),
        "全部历史每期增长后仍应复用以前的已开奖学习样本");
    Assert(PredictionLearningService.IsSameAnalysisBucket(0, 1313),
        "云端使用的全部历史标识0应与本地实际样本数归入同一学习组");
    Assert(!PredictionLearningService.IsSameAnalysisBucket(200, 1313),
        "固定200期样本不能与全部历史样本混用");
    Assert(!PredictionLearningService.IsSameAnalysisBucket(500, 1313),
        "旧版固定500期样本不能与全部历史样本混用");
    Assert(PredictionLearningService.IsSameAnalysisBucket(100, 100),
        "固定周期应继续严格匹配");
}

void EightZodiacBonusIsBounded()
{
    Assert(V65RuleScoringEngine.CalculateEightZodiacBonus(0.82) <= 3,
        "八肖关联加分不应大到单独改变榜首");
}

void MlFeaturesAreLeakageSafe()
{
    var records = new List<DatabaseHelper.HistoryRecord>
    {
        History("1", "01", "鼠"), History("2", "02", "鼠"), History("3", "01", "牛"),
        History("4", "03", "虎"), History("5", "01", "鼠"), History("6", "04", "兔")
    };
    var before = MachineLearningPredictionService.BuildFeatures(records, 5, "鼠");
    var after = MachineLearningPredictionService.BuildFeatures(records, 4, "鼠");
    Assert(before.Recent5Count == 3, "recent 5 count should only use prior records");
    Assert(after.Recent5Count == 2, "feature extraction used future records");
    Assert(before.Gap1RepeatCount >= 1, "gap-1 feature missing");
    Assert(after.Gap2RepeatCount >= 0, "gap-2 feature missing");
}

void MlModelsReturnRankedProbabilities()
{
    var records = new List<DatabaseHelper.HistoryRecord>();
    string[] z = { "鼠", "牛", "鼠", "虎", "鼠", "兔", "鼠", "龙", "牛", "鼠", "蛇", "鼠" };
    for (int i = 0; i < z.Length; i++) records.Add(History((i + 1).ToString(), (i + 1).ToString("00"), z[i]));
    var result = MachineLearningPredictionService.Predict(records, 10, MlModelKind.LightGbmStyle);
    Assert(result.Count == 12, "one probability per zodiac is required");
    Assert(result.All(x => x.Probability is >= 0 and <= 1), "probability outside [0,1]");
    Assert(result.SequenceEqual(result.OrderByDescending(x => x.Probability)), "results are not ranked");
    Assert(result.Take(3).Count() == 3 && result.Take(6).Count() == 6, "TOP3/TOP6 unavailable");
}

void MlRollingBacktestRecordsMetrics()
{
    var records = new List<DatabaseHelper.HistoryRecord>();
    string[] z = { "鼠", "牛", "鼠", "虎", "鼠", "兔", "鼠", "龙", "牛", "鼠", "蛇", "鼠", "马", "鼠" };
    for (int i = 0; i < z.Length; i++) records.Add(History((i + 1).ToString(), (i + 1).ToString("00"), z[i]));
    var report = MachineLearningPredictionService.RollingBacktest(records, 5, 3, MlModelKind.XgBoostStyle);
    Assert(report.Predictions.Count == records.Count - 5, "rolling backtest count is incorrect");
    Assert(report.Top3HitRate is >= 0 and <= 1 && report.Top6HitRate is >= 0 and <= 1, "invalid hit rate");
    Assert(report.MaximumConsecutiveMisses >= 0, "missing max consecutive misses");
    Assert(report.Predictions.All(x => x.TrainingCount <= x.TargetIndex), "backtest used future data");
}

void MlSelectsHighestGainFeature()
{
    var method = typeof(MachineLearningPredictionService).GetMethod(
        "SelectBestSplitFeature", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
    Assert(method != null, "ML scorer does not expose the production split selector for regression testing");
    int width = MachineLearningPredictionService.FeatureNames.Count;
    var samples = Enumerable.Range(0, 5).Select(_ => new double[width]).ToArray();
    samples[0][0] = 0; samples[1][0] = 0; samples[2][0] = 0; samples[3][0] = 1; samples[4][0] = 1;
    int distractor = width - 1;
    samples[0][distractor] = 0; samples[1][distractor] = 1; samples[2][distractor] = 0;
    samples[3][distractor] = 1; samples[4][distractor] = 0;
    var labels = new double[] { 0, 0, 0, 1, 1 };
    string selected = Convert.ToString(method!.Invoke(null, new object[] { samples, labels })) ?? "";
    Assert(selected == MachineLearningPredictionService.FeatureNames[0],
        $"expected highest-gain {MachineLearningPredictionService.FeatureNames[0]}, got {selected}");
}

void FeatureEngineExposesThirtyPlusFeatures()
{
    var records = new List<DatabaseHelper.HistoryRecord>();
    string[] zodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
    for (int i = 0; i < 140; i++)
        records.Add(History((2023001 + i).ToString(), ((i * 7) % 49 + 1).ToString("00"), zodiacs[(i * 5 + i / 9) % 12]));
    var ruleFeature = FeatureEngine.BuildFeatures(records).Single(x => x.Zodiac == "鼠");
    var mlFeature = MachineLearningPredictionService.BuildFeatures(records, records.Count, "鼠");
    Assert(FeatureEngine.FeatureNames.Count >= 30, $"FeatureEngine only exposes {FeatureEngine.FeatureNames.Count} features");
    Assert(MachineLearningPredictionService.FeatureNames.Count == FeatureEngine.FeatureNames.Count,
        "ML and FeatureEngine feature dimensions differ");
    Assert(ruleFeature.ToVector().Length == FeatureEngine.FeatureNames.Count && mlFeature.ToVector().Length == FeatureEngine.FeatureNames.Count,
        "feature vector length differs from feature names");
    Assert(ruleFeature.ToVector().All(double.IsFinite) && mlFeature.ToVector().All(double.IsFinite),
        "feature vector contains NaN or Infinity");
    Assert(!FeatureEngine.FeatureNames.Any(x => x.Contains("five_element", StringComparison.OrdinalIgnoreCase)),
        "five-element feature was reintroduced");
}

void FiveElementSignalsAreRemoved()
{
    Assert(!MachineLearningPredictionService.FeatureNames.Any(x => x.Contains("five_element", StringComparison.OrdinalIgnoreCase)),
        "five-element feature is still present in ML input");
    Assert(!typeof(ZodiacFeature).GetProperties().Any(x => x.Name.Contains("FiveElement", StringComparison.OrdinalIgnoreCase)),
        "five-element fields are still present in rule features");
    var records = new List<DatabaseHelper.HistoryRecord>();
    for (int i = 0; i < 40; i++) records.Add(History((i + 1).ToString(), ((i % 10) + 1).ToString("00"), i % 3 == 0 ? "鼠" : "牛"));
    Assert(MachineLearningPredictionService.BuildFeatures(records, records.Count, "鼠").ToVector().Length >= 30,
        "ML vector should contain at least thirty non-five-element features");
    var engines = new[] { ShortTermEngine.Predict(records), MediumTermEngine.Predict(records), LongTermEngine.Predict(records) };
    var report = AIReportEngine.Generate(records, engines, MLPredictEngine.Predict(records), ColorEngine.Predict(records));
    Assert(!report.Text.Contains("五行", StringComparison.Ordinal), "AI report still exposes five-element analysis");
}

void V7FeatureEngineExposesIndependentWindows()
{
    var records = new List<DatabaseHelper.HistoryRecord>();
    for (int i = 0; i < 120; i++) records.Add(History((i + 1).ToString(), "01", i < 70 ? "牛" : (i % 3 == 0 ? "鼠" : "牛")));
    var shortFeatures = FeatureEngine.BuildFeatures(records, 50);
    var mediumFeatures = FeatureEngine.BuildFeatures(records, 100);
    Assert(shortFeatures.Count == 12 && mediumFeatures.Count == 12, "feature engine should return all zodiacs");
    Assert(shortFeatures.Single(x => x.Zodiac == "鼠").MaximumOmission < mediumFeatures.Single(x => x.Zodiac == "鼠").MaximumOmission, "window engines are sharing data");
    Assert(shortFeatures.All(x => x.CurrentOmission >= 0 && x.MaximumOmission >= x.CurrentOmission), "omission features invalid");
}

void V7EnginesAreIndependent()
{
    var records = new List<DatabaseHelper.HistoryRecord>();
    for (int i = 0; i < 120; i++) records.Add(History((i + 1).ToString(), "01", i % 2 == 0 ? "鼠" : "牛"));
    var shortResult = ShortTermEngine.Predict(records);
    var mediumResult = MediumTermEngine.Predict(records);
    var longResult = LongTermEngine.Predict(records);
    Assert(shortResult.Engine == "ShortTermEngine" && shortResult.Window == 50, "short engine metadata incorrect");
    Assert(mediumResult.Engine == "MediumTermEngine" && mediumResult.Window == 100, "medium engine metadata incorrect");
    Assert(longResult.Engine == "LongTermEngine" && longResult.Window == 0, "long engine metadata incorrect");
    Assert(shortResult.Top6.Count <= 6 && mediumResult.Top6.Count <= 6 && longResult.Top6.Count <= 6, "TOP6 output invalid");
    Assert(shortResult.Features.All(x => !(x.ShortForbidden && shortResult.Top6.Contains(x.Zodiac))), "short-forbidden zodiac was not filtered");
}

void V7MlPredictionFacadeWorks()
{
    var records = new List<DatabaseHelper.HistoryRecord>();
    for (int i = 0; i < 40; i++) records.Add(History((i + 1).ToString(), (i + 1).ToString("00"), i % 4 == 0 ? "鼠" : "牛"));
    var result = MLPredictEngine.Predict(records, MlModelKind.LightGbmStyle);
    Assert(result.Probabilities.Count == 12 && result.Top6.Count == 6, "ML facade output is incomplete");
    Assert(result.Probabilities.Values.All(x => x is >= 0 and <= 1), "ML facade probability invalid");
}

void V82MarketStateIsNormalizedAndLeakageSafe()
{
    string[] zodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
    var records = new List<DatabaseHelper.HistoryRecord>();
    for (int i = 0; i < 140; i++)
        records.Add(History((2023001 + i).ToString(), ((i * 11) % 49 + 1).ToString("00"), zodiacs[(i * 7 + i / 8) % 12]));
    var before = MarketStateEngine.Detect(records, 100);
    records.Add(History("2099999", "49", "鼠"));
    var after = MarketStateEngine.Detect(records, 100);
    Assert(before.Probabilities.Count == 4, "state model must return four states");
    Assert(Math.Abs(before.Probabilities.Values.Sum() - 1d) < 1e-9, "state probabilities must sum to one");
    Assert(before.Probabilities.Values.All(double.IsFinite), "state probabilities contain invalid values");
    Assert(before.PrimaryState == after.PrimaryState && Math.Abs(before.Confidence - after.Confidence) < 1e-12,
        "state model used a record after the target boundary");
    Assert(before.Evidence.Count > 0, "state decision must remain auditable");
}

void V82CrossFeaturesAreNamedFiniteAndLeakageSafe()
{
    string[] expected =
    {
        "omission_x_momentum_5_20", "omission_x_momentum_10_50", "recent_7_x_short_forbidden",
        "repeat_x_omission", "long_x_short_trend", "omission_ratio_x_repeat_trend",
        "recent_10_rate_x_historical_rate", "color_affinity_x_color_trend"
    };
    string[] zodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
    var records = new List<DatabaseHelper.HistoryRecord>();
    for (int i = 0; i < 130; i++)
        records.Add(History((2023001 + i).ToString(), ((i * 13) % 49 + 1).ToString("00"), zodiacs[(i * 3 + i / 11) % 12]));
    var before = MachineLearningPredictionService.BuildFeatures(records, 100, "鼠").ToVector();
    records.Add(History("2099999", "49", "鼠"));
    var after = MachineLearningPredictionService.BuildFeatures(records, 100, "鼠").ToVector();
    Assert(expected.All(FeatureEngine.FeatureNames.Contains), "one or more V8.2 cross features are missing");
    Assert(FeatureEngine.FeatureNames.Count >= 50, "V8.2 should expose at least 50 total features");
    Assert(before.Length == FeatureEngine.FeatureNames.Count && before.All(double.IsFinite), "cross feature vector is invalid");
    Assert(before.SequenceEqual(after), "cross feature extraction used data after the target boundary");
}

void V82PairwiseRankerReturnsNormalizedRanking()
{
    string[] zodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
    var records = new List<DatabaseHelper.HistoryRecord>();
    for (int i = 0; i < 90; i++)
        records.Add(History((2023001 + i).ToString(), ((i * 17) % 49 + 1).ToString("00"), zodiacs[(i * 5 + i / 6) % 12]));
    var before = ZodiacRankingEngine.Predict(records, 80, minimumTraining: 30);
    records.Add(History("2099999", "49", "鼠"));
    var after = ZodiacRankingEngine.Predict(records, 80, minimumTraining: 30);
    Assert(before.Items.Count == 12 && before.Items.Select(x => x.Zodiac).Distinct().Count() == 12,
        "ranking must contain twelve unique zodiacs");
    Assert(before.Items.Select(x => x.Rank).SequenceEqual(Enumerable.Range(1, 12)), "ranking positions must be 1 through 12");
    Assert(Math.Abs(before.Items.Sum(x => x.Probability) - 1d) < 1e-9, "ranking probabilities must sum to one");
    Assert(before.Items.All(x => double.IsFinite(x.Score) && double.IsFinite(x.Probability)), "ranking contains invalid values");
    Assert(before.Top3.SequenceEqual(before.Items.Take(3).Select(x => x.Zodiac)) &&
           before.Top6.SequenceEqual(before.Items.Take(6).Select(x => x.Zodiac)), "TOP3/TOP6 are not ranking prefixes");
    Assert(before.Items.Select(x => x.Zodiac).SequenceEqual(after.Items.Select(x => x.Zodiac)),
        "ranker used a record after the target boundary");
}

void V83StateProbabilitiesConditionRanking()
{
    string[] zodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
    var records = new List<DatabaseHelper.HistoryRecord>();
    for (int i = 0; i < 80; i++)
        records.Add(History((2023001 + i).ToString(), ((i * 19) % 49 + 1).ToString("00"), zodiacs[(i * 5 + i / 7) % 12]));
    var states = Enum.GetValues<MarketStateKind>().Select(state => new MarketStateResult
    {
        PrimaryState = state,
        Confidence = 1,
        Probabilities = Enum.GetValues<MarketStateKind>().ToDictionary(x => x, x => x == state ? 1d : 0d)
    }).ToList();
    var features = FeatureEngine.BuildFeatures(records);
    var model = new ZodiacRankingModel();
    foreach (var state in states)
        model.Update(features, records[^1].SpecialZodiac, state.Probabilities);
    var ranking = model.Rank(features, states[0].Probabilities);
    Assert(model.FeatureWeights().Keys.Count(x => x.StartsWith("state_", StringComparison.Ordinal)) == 4,
        "ranker should expose four learned state-conditioned features");
    Assert(ranking.Items.Count == 12 && Math.Abs(ranking.Items.Sum(x => x.Probability) - 1d) < 1e-9,
        "state-conditioned ranking must remain complete and normalized");
}

void V83RankingExposesStabilityMetrics()
{
    string[] zodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
    var records = new List<DatabaseHelper.HistoryRecord>();
    for (int i = 0; i < 90; i++)
        records.Add(History((2023001 + i).ToString(), ((i * 19) % 49 + 1).ToString("00"), zodiacs[(i * 5 + i / 7) % 12]));
    var state = MarketStateEngine.Detect(records);
    var model = new ZodiacRankingModel();
    var features = FeatureEngine.BuildFeatures(records);
    model.Update(features, records[^1].SpecialZodiac, state.Probabilities);
    var first = model.Rank(features, state.Probabilities);
    var second = model.Rank(features, state.Probabilities, first);
    Assert(double.IsFinite(second.Top3Margin) && second.Top3Margin >= 0, "top3 margin is invalid");
    Assert(second.RankConfidence is >= 0 and <= 1, "rank confidence must be normalized");
    Assert(second.MeanAbsoluteRankChange == 0, "unchanged ranking should have zero rank change");
}

void V83CanAblateStateMissingForAudit()
{
    string[] zodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
    var records = new List<DatabaseHelper.HistoryRecord>();
    for (int i = 0; i < 100; i++)
        records.Add(History((2023001 + i).ToString(), ((i * 11) % 49 + 1).ToString("00"), zodiacs[(i * 7 + i / 9) % 12]));
    var state = MarketStateEngine.Detect(records);
    var all = new ZodiacRankingModel();
    var ablated = new ZodiacRankingModel(includeStateMissingFeature: false);
    var features = FeatureEngine.BuildFeatures(records);
    all.Update(features, records[^1].SpecialZodiac, state.Probabilities);
    ablated.Update(features, records[^1].SpecialZodiac, state.Probabilities);
    Assert(Math.Abs(ablated.FeatureWeights()["state_missing"]) < 1e-12 &&
           Math.Abs(all.FeatureWeights()["state_missing"]) > 1e-12,
        "state-missing ablation did not keep the audited feature disabled");
    Assert(FeatureEngine.FeatureNames.All(name => ablated.FeatureWeights().ContainsKey(name)),
        "state-missing ablation removed unrelated base features");
    var ranking = ablated.Rank(features, state.Probabilities);
    Assert(ranking.Items.Count == 12 && ranking.Items.Select(x => x.Zodiac).Distinct().Count() == 12,
        "state-missing ablation produced an invalid ranking");
}

void V82ColorBacktestIsIndependentFromZodiac()
{
    string[] zodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
    var first = new List<DatabaseHelper.HistoryRecord>();
    var second = new List<DatabaseHelper.HistoryRecord>();
    for (int i = 0; i < 90; i++)
    {
        string number = ((i * 23) % 49 + 1).ToString("00");
        first.Add(History((2023001 + i).ToString(), number, zodiacs[i % 12]));
        second.Add(History((2023001 + i).ToString(), number, zodiacs[(i * 7 + 3) % 12]));
    }
    var a = ColorBacktestEngine.Run(first, 20);
    var b = ColorBacktestEngine.Run(second, 20);
    Assert(a.Samples == 70 && b.Samples == 70, "color backtest sample count is incorrect");
    Assert(a.MainHitRate is >= 0 and <= 1 && a.MainDefenseHitRate is >= 0 and <= 1 && a.ExclusionSuccessRate is >= 0 and <= 1,
        "color backtest returned invalid rates");
    Assert(a.MainHits == b.MainHits && a.MainDefenseHits == b.MainDefenseHits && a.ExclusionSuccesses == b.ExclusionSuccesses,
        "color backtest incorrectly depends on zodiac values");
    Assert(a.MaximumConsecutiveMainDefenseMisses >= 0, "color miss-run metric is invalid");
}

void V7ColorEngineWorks()
{
    var records = new List<DatabaseHelper.HistoryRecord>();
    for (int i = 0; i < 30; i++) records.Add(History((i + 1).ToString(), ((i % 10) + 1).ToString("00"), "鼠"));
    var result = ColorEngine.Predict(records);
    Assert(result.Main != result.Defense && result.Excluded != result.Main && result.Excluded != result.Defense, "color roles must be distinct");
    Assert(result.Probabilities.Count == 3, "color model must be independent red/blue/green");
}

void V7AutoOptimizerWorks()
{
    var records = new List<DatabaseHelper.HistoryRecord>();
    for (int i = 0; i < 30; i++) records.Add(History((i + 1).ToString(), (i % 10 + 1).ToString("00"), i % 2 == 0 ? "鼠" : "牛"));
    var result = AutoOptimizeEngine.Optimize(records, 8);
    Assert(result.Candidates.Count >= 2 && result.Best != null, "optimizer did not compare schemes");
    Assert(result.Candidates.All(x => x.Top6HitRate is >= 0 and <= 1), "optimizer metric invalid");
}

void V7AiReportExplainsState()
{
    var records = new List<DatabaseHelper.HistoryRecord>();
    for (int i = 0; i < 30; i++) records.Add(History((i + 1).ToString(), ((i % 10) + 1).ToString("00"), i % 2 == 0 ? "鼠" : "牛"));
    var shortResult = ShortTermEngine.Predict(records);
    var mediumResult = MediumTermEngine.Predict(records);
    var longResult = LongTermEngine.Predict(records);
    var ml = MLPredictEngine.Predict(records);
    var color = ColorEngine.Predict(records);
    var report = AIReportEngine.Generate(records, new[] { shortResult, mediumResult, longResult }, ml, color);
    Assert(report.Items.Count >= 3, "AI report should explain multiple signals");
    Assert(report.Text.Contains("短周期") && report.Text.Contains("波色"), "AI report missing state explanations");
    Assert(report.IsPrediction == false, "AI report layer must not be marked as prediction");
}

void V7AiReportOmitsRepeatedImplementationNotes()
{
    var records = new List<DatabaseHelper.HistoryRecord>();
    for (int i = 0; i < 30; i++)
        records.Add(History((i + 1).ToString(), ((i % 10) + 1).ToString("D2"), i % 2 == 0 ? "鼠" : "牛"));

    var report = AIReportEngine.Generate(records,
        new[] { ShortTermEngine.Predict(records), MediumTermEngine.Predict(records), LongTermEngine.Predict(records) },
        MLPredictEngine.Predict(records), ColorEngine.Predict(records));

    Assert(!report.Text.Contains("三套周期模型已独立完成", StringComparison.Ordinal),
        "AI report still repeats the three-engine implementation note");
    Assert(!report.Text.Contains("ML评分层已生成", StringComparison.Ordinal),
        "AI report still repeats the ML implementation note");
}

void VerifiedColorHitsAreVisuallyEmphasized()
{
    DatabaseHelper.SaveVerifiedValidationPrediction("999303", "鼠,牛,虎", "鼠,牛,虎,兔,龙,蛇",
        "鼠", "01", 1, V7PredictionHistoryService.AutoLearningValidationHistoryKey,
        "V7 AutoLearning Validation", "波色排除:绿;主:红;防:蓝", true, true, "红");

    Assert(AIPredictHistoryForm.ShouldEmphasizeWave("01", "红"),
        "the actual red wave should emphasize the matching main wave");
    Assert(!AIPredictHistoryForm.ShouldEmphasizeWave("01", "蓝"),
        "the non-matching defense wave must not be emphasized");
    Assert(!AIPredictHistoryForm.ShouldEmphasizeWave("", "红"),
        "an unverified row must not emphasize either wave");
    Assert(AIPredictHistoryForm.GetWaveTextColor("红", isHit: true).ToArgb() ==
           System.Drawing.Color.FromArgb(220, 30, 30).ToArgb(),
        "a matching red wave should use the red hit color");
    Assert(AIPredictHistoryForm.GetWaveTextColor("蓝", isHit: false).ToArgb() ==
           System.Drawing.Color.Black.ToArgb(),
        "a non-matching wave should use black text");
    Assert(AIPredictHistoryForm.GetWaveTextColor("绿", isHit: false).ToArgb() ==
           System.Drawing.Color.Black.ToArgb(),
        "an unverified wave should use black text");
}

void V65LearningAcceptsOnlyMatchingSnapshots()
{
    var v65All = new DatabaseHelper.PredictionRecord { ModelVersion = "V6.5", AnalysisPeriods = 1318 };
    var v65Short = new DatabaseHelper.PredictionRecord { ModelVersion = "V6.5", AnalysisPeriods = 50 };
    var oldCloud = new DatabaseHelper.PredictionRecord { ModelVersion = "云端每日自动预测", AnalysisPeriods = 1318 };
    var oldV3 = new DatabaseHelper.PredictionRecord { ModelVersion = "V3", AnalysisPeriods = 1318 };
    var v7 = new DatabaseHelper.PredictionRecord { ModelVersion = "V7 AutoLearning Validation", AnalysisPeriods = 7300 };

    Assert(PredictionLearningService.IsEligibleV65LearningSample(v65All, 1320),
        "V6.5全部历史样本应能被后续全部历史预测复用");
    Assert(PredictionLearningService.IsEligibleV65LearningSample(v65Short, 50),
        "V6.5同周期短期样本应能参与校准");
    Assert(!PredictionLearningService.IsEligibleV65LearningSample(v65Short, 100),
        "V6.5不同固定周期样本不能混用");
    Assert(!PredictionLearningService.IsEligibleV65LearningSample(oldCloud, 1320),
        "旧云端模型记录不能影响V6.5排序");
    Assert(!PredictionLearningService.IsEligibleV65LearningSample(oldV3, 1320),
        "V3记录不能影响V6.5排序");
    Assert(!PredictionLearningService.IsEligibleV65LearningSample(v7, 1320),
        "V7验证记录不能影响V6.5排序");
}

void ExperimentalModelKeysAreStable()
{
    Assert(ExperimentModels.ForPeriods(50) == ExperimentModels.Period50, "50期实验键错误");
    Assert(ExperimentModels.ForPeriods(100) == ExperimentModels.Period100, "100期实验键错误");
    Assert(ExperimentModels.ForPeriods(AISettings.AllHistoryModeValue) == ExperimentModels.AllHistory,
        "全历史实验键错误");
    Assert(ExperimentModels.AllKeys.Distinct().Count() == 4, "四个实验模型键必须互不相同");
    Assert(new ModelMemory(ExperimentModels.Period50).MemoryKey != new ModelMemory(ExperimentModels.Period100).MemoryKey,
        "50期与100期不得共用学习记忆");
}

void AutoLearningUsesThreeBaseSnapshots()
{
    string[] all = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
    string[] fifty = { "虎", "鼠", "牛", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
    string[] hundred = { "牛", "鼠", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
    var rows = new[]
    {
        new DatabaseHelper.PredictionRecord { Issue = "base-snapshot", ModelVersion = "V6.5", AnalysisPeriods = 50, FinalRankingJson = System.Text.Json.JsonSerializer.Serialize(fifty) },
        new DatabaseHelper.PredictionRecord { Issue = "base-snapshot", ModelVersion = "V6.5", AnalysisPeriods = 100, FinalRankingJson = System.Text.Json.JsonSerializer.Serialize(hundred) },
        new DatabaseHelper.PredictionRecord { Issue = "base-snapshot", ModelVersion = "V6.5", AnalysisPeriods = AISettings.AllHistoryModeValue, FinalRankingJson = System.Text.Json.JsonSerializer.Serialize(all) }
    };

    AutoLearningSnapshot snapshot = AutoLearningSnapshotBuilder.BuildFromBasePredictions(
        "base-snapshot", rows, new ModelMemory(ExperimentModels.AutoLearning).LoadOrCreate());
    Assert(snapshot.BaselineRanking.SequenceEqual(all), "自动学习基线必须来自全部历史基础模型快照");
    ZodiacMetaFeatures mouse = snapshot.Input.Zodiacs.Single(item => item.Zodiac == "鼠");
    Assert(mouse.BaseScores["AI"] == mouse.BaseScores["ML"] && mouse.BaseScores["ML"] < mouse.BaseScores["State"],
        "自动学习没有按50/100/全部历史三条独立快照生成特征");
}

void V65BacktestUsesFormalModelChain()
{
    var history = Enumerable.Range(1, 40).Select(index => new DatabaseHelper.HistoryRecord
    {
        Period = (2026000 + index).ToString(),
        SpecialZodiac = new[] { "鼠", "牛", "虎", "兔" }[index % 4],
        SpecialNumber = ((index % 49) + 1).ToString("D2")
    }).ToArray();

    AutoLearningSnapshot snapshot = V65ExperimentPipeline.BuildSnapshot(history, "2026041", new ModelMemoryState());
    Assert(snapshot.Input.Zodiacs.Count == 12, "V6.5回测没有生成完整十二生肖快照");
    Assert(snapshot.BaselineRanking.Count == 12, "V6.5回测没有使用全部历史正式模型作为基线");
    Assert(snapshot.Input.Zodiacs.All(item => item.BaseScores.Keys.OrderBy(key => key)
        .SequenceEqual(new[] { "AI", "ML", "Rule", "State" })),
        "V6.5回测没有从三条正式基础模型构造自动学习输入");
}

void V65BaseModelsUseConfiguredWeights()
{
    var history = Enumerable.Range(1, 40).Select(index => new DatabaseHelper.HistoryRecord
    {
        Period = (2026000 + index).ToString(),
        SpecialZodiac = new[] { "鼠", "牛", "虎", "兔", "龙" }[index % 5],
        SpecialNumber = ((index % 49) + 1).ToString("D2")
    }).ToArray();

    var models = V65ExperimentPipeline.RunBaseModels(history, "2026041");
    var fifty = models.Single(model => model.AnalysisPeriods == 50).Result.UsedWeights;
    var hundred = models.Single(model => model.AnalysisPeriods == 100).Result.UsedWeights;
    var all = models.Single(model => model.AnalysisPeriods == AISettings.AllHistoryModeValue).Result.UsedWeights;
    Assert(fifty.FrequencyWeight == 0.16 && fifty.PeriodPatternWeight == 0.32,
        "50期正式预测没有应用V6.5固定权重");
    Assert(hundred.FrequencyWeight == 0.24 && hundred.HotColdWeight == 0.20,
        "100期正式预测没有应用V6.5固定权重");
    Assert(all.FrequencyWeight == 0.17 && all.PeriodPatternWeight == 0.34,
        "全部历史正式预测没有应用V6.5固定权重");
}

void V65BacktestComparesFourExperimentModels()
{
    var history = Enumerable.Range(1, 48).Select(index => new DatabaseHelper.HistoryRecord
    {
        Period = (2026000 + index).ToString(),
        SpecialZodiac = new[] { "鼠", "牛", "虎", "兔", "龙", "蛇" }[index % 6],
        SpecialNumber = ((index % 49) + 1).ToString("D2")
    }).ToArray();

    var result = V65ExperimentBacktestService.Run(history, minimumTrainingPeriods: 12);
    Assert(result.Models.Select(model => model.ModelName).SequenceEqual(
            new[] { "V6.5-50期", "V6.5-100期", "V6.5-全部历史", "V6.5-自动学习" }),
        "V6.5回测没有只比较四条正式实验模型");
    Assert(result.Models.All(model => model.TotalTests == 36),
        "四个V6.5实验模型没有在相同目标期上比较");
}

void V65ExperimentScoreboardSummarizesModels()
{
    var records = new List<DatabaseHelper.PredictionRecord>();
    string[] ranking = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
    for (int issue = 1; issue <= 30; issue++)
    {
        foreach (int period in new[] { 50, 100, AISettings.AllHistoryModeValue })
        {
            int rank = period == 50 ? 1 : period == 100 ? 4 : 7;
            records.Add(new DatabaseHelper.PredictionRecord
            {
                Issue = issue.ToString(), ModelVersion = "V6.5", AnalysisPeriods = period,
                ActualZodiac = "鼠", FinalRankingJson = System.Text.Json.JsonSerializer.Serialize(ranking),
                ActualRank = rank, Top6Zodiac = rank <= 6 ? "鼠,牛,虎,兔,龙,蛇" : "牛,虎,兔,龙,蛇,马"
            });
        }
        records.Add(new DatabaseHelper.PredictionRecord
        {
            Issue = issue.ToString(), ModelVersion = "V6.5 AutoLearning", AnalysisPeriods = 7250,
            ActualZodiac = "鼠", ActualRank = 9, Top6Zodiac = "牛,虎,兔,龙,蛇,马"
        });
        records.Add(new DatabaseHelper.PredictionRecord
        {
            Issue = issue.ToString(), ModelVersion = "V7 ML LightGBM", AnalysisPeriods = 7200,
            ActualZodiac = "鼠", ActualRank = 3, Top6Zodiac = "鼠,牛,虎,兔,龙,蛇"
        });
    }

    IReadOnlyList<V65ExperimentScoreboardRow> rows = V65ExperimentScoreboardService.Build(records);
    V65ExperimentScoreboardRow[] v65Rows = rows.Where(row => row.Group == "V6.5四模型实验").ToArray();
    Assert(v65Rows.Select(row => row.ModelName).SequenceEqual(
            new[] { "V6.5-50期", "V6.5-100期", "V6.5-全部历史", "V6.5-自动学习" }),
        "实验成绩榜没有固定显示四条V6.5模型");
    V65ExperimentScoreboardRow fifty = v65Rows[0];
    Assert(fifty.Samples == 30 && fifty.Top3HitRate == 1 && fifty.Top6HitRate == 1 && fifty.Status == "领先",
        "领先模型的累计成绩或状态计算错误");
    V65ExperimentScoreboardRow auto = v65Rows[^1];
    Assert(auto.CurrentTop6Misses == 30 && auto.MaximumTop6Misses == 30 && auto.Status == "暂停",
        "连续TOP6未中模型没有进入暂停状态");
    Assert(rows.Any(row => row.Group == "智能预测模型" && row.ModelName == "智能预测-ML" && row.Samples == 30),
        "智能预测模型没有作为独立分组接入成绩榜");
}

void MainMenuOmitsDuplicateStatisticsChart()
{
    using var form = new Form1();
    var buttonTexts = Descendants(form).OfType<System.Windows.Forms.Button>()
        .Select(button => button.Text)
        .ToArray();
    Assert(buttonTexts.Contains("走势预测"), "trend prediction entry was removed unexpectedly");
    Assert(!buttonTexts.Contains("统计图表"), "duplicate statistics chart entry is still visible");

    static IEnumerable<System.Windows.Forms.Control> Descendants(System.Windows.Forms.Control root)
    {
        foreach (System.Windows.Forms.Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}

void V7PredictionsAreSavedToHistory()
{
    SeedHistory();
    var history = DatabaseHelper.GetLatestHistory(100);
    V7PredictionHistoryService.SaveAll("103", history);
    V7PredictionHistoryService.SaveAll("103", history);
    var records = DatabaseHelper.GetPredictionHistory(100).Where(x => x.Issue == "103").ToList();
    Assert(records.Count(x => x.ModelVersion.StartsWith("V7", StringComparison.OrdinalIgnoreCase)) == 5,
        "智能预测历史应保存五条独立模型记录");
    Assert(records.Any(x => x.ModelVersion == "V7 ShortTerm" && x.AnalysisPeriods == 7050), "智能预测短期记录缺失");
    Assert(records.Any(x => x.ModelVersion == "V7 MediumTerm" && x.AnalysisPeriods == 7100), "智能预测中期记录缺失");
    Assert(records.Any(x => x.ModelVersion == "V7 LongTerm" && x.AnalysisPeriods == 7000), "智能预测长期记录缺失");
    Assert(records.Any(x => x.ModelVersion == "V7 ML LightGBM" && x.AnalysisPeriods == 7200), "智能预测ML记录缺失");
    Assert(records.Any(x => x.ModelVersion == "V7 AutoLearning" && x.AnalysisPeriods == 7250), "智能预测自动学习记录缺失");
    Assert(V7PredictionHistoryService.ExtractColorPrediction("scores|波色排除:绿;主:红;防:蓝") == "主：红　防：蓝",
        "color history display format is incorrect");
    var colorMethod = typeof(AIPredictHistoryForm).GetMethod("GetWaveColorForDisplay",
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
    Assert(colorMethod != null, "wave-color display mapping is missing");
    Assert((System.Drawing.Color)colorMethod!.Invoke(null, new object[] { "红" })! == System.Drawing.Color.FromArgb(220, 30, 30) &&
           (System.Drawing.Color)colorMethod.Invoke(null, new object[] { "蓝" })! == System.Drawing.Color.FromArgb(30, 90, 210) &&
           (System.Drawing.Color)colorMethod.Invoke(null, new object[] { "绿" })! == System.Drawing.Color.FromArgb(0, 150, 70),
        "wave-color text should use its real red/blue/green display color");
    Assert(V7PredictionHistoryService.GetHistory(100).All(x => x.ModelVersion.StartsWith("V7", StringComparison.OrdinalIgnoreCase)),
        "智能预测历史混入了V6.5四模型记录");
    var orderedModels = V7PredictionHistoryService.GetHistory(100)
        .Where(x => x.Issue == "103")
        .Select(x => x.ModelVersion)
        .ToArray();
    Assert(orderedModels.SequenceEqual(new[] { "V7 ShortTerm", "V7 MediumTerm", "V7 ML LightGBM", "V7 AutoLearning", "V7 LongTerm" }),
        "智能预测历史模型排序不正确");
}

void AutoLearningPredictionIsFormalHistoryRow()
{
    var record = V7PredictionHistoryService.GetHistory(100)
        .Single(item => item.Issue == "103" && item.ModelVersion == "V7 AutoLearning");
    Assert(record.PredictZodiac.Split(',', StringSplitOptions.RemoveEmptyEntries).Length == 3,
        "automatic-learning TOP3 was not saved");
    Assert(record.Top6Zodiac.Split(',', StringSplitOptions.RemoveEmptyEntries).Length == 6,
        "automatic-learning TOP6 was not saved");
    Assert(!string.IsNullOrWhiteSpace(record.FinalRankingJson) &&
           !string.IsNullOrWhiteSpace(record.FeatureSnapshotJson) &&
           !string.IsNullOrWhiteSpace(record.WeightSnapshotJson),
        "automatic-learning row is missing learnable snapshots");
}

void CloudSiteUsesAutoLearningSlot()
{
    string script = File.ReadAllText(Path.Combine(ProjectRoot(), "site", "app.js"));
    Assert(script.Contains("['50', '100', 'auto', 'all']"),
        "cloud site does not display the automatic-learning slot");
    Assert(!script.Contains("['50', '100', '200', 'all']"),
        "cloud site still displays the removed 200-period slot");
}

void CloudWorkflowUsesSingleDailyRunAndFailedRetry()
{
    string workflow = File.ReadAllText(Path.Combine(ProjectRoot(), ".github", "workflows", "run-prediction.yml"));
    string[] cronLines = workflow.Split('\n')
        .Select(line => line.Trim())
        .Where(line => line.StartsWith("- cron:", StringComparison.Ordinal))
        .ToArray();
    Assert(cronLines.Contains("- cron: \"0 14 * * *\""),
        "cloud workflow is missing the 22:00 primary run");
    Assert(cronLines.Contains("- cron: \"0 15 * * *\""),
        "cloud workflow is missing the 23:00 failed-run retry");
    Assert(workflow.Contains("$primary.conclusion -ne 'success'", StringComparison.Ordinal),
        "23:00 retry is not conditioned on the 22:00 result");
    Assert(cronLines.Contains("- cron: \"7,22,37,52 * * * *\""),
        "cloud workflow is missing the mobile-trigger polling schedule");
    Assert(workflow.Contains("run_requested", StringComparison.Ordinal),
        "mobile-trigger polling does not consume the cloud run request");
}

void V6CloudEndpointsAreConsistent()
{
    const string endpoint = "https://smart-ledger-2026.ntr133.chatgpt.site/api/v6-sync";
    string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    string site = File.ReadAllText(Path.Combine(root, "site", "app.js"));
    string desktop = File.ReadAllText(Path.Combine(root, "CloudPredictionSyncService.cs"));
    string workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "run-prediction.yml"));
    Assert(site.Contains(endpoint, StringComparison.Ordinal), "V6 site is not using the active cloud API");
    Assert(desktop.Contains(endpoint, StringComparison.Ordinal), "V6 desktop sync is not using the active cloud API");
    Assert(workflow.Contains(endpoint, StringComparison.Ordinal), "V6 workflow is not using the active cloud API");
    Assert(!desktop.Contains("ntr361-smart-ledger.5rmwf2d5ff.workers.dev", StringComparison.Ordinal),
        "V6 desktop sync still uses the retired Worker URL");
    Assert(!workflow.Contains("ntr361-smart-ledger.5rmwf2d5ff.workers.dev", StringComparison.Ordinal),
        "V6 workflow still uses the retired Worker URL");
}

void V7HistoryUsesV6Layout()
{
    using var aiHistory = new AIPredictHistoryForm();
    var aiGrid = FindControl<System.Windows.Forms.DataGridView>(aiHistory);
    Assert(aiGrid != null && !aiGrid.Columns.Contains("ReviewDetails"),
        "AI prediction history should not display review details");

    using var form = new V7PredictionHistoryForm();
    var grid = FindControl<System.Windows.Forms.DataGridView>(form);
    Assert(form.WindowState == System.Windows.Forms.FormWindowState.Maximized, "V7 history should use the maximized V6 history effect");
    Assert(grid != null && grid.Columns.Count == 13, "V7 history should use all V6 history columns plus a color column");
    Assert(grid!.Columns.Contains("AnalysisPeriods") && grid.Columns.Contains("PredictNumber") && grid.Columns.Contains("ReviewDetails"), "V7 history is missing V6 history columns");
    Assert(grid.Columns.Contains("ColorPrediction"), "V7 history should display color prediction in an independent column");
    Assert(V7PredictionHistoryService.FormatAnalysisLabel(7050, "V7 ShortTerm") == "50期", "visible V7 label should be removed from history window");
}

void LegacyPredictionHistoryExcludesRemovedAndV7Rows()
{
    DatabaseHelper.SavePrediction("998001", "鼠,牛,虎", "鼠,牛,虎,兔,龙,蛇", "01,02,03", "V6.3", 100,
        "legacy", "legacy");
    DatabaseHelper.SavePrediction("998001", "马,羊,猴", "马,羊,猴,鸡,狗,猪", "07,08,09", "V7 ShortTerm", 998050,
        "v7", "v7");
    DatabaseHelper.SavePrediction("998002", "兔,龙,蛇", "兔,龙,蛇,马,羊,猴", "10,11,12", "V6.3", 200,
        "removed 200-period model", "removed 200-period model");
    DatabaseHelper.SavePrediction("998003", "鸡,狗,猪", "鸡,狗,猪,鼠,牛,虎", "13,14,15", "V6.3", 0,
        "old compatibility record", "old compatibility record");
    using var form = new AIPredictHistoryForm();
    var grid = FindControl<System.Windows.Forms.DataGridView>(form);
    Assert(grid != null, "legacy prediction history grid is missing");
    var versions = grid!.Rows.Cast<System.Windows.Forms.DataGridViewRow>()
        .Select(row => Convert.ToString(row.Cells["ModelVersion"].Value) ?? "")
        .ToArray();
    Assert(versions.Contains("V6.3"), "legacy prediction history lost its V6 row");
    Assert(versions.All(version => !version.StartsWith("V7", StringComparison.OrdinalIgnoreCase)),
        "legacy prediction history still displays V7 rows");
    var analysisLabels = grid.Rows.Cast<System.Windows.Forms.DataGridViewRow>()
        .Select(row => Convert.ToString(row.Cells["AnalysisPeriods"].Value) ?? "")
        .ToArray();
    Assert(!analysisLabels.Contains("200期"), "legacy prediction history still displays the removed 200-period model");
    Assert(!analysisLabels.Contains("旧记录"), "legacy prediction history still displays old compatibility rows");
}

void RemovedFixedPeriodModelHasNoEntryPoints()
{
    Assert(AISettings.GetPeriodOptions().All(option => option.Value is not 200 and not 500),
        "AI settings still exposes a retired fixed-period model");
    var field = typeof(DailyPredictionAutomation).GetField("Periods",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
    Assert(field?.GetValue(null) is int[] periods && !periods.Contains(200) && !periods.Contains(500),
        "daily automation still generates a retired fixed-period model");
    Assert(typeof(PredictionScoreService).GetMethod(nameof(PredictionScoreService.Predict))!
        .GetParameters()[0].DefaultValue is int scoreDefault && scoreDefault == int.MaxValue,
        "comprehensive scoring still defaults to the retired 500-period model");
    Assert(typeof(EnsemblePredictionService).GetMethod(nameof(EnsemblePredictionService.Predict))!
        .GetParameters()[0].DefaultValue is int ensembleDefault && ensembleDefault == int.MaxValue,
        "ensemble prediction still defaults to the retired 500-period model");
}

void DatabaseInitializationRemovesRetiredPredictions()
{
    int drawCount = DatabaseHelper.GetHistory().Count;
    DatabaseHelper.SavePrediction("998010", "鼠,牛,虎", "鼠,牛,虎,兔,龙,蛇", "01,02,03", "云端 V6.3", 0,
        "retired compatibility record", "retired compatibility record");
    DatabaseHelper.SavePrediction("998011", "马,羊,猴", "马,羊,猴,鸡,狗,猪", "07,08,09", "云端 V6.3", 200,
        "retired 200-period record", "retired 200-period record");
    DatabaseHelper.SavePrediction("998012", "兔,龙,蛇", "兔,龙,蛇,马,羊,猴", "10,11,12", "云端 V6.3", 50,
        "中信心 | 云端旧模型", "cloud record without component scores");
    DatabaseHelper.SavePrediction("998013", "鸡,狗,猪", "鸡,狗,猪,鼠,牛,虎", "13,14,15", "V6.3", 50,
        "鸡:80|频10|势10|漏25|冷10|周20;狗:70|频9|势9|漏20|冷9|周18", "local record");

    DatabaseHelper.InitializeDatabase();

    Assert(DatabaseHelper.GetPredictionHistory(int.MaxValue)
        .All(record => record.AnalysisPeriods is not 0 and not 200),
        "retired compatibility prediction rows were not removed during initialization");
    Assert(DatabaseHelper.GetPredictionHistory(int.MaxValue)
        .All(record => record.ModelVersion != "云端 V6.3"),
        "cloud predictions without component scores were not removed during initialization");
    Assert(DatabaseHelper.GetPredictionHistory(int.MaxValue)
        .Any(record => record.Issue == "998013" && record.ModelVersion == "V6.3"),
        "valid local prediction was removed by cloud-history cleanup");
    Assert(DatabaseHelper.GetHistory().Count == drawCount,
        "prediction cleanup must not remove draw history");
}

void AutomaticLearningWeightsStayBounded()
{
    var current = ModelWeights.Default;
    var next = new WeightOptimizer().Adjust(current, new ModelFeedback(
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["AI"] = 10,
            ["ML"] = -10,
            ["State"] = 2,
            ["Rule"] = -2
        }, "test"));

    Assert(Math.Abs(next.AI - current.AI) <= 0.050000001, "AI weight changed by more than five points");
    Assert(Math.Abs(next.ML - current.ML) <= 0.050000001, "ML weight changed by more than five points");
    Assert(new[] { next.AI, next.ML, next.State, next.Rule }.All(value => value >= 0 && value <= 0.70),
        "a model weight escaped the 0-70% range");
    Assert(Math.Abs(next.Sum - 1.0) < 0.000000001, "model weights no longer sum to 100%");
}

void MetaRankingIsSafeAndNormalized()
{
    string[] zodiacs = { "Rat", "Ox", "Tiger", "Rabbit", "Dragon", "Snake", "Horse", "Goat", "Monkey", "Rooster", "Dog", "Pig" };
    var input = new MetaPredictionInput("2027001", zodiacs.Select((zodiac, index) =>
        new ZodiacMetaFeatures(zodiac,
            new Dictionary<string, double> { ["AI"] = 12-index, ["ML"] = 12-index, ["State"] = index, ["Rule"] = 0 },
            new Dictionary<string, double> { ["frequency"] = (12-index)/12d })).ToArray());
    var baseline = zodiacs.ToArray();

    var cold = new MetaPredictionEngine().Predict(input, new ModelMemoryState(), baseline);
    Assert(cold.UsedFallback, "fewer than 100 samples must use the existing ranking");
    Assert(cold.Ranking.Select(item => item.Zodiac).SequenceEqual(baseline), "fallback changed the existing ranking");

    var memory = new ModelMemoryState { LearnedSamples = 100 };
    var ranked = new MetaPredictionEngine().Predict(input, memory, baseline);
    Assert(!ranked.UsedFallback, "a valid trained snapshot unexpectedly fell back");
    Assert(ranked.Ranking.Count == 12 && ranked.Ranking.Select(item => item.Zodiac).Distinct().Count() == 12,
        "meta ranking must contain exactly 12 unique zodiacs");
    Assert(Math.Abs(ranked.Ranking.Sum(item => item.Probability) - 1.0) < 0.000000001,
        "meta probabilities must sum to one");
}

void AutomaticLearningUsesDualMissThresholds()
{
    var engine = new AutoLearningEngine();
    var top3Memory = new ModelMemoryState { LearnedSamples = 100 };
    for (int i = 1; i <= 4; i++)
    {
        var outcome = engine.ApplyFeedback(top3Memory, Feedback(i, actualRank: 4));
        Assert(!outcome.FailureAnalysisTriggered, "TOP3 failure analysis triggered before five misses");
    }
    Assert(engine.ApplyFeedback(top3Memory, Feedback(5, actualRank: 4)).FailureAnalysisTriggered,
        "TOP3 five consecutive misses did not trigger failure analysis");

    var top6Memory = new ModelMemoryState { LearnedSamples = 100 };
    for (int i = 1; i <= 2; i++)
    {
        var outcome = engine.ApplyFeedback(top6Memory, Feedback(i, actualRank: 7));
        Assert(!outcome.FailureAnalysisTriggered, "TOP6 failure analysis triggered before three misses");
    }
    Assert(engine.ApplyFeedback(top6Memory, Feedback(3, actualRank: 7)).FailureAnalysisTriggered,
        "TOP6 three consecutive misses did not trigger failure analysis");

    static PredictionFeedback Feedback(int issue, int actualRank) => new(
        issue.ToString(), actualRank,
        new Dictionary<string, int> { ["AI"] = actualRank, ["ML"] = Math.Min(12, actualRank + 1), ["State"] = Math.Max(1, actualRank - 1), ["Rule"] = actualRank },
        new Dictionary<string, double> { ["frequency"] = -0.4, ["omission"] = 0.2 });
}

void PredictionFeedbackIsPersistedExactlyOnce()
{
    string[] zodiacs = { "Rat", "Ox", "Tiger", "Rabbit", "Dragon", "Snake", "Horse", "Goat", "Monkey", "Rooster", "Dog", "Pig" };
    var input = new MetaPredictionInput("999101", zodiacs.Select((zodiac,index) =>
        new ZodiacMetaFeatures(zodiac,
            new Dictionary<string,double> { ["AI"]=12-index, ["ML"]=index, ["State"]=6, ["Rule"]=0 },
            new Dictionary<string,double> { ["frequency"]=(12-index)/12d, ["omission"]=index/12d })).ToArray());
    DatabaseHelper.SavePrediction("999101", string.Join(",", zodiacs.Take(3)), string.Join(",", zodiacs.Take(6)),
        "01,02,03", "V6.5 AutoLearning", 7250, "test", "test", JsonSerializer.Serialize(zodiacs),
        JsonSerializer.Serialize(input.Zodiacs.ToDictionary(item => item.Zodiac, item => item.BaseScores)),
        JsonSerializer.Serialize(input), JsonSerializer.Serialize(ModelWeights.Default));
    var row = DatabaseHelper.GetPredictionHistory(int.MaxValue).First(item => item.Issue == "999101" && item.AnalysisPeriods == 7250);
    LearningOutcome first = DatabaseHelper.ApplyAutomaticLearningForPrediction(row.Id, "Tiger");
    LearningOutcome second = DatabaseHelper.ApplyAutomaticLearningForPrediction(row.Id, "Tiger");
    var saved = DatabaseHelper.GetPredictionHistory(int.MaxValue).First(item => item.Id == row.Id);
    Assert(first.Updated, "first prediction feedback was not learned");
    Assert(!second.Updated, "the same prediction feedback was learned twice");
    Assert(saved.LearningStatus == "Learned" && saved.ActualRank == 3, "learned state or actual rank was not persisted");
}

void AutomaticLearningEvaluationIsChronological()
{
    string[] zodiacs = { "Rat", "Ox", "Tiger", "Rabbit", "Dragon", "Snake", "Horse", "Goat", "Monkey", "Rooster", "Dog", "Pig" };
    var rows = new List<DatabaseHelper.HistoryRecord>();
    int issue = 2023001;
    for (int i = 0; i < 180; i++)
    {
        int year = i < 120 ? 2023 + i/50 : 2026;
        rows.Add(new DatabaseHelper.HistoryRecord
        {
            Period = (year*1000 + i%500 + 1).ToString(),
            OpenTime = $"{year}-01-01 21:30:00",
            SpecialZodiac = zodiacs[i%12],
            SpecialNumber = ((i%49)+1).ToString("D2")
        });
        issue++;
    }
    AutoLearningEvaluationResult result = AutoLearningEvaluation.Run(rows);
    Assert(result.TrainingSamples > 0 && result.TestSamples == 60, "evaluation did not use the intended train/test years");
    Assert(!result.FutureDataLeakageDetected, "evaluation exposed the target or future draw to prediction");
    Assert(result.Baseline.Samples == result.Learning.Samples, "baseline and learning evaluated different issues");
    Assert(result.Latest50.Count == 50 && result.Latest50[^1].Issue == rows[^1].Period,
        "evaluation did not retain the latest 50 chronological validation records");
    Assert(result.ColorTrainingSamples > 0 && result.BaselineColor.Samples == 60 && result.LearningColor.Samples == 60,
        "color evaluation did not use the intended chronological train/test split");
    Assert(result.Latest50.All(item => !string.IsNullOrWhiteSpace(item.MainColor) &&
        !string.IsNullOrWhiteSpace(item.DefenseColor) && !string.IsNullOrWhiteSpace(item.ActualColor)),
        "latest 50 validation rows are missing color predictions or actual colors");
    AutoLearningEvaluation.SaveLatest50ToPredictionHistory(result);
    var savedColor = DatabaseHelper.GetPredictionHistory(int.MaxValue)
        .First(item => item.Issue == rows[^1].Period && item.ModelVersion == "V7 AutoLearning Validation");
    Assert(V7PredictionHistoryService.ExtractColorPrediction(savedColor.ScoreDetails) != "-",
        "latest-50 history did not display main and defense colors");
    Assert(savedColor.ReviewDetails.Contains("主波") && savedColor.ReviewDetails.Contains("双波"),
        "latest-50 history did not record color hit outcomes");
}

void ColorLearningWeightsStayBounded()
{
    var state = new ColorLearningState();
    ColorLearningWeights before = state.Weights;
    ColorLearningOutcome outcome = new ColorAutoLearningEngine().ApplyFeedback(state,
        ColorFeedback("color-1", actual: "红", main: "蓝", defense: "绿"));

    Assert(outcome.Updated, "valid color feedback was not learned");
    Assert(Math.Abs(state.Weights.Frequency - before.Frequency) <= 0.050000001,
        "color frequency weight changed by more than five points");
    Assert(Math.Abs(state.Weights.Transition - before.Transition) <= 0.050000001,
        "color transition weight changed by more than five points");
    Assert(Math.Abs(state.Weights.Omission - before.Omission) <= 0.050000001,
        "color omission weight changed by more than five points");
    Assert(new[] { state.Weights.Frequency, state.Weights.Transition, state.Weights.Omission }
        .All(value => value >= 0.05 && value <= 0.85), "a color weight escaped the 5-85% range");
    Assert(Math.Abs(state.Weights.Sum - 1) < 0.000000001, "color weights do not sum to 100%");
}

void ColorLearningUsesIndependentMissThresholds()
{
    var engine = new ColorAutoLearningEngine();
    var mainState = new ColorLearningState();
    for (int i = 1; i <= 4; i++)
        Assert(!engine.ApplyFeedback(mainState, ColorFeedback($"main-{i}", "蓝", "红", "蓝")).FailureAnalysisTriggered,
            "main-color failure analysis triggered before five misses");
    ColorLearningOutcome mainTrigger = engine.ApplyFeedback(mainState, ColorFeedback("main-5", "蓝", "红", "蓝"));
    Assert(mainTrigger.FailureAnalysisTriggered && mainTrigger.Reason.Contains("主波连续5期"),
        "main-color five consecutive misses did not trigger failure analysis");

    var dualState = new ColorLearningState();
    for (int i = 1; i <= 2; i++)
        Assert(!engine.ApplyFeedback(dualState, ColorFeedback($"dual-{i}", "绿", "红", "蓝")).FailureAnalysisTriggered,
            "dual-color failure analysis triggered before three misses");
    ColorLearningOutcome dualTrigger = engine.ApplyFeedback(dualState, ColorFeedback("dual-3", "绿", "红", "蓝"));
    Assert(dualTrigger.FailureAnalysisTriggered && dualTrigger.Reason.Contains("双波连续3期"),
        "dual-color three consecutive misses did not trigger failure analysis");
}

void ColorFeedbackIsIdempotent()
{
    var state = new ColorLearningState();
    var engine = new ColorAutoLearningEngine();
    ColorLearningOutcome first = engine.ApplyFeedback(state, ColorFeedback("same-color-issue", "红", "蓝", "绿"));
    ColorLearningWeights afterFirst = state.Weights;
    ColorLearningOutcome second = engine.ApplyFeedback(state, ColorFeedback("same-color-issue", "红", "蓝", "绿"));
    Assert(first.Updated && !second.Updated, "the same color issue was learned more than once");
    Assert(state.LearnedIssues.Count == 1 && state.Weights == afterFirst,
        "duplicate color feedback changed state");
}

ColorPredictionFeedback ColorFeedback(string issue, string actual, string main, string defense) => new(
    issue, actual, main, defense,
    new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.OrdinalIgnoreCase)
    {
        ["红"] = new Dictionary<string, double> { ["frequency"] = 0.9, ["transition"] = 0.8, ["omission"] = 0.1 },
        ["蓝"] = new Dictionary<string, double> { ["frequency"] = 0.2, ["transition"] = 0.3, ["omission"] = 0.9 },
        ["绿"] = new Dictionary<string, double> { ["frequency"] = 0.4, ["transition"] = 0.5, ["omission"] = 0.6 }
    });

void ColorPredictionConsumesLearnedWeights()
{
    var rows = new List<DatabaseHelper.HistoryRecord> { History("color-001", "03", "") };
    for (int i = 0; i < 10; i++) rows.Add(History($"color-r-{i}", "01", ""));
    for (int i = 0; i < 9; i++) rows.Add(History($"color-b-{i}", "02", ""));

    ColorPredictionResult frequency = ColorEngine.Predict(rows, new ColorLearningWeights(0.85, 0.10, 0.05));
    ColorPredictionResult omission = ColorEngine.Predict(rows, new ColorLearningWeights(0.10, 0.05, 0.85));
    Assert(frequency.Main == "红", "frequency-heavy color prediction did not favor the frequent red color");
    Assert(omission.Main == "绿", "omission-heavy color prediction did not favor the omitted green color");
    Assert(omission.FeatureSignals.Count == 3 && omission.FeatureSignals.Values.All(features =>
        features.ContainsKey("frequency") && features.ContainsKey("transition") && features.ContainsKey("omission")),
        "color prediction did not expose all learnable feature signals");
    Assert(Math.Abs(omission.Probabilities.Values.Sum() - 1) < 0.000000001,
        "weighted color probabilities do not sum to one");
}

void ColorPredictionHistoryPersistsSnapshot()
{
    var rows = new List<DatabaseHelper.HistoryRecord>();
    for (int i = 0; i < 60; i++) rows.Add(History($"snapshot-{i:D3}", ((i % 49) + 1).ToString("D2"), "鼠"));
    V7PredictionHistoryService.SaveAll("999202", rows);
    var record = DatabaseHelper.GetPredictionHistory(int.MaxValue)
        .Single(item => item.Issue == "999202" && item.ModelVersion == "V7 ML LightGBM");
    Assert(record.ScoreDetails.Contains("波色学习:"), "color learning snapshot was not saved in prediction history");
    ColorLearningOutcome first = DatabaseHelper.ApplyColorLearningForPrediction(record.Id, "01");
    ColorLearningOutcome second = DatabaseHelper.ApplyColorLearningForPrediction(record.Id, "01");
    Assert(first.Updated && !second.Updated, "online color feedback was not persisted exactly once");
}

T? FindControl<T>(System.Windows.Forms.Control root) where T : System.Windows.Forms.Control
{
    if (root is T found) return found;
    foreach (System.Windows.Forms.Control child in root.Controls)
    {
        var result = FindControl<T>(child);
        if (result != null) return result;
    }
    return null;
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

string ProjectRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

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
