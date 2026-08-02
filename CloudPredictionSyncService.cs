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
    private const string RootUrl = "https://smart-ledger-2026.ntr133.chatgpt.site/api/v6-sync";
    private static readonly HttpClient Client = CreateClient();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<CloudSyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        int newDraws = await SyncHistoryAsync(cancellationToken);
        CloudManifest manifest = await DownloadAsync<CloudManifest>(
            $"{RootUrl}/manifest?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
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
                    $"{RootUrl}/prediction?file={Uri.EscapeDataString(fileName)}&t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                    cancellationToken);
                AtomicWrite(localFile, prediction);
            }
            catch (HttpRequestException) when (File.Exists(localFile))
            {
                prediction = JsonSerializer.Deserialize<CloudDailyPrediction>(
                    File.ReadAllText(localFile), JsonOptions)
                    ?? throw new InvalidDataException($"本地预测档案损坏：{fileName}");
            }
            rows += ImportPrediction(prediction);
            files++;
        }

        DatabaseHelper.BatchVerifyAIPredicts();
        return new CloudSyncResult(DatabaseHelper.GetLatestPeriod(), manifest.LatestIssue,
            newDraws, files, rows);
    }

    public static int ImportPrediction(CloudDailyPrediction prediction)
    {
        if (prediction.Status != "success" || prediction.Issue <= 0)
            throw new InvalidDataException("云端预测档案状态或期号无效");
        int saved = 0;
        foreach ((string periodText, CloudAiPrediction item) in prediction.AiZodiac)
        {
            // The all-history window is serialized as "all" in cloud JSON and
            // represented internally by AISettings.AllHistoryModeValue (0).
            int periods = periodText.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? AISettings.AllHistoryModeValue
                : int.TryParse(periodText, out int parsedPeriods) ? parsedPeriods : -1;
            if (periods < 0)
                throw new InvalidDataException($"第{prediction.Issue}期 AI 预测周期无效：{periodText}");

            // V6.3 已取消500期模型；旧云端档案保留但不再导入该周期。
            if (periods == 500)
                continue;

            if (item.Top3.Count == 0 || item.Top6.Count == 0 || item.Numbers.Count == 0)
                throw new InvalidDataException($"第{prediction.Issue}期 AI 预测内容不完整：{periodText}");

            DatabaseHelper.SaveCloudPrediction(prediction.Issue.ToString(), prediction.GeneratedAt,
                string.Join(',', item.Top3), string.Join(',', item.Top6),
                string.Join(',', item.Numbers.Select(number => number.ToString("D2"))),
                "云端 V6.3", periods, $"{item.Confidence}信心 | {item.BestModel}");
            saved++;
        }
        return saved;
    }

    private static async Task<int> SyncHistoryAsync(CancellationToken cancellationToken)
    {
        CloudHistoryArchive archive = await DownloadAsync<CloudHistoryArchive>(
            $"{RootUrl}/history?t={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", cancellationToken);
        if (archive.Status != "success" || archive.Records.Count == 0)
            throw new InvalidDataException("云端开奖档案为空");
        var records = archive.Records.Select(item => new DataCrawler.CrawlRecord
        {
            Period = item.Issue,
            Numbers = item.Numbers,
            SpecialNumber = item.SpecialNumber,
            SpecialZodiac = item.SpecialZodiac,
            ShengXiao = item.SpecialZodiac,
            Date = string.IsNullOrWhiteSpace(item.OpenTime) ? item.Date : item.OpenTime
        }).ToList();
        DataCrawler.ValidateCrawlRecords(records);
        return DatabaseHelper.SaveCrawlerData(records);
    }

    private static async Task<T> DownloadAsync<T>(string url, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await Client.GetAsync(url, cancellationToken);
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
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LiuHeAnalysis/3.5");
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
    [JsonPropertyName("top3")] public List<string> Top3 { get; set; } = new();
    [JsonPropertyName("top6")] public List<string> Top6 { get; set; } = new();
    [JsonPropertyName("numbers")] public List<int> Numbers { get; set; } = new();
    [JsonPropertyName("confidence")] public string Confidence { get; set; } = "";
    [JsonPropertyName("best_model")] public string BestModel { get; set; } = "";
}
