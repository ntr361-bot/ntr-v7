using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace 六合分析软件;

public sealed record CloudSyncResult(
    string LatestDrawIssue,
    long LatestPredictionIssue,
    int NewDrawCount,
    int PredictionFileCount,
    int PredictionRowCount);

public static class CloudPredictionSyncService
{
    private const string MachineSyncUrl = "https://v6-sync-ingress-2026.ntr133.chatgpt.site/api/sync/desktop";
    private static readonly HttpClient Client = CreateClient();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly JsonSerializerOptions RuntimeStateOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<CloudSyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        int newDraws = await SyncHistoryAsync(cancellationToken);
        CloudManifest manifest = await DownloadAsync<CloudManifest>(
            "manifest",
            cancellationToken);
        if (manifest.Status != "success" || manifest.Records.Count == 0)
            throw new InvalidDataException("云端预测清单为空");

        int files = 0;
        int rows = 0;
        foreach (string fileName in manifest.Records)
        {
            if (!IsSafePredictionFile(fileName))
                throw new InvalidDataException($"云端预测文件名无效：{fileName}");
            string localFile = Path.Combine(AppPaths.CloudPredictionDirectory, fileName);
            CloudDailyPrediction prediction;
            try
            {
                prediction = await DownloadAsync<CloudDailyPrediction>(
                    $"prediction?file={Uri.EscapeDataString(fileName)}",
                    cancellationToken);
                AtomicWrite(localFile, prediction);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                // The manifest can be published slightly before the matching
                // prediction file. Do not fail the whole sync for that one file.
                AppLogger.Info("V6云端档案同步", $"跳过尚未发布的预测档案：{fileName}");
                continue;
            }
            catch (HttpRequestException) when (File.Exists(localFile))
            {
                prediction = JsonSerializer.Deserialize<CloudDailyPrediction>(
                    File.ReadAllText(localFile), JsonOptions)
                    ?? throw new InvalidDataException($"本地预测档案损坏：{fileName}");
            }
            try
            {
                rows += ImportPrediction(prediction);
                files++;
            }
            catch (InvalidDataException)
            {
                // A legacy cloud file must not block newer, valid prediction
                // files from being imported. Keep the sync moving so the local
                // 6.3 ledger can catch up to the cloud manifest.
                continue;
            }
        }

        try
        {
            SymmetricRuntimeStateSnapshot runtimeState = await DownloadAsync<SymmetricRuntimeStateSnapshot>(
                "runtime-state", cancellationToken);
            int merged = SymmetricRuntimeStateSync.MergeIntoLocal(runtimeState);
            AppLogger.Info("V6同构状态同步", $"已合并云端运行状态，补齐预测记录 {merged} 条，状态哈希 {runtimeState.StateHash}");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            AppLogger.Info("V6同构状态同步", "云端尚未发布同构运行状态，保留现有开奖与预测档案同步");
        }

        DatabaseHelper.BatchVerifyAIPredicts();
        return new CloudSyncResult(DatabaseHelper.GetLatestPeriod(), manifest.LatestIssue,
            newDraws, files, rows);
    }

    public static int ImportPrediction(CloudDailyPrediction prediction)
    {
        if (prediction.Status != "success" || prediction.Issue <= 0)
            throw new InvalidDataException("云端预测档案状态或期号无效");

        if (!HasCompleteLocalEquivalent(prediction))
        {
            // Keep legacy summary-only files in the cache, but never turn them
            // into misleading formal history rows.
            AppLogger.Info("V6云端档案同步", $"跳过不完整预测快照：{prediction.Issue}");
            return 0;
        }

        int imported = 0;
        foreach ((string modelKey, CloudAiPrediction item) in prediction.AiZodiac)
        {
            if (!IsCompleteModelSnapshot(item)) continue;
            int analysisPeriods = item.AnalysisPeriods > 0
                ? item.AnalysisPeriods
                : ParseAnalysisPeriods(modelKey);
            if (analysisPeriods <= 0) continue;

            string[] ranking = item.Ranking.OrderBy(row => row.Rank)
                .ThenBy(row => row.Zodiac, StringComparer.Ordinal)
                .Select(row => row.Zodiac).ToArray();
            string[] top3 = item.Top3.Count > 0 ? item.Top3.ToArray() : ranking.Take(3).ToArray();
            string[] top6 = item.Top6.Count > 0 ? item.Top6.ToArray() : ranking.Take(6).ToArray();
            var record = new DatabaseHelper.PredictionRecord
            {
                Issue = prediction.Issue.ToString(),
                PredictTime = prediction.GeneratedAt,
                PredictionGroupId = $"PRED-{prediction.Issue}",
                PredictNumber = string.Join(',', item.Numbers.Select(number => number.ToString("D2"))),
                PredictZodiac = string.Join(',', top3),
                Top6Zodiac = string.Join(',', top6),
                AnalysisPeriods = analysisPeriods,
                ScoreDetails = JsonSerializer.Serialize(item.FactorScores, JsonOptions),
                ModelVersion = ResolveModelVersion(prediction.ModelVersion),
                ActualNumber = "",
                ActualZodiac = "",
                HitResult = "未开奖",
                Top6HitResult = "未开奖",
                LearningDetails = $"云端生成；信心={item.Confidence}；最佳模型={item.BestModel}",
                FinalRankingJson = item.FinalRankingJson,
                BaseModelScoresJson = item.BaseModelScoresJson,
                FeatureSnapshotJson = item.FeatureSnapshotJson,
                WeightSnapshotJson = item.WeightSnapshotJson,
                MappingSnapshotJson = V65MappingService.CreateSnapshot(
                    prediction.Issue.ToString(), ResolvePredictionDate(prediction)),
                LearningStatus = "Pending",
                PredictionSource = "云端同步"
            };
            imported += DatabaseHelper.MergeSynchronizedPrediction(record);
        }
        return imported;
    }

    private static bool IsCompleteModelSnapshot(CloudAiPrediction item) =>
        item.AnalysisPeriods > 0 && item.Ranking.Count == 12 &&
        item.Ranking.Select(row => row.Zodiac).Distinct(StringComparer.Ordinal).Count() == 12 &&
        item.FactorScores.Count == 12 &&
        item.FactorScores.Keys.All(zodiac => item.Ranking.Any(row => row.Zodiac == zodiac)) &&
        !string.IsNullOrWhiteSpace(item.FinalRankingJson) &&
        !string.IsNullOrWhiteSpace(item.BaseModelScoresJson);

    private static int ParseAnalysisPeriods(string modelKey) =>
        int.TryParse(modelKey, out int periods) ? periods : 0;

    private static string ResolveModelVersion(string value) =>
        string.IsNullOrWhiteSpace(value) ? AIEngine.Version : value;

    private static DateTime ResolvePredictionDate(CloudDailyPrediction prediction) =>
        DateTime.TryParse(prediction.GeneratedAt, out DateTime generated)
            ? generated
            : DateTime.Today;

    public static bool HasCompleteLocalEquivalent(CloudDailyPrediction prediction)
    {
        if (prediction.Status != "success" || prediction.Issue <= 0 || prediction.AiZodiac.Count == 0)
            return false;
        return prediction.AiZodiac.Values.All(IsCompleteModelSnapshot);
    }

    private static async Task<int> SyncHistoryAsync(CancellationToken cancellationToken)
    {
        CloudHistoryArchive archive = await DownloadAsync<CloudHistoryArchive>(
            "history", cancellationToken);
        return ImportHistoryArchive(archive);
    }

    /// <summary>
    /// 从仓库内提交的 history.json 重建/补齐开奖数据库。
    /// 数据库不再进入 Git 仓库，云端工作流每次运行前用它恢复完整历史。
    /// </summary>
    public static int ImportLocalHistoryArchive(string historyJsonPath)
    {
        if (string.IsNullOrWhiteSpace(historyJsonPath) || !File.Exists(historyJsonPath))
            throw new InvalidDataException($"本地开奖档案不存在：{historyJsonPath}");
        DatabaseHelper.InitializeDatabase();
        CloudHistoryArchive? archive = JsonSerializer.Deserialize<CloudHistoryArchive>(
            File.ReadAllText(historyJsonPath), JsonOptions);
        if (archive is null)
            throw new InvalidDataException("本地开奖档案无法解析");
        return ImportHistoryArchive(archive);
    }

    /// <summary>
    /// 从仓库内提交的 runtime-state.json 恢复预测历史与模型记忆，
    /// 使数据库重建后与上次提交的完整运行状态一致（本地端同步复用同一入口）。
    /// </summary>
    public static int ImportLocalRuntimeState(string runtimeStateJsonPath)
    {
        if (string.IsNullOrWhiteSpace(runtimeStateJsonPath) || !File.Exists(runtimeStateJsonPath))
            throw new InvalidDataException($"本地运行状态文件不存在：{runtimeStateJsonPath}");
        DatabaseHelper.InitializeDatabase();
        SymmetricRuntimeStateSnapshot? runtimeState = JsonSerializer.Deserialize<SymmetricRuntimeStateSnapshot>(
            File.ReadAllText(runtimeStateJsonPath), RuntimeStateOptions);
        if (runtimeState is null)
            throw new InvalidDataException("本地运行状态无法解析");
        return SymmetricRuntimeStateSync.MergeIntoLocal(runtimeState);
    }

    private static int ImportHistoryArchive(CloudHistoryArchive archive)
    {
        if (archive.Status != "success" || archive.Records.Count == 0)
            throw new InvalidDataException("开奖档案为空或状态无效");
        var records = archive.Records.Select(item => new DataCrawler.CrawlRecord
        {
            Period = item.Issue,
            Numbers = item.Numbers,
            SpecialNumber = item.SpecialNumber,
            SpecialZodiac = item.SpecialZodiac,
            ShengXiao = item.SpecialZodiac,
            SpecialWaveColor = item.SpecialWaveColor,
            WaveColorSource = item.WaveColorSource,
            Date = string.IsNullOrWhiteSpace(item.OpenTime) ? item.Date : item.OpenTime
        }).ToList();
        DataCrawler.ValidateCrawlRecords(records);
        return DatabaseHelper.SaveCrawlerData(records);
    }

    public static HttpRequestMessage CreateMachineSyncRequest(string resource)
    {
        if (string.IsNullOrWhiteSpace(resource) || resource.StartsWith("/", StringComparison.Ordinal))
            throw new ArgumentException("云端同步资源无效", nameof(resource));
        string key = Environment.GetEnvironmentVariable("V65_CLOUD_SYNC_KEY") ?? "";
        if (string.IsNullOrWhiteSpace(key))
        {
            string localKeyPath = Path.Combine(AppPaths.DataDirectory, "cloud-sync.key");
            if (File.Exists(localKeyPath)) key = File.ReadAllText(localKeyPath).Trim();
        }
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("未配置此电脑的云端同步密钥");
        var request = new HttpRequestMessage(HttpMethod.Get, $"{MachineSyncUrl}/{resource}");
        request.Headers.Add("X-V6-Machine-Key", key);
        return request;
    }

    private static async Task<T> DownloadAsync<T>(string resource, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateMachineSyncRequest(resource);
        using HttpResponseMessage response = await Client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new HttpRequestException("云端同步文件尚未发布", null, response.StatusCode);
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"云端同步失败，HTTP {(int)response.StatusCode} ({response.StatusCode})，返回：{body}",
                null,
                response.StatusCode);
        }
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("云端同步文件无法解析");
    }

    private static void AtomicWrite<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
            using JsonDocument _ = JsonDocument.Parse(File.ReadAllBytes(temporary));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool IsSafePredictionFile(string value) =>
        value.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
        long.TryParse(Path.GetFileNameWithoutExtension(value), out long issue) && issue > 0 &&
        Path.GetFileName(value) == value;

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            // The local proxy fails TLS negotiation with the cloud host.
            UseProxy = false
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36");
        return client;
    }
}

public sealed class CloudManifest
{
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("latest_issue")] public long LatestIssue { get; set; }
    [JsonPropertyName("records")] public List<string> Records { get; set; } = new();
}

public sealed class CloudHistoryArchive
{
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("records")] public List<CloudHistoryRecord> Records { get; set; } = new();
}

public sealed class CloudDailyPrediction
{
    [JsonPropertyName("issue")] public long Issue { get; set; }
    [JsonPropertyName("generated_at")] public string GeneratedAt { get; set; } = "";
    [JsonPropertyName("model_version")] public string ModelVersion { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("ai_zodiac")] public Dictionary<string, CloudAiPrediction> AiZodiac { get; set; } = new();
}

public sealed class CloudAiPrediction
{
    [JsonPropertyName("analysis_periods")] public int AnalysisPeriods { get; set; }
    [JsonPropertyName("top3")] public List<string> Top3 { get; set; } = new();
    [JsonPropertyName("top6")] public List<string> Top6 { get; set; } = new();
    [JsonPropertyName("numbers")] public List<int> Numbers { get; set; } = new();
    [JsonPropertyName("confidence")] public string Confidence { get; set; } = "";
    [JsonPropertyName("best_model")] public string BestModel { get; set; } = "";
    [JsonPropertyName("ranking")] public List<CloudZodiacSnapshot> Ranking { get; set; } = new();
    [JsonPropertyName("factor_scores")] public Dictionary<string, CloudFactorSnapshot> FactorScores { get; set; } = new();
    [JsonPropertyName("final_ranking_json")] public string FinalRankingJson { get; set; } = "";
    [JsonPropertyName("base_model_scores_json")] public string BaseModelScoresJson { get; set; } = "";
    [JsonPropertyName("feature_snapshot_json")] public string FeatureSnapshotJson { get; set; } = "";
    [JsonPropertyName("weight_snapshot_json")] public string WeightSnapshotJson { get; set; } = "";
}

public sealed class CloudZodiacSnapshot
{
    [JsonPropertyName("zodiac")] public string Zodiac { get; set; } = "";
    [JsonPropertyName("rank")] public int Rank { get; set; }
    [JsonPropertyName("total_score")] public double TotalScore { get; set; }
}

public sealed class CloudFactorSnapshot
{
    [JsonPropertyName("frequency")] public double Frequency { get; set; }
    [JsonPropertyName("trend")] public double Trend { get; set; }
    [JsonPropertyName("omission")] public double Omission { get; set; }
    [JsonPropertyName("hot_cold")] public double HotCold { get; set; }
    [JsonPropertyName("period")] public double Period { get; set; }
    [JsonPropertyName("consecutive")] public double Consecutive { get; set; }
    [JsonPropertyName("eight_zodiac")] public double EightZodiac { get; set; }
}
