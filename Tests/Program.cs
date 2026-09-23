Warning: truncated output (original token count: 42688)
Total output lines: 2977

using System.Text.Json;
using System.Collections.Immutable;
using System.Windows.Forms;
using 六合分析软件;
using 六合分析软件.MacroReasoning;

if (args.Contains("--readonly-db-snapshot-smoke", StringComparer.OrdinalIgnoreCase))
    return ReadOnlyDatabaseSnapshotTests.Run();

if (args.Contains("--p16-p20-smoke", StringComparer.OrdinalIgnoreCase))
{
    try { return MacroP16P20Tests.Run(); }
    catch (Exception error) { Console.Error.WriteLine(error); return 1; }
}
if (args.Contains("--p21-p24-smoke", StringComparer.OrdinalIgnoreCase))
    return MacroP21P24Tests.Run();

if (args.Contains("--macro-p21-real", StringComparer.OrdinalIgnoreCase))
{
    int samples=args.SkipWhile(x=>x!="--samples").Skip(1).Select(int.Parse).FirstOrDefault();
    if(samples is not (300 or 600 or 1000)) throw new InvalidDataException("--samples must be 300, 600 or 1000");
    string baseRoot=Path.Combine(Directory.GetCurrentDirectory(),"data","macro-p21",samples.ToString());
    string root=File.Exists(Path.Combine(baseRoot,"report.json")) ? baseRoot : baseRoot+"-run-"+DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
    Directory.CreateDirectory(root);
    Environment.SetEnvironmentVariable("LIUHE_DATA_DIR",Path.Combine(Directory.GetCurrentDirectory(),"data")); DatabaseHelper.InitializeDatabase();
    var built=MacroHistoricalFrameBuilder.Build(DatabaseHelper.GetHistory(),samples,root);
    var audit=new MacroReasoningAuditStore(Path.Combine(root,"reasoning.db"),built.Run);
    var evaluator=new MacroWalkForwardEvaluator(built.Registry,audit,built.Frames,built.Warmup,built.Weights,"p21-hold-control-v1");
    var metrics=evaluator.Evaluate(built.Run,built.Frames.Select(x=>x.Issue).ToImmutableArray(),built.Split);
    string report=Path.Combine(root,"report.json"); File.WriteAllText(report,JsonSerializer.Serialize(new {samples,metrics,evaluator.SplitMetrics,Rows=evaluator.Rows},new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine("REPORT_PATH="+report); return 0;
}

if (args.Contains("--four-expert-chain-smoke", StringComparer.OrdinalIgnoreCase))
    return FourExpertChainTests.Run();

if (args.Contains("--macro-contract-smoke", StringComparer.OrdinalIgnoreCase))
    return MacroReasoningContractTests.Run();

if (args.Contains("--p4-acceptance", StringComparer.OrdinalIgnoreCase))
    return P4AcceptanceTests.Run();

if (args.Contains("--p5-registry-smoke", StringComparer.OrdinalIgnoreCase))
    return ExpertRegistryTests.Run();

if (args.Contains("--p7-observation-smoke", StringComparer.OrdinalIgnoreCase))
    return MacroObservationEngineTests.Run();

if (args.Contains("--p6-snapshot-smoke", StringComparer.OrdinalIgnoreCase))
    return ImmutableExpertSnapshotTests.Run();

if (args.Contains("--p8-hypothesis-smoke", StringComparer.OrdinalIgnoreCase))
    return MacroHypothesisEngineTests.Run();

if (args.Contains("--p9-p13-smoke", StringComparer.OrdinalIgnoreCase))
    return MacroReasoningP9P13Tests.Run();

if (args.Contains("--p14-gating-smoke", StringComparer.OrdinalIgnoreCase))
    return MacroGatingP14Tests.Run();

if (args.Contains("--p15-uncertainty-smoke", StringComparer.OrdinalIgnoreCase))
    return MacroUncertaintyP15Tests.Run();

if (args.Contains("--integrated-v7-macro-adapter-smoke", StringComparer.OrdinalIgnoreCase))
    return IntegratedV7MacroExpertAdapterTests.Run();

if (args.Contains("--v65-base-macro-adapter-smoke", StringComparer.OrdinalIgnoreCase))
    return V65BaseMacroExpertAdapterTests.Run();

if (args.Contains("--learning-pipeline-smoke", StringComparer.OrdinalIgnoreCase))
    return RejectLegacyLearningPipelineAlias();

static int RejectLegacyLearningPipelineAlias()
{
    Console.Error.WriteLine("旧 OnlineLearningPipeline P0-P4 草案未编译、未验收。请使用 --p4-acceptance 验收当前 V7 独立学习 P4。");
    return 2;
}

if (args.Contains("--historical-replay-smoke", StringComparer.OrdinalIgnoreCase) ||
    args.Contains("--historical-replay-full", StringComparer.OrdinalIgnoreCase))
{
    string sourceDb = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "六合分析软件", "history.db");
    string smokeDir = Path.Combine(Path.GetTempPath(), "liuhe-real-replay-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(smokeDir);
    using (var source = new System.Data.SQLite.SQLiteConnection($"Data Source={sourceDb};Version=3;Read Only=True;"))
    using (var target = new System.Data.SQLite.SQLiteConnection($"Data Source={Path.Combine(smokeDir, "history.db")};Version=3;"))
    {
        source.Open();
        target.Open();
        source.BackupDatabase(target, "main", "main", -1, null, 100);
    }
    Environment.SetEnvironmentVariable("LIUHE_DATA_DIR", smokeDir);
    DatabaseHelper.InitializeDatabase();
    var allRealHistory = DatabaseHelper.GetHistory().OrderBy(row => long.Parse(row.Period)).ToArray();
    var realHistory = args.Contains("--historical-replay-full", StringComparer.OrdinalIgnoreCase)
        ? allRealHistory
        : allRealHistory.Take(112).ToArray();
    int warmup = args.Contains("--historical-replay-full", StringComparer.OrdinalIgnoreCase) ? 100 : 100;
    HistoricalReplayResult replay = new HistoricalReplayEngine().Run(realHistory,
        new HistoricalReplayOptions(warmup,
            args.Contains("--historical-replay-full", StringComparer.OrdinalIgnoreCase) ? "real-full" : "real-smoke",
            Path.Combine(smokeDir, "experiment.db")));
    EvaluationReport report = EvaluationPipeline.Evaluate(replay.Predictions);
    string reportPath = Path.Combine(smokeDir, "replay-report.json");
    string reportJson = JsonSerializer.Serialize(new { replay.WarmupSamples, TargetIssueCount = replay.TargetIssues.Count, PredictionCount = replay.Predictions.Count, replay.FutureDataLeakageDetected, RandomSeed = 6501, MonteCarloIterations = 10000, Report = report }, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(reportPath, reportJson);
    Console.WriteLine(reportJson);
    Console.WriteLine($"REPORT_PATH={reportPath}");
    return replay.FutureDataLeakageDetected ? 1 : 0;
}

if (args.Contains("--candidate-stage2-full", StringComparer.OrdinalIgnoreCase) ||
    args.Contains("--candidate-stage2-smoke", StringComparer.OrdinalIgnoreCase))
{
    string sourceDb = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "六合分析软件", "history.db");
    string runDir = Path.Combine(Path.GetTempPath(), "liuhe-candidate-stage2-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(runDir);
    string isolatedData = Path.Combine(runDir, "data");
    Directory.CreateDirectory(isolatedData);
    // Copy the isolated input snapshot before opening SQLite. Opening a zero-byte
    // destination first makes SQLite.BackupDatabase fail with CantOpen on some hosts.
    File.Copy(sourceDb, Path.Combine(isolatedData, "history.db"), overwrite: true);
    Environment.SetEnvironmentVariable("LIUHE_DATA_DIR", isolatedData);
    DatabaseHelper.InitializeDatabase();
    var allHistory = DatabaseHelper.GetHistory().OrderBy(x => long.Parse(x.Period)).ToArray();
    var history = args.Contains("--candidate-stage2-full", StringComparer.OrdinalIgnoreCase) ? allHistory : allHistory.Take(112).ToArray();
    string store = Path.Combine(runDir, "candidate-experiment.db");
    var replay = new CandidateStage2ReplayEngine().Run(history, store);
    var report = CandidateStage2Evaluation.Evaluate(replay.Candidates, replay.Controls, replay.ExperimentId, store);
    string reportPath = Path.Combine(runDir, "candidate-stage2-report.json");
    File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(new { report.ExperimentId, TargetIssueCount = replay.Controls.Select(x => x.TargetIssue).Distinct().Count(), CandidateSnapshotCount = replay.Candidates.Count, report.TripleFailureOpportunity, report.StrongFailureOpportunity, report.LeakageDetected, report.Performance, report.Rescue, report.Diversity, report.Conditional, report.MarketStates, report.TrainingValidationHoldout, report.Rolling, report.RandomConditional, report.MlModesDiffer, report.SelectorComparison, ReportPath = reportPath }, new JsonSerializerOptions { WriteIndented = true }));
    return report.LeakageDetected ? 1 : 0;
}

if (args.Contains("--normal-number-research", StringComparer.OrdinalIgnoreCase))
{
    string sourceDb = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "六合分析软件", "history.db");
    string runDir = Path.Combine(Path.GetTempPath(), "liuhe-normal-number-research-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(runDir);
    string copy = Path.Combine(runDir, "history.db"); File.Copy(sourceDb, copy, true); Environment.SetEnvironmentVariable("LIUHE_DATA_DIR", runDir); DatabaseHelper.InitializeDatabase();
    var history = DatabaseHelper.GetHistory().OrderBy(x => long.Parse(x.Period)).ToArray(); string source = Path.Combine(runDir, "normal-research.db"); NormalNumberResearch.SaveSource(source, history);
    var report = NormalNumberResearch.Run(history, source); string reportPath = Path.Combine(runDir, "normal-number-signal-report.json"); NormalNumberResearch.Save(reportPath, report);
    Console.WriteLine(JsonSerializer.Serialize(new { report.ReportTitle, report.N, report.EarliestIssue, report.LatestIssue, report.IncompleteNormalCount, report.MissingSpecialCount, report.MissingZodiacCount, report.NumberAnomalyCount, report.CandidateDecision, report.FutureDataLeakageDetected, ReportPath = reportPath }, new JsonSerializerOptions { WriteIndented = true })); return report.FutureDataLeakageDetected ? 1 : 0;
}

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

if (args.Contains("--model-redundancy-report", StringComparer.OrdinalIgnoreCase))
{
    string sourceDb = Path.Combine(Directory.GetCurrentDirectory(), "data", "history.db");
    if (!File.Exists(sourceDb))
        sourceDb = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "六合分析软件", "history.db");
    string runDir = Path.Combine(Path.GetTempPath(), "liuhe-redundancy-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(runDir);
    File.Copy(sourceDb, Path.Combine(runDir, "history.db"), true);
    Environment.SetEnvironmentVariable("LIUHE_DATA_DIR", runDir);
    DatabaseHelper.InitializeDatabase();
    var report = ModelRedundancyReportService.Run(DatabaseHelper.GetHistory(), 50,
        maxTargets: 300, mlMaxTargets: 40);
    string path = Path.Combine(runDir, "model-redundancy-report.json");
    File.WriteAllText(path, ModelRedundancyReportService.ToJson(report));
    File.WriteAllText(Path.Combine(runDir, "model-redundancy-report.md"), ModelRedundancyReportService.ToMarkdown(report));
    Console.WriteLine($"REPORT_PATH={path}");
    Console.WriteLine(JsonSerializer.Serialize(new { report.SampleCount, report.Models, report.Top3HitRates, report.Top6HitRates }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
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
    ("V7使用GPT-5.6 Sol", V63UsesGpt56Sol),
    ("自动识别下一期", AutoDetectNextIssue),
    ("指定期号运行", ExplicitIssue),
    ("已存在文件时跳过", ExistingFileSkips),
    ("强制覆盖", ForceOverwrite),
    ("历史数据为空", EmptyHistoryFails),
    ("历史数据格式错误", InvalidHistoryFails),
    ("云端定时预测要求开奖期号推进", ScheduledPredictionRequiresAdvancedDraw),
    ("输出 JSON 校验", OutputJsonIsValid),
    ("latest.json 更新", LatestJsonUpdates),
    ("重复期号检测", DuplicateIssueFails),
    ("dry-run 不修改文件", DryRunDoesNotWrite),
    ("历史数据截止期生效", HistoryCutoffWorks),
    ("历史截止期内可读取下一期基础预测快照", PredictionSnapshotsRemainVisibleDuringHistoryCutoff),
    ("特码规律生成六肖", ZodiacRuleGeneratesSix),
    ("全功能dry-run不写文件", DailyDryRunDoesNotWrite),
    ("每日自动任务同时生成V7预测", DailyAutomationGeneratesV7Predictions),
    ("有效抓取数据校验", ValidCrawlDataPasses),
    ("损坏抓取数据拒绝", InvalidCrawlDataFails)
    ,("预测清单包含全部期号", PredictionManifestContainsAllIssues)
    ,("云端预测不再导入200期模型", CloudPredictionSkipsRemoved200Period)
    ,("云端同步不把缓存预测误称为已导入历史", CloudSyncDoesNotClaimCachedPredictionsAreImported)
    ,("云端开奖档案导出", CloudHistoryExportIsValid)
    ,("开奖记录未变化时不重复改写云端档案", UnchangedCloudHistoryIsNotRewritten)
    ,("开奖档案导出包含波色", CloudHistoryExportIncludesWaveColors)
    ,("本地开奖档案可重建数据库并保留波色", LocalHistoryArchiveRebuildRestoresRecords)
    ,("运行状态档案可恢复预测历史与模型记忆", RuntimeStateArchiveRestoresPredictionsAndMemory)
    ,("V7云端工作流重建数据库且不再提交数据库文件", CloudWorkflowRebuildsDatabaseFromCommittedJson)
    ,("运行器不引用已退休的独立学习类型", PredictionRunnerDoesNotReferenceRetiredIndependentLearning)
    ,("提交的 runtime-state.json 哈希与当前代码一致", CommittedRuntimeStateHashIsValid)
    ,("运行状态规范哈希稳定且与序列化细节无关", RuntimeStateHashIsCanonicalAndStable)
    ,("历史预测逐项写入命中结果", PublishedPredictionVerificationIsRecorded)
    ,("超长遗漏不会继续抬高预测分", ExtremeOmissionDoesNotKeepRising)
    ,("全部历史学习跨期数复用样本", AllHistoryLearningUsesStableBucket)
    ,("V6.5学习只接收同版本同周期样本", V65LearningAcceptsOnlyMatchingSnapshots)
    ,("四模型实验键独立且稳定", ExperimentalModelKeysAreStable)
    ,("自动学习只读取同一期三条基础快照", AutoLearningUsesThreeBaseSnapshots)
    ,("V6.5自动学习在正式使用前完成历史预训练", V65AutoLearningBootstrapsHistoricalMemory)
    ,("V6.5回测使用正式三模型快照", V65BacktestUsesFormalModelChain)
    ,("V6.5三条基础模型应用各自固定权重", V65BaseModelsUseConfiguredWeights)
    ,("V6.5回测实际比较四条实验模型", V65BacktestComparesFourExperimentModels)
    ,("V6.5实验成绩榜统计四模型与状态", V65ExperimentScoreboardSummarizesModels)
    ,("成绩榜统计合并后的V7引擎", ScoreboardTracksMergedV7Engine)
    ,("成绩榜计入无排名但有命中的历史记录", ScoreboardCountsVerifiedRowsWithoutRanking)
    ,("成绩榜计入动态样本数的全部历史记录", ScoreboardCountsDynamicAllHistoryRows)
    ,("成绩榜近30期明细只读取指定模型的已开奖记录", ScoreboardProvidesThirtyVerifiedModelDetails)
    ,("成绩榜明细在最近30期成绩前展示该模型最新预测", ScoreboardDetailsIncludeLatestPrediction)
    ,("成绩榜提供直接的近30期明细入口", ScoreboardProvidesDirectDetailEntry)
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
    ,("智能预测历史独立保存两条模型记录", V7PredictionsAreSavedToHistory)
    ,("智能预测历史自动学习是独立正式记录", AutoLearningPredictionIsFormalHistoryRow)
    ,("V7记录保存完整12生肖排序", V7HistoryStoresCompleteRanking)
    ,("V7自动学习使用独立记忆库", V7LearningUsesIndependentMemory)
    ,("V7错因解释使用V7快照", V7ReviewUsesV7Snapshot)
    ,("cloud site replaces removed 200 period with automatic learning", CloudSiteUsesAutoLearningSlot)
    ,("cloud workflow runs at 22:00 with one failed-run retry", CloudWorkflowUsesSingleDailyRunAndFailedRetry)
    ,("V7 history uses the V6 history layout", V7HistoryUsesV6Layout)
    ,("verified color hits are visually emphasized", VerifiedColorHitsAreVisuallyEmphasized)
    ,("main menu omits duplicate statistics chart", MainMenuOmitsDuplicateStatisticsChart)
    ,("AI prediction history includes V7 but excludes retired legacy rows", LegacyPredictionHistoryExcludesRemovedAndV7Rows)
    ,("retired fixed-period models are absent from production defaults", RemovedFixedPeriodModelHasNoEntryPoints)
    ,("database initialization preserves retired predictions as archived history", DatabaseInitializationPreservesRetiredPredictions)
    ,("V7 default data directory is stable across release builds", DefaultDataDirectoryIsStableAcrossReleaseBuilds)
    ,("database backup uses stable data directory and contains current rows", DatabaseBackupUsesStableDataDirectoryAndContainsCurrentRows)
    ,("prediction history keeps the first issued snapshot", PredictionHistoryKeepsFirstIssuedSnapshot)
    ,("initialization preserves the legacy prediction archive table", InitializationPreservesLegacyPredictionArchiveTable)
    ,("legacy promotion never replaces a richer prediction database", LegacyPromotionNeverReplacesRicherPredictionDatabase)
    ,("legacy promotion copies committed WAL and legacy archive rows", LegacyPromotionCopiesCommittedWalAndArchiveRows)
    ,("legacy promotion never replaces a stable legacy archive", LegacyPromotionNeverReplacesStableLegacyArchive)
    ,("initialization preserves duplicate prediction snapshots", InitializationPreservesDuplicatePredictionSnapshots)
    ,("concurrent prediction saves are atomic and idempotent", ConcurrentPredictionSavesAreAtomicAndIdempotent)
    ,("concurrent database backups publish one valid snapshot", ConcurrentDatabaseBackupsPublishOneValidSnapshot)
    ,("ambiguous legacy migration never creates an empty stable database", AmbiguousLegacyMigrationNeverCreatesEmptyStableDatabase)
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
    ,("V7 site, desktop sync, and publisher use one cloud API", V6CloudEndpointsAreConsistent)
    ,("V7桌面云同步无需旧机器密钥即可使用独立入口", DesktopCloudSyncUsesMachineIngress)
    ,("V7桌面云同步不依赖旧机器密钥配置", DesktopCloudSyncReadsLocalMachineCredential)
    ,("V7桌面云同步优先读取 GitHub 正式档案", GitHubDesktopCloudSyncUsesPublishedV7Archive)
    ,("V7桌面云同步实际使用 GitHub 正式档案", GitHubCloudSyncIsPrimary)
    ,("V7桌面云同步状态展示实际来源", CloudSyncStatusShowsSource)
    ,("V6.5 mapping service has complete validated maps", V65MappingServiceProvidesCompleteValidatedMaps)
    ,("V6.5 prediction persists target-year mapping snapshot", V65MappingSnapshotIsStoredWithPrediction)
    ,("web wave-color mapping is parsed independently from number API", WebWaveColorMappingIsParsed)
    ,("scoreboard reserves space for the horizontal scroll bar", ScoreboardReservesHorizontalScrollSpace)
    ,("current web wave map is never claimed for a prior lunar year", HistoricalWaveColorDoesNotClaimCurrentWebMap)
    ,("crawler save backfills a missing wave color without counting a new draw", CrawlerSaveBackfillsWaveColor)
    ,("旁路 PredictionTrace 是不可变且不触碰正式预测历史", PredictionTraceIsImmutableAndIsolated)
    ,("正式四模型可旁路捕获 Trace 与开奖结果", FormalPredictionTraceCapturesLiveAndOutcome)
    ,("正式 Trace 接受动态全历史样本数", FormalPredictionTraceAcceptsDynamicAllHistoryPeriod)
    ,("AutoLearningV2 快照和残差输出完全旁路且可解释", AutoLearningV2SnapshotAndResidualAreIsolated)
    ,("AutoLearningV2 独立信号必须通过前缀泄漏审计", AutoLearningV2IndependentSignalAudit)
    ,("AutoLearningV2 WalkForward 计算 Rescue/Harm 且拒绝未来数据", AutoLearningV2WalkForwardMetricsAreLeakageSafe)
    ,("AutoLearningV2 报告区分留出指标且不宣称自动上线", AutoLearningV2ReportIsExplicitlyExperimental)
    ,("AutoLearningV2 实验快照写入独立表", AutoLearningV2ExperimentStorageIsIsolated)
    ,("云端预测档案保留本地同等完整模型快照", CloudPredictionArchiveKeepsFullLocalSnapshots)
    ,("同构模型状态快照具有稳定哈希且学习事件冲突可检测", SymmetricModelStateSnapshotDetectsConflicts)
    ,("云端发布流程包含同构运行状态", CloudWorkflowPublishesSymmetricRuntimeState)
    ,("桌面同步读取同构运行状态", DesktopSyncReadsSymmetricRuntimeState)
    ,("同构状态冲突不会部分写入", SymmetricStateConflictDoesNotPartiallyMerge)
    ,("预测历史保留本地与云端来源", PredictionHistoryPreservesPredictionSource)
    ,("历史重放实验快照保持统一期号与截止", HistoricalReplayContractsAreEnforced)
    ,("Candidate Stage 2 旁路适配与基础评估", CandidateStage2ContractsAreEnforced)
    ,("冗余度报告确定性且防泄漏", ModelRedundancyReportIsDeterministicAndLeakageSafe)
    ,("V6.5预测历史只显示正式展示档", V65HistoryShowsOnlyDisplayedModels)
    ,("手动刷新生成四个V6.5日更模型", RefreshAllPeriodsGeneratesAllV65DailyModels)
    ,("成绩榜不展示没有预测记录的模型", ScoreboardHidesModelsWithoutPredictionRecords)
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
    DataGridView grid = scoreboard.Controls.OfType<DataGridView>().Single();
    Assert(grid.ScrollBars == ScrollBars.Vertical,
        "the grid's native horizontal bar must be hidden when the explicit bar is present");
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
    Assert(AIEngine.Version == "AI生肖预测 V7", "预测模型不是V7");
    Assert(OpenAIService.Model == "gpt-5.6-sol", "V7外部分析模型不是GPT-5.6 Sol");
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
    DatabaseHelper.SavePrediction("100", "鼠", "鼠,牛,虎", "01", "V6.5", 50, "past");
    DatabaseHelper.SavePrediction("103", "马", "马,羊,猴", "07", "V6.5", 50, "future");
    using (DatabaseHelper.UseHistoryThroughIssue(101))
    {
        Assert(DatabaseHelper.GetLatestPeriod() == "101", "截止期后最新期号应为101");
        Assert(DatabaseHelper.GetLatestHistory(50).Count == 2, "截止期不应包含未来数据");
        Assert(DatabaseHelper.GetPredictionHistory(int.MaxValue).All(record =>
            !long.TryParse(record.Issue, out long issue) || issue <= 102),
            "截止期预测校准不应读取未来预测反馈");
    }
    Assert(DatabaseHelper.GetLatestPeriod() == "102", "离开截止范围后应恢复全部数据");
}

void PredictionSnapshotsRemainVisibleDuringHistoryCutoff()
{
    const string issue = "102";
    foreach (int period in new[] { 50, 100, 1320 })
        DatabaseHelper.SavePrediction(issue, "鼠", "鼠,牛,虎,兔,龙,蛇", "01", "V6.5", period,
            "base", finalRankingJson: "[\"鼠\",\"牛\",\"虎\",\"兔\",\"龙\",\"蛇\",\"马\",\"羊\",\"猴\",\"鸡\",\"狗\",\"猪\"]");

    using (DatabaseHelper.UseHistoryThroughIssue(101))
        Assert(V7PredictionHistoryService.HasCompleteV65BaseSnapshots(issue, DatabaseHelper.GetPredictionHistory(int.MaxValue)),
            "生成下一期自动学习时必须能读取同一期三条基础预测快照");
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

void ScheduledPredictionRequiresAdvancedDraw()
{
    var stagnant = new LotteryRefreshResult("2026223", "2026223", "2026223", 500, 0, false);
    AssertThrows<InvalidDataException>(() => LotteryDataRefresh.RequireAdvance(stagnant),
        "scheduled prediction must fail when the draw issue did not advance");

    LotteryDataRefresh.RequireAdvance(new LotteryRefreshResult("2026223", "2026224", "2026224", 500, 1, false));
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

void CloudHistoryExportIncludesWaveColors()
{
    var record = new DataCrawler.CrawlRecord
    {
        Period = "998101",
        Date = "2026-08-01",
        Numbers = "010203040506",
        SpecialNumber = "07",
        SpecialZodiac = "鼠",
        ShengXiao = "鼠",
        SpecialWaveColor = "红",
        WaveColorSource = "WebPage2026"
    };
    Assert(DatabaseHelper.SaveCrawlerData(new List<DataCrawler.CrawlRecord> { record }) == 1,
        "应写入带波色的测试记录");
    string output = Path.Combine(FreshDirectory(), "history.json");
    CloudHistoryAutomation.Export(output);
    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(output));
    JsonElement[] records = document.RootElement.GetProperty("records")
        .EnumerateArray().Where(item => item.GetProperty("issue").GetString() == "998101").ToArray();
    Assert(records.Length == 1, "导出应包含测试记录");
    Assert(records[0].GetProperty("special_wave_color").GetString() == "红",
        "导出应携带波色");
    Assert(records[0].GetProperty("wave_color_source").GetString() == "WebPage2026",
        "导出应携带波色来源");
}

void LocalHistoryArchiveRebuildRestoresRecords()
{
    string archive = Path.Combine(FreshDirectory(), "history.json");
    File.WriteAllText(archive, """
    {
      "status": "success",
      "updated_at": "2026-08-01T00:00:00+08:00",
      "latest_issue": "998202",
      "records": [
        {
          "issue": "998201",
          "numbers": "010203040506",
          "special_number": "07",
          "special_zodiac": "鼠",
          "open_time": "2026-08-01",
          "date": "2026-08-01",
          "special_wave_color": "红",
          "wave_color_source": "WebPage2026"
        },
        {
          "issue": "998202",
          "numbers": "111213141516",
          "special_number": "17",
          "special_zodiac": "牛",
          "open_time": "2026-08-02",
          "date": "2026-08-02",
          "special_wave_color": "蓝",
          "wave_color_source": "WebPage2026"
        }
      ]
    }
    """);
    Assert(CloudPredictionSyncService.ImportLocalHistoryArchive(archive) == 2,
        "应写入2条重建记录");
    DatabaseHelper.HistoryRecord? first = DatabaseHelper.GetLatestHistory(int.MaxValue)
        .FirstOrDefault(item => item.Period == "998201");
    Assert(first is not null, "重建后应能查到998201");
    Assert(first.SpecialWaveColor == "红" && first.WaveColorSource == "WebPage2026",
        "重建应保留波色与来源");
}

void RuntimeStateArchiveRestoresPredictionsAndMemory()
{
    string memoryKey = ExperimentModels.MemoryKey("test-restore-memory");
    var memory = new ModelMemoryState
    {
        LearnedSamples = 3,
        LastTrainingIssue = "998401",
        Weights = ModelWeights.Default
    };
    string memoryJson = JsonSerializer.Serialize(memory);
    var prediction = new DatabaseHelper.PredictionRecord
    {
        Issue = "998401",
        PredictTime = "2026-08-17T10:00:00+08:00",
        PredictionGroupId = "PRED-998401",
        PredictNumber = "01,07,13,19,25,31,37,43",
        PredictZodiac = "鼠,牛,虎",
        Top6Zodiac = "鼠,牛,虎,兔,龙,蛇",
        AnalysisPeriods = 50,
        ScoreDetails = "{\"鼠\":1.0}",
        ModelVersion = "V6.5",
        ActualNumber = "07",
        ActualZodiac = "鼠",
        HitResult = "命中",
        Top6HitResult = "命中",
        LearningDetails = "测试恢复",
        PredictionSource = "云端同步"
    };
    var snapshot = new SymmetricRuntimeStateSnapshot("v1", AIEngine.Version, "test-code",
        new[] { prediction },
        new Dictionary<string, string> { [memoryKey] = memoryJson },
        "", "2026-08-17T10:00:00Z");
    snapshot = snapshot with { StateHash = SymmetricRuntimeStateSync.Hash(snapshot) };
    string path = Path.Combine(FreshDirectory(), "runtime-state.json");
    File.WriteAllText(path, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    }));
    Assert(CloudPredictionSyncService.ImportLocalRuntimeState(path) == 1,
        "应合并1条恢复的预测记录");
    var restored = DatabaseHelper.GetPredictionHistory(int.MaxValue)
        .SingleOrDefault(row => row.Issue == "998401" && row.AnalysisPeriods == 50 && row.ModelVersion == "V6.5");
    Assert(restored is not null && restored.HitResult == "命中" && restored.ActualZodiac == "鼠",
        "恢复的预测记录应保留命中结果");
    string? restoredMemory = DatabaseHelper.LoadModelMemoryJson(memoryKey);
    Assert(restoredMemory == memoryJson, "恢复的模型记忆应原样保存");
}

void CloudWorkflowRebuildsDatabaseFromCommittedJson()
{
    string workflow = File.ReadAllText(Path.Combine(ProjectRoot(), ".github", "workflows", "run-prediction.yml"));
    Assert(workflow.Contains("--rebuild-db --rebuild-only", StringComparison.Ordinal),
        "云端工作流没有在运行前重建数据库");
    Assert(!workflow.Contains("git add data/history.db", StringComparison.Ordinal),
        "云端提交阶段不应再把数据库写入仓库");
    Assert(workflow.Contains("v7-history-db", StringComparison.Ordinal),
        "V7云端没有把数据库作为产物发布");
}

void CommittedRuntimeStateHashIsValid()
{
    string path = Path.Combine(ProjectRoot(), "site", "data", "runtime-state.json");
    Assert(File.Exists(path), "缺少提交的 runtime-state.json");
    SymmetricRuntimeStateSnapshot? snapshot = JsonSerializer.Deserialize<SymmetricRuntimeStateSnapshot>(
        File.ReadAllText(path), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    Assert(snapshot is not null, "runtime-state.json 无法解析");
    Assert(string.Equals(SymmetricRuntimeStateSync.Hash(snapshot!), snapshot!.StateHash, StringComparison.OrdinalIgnoreCase),
        "提交的 runtime-state.json 哈希与当前代码不一致，请重新导出运行状态");
}

void RuntimeStateHashIsCanonicalAndStable()
{
    var prediction = new DatabaseHelper.PredictionRecord
    {
        Issue = "998501",
        ModelVersion = "V6.5",
        AnalysisPeriods = 50,
        PredictZodiac = "马,虎,鼠",
        Top6Zodiac = "马,虎,鼠,龙,猴,羊",
        HitResult = "未开奖",
        ScoreDetails = "马:67.8|频100.0|势80.0",
        FinalRankingJson = "[\"马\",\"虎\"]",
        PredictionSource = "本地生成"
    };
    var snapshot = new SymmetricRuntimeStateSnapshot("v1", AIEngine.Version, "test",
        new[] { prediction },
        new Dictionary<string, string> { ["auto-learning-meta-v2|t"] = "{\"LearnedSamples\":2}" },
        "", "2026-08-18T00:00:00Z");
    string first = SymmetricRuntimeStateSync.Hash(snapshot);
    string second = SymmetricRuntimeStateSync.Hash(snapshot);
    Assert(first == second && first.Length == 64, "规范哈希应稳定");
    SymmetricRuntimeStateSnapshot? roundTrip = JsonSerializer.Deserialize<SymmetricRuntimeStateSnapshot>(
        JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    Assert(roundTrip is not null && SymmetricRuntimeStateSync.Hash(roundTrip) == first,
        "规范哈希不应受序列化往返影响");
}

void ModelRedundancyReportIsDeterministicAndLeakageSafe()
{
    SeedHistory();
    string[] zodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
    for (int i = 0; i < 140; i++)
        DatabaseHelper.InsertHistory((100…17688 tokens truncated….SQLiteConnection($"Data Source={stable};Version=3;Read Only=True;");
    verify.Open();
    Assert(QueryCount(verify, "SELECT COUNT(*) FROM AIPredictHistory WHERE PredictPeriod='998018'") == 1,
        "legacy promotion replaced the stable legacy prediction archive");
}

void InvokeLegacyPromotion(string stable, string legacy)
{
    var promote = typeof(DatabaseHelper).GetMethod("TryPromoteLegacyDatabase",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
    Assert(promote is not null, "legacy promotion entry point is missing");
    promote!.Invoke(null, new object[] { stable, legacy });
}

long QueryCount(System.Data.SQLite.SQLiteConnection connection, string sql)
{
    using var command = new System.Data.SQLite.SQLiteCommand(sql, connection);
    return Convert.ToInt64(command.ExecuteScalar());
}

void InitializationPreservesDuplicatePredictionSnapshots()
{
    const string issue = "998019";
    using (var connection = new System.Data.SQLite.SQLiteConnection(
        $"Data Source={DatabaseHelper.DatabasePath};Version=3;"))
    {
        connection.Open();
        using var command = new System.Data.SQLite.SQLiteCommand(@"
            DROP INDEX IF EXISTS idx_prediction_issue_periods;
            DELETE FROM PredictionHistory WHERE Issue='998019';
            INSERT INTO PredictionHistory(Issue,PredictTime,PredictNumber,PredictZodiac,Top6Zodiac,AnalysisPeriods,ModelVersion)
                VALUES ('998019','2026-08-12 20:00:00','01','鼠','鼠,牛,虎,兔,龙,蛇',100,'V6.5');
            INSERT INTO PredictionHistory(Issue,PredictTime,PredictNumber,PredictZodiac,Top6Zodiac,AnalysisPeriods,ModelVersion)
                VALUES ('998019','2026-08-12 20:01:00','02','牛','牛,虎,兔,龙,蛇,马',100,'V6.5');", connection);
        command.ExecuteNonQuery();
    }

    DatabaseHelper.InitializeDatabase();

    var rows = DatabaseHelper.GetPredictionHistory(int.MaxValue)
        .Where(record => record.Issue == issue && record.AnalysisPeriods == 100 && record.ModelVersion == "V6.5")
        .OrderBy(record => record.Id)
        .ToList();
    Assert(rows.Count == 2, "initialization physically deleted an older duplicate prediction snapshot");
    Assert(rows[0].PredictZodiac == "鼠" && rows[1].PredictZodiac == "牛",
        "initialization changed the order or content of archived prediction snapshots");
}

void ConcurrentPredictionSavesAreAtomicAndIdempotent()
{
    const string issue = "998020";
    using (var cleanup = new System.Data.SQLite.SQLiteConnection(
        $"Data Source={DatabaseHelper.DatabasePath};Version=3;"))
    {
        cleanup.Open();
        using var command = new System.Data.SQLite.SQLiteCommand(
            "DELETE FROM PredictionHistory WHERE Issue=@Issue", cleanup);
        command.Parameters.AddWithValue("@Issue", issue);
        command.ExecuteNonQuery();
    }

    using var gate = new ManualResetEventSlim(false);
    var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
    Task[] writers = Enumerable.Range(1, 24).Select(index => Task.Run(() =>
    {
        gate.Wait();
        try
        {
            DatabaseHelper.SavePrediction(issue, "鼠,牛,虎", "鼠,牛,虎,兔,龙,蛇", index.ToString("D2"),
                "V6.5", 100, $"concurrent snapshot {index}");
        }
        catch (Exception error)
        {
            errors.Enqueue(error);
        }
    })).ToArray();
    gate.Set();
    Task.WaitAll(writers);

    Assert(errors.IsEmpty, $"concurrent prediction save failed: {errors.FirstOrDefault()?.Message}");
    int saved = DatabaseHelper.GetPredictionHistory(int.MaxValue)
        .Count(record => record.Issue == issue && record.AnalysisPeriods == 100 && record.ModelVersion == "V6.5");
    Assert(saved == 1, $"concurrent prediction save created {saved} snapshots instead of one");
}

void ConcurrentDatabaseBackupsPublishOneValidSnapshot()
{
    string backupPath = Path.Combine(testData, "Backup", $"{DateTime.Now:yyyyMMdd}.db");
    if (File.Exists(backupPath)) File.Delete(backupPath);

    using var gate = new ManualResetEventSlim(false);
    Task<string>[] writers = Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
    {
        gate.Wait();
        return DatabaseBackupService.Backup();
    })).ToArray();
    gate.Set();
    Task.WaitAll(writers);

    Assert(File.Exists(backupPath), "concurrent startup removed the successfully published daily backup");
    Assert(writers.All(writer => !writer.Result.Contains("失败", StringComparison.Ordinal)),
        "one concurrent backup reported a failure instead of accepting the already-published snapshot");
    using var connection = new System.Data.SQLite.SQLiteConnection(
        $"Data Source={backupPath};Version=3;Read Only=True;");
    connection.Open();
    using var check = new System.Data.SQLite.SQLiteCommand("PRAGMA quick_check", connection);
    Assert(string.Equals(Convert.ToString(check.ExecuteScalar()), "ok", StringComparison.OrdinalIgnoreCase),
        "concurrent startup left a corrupt daily backup");
    Assert(!Directory.EnumerateFiles(Path.Combine(testData, "Backup"), ".*.tmp*").Any(),
        "backup publication left temporary SQLite WAL/SHM files behind");
}

void AmbiguousLegacyMigrationNeverCreatesEmptyStableDatabase()
{
    string fixtureDirectory = Path.Combine(testData, "promotion-ambiguous-fixture");
    Directory.CreateDirectory(fixtureDirectory);
    string stable = Path.Combine(fixtureDirectory, "stable.db");
    string first = Path.Combine(fixtureDirectory, "first.db");
    string second = Path.Combine(fixtureDirectory, "second.db");
    if (File.Exists(stable)) File.Delete(stable);
    CreateMigrationFixture(first, historyRows: 1, predictionRows: 1);
    CreateMigrationFixture(second, historyRows: 1, predictionRows: 1);

    var migrate = typeof(DatabaseHelper).GetMethod("PromoteLegacyDatabaseOrThrow",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
    Assert(migrate is not null, "safe legacy migration coordinator is missing");
    bool rejected = false;
    try
    {
        migrate!.Invoke(null, new object[] { stable, new[] { first, second } });
    }
    catch (System.Reflection.TargetInvocationException error)
        when (error.InnerException is InvalidOperationException)
    {
        rejected = true;
    }

    Assert(rejected, "ambiguous legacy databases were silently accepted");
    Assert(!File.Exists(stable), "ambiguous migration created an empty stable database and blocked future retries");
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
            ["V7"] = -2
        }, "test"));

    Assert(Math.Abs(next.AI - current.AI) <= 0.050000001, "AI weight changed by more than five points");
    Assert(Math.Abs(next.ML - current.ML) <= 0.050000001, "ML weight changed by more than five points");
    Assert(new[] { next.AI, next.ML, next.State, next.V7 }.All(value => value >= 0 && value <= 0.70),
        "a model weight escaped the 0-70% range");
    Assert(Math.Abs(next.Sum - 1.0) < 0.000000001, "model weights no longer sum to 100%");
}

void MetaRankingIsSafeAndNormalized()
{
    string[] zodiacs = { "Rat", "Ox", "Tiger", "Rabbit", "Dragon", "Snake", "Horse", "Goat", "Monkey", "Rooster", "Dog", "Pig" };
    var input = new MetaPredictionInput("2027001", zodiacs.Select((zodiac, index) =>
        new ZodiacMetaFeatures(zodiac,
            new Dictionary<string, double> { ["AI"] = 12-index, ["ML"] = 12-index, ["State"] = index, ["V7"] = 0 },
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
        new Dictionary<string, int> { ["AI"] = actualRank, ["ML"] = Math.Min(12, actualRank + 1), ["State"] = Math.Max(1, actualRank - 1), ["V7"] = actualRank },
        new Dictionary<string, double> { ["frequency"] = -0.4, ["omission"] = 0.2 });
}

void PredictionFeedbackIsPersistedExactlyOnce()
{
    string[] zodiacs = { "Rat", "Ox", "Tiger", "Rabbit", "Dragon", "Snake", "Horse", "Goat", "Monkey", "Rooster", "Dog", "Pig" };
    var input = new MetaPredictionInput("999101", zodiacs.Select((zodiac,index) =>
        new ZodiacMetaFeatures(zodiac,
            new Dictionary<string,double> { ["AI"]=12-index, ["ML"]=index, ["State"]=6, ["V7"]=0 },
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
        .Single(item => item.Issue == "999202" && item.ModelVersion == "V7 AutoLearning");
    Assert(record.ScoreDetails.Contains("波色学习:"), "color learning snapshot was not saved in prediction history");
    ColorLearningOutcome first = DatabaseHelper.ApplyColorLearningForPrediction(record.Id, "01");
    ColorLearningOutcome second = DatabaseHelper.ApplyColorLearningForPrediction(record.Id, "01");
    Assert(first.Updated && !second.Updated, "online color feedback was not persisted exactly once");
}

void PredictionTraceIsImmutableAndIsolated()
{
    const string issue = "999801";
    int before = DatabaseHelper.GetPredictionHistory(int.MaxValue).Count;
    PredictionTraceSnapshot original = TraceFixture(issue, cutoffIssue: "999800", frequencyRaw: 41);

    PredictionTraceService.SaveLive(original);
    PredictionTraceSnapshot saved = PredictionTraceService.GetLive(issue)
        ?? throw new InvalidOperationException("未读取到刚写入的 Trace");

    Assert(saved.HistoryCutoffIssue == "999800" && saved.HistorySampleCount == 50,
        "Trace 没有保存真实历史边界");
    Assert(saved.BaseModels.Count == 3 && saved.BaseModels.All(model => model.Ranking.Count == 12),
        "Trace 没有保存三基础模型的完整12生肖排序");
    Assert(saved.BaseModels[0].Ranking[0].Factors["F"].Raw == 41 &&
        Math.Abs(saved.BaseModels[0].Ranking[0].Factors["F"].Contribution - 6.56) < 0.000001,
        "Trace 没有保存因子原始值和实际贡献");
    Assert(saved.AutoLearning.Zodiacs.Count == 12 && saved.AutoLearning.Weights.ContainsKey("V7"),
        "Trace 没有保存 AutoLearning 输入与权重快照");
    Assert(DatabaseHelper.GetPredictionHistory(int.MaxValue).Count == before,
        "旁路 Trace 不得写入正式 PredictionHistory");

    AssertThrows<InvalidOperationException>(() =>
        PredictionTraceService.SaveLive(TraceFixture(issue, cutoffIssue: "999800", frequencyRaw: 42)),
        "同一期不同内容的 Trace 不得覆盖原始快照");
    PredictionTraceSnapshot unchanged = PredictionTraceService.GetLive(issue)
        ?? throw new InvalidOperationException("冲突写入后 Trace 丢失");
    Assert(unchanged.BaseModels[0].Ranking[0].Factors["F"].Raw == 41,
        "冲突 Trace 覆盖了原始快照");
}

void FormalPredictionTraceCapturesLiveAndOutcome()
{
    const string issue = "999802";
    int before = DatabaseHelper.GetPredictionHistory(int.MaxValue).Count;
    AIEngine.PredictResult[] baseModels = new[] { 50, 100, AISettings.AllHistoryModeValue }
        .Select((period, modelIndex) => FormalTracePrediction(period, modelIndex)).ToArray();
    AutoLearningSnapshot auto = FormalTraceAutoLearning(issue);

    PredictionTraceService.CaptureLive(issue, "999801", 50, baseModels, auto, "test-commit");
    PredictionTraceSnapshot trace = PredictionTraceService.GetLive(issue)
        ?? throw new InvalidOperationException("未捕获正式四模型 Trace");
    Assert(trace.BaseModels.Select(model => model.ModelKey).OrderBy(key => key).SequenceEqual(
            new[] { "v65-50", "v65-100", "v65-all" }.OrderBy(key => key)),
        "Trace 没有使用正式三基础模型身份");
    Assert(trace.BaseModels.All(model => model.Ranking.Count == 12) &&
        trace.BaseModels[0].Ranking[0].Factors["F"].Contribution > 0,
        "Trace 没有保存正式基础模型因子贡献");
    Assert(trace.AutoLearning.Zodiacs.Count == 12 && trace.AutoLearning.Zodiacs[0].Rank50 is > 0 and <= 12 &&
        trace.AutoLearning.Weights.ContainsKey("AI"), "Trace 没有保存正式 AutoLearning 输入");
    Assert(DatabaseHelper.GetPredictionHistory(int.MaxValue).Count == before,
        "旁路捕获不得创建或修改正式 PredictionHistory");

    PredictionTraceService.RecordLiveOutcome(issue, "马", "07",
        new PredictionTraceLearningState(new Dictionary<string, double> { ["AI"] = .4, ["ML"] = .4, ["State"] = .2, ["V7"] = 0 },
            new Dictionary<string, double> { ["model_consensus"] = .1 }),
        new PredictionTraceLearningState(new Dictionary<string, double> { ["AI"] = .35, ["ML"] = .4, ["State"] = .2, ["V7"] = .05 },
            new Dictionary<string, double> { ["model_consensus"] = .12 }), true);
    PredictionTraceOutcome outcome = PredictionTraceService.GetLiveOutcome(issue)
        ?? throw new InvalidOperationException("未保存开奖后的旁路结果");
    Assert(outcome.ActualZodiac == "马" && outcome.AutoRank is > 0 and <= 12 &&
        outcome.BaseRanks.Count == 3 && outcome.Top6Hit == (outcome.AutoRank <= 6) && outcome.WeightUpdateTriggered,
        "开奖结果没有保存真实名次、命中和学习前后状态");
}

void FormalPredictionTraceAcceptsDynamicAllHistoryPeriod()
{
    const string issue = "999803";
    AIEngine.PredictResult[] baseModels = new[] { 50, 100, 1323 }
        .Select((period, modelIndex) => FormalTracePrediction(period, modelIndex)).ToArray();

    PredictionTraceService.CaptureLive(issue, "999802", 1323, baseModels,
        FormalTraceAutoLearning(issue), "test-commit");
    PredictionTraceSnapshot trace = PredictionTraceService.GetLive(issue)
        ?? throw new InvalidOperationException("未捕获动态全历史 Trace");
    PredictionTraceBaseModel allHistory = trace.BaseModels.Single(model => model.ModelKey == ExperimentModels.AllHistory);
    Assert(allHistory.AnalysisPeriods == 1323 && Math.Abs(allHistory.Weights["P"] - .34) < .000001,
        "动态全历史 Trace 没有使用全部历史模型权重");
}

void AutoLearningV2SnapshotAndResidualAreIsolated()
{
    const string issue = "999803";
    int before = DatabaseHelper.GetPredictionHistory(int.MaxValue).Count;
    PredictionTraceSnapshot trace = TraceFixture(issue, "999802", 41);
    AutoLearningV2Snapshot snapshot = AutoLearningV2Service.BuildSnapshot(trace,
        new AutoLearningV2HistoryFeatures(18, 50, .42, .48, .14, .23));
    Assert(snapshot.Zodiacs.Count == 12 && snapshot.Zodiacs[0].RankMean > 0 && snapshot.Zodiacs[0].RankStd >= 0,
        "V2 没有保存完整三模型分歧特征");
    Assert(snapshot.Zodiacs[0].FactorFeatures.ContainsKey("F_mean") &&
        snapshot.Zodiacs[0].FactorFeatures.ContainsKey("B_std"),
        "V2 没有保存底层因子均值和标准差");
    Assert(snapshot.Config.Lambda is .05 or .10 or .15 && snapshot.Zodiacs.All(row => row.Rank is >= 1 and <= 12),
        "V2 残差参数或完整排序无效");
    Assert(snapshot.Zodiacs.Any(row => Math.Abs(row.FinalScore - row.BaseScore) > 0.000001),
        "V2 没有产生可解释的残差修正");
    Assert(snapshot.Zodiacs.All(row => row.Explanation.MaxPositiveFeature is not null),
        "V2 缺少最大正贡献解释");
    Assert(snapshot.Confidence is "High" or "Medium" or "Low" && snapshot.JointFailureRisk is >= 0 and <= 1,
        "V2 Confidence 或 JointFailureRisk 越界");
    Assert(DatabaseHelper.GetPredictionHistory(int.MaxValue).Count == before,
        "V2 不得写入正式 PredictionHistory");

    AutoLearningV2State state = new();
    ModelWeights oldWeights = state.Weights;
    state = AutoLearningV2Service.UpdateState(state, snapshot, actualZodiac: "鼠");
    Assert(Math.Abs(state.Weights.AI - oldWeights.AI) <= .02 && state.Weights.Sum > .999 && state.Weights.Sum < 1.001,
        "V2 单期权重变化或归一化不符合边界");
    Assert(state.ObservedSamples == 1 && state.Decay == .98, "V2 没有执行单期更新和默认衰减");
}

void AutoLearningV2IndependentSignalAudit()
{
    var provider = new AutoLearningV2TestSignalProvider("test-signal", "v1", "999803",
        new[] { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" });
    IndependentSignalSnapshot accepted = AutoLearningV2SignalAudit.Validate(provider, "999803", "999802");
    Assert(accepted.LeakageAuditPassed && accepted.Ranking.Count == 12, "合法独立信号没有通过审计");

    var future = new AutoLearningV2TestSignalProvider("future", "v1", "999804",
        new[] { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" });
    AssertThrows<InvalidDataException>(() => AutoLearningV2SignalAudit.Validate(future, "999803", "999802"),
        "预测期之后生成的独立信号必须被拒绝");
}

void AutoLearningV2WalkForwardMetricsAreLeakageSafe()
{
    var rows = Enumerable.Range(1, 16).Select(index => new AutoLearningV2EvaluationRow(
        index.ToString("D4"), index % 2 == 0 ? "鼠" : "牛",
        new[] { "鼠", "牛", "虎", "兔", "龙", "蛇" },
        new[] { "牛", "鼠", "虎", "兔", "龙", "蛇" },
        new[] { "虎", "龙", "马", "羊", "猴", "鸡" })).ToArray();
    AutoLearningV2EvaluationReport report = AutoLearningV2WalkForward.Evaluate(rows, 8);
    Assert(report.TestSamples == 8 && report.RescueCount >= 0 && report.HarmCount >= 0,
        "WalkForward 没有按时间切分并统计 Rescue/Harm");
    Assert(report.HoldoutIssue == "0008" && !report.FutureDataLeakageDetected,
        "WalkForward 的 holdout 边界或泄漏标记错误");
}

void AutoLearningV2ReportIsExplicitlyExperimental()
{
    var rows = Enumerable.Range(1, 16).Select(index => new AutoLearningV2EvaluationRow(
        index.ToString("D4"), index % 2 == 0 ? "鼠" : "牛",
        new[] { "鼠", "牛", "虎", "兔", "龙", "蛇" },
        new[] { "牛", "鼠", "虎", "兔", "龙", "蛇" },
        new[] { "虎", "龙", "马", "羊", "猴", "鸡" })).ToArray();
    string report = AutoLearningV2ReportService.Render(AutoLearningV2WalkForward.Evaluate(rows, 8), "test-code", ".10", ".98");
    Assert(report.Contains("AutoLearning V2 旁路实验报告") && report.Contains("Holdout") &&
        report.Contains("不自动替换正式 AutoLearning") && report.Contains("RescueRate") && report.Contains("HarmRate"),
        "V2 报告缺少实验边界或核心指标");
}

void AutoLearningV2ExperimentStorageIsIsolated()
{
    string runId = "v2-test-run";
    AutoLearningV2ExperimentService.SaveRun(new AutoLearningV2ExperimentRun(runId, "test-code", ".10", ".98", "0001", "0008", "0009", "0016"));
    AutoLearningV2ExperimentService.SavePrediction(runId, new AutoLearningV2ExperimentPrediction("0009", "鼠,牛,虎", 2, .41, .03, .44, "Medium"));
    AutoLearningV2ExperimentRun run = AutoLearningV2ExperimentService.GetRun(runId) ?? throw new InvalidOperationException("V2 实验运行未保存");
    Assert(run.ModelKey == AutoLearningV2Service.ModelKey && AutoLearningV2ExperimentService.GetPredictionCount(runId) == 1,
        "V2 实验记录未写入独立存储");
    Assert(!DatabaseHelper.GetPredictionHistory(int.MaxValue).Any(row => row.Issue == "0009" && row.ModelVersion == AutoLearningV2Service.ModelKey),
        "V2 实验不能写入正式 PredictionHistory");
}

void CloudPredictionArchiveKeepsFullLocalSnapshots()
{
    string[] zodiacs = { "虎", "猴", "鼠", "兔", "马", "龙", "牛", "蛇", "羊", "鸡", "狗", "猪" };
    CloudDailyPrediction prediction = new()
    {
        Issue = 2026226,
        GeneratedAt = "2026-08-15T09:00:00+08:00",
        ModelVersion = AIEngine.Version,
        Status = "success",
        AiZodiac = new Dictionary<string, CloudAiPrediction>
        {
            ["50"] = new CloudAiPrediction
            {
                AnalysisPeriods = 50,
                Top3 = new() { "虎", "猴", "鼠" },
                Top6 = new() { "虎", "猴", "鼠", "兔", "马", "龙" },
                Ranking = zodiacs.Select((zodiac, index) => new CloudZodiacSnapshot { Zodiac = zodiac, Rank = index + 1, TotalScore = 92 - index }).ToList(),
                FactorScores = zodiacs.ToDictionary(zodiac => zodiac, zodiac => new CloudFactorSnapshot { Frequency = 20, Trend = 18, Omission = 15, HotCold = 14, Period = 16, Consecutive = 0, EightZodiac = 2 }),
                FinalRankingJson = "[\"虎\",\"猴\",\"鼠\"]",
                BaseModelScoresJson = "{\"虎\":92}",
                FeatureSnapshotJson = "{\"schema\":\"full-local-v1\"}",
                WeightSnapshotJson = "{\"model\":\"V65RuleScoringEngine\"}"
            }
        }
    };
    Assert(CloudPredictionSyncService.HasCompleteLocalEquivalent(prediction),
        "云端预测档案没有本地同等的完整排名和因子快照");
    Assert(CloudPredictionSyncService.ImportPrediction(prediction) == 1,
        "完整云端预测档案应写入正式 PredictionHistory");
    var imported = DatabaseHelper.GetPredictionHistory(int.MaxValue)
        .Single(row => row.Issue == "2026226" && row.AnalysisPeriods == 50);
    Assert(imported.PredictionSource == "云端同步" && imported.Top6Zodiac == "虎,猴,鼠,兔,马,龙",
        "云端完整预测档案没有保留来源和前六排名");
}

void SymmetricModelStateSnapshotDetectsConflicts()
{
    var state = new SymmetricModelStateSnapshot("V6.5", "test-code", "20260815T010000Z",
        new[] { new SymmetricPredictionSnapshot("2026226", "V6.5", 50, "虎,猴,鼠", "虎,猴,鼠,兔,马,龙", "scores-v1") },
        new[] { new SymmetricLearningEvent("2026225", "V6.5 AutoLearning", "before-hash", "马", "after-hash") });
    string hash1 = SymmetricModelSync.CanonicalHash(state);
    string hash2 = SymmetricModelSync.CanonicalHash(JsonSerializer.Deserialize<SymmetricModelStateSnapshot>(
        JsonSerializer.Serialize(state))!);
    Assert(hash1 == hash2 && hash1.Length == 64, "同构状态快照哈希不稳定");
    AssertThrows<InvalidDataException>(() => SymmetricModelSync.ValidateEventConflict(
        state.LearningEvents, new SymmetricLearningEvent("2026225", "V6.5 AutoLearning", "different", "马", "after-hash")),
        "同一期不同学习事件必须拒绝合并");
}

void CloudWorkflowPublishesSymmetricRuntimeState()
{
    string workflow = File.ReadAllText(Path.Combine(ProjectRoot(), ".github", "workflows", "run-prediction.yml"));
    Assert(workflow.Contains("runtime-state.json", StringComparison.Ordinal),
        "云端 workflow 没有生成或发布同构运行状态");
    Assert(workflow.Contains("modelState", StringComparison.Ordinal),
        "云端发布 payload 没有包含模型运行状态");
}

void DesktopSyncReadsSymmetricRuntimeState()
{
    string source = File.ReadAllText(Path.Combine(ProjectRoot(), "CloudPredictionSyncService.cs"));
    Assert(source.Contains("runtime-state", StringComparison.Ordinal),
        "桌面同步没有读取云端同构运行状态");
    Assert(source.Contains("SymmetricRuntimeStateSync.MergeIntoLocal", StringComparison.Ordinal),
        "桌面同步没有合并同构运行状态");
}

void SymmetricStateConflictDoesNotPartiallyMerge()
{
    string[] zodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
    var existing = new DatabaseHelper.PredictionRecord
    {
        Issue = "999901", AnalysisPeriods = 50, ModelVersion = "V6.5", PredictZodiac = "鼠",
        Top6Zodiac = "鼠,牛,虎,兔,龙,蛇", HitResult = "未开奖", Top6HitResult = "未开奖"
    };
    DatabaseHelper.MergeSynchronizedPrediction(existing);
    var conflicting = new DatabaseHelper.PredictionRecord
    {
        Issue = existing.Issue, AnalysisPeriods = existing.AnalysisPeriods, ModelVersion = existing.ModelVersion,
        PredictZodiac = "牛", Top6Zodiac = existing.Top6Zodiac, HitResult = existing.HitResult,
        Top6HitResult = existing.Top6HitResult
    };
    var incoming = new SymmetricRuntimeStateSnapshot("v1", AIEngine.Version, "test-code",
        new[] { conflicting }, new Dictionary<string, string>(), "", "2026-08-15T00:00:00Z");
    incoming = incoming with { StateHash = SymmetricRuntimeStateSync.Hash(incoming) };
    Assert(SymmetricRuntimeStateSync.MergeIntoLocal(incoming) == 0,
        "冲突预测行应跳过而不是写入");
    Assert(DatabaseHelper.GetPredictionHistory(int.MaxValue).Count(row => row.Issue == "999901") == 1,
        "冲突状态不应产生部分写入");
    var local = DatabaseHelper.GetPredictionHistory(int.MaxValue)
        .Single(row => row.Issue == "999901" && row.AnalysisPeriods == 50 && row.ModelVersion == "V6.5");
    Assert(local.PredictZodiac == "鼠", "冲突时本地首次快照必须保留");

    string memoryKey = ExperimentModels.MemoryKey("conflict-test");
    DatabaseHelper.SaveModelMemoryJson(memoryKey, "{\"LearnedSamples\":1}");
    var cloudOnly = new DatabaseHelper.PredictionRecord
    {
        Issue = "999904", AnalysisPeriods = 100, ModelVersion = "V6.5",
        PredictZodiac = "马,羊,猴", Top6Zodiac = "马,羊,猴,鸡,狗,猪",
        HitResult = "未开奖", Top6HitResult = "未开奖"
    };
    var memoryConflict = new SymmetricRuntimeStateSnapshot("v1", AIEngine.Version, "test-code",
        new[] { cloudOnly },
        new Dictionary<string, string> { [memoryKey] = "{\"LearnedSamples\":2}" },
        "", "2026-08-15T00:00:00Z");
    memoryConflict = memoryConflict with { StateHash = SymmetricRuntimeStateSync.Hash(memoryConflict) };
    Assert(SymmetricRuntimeStateSync.MergeIntoLocal(memoryConflict) == 1,
        "模型记忆冲突不应阻断缺失预测记录导入");
    Assert(DatabaseHelper.GetPredictionHistory(int.MaxValue)
        .Any(row => row.Issue == "999904" && row.AnalysisPeriods == 100 && row.ModelVersion == "V6.5"),
        "记忆冲突时云端缺失预测记录没有导入");
    Assert(DatabaseHelper.LoadModelMemoryJson(memoryKey) == "{\"LearnedSamples\":1}",
        "冲突时必须保留本机模型记忆");
}

void V7HistoryStoresCompleteRanking()
{
    var record = V7PredictionHistoryService.GetHistory(100)
        .Single(item => item.Issue == "103" && item.ModelVersion == "V7 AutoLearning");
    var ranking = JsonSerializer.Deserialize<string[]>(record.FinalRankingJson);
    Assert(ranking is { Length: 12 } && ranking.Distinct().Count() == 12,
        "V7自动学习记录必须保存完整12生肖排序");
}

void V7LearningUsesIndependentMemory()
{
    var record = V7PredictionHistoryService.GetHistory(100)
        .Single(item => item.Issue == "103" && item.ModelVersion == "V7 AutoLearning");
    string actual = JsonSerializer.Deserialize<string[]>(record.FinalRankingJson)![0];
    string v65MemoryKey = new ModelMemory(ExperimentModels.AutoLearning).MemoryKey;
    string? v65Before = DatabaseHelper.LoadModelMemoryJson(v65MemoryKey);
    LearningOutcome outcome = DatabaseHelper.ApplyAutomaticLearningForPrediction(record.Id, actual);
    Assert(outcome.Updated, "V7自动学习反馈没有更新");
    string? v7Memory = DatabaseHelper.LoadModelMemoryJson(new ModelMemory(ExperimentModels.IntelligentHistory).MemoryKey);
    string? v65Memory = DatabaseHelper.LoadModelMemoryJson(v65MemoryKey);
    Assert(!string.IsNullOrWhiteSpace(v7Memory), "V7自动学习没有写入独立记忆库");
    Assert(v65Memory == v65Before, "V7自动学习错误写入或篡改V6.5记忆库");
}

void V7ReviewUsesV7Snapshot()
{
    string review = V7PredictionReviewService.BuildReview(
        "鸡:0.1538;马:0.1410;虎:0.1282;蛇:0.1154;龙:0.1026;鼠:0.0897;牛:0.0769;猪:0.0641;羊:0.0513;猴:0.0385;狗:0.0256;兔:0.0128",
        "鸡,马,虎", "兔");
    Assert(review.Contains("排名第12", StringComparison.Ordinal) &&
           review.Contains("V7", StringComparison.Ordinal),
        "V7错因解释没有使用V7概率排序");
}

void PredictionHistoryPreservesPredictionSource()
{
    DatabaseHelper.SavePrediction("999902", "鼠", "鼠,牛,虎,兔,龙,蛇", "01", "V6.5", 50, "local-test");
    DatabaseHelper.PredictionRecord local = DatabaseHelper.GetPredictionHistory(int.MaxValue)
        .Single(row => row.Issue == "999902");
    Assert(local.PredictionSource == "本地生成", "本地预测没有标记来源");

    DatabaseHelper.PredictionRecord cloud = new()
    {
        Issue = "999903", PredictTime = "2026-08-15 10:00:00", PredictNumber = "02",
        PredictZodiac = "牛", Top6Zodiac = "牛,鼠,虎,兔,龙,蛇", AnalysisPeriods = 50,
        ScoreDetails = "cloud-test", ModelVersion = "V6.5", HitResult = "未开奖",
        Top6HitResult = "未开奖", PredictionSource = "云端同步"
    };
    Assert(DatabaseHelper.MergeSynchronizedPrediction(cloud) == 1, "云端记录没有写入");
    Assert(DatabaseHelper.GetPredictionHistory(int.MaxValue).Single(row => row.Issue == "999903").PredictionSource == "云端同步",
        "云端同步记录没有保留来源");
}

void HistoricalReplayContractsAreEnforced()
{
    var history = Enumerable.Range(1, 112).Select(index => new DatabaseHelper.HistoryRecord
    {
        Period = (2026000 + index).ToString(), SpecialZodiac = new[] { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" }[index % 12],
        SpecialNumber = "01", Numbers = "01,02,03,04,05,06"
    }).ToArray();
    string store = Path.Combine(Path.GetTempPath(), "liuhe-replay-contract-" + Guid.NewGuid().ToString("N") + ".db");
    HistoricalReplayResult replay = new HistoricalReplayEngine().Run(history, new HistoricalReplayOptions(100, "contract-run", store));
    Assert(replay.TargetIssues.Count == 12, "重放评估期号数量错误");
    Assert(replay.Predictions.GroupBy(row => row.TargetIssue).All(group =>
        group.All(row => row.HistorySampleCount == int.Parse(group.Key) - 2026001)), "快照未记录真实 cutoff 样本数");
    Assert(replay.Predictions.All(row => long.Parse(row.HistoryCutoffIssue) < long.Parse(row.TargetIssue)), "存在未来 cutoff");
    Assert(replay.Predictions.Select(row => row.TargetIssue).Distinct().Count() == 12, "目标期集合不一致");
    EvaluationReport report = EvaluationPipeline.Evaluate(replay.Predictions);
    Assert(report.CommonEvaluationSet.Count == 12 && report.MissingPredictionCount == 0, "共同评估集合不完整");
    Assert(!replay.FutureDataLeakageDetected, "正常重放被错误标记为泄漏");
    Assert(report.RescueHarm.Count == 1 && report.Bootstrap95.Count > 0, "Rescue/Harm 或 Bootstrap 评估未生成");
    Assert(report.RandomMonteCarlo.Iterations == 10000 && report.PairedComparisons.Count > 0 && report.McNemar.Count > 0, "随机基准或配对评估未生成");
    Assert(report.Relationships.Count > 0 && report.RankChanges.Count == 1 && report.ConsensusBins.Count == 5 && report.JointFailureRiskBins.Count == 3 && report.ConfidenceGroups.Count == 3, "相关性、排名变化或分箱评估未生成");
    Assert(File.Exists(store), "实验快照没有写入独立存储");
    using var connection = new System.Data.SQLite.SQLiteConnection($"Data Source={store};Version=3;Read Only=True;");
    connection.Open();
    using var production = new System.Data.SQLite.SQLiteCommand("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='PredictionHistory'", connection);
    Assert(Convert.ToInt32(production.ExecuteScalar()) == 0, "实验库不应创建生产 PredictionHistory 表");
}

void CandidateStage2ContractsAreEnforced()
{
    var history = Enumerable.Range(1, 112).Select(index => new DatabaseHelper.HistoryRecord
    {
        Period = (2026000 + index).ToString(), SpecialZodiac = new[] { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" }[index % 12], SpecialNumber = "01", Numbers = "01,02,03,04,05,06"
    }).ToArray();
    string store = Path.Combine(Path.GetTempPath(), "candidate-stage2-contract-" + Guid.NewGuid().ToString("N") + ".db");
    var result = new CandidateStage2ReplayEngine().Run(history, store);
    Assert(result.Candidates.Count > 0 && result.Controls.Count > 0, "Candidate 旁路没有生成快照");
    Assert(result.Candidates.All(x => long.Parse(x.HistoryCutoffIssue) < long.Parse(x.TargetIssue) && x.LeakageAuditPassed), "Candidate cutoff 或泄漏审计失败");
    var report = CandidateStage2Evaluation.Evaluate(result.Candidates, result.Controls, result.ExperimentId, store, 6501, 100);
    Assert(report.Performance.Count >= 4 && report.Rescue.Count >= 4 && !report.LeakageDetected,
        "Candidate Stage 2 评估不完整");
    Assert(File.Exists(store), "Candidate 实验快照没有写入独立库");
}

string ProjectRoot()
{
    // 测试既可能从 Tests/bin 运行，也可能因项目引用而从主程序 bin 运行；
    // 不能假定固定的 ".." 层数，否则云端工作流会在生成预测后找错源码目录。
    foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "六合分析软件.csproj")))
                return directory.FullName;
        }
    }
    throw new DirectoryNotFoundException("未找到六合分析软件项目根目录");
}

void PredictionRunnerDoesNotReferenceRetiredIndependentLearning()
{
    string runner = File.ReadAllText(Path.Combine(ProjectRoot(), "PredictionRunner", "Program.cs"));
    Assert(!runner.Contains("IndependentLearningHistoricalTraining", StringComparison.Ordinal),
        "PredictionRunner 仍引用已删除的 IndependentLearningHistoricalTraining，导致所有运行器命令无法编译");
    Assert(!runner.Contains("IndependentLearningModel", StringComparison.Ordinal),
        "PredictionRunner 仍引用已删除的 IndependentLearningModel，导致所有运行器命令无法编译");
    Assert(!runner.Contains("IndependentLearningDaily", StringComparison.Ordinal),
        "PredictionRunner 仍引用已删除的 IndependentLearningDaily，导致所有运行器命令无法编译");
}

AIEngine.PredictResult FormalTracePrediction(int periods, int modelIndex)
{
    string[] zodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
    var scores = zodiacs.Select((zodiac, index) => new V65RuleScoringEngine.ZodiacScoreV2
    {
        Zodiac = zodiac, TotalScore = 120 - index - modelIndex,
        FrequencyScore = 60 - index, RecentTrendScore = 50 - index, OmissionScore = 40 - index,
        HotColdScore = 30 - index, PeriodPatternScore = 20 - index, ConsecutiveScore = 10 - index,
        EightZodiacScore = index == 0 ? 2 : 0, TotalAppear = 10 - index, CurrentOmission = index
    }).ToList();
    return new AIEngine.PredictResult
    {
        AnalysisPeriods = periods, Version = AIEngine.Version, PredictPeriod = "999802", PredictTime = DateTime.Now,
        AllScores = scores, Top3 = scores.Take(3).Select(item => item.Zodiac).ToList(),
        Top6 = scores.Take(6).Select(item => item.Zodiac).ToList()
    };
}

AutoLearningSnapshot FormalTraceAutoLearning(string issue)
{
    string[] zodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
    var input = new MetaPredictionInput(issue, zodiacs.Select((zodiac, index) => new ZodiacMetaFeatures(zodiac,
        new Dictionary<string, double> { ["AI"] = (12 - index) / 12d, ["ML"] = (11 - index) / 12d,
            ["State"] = (10 - index) / 12d, ["V7"] = index == 0 ? 1 : 0 },
        new Dictionary<string, double> { ["model_consensus"] = index == 0 ? 1 : .2 })).ToArray());
    var result = new MetaPredictionResult(zodiacs.Select((zodiac, index) => new RankedZodiac(zodiac,
        (12 - index) / 78d, index + 1)).ToArray(), false, "");
    return new AutoLearningSnapshot(input, zodiacs, result,
        new ModelWeights(.4, .4, .2, 0));
}

PredictionTraceSnapshot TraceFixture(string issue, string cutoffIssue, double frequencyRaw)
{
    string[] zodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
    var models = new[] { ("v65-50", .16), ("v65-100", .24), ("v65-all", .17) }
        .Select(model => new PredictionTraceBaseModel(
            model.Item1, model.Item1, 50,
            new Dictionary<string, double> { ["F"] = model.Item2, ["T"] = .16, ["O"] = .20, ["H"] = .16, ["P"] = .32, ["C"] = 0 },
            zodiacs.Select((zodiac, index) => new PredictionTraceZodiac(
                zodiac, index + 1, 50 - index,
                new Dictionary<string, PredictionTraceFactor>(StringComparer.Ordinal)
                {
                    ["F"] = new(frequencyRaw, frequencyRaw * model.Item2),
                    ["T"] = new(50, 8), ["O"] = new(50, 10), ["H"] = new(50, 8),
                    ["P"] = new(50, 16), ["C"] = new(50, 0), ["B"] = new(2, 2)
                })).ToArray())).ToArray();
    var autoRows = zodiacs.Select((zodiac, index) => new PredictionTraceAutoZodiac(
        zodiac, index + 1, index + 1, index + 1, index + 1,
        (12 - index) / 12d, (12 - index) / 12d, (12 - index) / 12d,
        index == 0 ? 1 : 0, index == 0 ? 1 : 0, 0.2 - index * .01, 1d / 12)).ToArray();
    return new PredictionTraceSnapshot(issue, "Live", "trace-v1", DateTimeOffset.Parse("2026-08-14T00:00:00Z"),
        cutoffIssue, 50, "V6.5", "test-commit", "Complete", models,
        new PredictionTraceAutoLearning(autoRows,
            new Dictionary<string, double> { ["AI"] = .1, ["ML"] = .2, ["State"] = .2, ["V7"] = .5 },
            new Dictionary<string, double> { ["model_consensus"] = -.1 }, false, ""));
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

sealed record AutoLearningV2TestSignalProvider(string SourceName, string ModelVersion, string GeneratedForIssue,
    IReadOnlyList<string> Ranking) : IIndependentSignalProvider
{
    public IndependentSignalSnapshot GetSnapshot(string issue, IReadOnlyList<DatabaseHelper.HistoryRecord> prefix) =>
        new(SourceName, ModelVersion, GeneratedForIssue, Ranking, true);
}
