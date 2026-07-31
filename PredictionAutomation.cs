using System.Text.Json;
using System.Text.Json.Serialization;

namespace 六合分析软件;

public sealed class PredictionRunOptions
{
    public long? Issue { get; init; }
    public bool Force { get; init; }
    public bool DryRun { get; init; }
    public string OutputDirectory { get; init; } = Path.Combine("site", "data", "predictions");
}

public sealed class PredictionRunResult
{
    public required long Issue { get; init; }
    public required string Status { get; init; }
    public string? OutputFile { get; init; }
    public bool Changed { get; init; }
}

public sealed class PublishedPrediction
{
    [JsonPropertyName("issue")]
    public long Issue { get; init; }

    [JsonPropertyName("generated_at")]
    public required string GeneratedAt { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "success";

    [JsonPropertyName("model_version")]
    public required string ModelVersion { get; init; }

    [JsonPropertyName("data_range")]
    public required PublishedDataRange DataRange { get; init; }

    [JsonPropertyName("prediction")]
    public required PublishedPredictionValues Prediction { get; init; }

    [JsonPropertyName("validation")]
    public required PublishedValidation Validation { get; init; }

    [JsonPropertyName("analysis_periods")]
    public int AnalysisPeriods { get; init; }

    [JsonPropertyName("confidence")]
    public string Confidence { get; init; } = "";

    [JsonPropertyName("best_model")]
    public string BestModel { get; init; } = "";

    [JsonPropertyName("top3")]
    public IReadOnlyList<string> Top3 => Prediction.Zodiacs.Take(3).ToArray();

    [JsonPropertyName("top6")]
    public IReadOnlyList<string> Top6 => Prediction.Recommendations;
}

public sealed record PublishedDataRange(
    [property: JsonPropertyName("start_issue")] long StartIssue,
    [property: JsonPropertyName("end_issue")] long EndIssue,
    [property: JsonPropertyName("sample_count")] int SampleCount);

public sealed record PublishedPredictionValues(
    [property: JsonPropertyName("zodiacs")] IReadOnlyList<string> Zodiacs,
    [property: JsonPropertyName("numbers")] IReadOnlyList<int> Numbers,
    [property: JsonPropertyName("wave_colors")] IReadOnlyList<string> WaveColors,
    [property: JsonPropertyName("recommendations")] IReadOnlyList<string> Recommendations);

public sealed record PublishedValidation(
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);

public static class PredictionAutomation
{
    private static readonly HashSet<string> ValidZodiacs = new(StringComparer.Ordinal)
    {
        "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static PredictionRunResult Run(
        PredictionRunOptions options,
        Func<long, AIEngine.PredictResult>? predictor = null)
    {
        Log("INFO", "开始读取历史数据");
        DatabaseHelper.InitializeDatabase();
        List<DatabaseHelper.HistoryRecord> history = DatabaseHelper.GetLatestHistory(int.MaxValue);
        IReadOnlyList<string> warnings = ValidateHistory(history);

        long latestDraw = ParseIssue(history[0].Period, "最新开奖期号");
        long latestPrediction = FindLatestPredictionIssue(options.OutputDirectory);
        long targetIssue = options.Issue ?? checked(latestDraw + 1);

        Log("INFO", $"最新开奖期号：{latestDraw}");
        Log("INFO", $"最新预测期号：{latestPrediction}");
        Log("INFO", $"目标预测期号：{targetIssue}");
        Log("INFO", $"使用历史数据数量：{history.Count}");

        if (targetIssue <= latestDraw)
            throw new InvalidOperationException($"目标预测期号 {targetIssue} 必须大于最新开奖期号 {latestDraw}");

        string outputFile = Path.Combine(options.OutputDirectory, $"{targetIssue}.json");
        if (File.Exists(outputFile) && !options.Force)
        {
            Log("INFO", $"第{targetIssue}期预测已经存在，已跳过。");
            return new PredictionRunResult { Issue = targetIssue, Status = "skipped", OutputFile = outputFile, Changed = false };
        }

        if (options.DryRun)
        {
            Log("SUCCESS", $"检查通过；将生成第{targetIssue}期预测（dry-run 未写入文件）");
            return new PredictionRunResult { Issue = targetIssue, Status = "dry-run", Changed = false };
        }

        Log("INFO", "开始运行预测模型");
        AIEngine.PredictResult calculated = predictor?.Invoke(targetIssue)
            ?? AIEngine.GenerateForAutomation(targetPeriod: targetIssue.ToString());
        calculated.PredictPeriod = targetIssue.ToString();
        ValidatePrediction(calculated, targetIssue);
        AIEngine.SavePredictionHistory(calculated);

        DateTimeOffset generatedAt = DateTimeOffset.Now;
        long startIssue = history.Select(h => ParseIssue(h.Period, "历史期号")).Min();
        var document = new PublishedPrediction
        {
            Issue = targetIssue,
            GeneratedAt = generatedAt.ToString("O"),
            ModelVersion = calculated.Version,
            DataRange = new PublishedDataRange(startIssue, latestDraw, history.Count),
            Prediction = new PublishedPredictionValues(
                calculated.Top3.ToArray(),
                calculated.RecommendedNumbers.ToArray(),
                Array.Empty<string>(),
                calculated.Top6.ToArray()),
            Validation = new PublishedValidation(true, warnings),
            AnalysisPeriods = calculated.AnalysisPeriods,
            Confidence = calculated.Confidence,
            BestModel = calculated.BestModel
        };

        Directory.CreateDirectory(options.OutputDirectory);
        AtomicWriteJson(outputFile, document, ValidatePublishedPrediction);
        string latestFile = Path.Combine(options.OutputDirectory, "latest.json");
        bool updateLatest = true;
        if (File.Exists(latestFile))
        {
            using JsonDocument currentLatest = JsonDocument.Parse(File.ReadAllBytes(latestFile));
            updateLatest = !currentLatest.RootElement.TryGetProperty("latest_issue", out JsonElement currentIssue) ||
                !currentIssue.TryGetInt64(out long currentValue) || currentValue <= targetIssue;
        }
        if (updateLatest) AtomicWriteJson(latestFile, new
        {
            latest_issue = targetIssue,
            prediction_file = $"{targetIssue}.json",
            updated_at = generatedAt.ToString("O"),
            status = "success"
        }, element =>
        {
            if (element.GetProperty("latest_issue").GetInt64() != targetIssue)
                throw new InvalidDataException("latest.json 期号校验失败");
        });
        else Log("INFO", $"第{targetIssue}期已补齐，网站最新期号保持不变");

        Log("INFO", $"输出文件：{outputFile}");
        Log("INFO", "文件校验通过");
        Log("SUCCESS", $"第{targetIssue}期预测已生成");
        return new PredictionRunResult { Issue = targetIssue, Status = "success", OutputFile = outputFile, Changed = true };
    }

    public static IReadOnlyList<string> ValidateHistory(IReadOnlyList<DatabaseHelper.HistoryRecord> history)
    {
        if (history.Count == 0)
            throw new InvalidDataException("历史数据为空");

        var warnings = new List<string>();
        var issues = new HashSet<long>();
        foreach (DatabaseHelper.HistoryRecord record in history)
        {
            long issue = ParseIssue(record.Period, "历史期号");
            if (!issues.Add(issue))
                throw new InvalidDataException($"历史数据存在重复期号：{issue}");
            if (!int.TryParse(record.SpecialNumber, out int number) || number is < 1 or > 49)
                throw new InvalidDataException($"第{issue}期特码数字无效：{record.SpecialNumber}");
            if (!ValidZodiacs.Contains(record.SpecialZodiac))
                throw new InvalidDataException($"第{issue}期特码生肖字段无效：{record.SpecialZodiac}");
        }

        long[] ordered = issues.OrderBy(value => value).ToArray();
        for (int i = 1; i < ordered.Length; i++)
        {
            if (!AreConsecutive(ordered[i - 1], ordered[i]))
            {
                warnings.Add($"历史期号不连续：{ordered[i - 1]} 后为 {ordered[i]}");
                break;
            }
        }
        return warnings;
    }

    private static bool AreConsecutive(long previous, long current)
    {
        if (current == previous + 1)
            return true;

        string previousText = previous.ToString();
        string currentText = current.ToString();
        if (previousText.Length == 7 && currentText.Length == 7 &&
            int.TryParse(previousText[..4], out int previousYear) &&
            int.TryParse(currentText[..4], out int currentYear) &&
            int.TryParse(currentText[4..], out int currentSequence))
        {
            return currentYear == previousYear + 1 && currentSequence == 1;
        }

        return false;
    }

    public static long FindLatestPredictionIssue(string outputDirectory)
    {
        if (!Directory.Exists(outputDirectory))
            return 0;
        return Directory.EnumerateFiles(outputDirectory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Select(name => long.TryParse(name, out long issue) ? issue : 0)
            .DefaultIfEmpty(0)
            .Max();
    }

    private static long ParseIssue(string text, string field)
    {
        if (!long.TryParse(text, out long issue) || issue <= 0)
            throw new InvalidDataException($"{field}不是有效数字：{text}");
        return issue;
    }

    private static void ValidatePrediction(AIEngine.PredictResult result, long targetIssue)
    {
        if (result.PredictPeriod != targetIssue.ToString())
            throw new InvalidDataException("预测结果期号不正确");
        if (result.Top3.Count == 0 || result.Top6.Count == 0)
            throw new InvalidDataException("预测生肖列表为空");
        if (result.RecommendedNumbers.Count == 0)
            throw new InvalidDataException("预测号码列表为空");
        if (result.Top3.Any(zodiac => !ValidZodiacs.Contains(zodiac)) ||
            result.Top6.Any(zodiac => !ValidZodiacs.Contains(zodiac)))
            throw new InvalidDataException("预测结果包含非法生肖");
    }

    private static void ValidatePublishedPrediction(JsonElement element)
    {
        if (element.GetProperty("status").GetString() != "success")
            throw new InvalidDataException("输出状态不是 success");
        if (!element.GetProperty("validation").GetProperty("passed").GetBoolean())
            throw new InvalidDataException("输出校验状态未通过");
        if (element.GetProperty("prediction").GetProperty("zodiacs").GetArrayLength() == 0 ||
            element.GetProperty("prediction").GetProperty("numbers").GetArrayLength() == 0)
            throw new InvalidDataException("输出预测内容为空");
    }

    private static void AtomicWriteJson<T>(string path, T value, Action<JsonElement> validate)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporaryPath = fullPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(temporaryPath));
            validate(document.RootElement);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void Log(string level, string message) => Console.WriteLine($"[{level}] {message}");
}
