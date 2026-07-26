using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace 六合分析软件;

public sealed record DailyAiPrediction(
    [property: JsonPropertyName("analysis_periods")] int AnalysisPeriods,
    [property: JsonPropertyName("top3")] IReadOnlyList<string> Top3,
    [property: JsonPropertyName("top6")] IReadOnlyList<string> Top6,
    [property: JsonPropertyName("numbers")] IReadOnlyList<int> Numbers,
    [property: JsonPropertyName("confidence")] string Confidence,
    [property: JsonPropertyName("best_model")] string BestModel);

public static class DailyPredictionAutomation
{
    private static readonly int[] Periods = { 50, 100, 200, AISettings.AllHistoryModeValue };
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string Generate(long targetIssue, string outputDirectory, bool force = false, bool dryRun = false)
    {
        string outputFile = Path.Combine(outputDirectory, $"{targetIssue}.json");
        if (File.Exists(outputFile) && !force)
        {
            Console.WriteLine($"[INFO] 第{targetIssue}期全部预测记录已存在，已跳过");
            return outputFile;
        }
        if (dryRun)
        {
            Console.WriteLine($"[SUCCESS] 第{targetIssue}期全功能预测检查通过（dry-run）");
            return outputFile;
        }

        Console.WriteLine($"[INFO] 开始生成第{targetIssue}期全部预测结果");
        var ai = new Dictionary<string, DailyAiPrediction>();
        foreach (int period in Periods)
        {
            AIEngine.PredictResult result = AIEngine.GenerateForAutomation(period, targetIssue.ToString());
            if (result.Top3.Count == 0 || result.Top6.Count == 0)
                throw new InvalidDataException($"{period}期 AI 预测结果为空");
            AIEngine.SavePredictionHistory(result);
            string periodKey = period == AISettings.AllHistoryModeValue ? "all" : period.ToString();
            ai[periodKey] = new DailyAiPrediction(result.AnalysisPeriods, result.Top3.ToArray(),
                result.Top6.ToArray(), result.RecommendedNumbers.ToArray(), result.Confidence, result.BestModel);
        }

        ZodiacRulePrediction rule = ZodiacRulePredictionService.Predict(targetIssue);
        int targetYear = checked((int)(targetIssue / 1000));
        PredictionScoreService.ScoreResult score = PredictionScoreService.Predict(500, targetYear);
        EnsemblePredictionService.PredictionReport ensemble = EnsemblePredictionService.Predict(500);
        if (score.Predictions.Count == 0 || ensemble.Predictions.Count == 0 || rule.Zodiacs.Count == 0)
            throw new InvalidDataException("综合预测或特码规律结果为空");

        var document = new
        {
            issue = targetIssue,
            generated_at = DateTimeOffset.Now.ToString("O"),
            status = "success",
            model_version = AIEngine.Version,
            source_issue = rule.SourceIssue,
            ai_zodiac = ai,
            special_rule = new
            {
                source_issue = rule.SourceIssue,
                first_number = rule.FirstNumber,
                last_number = rule.LastNumber,
                tail_sum = rule.TailSum,
                mapped_number = rule.MappedNumber,
                mapped_zodiac = rule.MappedZodiac,
                zodiacs = rule.Zodiacs
            },
            comprehensive_score = score.Predictions.Take(6).Select(item => new
            {
                zodiac = item.Zodiac, numbers = item.Number, total_score = item.TotalScore,
                confidence = item.Confidence, reason = item.Reason
            }).ToArray(),
            ensemble = ensemble.Predictions.Take(6).Select(item => new
            {
                zodiac = item.Zodiac, final_score = Math.Round(item.FinalScore, 4),
                confidence = item.Confidence, reason = item.Reason
            }).ToArray(),
            validation = new { passed = true, warnings = Array.Empty<string>() },
            verification = new
            {
                status = "pending",
                actual_number = (string?)null,
                actual_zodiac = (string?)null,
                message = "等待开奖"
            }
        };

        AtomicWrite(outputFile, document);
        UpdateLatest(outputDirectory, targetIssue, document.generated_at);
        UpdateManifest(outputDirectory);
        Console.WriteLine($"[SUCCESS] 第{targetIssue}期全部预测结果已保存");
        return outputFile;
    }

    public static IReadOnlyList<long> GenerateMissing(string predictionDirectory, string outputDirectory,
        long? requestedIssue = null, long? startIssue = null, bool force = false, bool dryRun = false)
    {
        DatabaseHelper.InitializeDatabase();
        long[] draws = DatabaseHelper.GetLatestHistory(int.MaxValue)
            .Select(record => long.Parse(record.Period)).OrderBy(issue => issue).ToArray();
        if (draws.Length == 0) throw new InvalidDataException("历史数据为空");

        long firstPublished = startIssue ??
            EnumerateIssues(predictionDirectory).DefaultIfEmpty(draws[^1] + 1).Min();
        long[] targets = requestedIssue.HasValue
            ? new[] { requestedIssue.Value }
            : draws.Where(issue => issue >= firstPublished).Append(checked(draws[^1] + 1)).Distinct().ToArray();
        targets = targets.Where(issue => force ||
            !File.Exists(Path.Combine(outputDirectory, $"{issue}.json")) ||
            !File.Exists(Path.Combine(predictionDirectory, $"{issue}.json"))).OrderBy(issue => issue).ToArray();

        foreach (long target in targets)
        {
            long prior = draws.LastOrDefault(issue => issue < target);
            if (prior == 0) throw new InvalidDataException($"第{target}期没有可用的前置开奖数据");
            Console.WriteLine($"[INFO] 补齐第{target}期，数据截止第{prior}期");
            using (DatabaseHelper.UseHistoryThroughIssue(prior))
            {
                PredictionAutomation.Run(new PredictionRunOptions
                {
                    Issue = target, Force = force, DryRun = dryRun, OutputDirectory = predictionDirectory
                });
                Generate(target, outputDirectory, force, dryRun);
            }
        }
        if (targets.Length == 0) Console.WriteLine("[SUCCESS] 所有预测功能的每期记录已齐全");
        if (!dryRun)
        {
            int verified = VerifyPublishedPredictions(outputDirectory);
            Console.WriteLine($"[INFO] 已写入{verified}期预测命中结果");
            UpdateManifest(outputDirectory);
        }
        return targets;
    }

    public static int VerifyPublishedPredictions(string directory)
    {
        var draws = DatabaseHelper.GetLatestHistory(int.MaxValue)
            .Where(record => long.TryParse(record.Period, out _))
            .ToDictionary(record => long.Parse(record.Period));
        int updated = 0;

        foreach (long issue in EnumerateIssues(directory).OrderBy(value => value))
        {
            if (!draws.TryGetValue(issue, out DatabaseHelper.HistoryRecord? draw)) continue;
            string path = Path.Combine(directory, $"{issue}.json");
            JsonObject root = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
                ?? throw new InvalidDataException($"第{issue}期预测文件无法解析");
            JsonObject? existing = root["verification"] as JsonObject;
            if (existing?["status"]?.GetValue<string>() == "verified" &&
                existing["actual_number"]?.GetValue<string>() == draw.SpecialNumber &&
                existing["actual_zodiac"]?.GetValue<string>() == draw.SpecialZodiac)
                continue;

            var aiResults = new JsonObject();
            if (root["ai_zodiac"] is JsonObject ai)
            {
                foreach ((string period, JsonNode? value) in ai)
                {
                    if (value is not JsonObject item) continue;
                    bool top3Hit = ContainsZodiac(item["top3"], draw.SpecialZodiac);
                    bool top6Hit = ContainsZodiac(item["top6"], draw.SpecialZodiac);
                    item["actual_zodiac"] = draw.SpecialZodiac;
                    item["top3_hit"] = top3Hit;
                    item["top6_hit"] = top6Hit;
                    aiResults[period] = new JsonObject
                    {
                        ["top3_hit"] = top3Hit,
                        ["top6_hit"] = top6Hit,
                        ["top3_result"] = top3Hit ? "命中" : "未命中",
                        ["top6_result"] = top6Hit ? "命中" : "未命中"
                    };
                }
            }

            bool ruleHit = false;
            if (root["special_rule"] is JsonObject rule)
            {
                ruleHit = ContainsZodiac(rule["zodiacs"], draw.SpecialZodiac);
                rule["actual_zodiac"] = draw.SpecialZodiac;
                rule["hit"] = ruleHit;
                rule["result"] = ruleHit ? "命中" : "未命中";
            }

            MarkRanking(root["comprehensive_score"], draw.SpecialZodiac);
            MarkRanking(root["ensemble"], draw.SpecialZodiac);
            root["verification"] = new JsonObject
            {
                ["status"] = "verified",
                ["verified_at"] = DateTimeOffset.Now.ToString("O"),
                ["actual_number"] = draw.SpecialNumber,
                ["actual_zodiac"] = draw.SpecialZodiac,
                ["ai_zodiac"] = aiResults,
                ["special_rule_hit"] = ruleHit,
                ["message"] = "已按真实开奖结果验算"
            };
            AtomicWrite(path, root);
            updated++;
        }

        return updated;
    }

    private static bool ContainsZodiac(JsonNode? node, string actual) =>
        node is JsonArray array && array.Any(item => item?.GetValue<string>() == actual);

    private static void MarkRanking(JsonNode? node, string actual)
    {
        if (node is not JsonArray array) return;
        foreach (JsonNode? value in array)
        {
            if (value is not JsonObject item) continue;
            bool hit = item["zodiac"]?.GetValue<string>() == actual;
            item["hit"] = hit;
            item["result"] = hit ? "命中" : "未命中";
        }
    }

    public static string UpdateManifest(string directory)
    {
        long[] issues = EnumerateIssues(directory).Distinct().OrderBy(issue => issue).ToArray();
        if (issues.Length == 0) throw new InvalidDataException("没有可写入清单的预测记录");
        string updatedAt = DateTimeOffset.Now.ToString("O");
        string path = Path.Combine(directory, "manifest.json");
        AtomicWrite(path, new
        {
            status = "success",
            updated_at = updatedAt,
            latest_issue = issues[^1],
            records = issues.Select(issue => $"{issue}.json").ToArray()
        });

        JsonElement[] predictions = issues.Select(issue =>
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllBytes(Path.Combine(directory, $"{issue}.json")));
            return document.RootElement.Clone();
        }).ToArray();
        AtomicWrite(Path.Combine(directory, "history.json"), new
        {
            status = "success",
            updated_at = updatedAt,
            latest_issue = issues[^1],
            predictions
        });
        return path;
    }

    private static IEnumerable<long> EnumerateIssues(string directory) => Directory.Exists(directory)
        ? Directory.EnumerateFiles(directory, "*.json").Select(Path.GetFileNameWithoutExtension)
            .Select(name => long.TryParse(name, out long issue) ? issue : 0).Where(issue => issue > 0)
        : Array.Empty<long>();

    private static void UpdateLatest(string directory, long issue, string generatedAt)
    {
        string path = Path.Combine(directory, "latest.json");
        if (File.Exists(path))
        {
            using JsonDocument existing = JsonDocument.Parse(File.ReadAllBytes(path));
            if (existing.RootElement.GetProperty("latest_issue").GetInt64() > issue) return;
        }
        AtomicWrite(path, new { latest_issue = issue, prediction_file = $"{issue}.json", updated_at = generatedAt, status = "success" });
    }

    private static void AtomicWrite<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
            using JsonDocument parsed = JsonDocument.Parse(File.ReadAllBytes(temporary));
            if (parsed.RootElement.GetProperty("status").GetString() != "success")
                throw new InvalidDataException("预测记录校验失败");
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
