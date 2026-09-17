using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace 六合分析软件.MacroReasoning;

public enum WebsiteMaterialPolarity
{
    Positive,
    Negative,
    Conditional,
    Unknown
}

public enum WebsiteMaterialTarget
{
    Zodiac,
    Number,
    Mixed,
    Unknown
}

public sealed record WebsiteResearchMaterial(
    string MaterialKey,
    string SourceId,
    string MaterialName,
    long Issue,
    WebsiteMaterialPolarity Polarity,
    WebsiteMaterialTarget Target,
    ImmutableArray<string> Zodiacs,
    ImmutableArray<int> Numbers,
    ImmutableArray<string> Tags,
    DateTimeOffset CapturedAt,
    string RawText);

public sealed record WebsiteResearchOutcomeContext(
    long Issue,
    string ActualZodiac,
    DateOnly DrawDate,
    DateTimeOffset TrainingCutoff,
    ImmutableDictionary<string, double> ZodiacRandomBaseline,
    string MappingVersion);

public sealed record WebsiteResearchSettlement(
    string MaterialKey,
    string SourceId,
    string MaterialName,
    long Issue,
    string ActualZodiac,
    int Coverage,
    bool Success,
    double Baseline,
    double ExcessOverBaseline,
    DateTimeOffset CapturedAt,
    string BaselineModel);

public sealed record WebsiteMaterialExperience(
    string MaterialKey,
    string SourceId,
    string MaterialName,
    WebsiteMaterialPolarity Polarity,
    WebsiteMaterialTarget Target,
    int Samples,
    int Recent20Samples,
    int Recent50Samples,
    double LongRate,
    double Recent20Rate,
    double Recent50Rate,
    double MeanBaseline,
    double LongEdge,
    double Recent20Edge,
    double Recent50Edge,
    double Confidence,
    bool EligibleForDecision,
    string Status);

public sealed record WebsiteMaterialCorrelation(
    string LeftMaterialKey,
    string RightMaterialKey,
    int OverlapIssues,
    double MeanJaccard,
    bool LikelyDuplicateFamily);

public sealed record WebsiteResearchBrainSnapshot(
    string SchemaVersion,
    DateTimeOffset GeneratedAt,
    int MaterialCount,
    int TrainingEligibleMaterialCount,
    int RejectedPostDrawMaterialCount,
    int SettledCount,
    ImmutableArray<WebsiteMaterialExperience> Experiences,
    ImmutableArray<WebsiteMaterialCorrelation> Correlations,
    ImmutableArray<string> Notes);

// This is deliberately a separate, append-only examination record.  It is not a
// V7/P25 prediction and it is never read by the production prediction pipeline.
public sealed record WebsiteResearchEvidenceContribution(
    string MaterialKey,
    string SourceId,
    string MaterialName,
    string SourceHash,
    ImmutableArray<string> Zodiacs,
    int TrainingSamples,
    bool AppliedToRanking,
    double LearnedEdge,
    string Decision);

public sealed record WebsiteResearchExamSettlement(
    string ActualZodiac,
    int ActualRank,
    bool Top3Hit,
    bool Top6Hit,
    DateTimeOffset SettledAt);

public sealed record WebsiteResearchDailyExam(
    long Issue,
    DateTimeOffset GeneratedAt,
    string BrainVersion,
    string LearningStage,
    int MaterialCount,
    int EffectiveMaterialCount,
    ImmutableArray<string> Ranking,
    ImmutableArray<string> Top3,
    ImmutableArray<string> Top6,
    ImmutableArray<WebsiteResearchEvidenceContribution> Evidence,
    WebsiteResearchExamSettlement? Settlement);

public sealed record WebsiteResearchPerformanceWindow(int SampleCount, double Top3Rate, double Top6Rate);

public sealed record WebsiteResearchDailyExamArchive(
    string SchemaVersion,
    DateTimeOffset UpdatedAt,
    ImmutableArray<WebsiteResearchDailyExam> Exams,
    WebsiteResearchPerformanceWindow Recent20,
    WebsiteResearchPerformanceWindow Recent50,
    WebsiteResearchPerformanceWindow AllForward,
    int MaximumTop6MissStreak,
    int CurrentTop6MissStreak,
    ImmutableArray<string> Notes);

public sealed record WebsiteResearchSeedHint(
    string Name,
    WebsiteMaterialPolarity Polarity,
    WebsiteMaterialTarget Target,
    ImmutableArray<string> Tags);

public static class WebsiteResearchSeedCatalog
{
    // Seed hints come from long-term human observation. They are NOT a whitelist or fixed weights.
    // New/unknown materials must remain discoverable and learnable.
    public static ImmutableArray<WebsiteResearchSeedHint> All { get; } =
    [
        new("八肖六码", WebsiteMaterialPolarity.Positive, WebsiteMaterialTarget.Mixed,
            ["nested-coverage", "zodiac", "number"]),
        new("单双五肖", WebsiteMaterialPolarity.Conditional, WebsiteMaterialTarget.Zodiac,
            ["partition", "conditional"]),
        new("绝杀三肖", WebsiteMaterialPolarity.Negative, WebsiteMaterialTarget.Zodiac,
            ["exclusion", "kill"]),
        new("绝杀二肖", WebsiteMaterialPolarity.Negative, WebsiteMaterialTarget.Zodiac,
            ["exclusion", "kill"]),
        new("稳赚六肖", WebsiteMaterialPolarity.Positive, WebsiteMaterialTarget.Zodiac,
            ["coverage", "positive"]),
        new("四肖八码", WebsiteMaterialPolarity.Positive, WebsiteMaterialTarget.Mixed,
            ["zodiac", "number", "mixed"])
    ];
}

public static class WebsiteResearchMaterialInterpreter
{
    private static readonly string[] Zodiac = ["鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪"];
    private static readonly Regex NumberRegex = new(@"(?<!\d)(0?[1-9]|[1-4]\d)(?!\d)", RegexOptions.Compiled);

    public static WebsiteResearchMaterial InterpretSection(
        string sourceId,
        string materialName,
        long issue,
        string rawText,
        DateTimeOffset capturedAt)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("资料来源不能为空", nameof(sourceId));
        if (string.IsNullOrWhiteSpace(materialName)) materialName = "未命名资料";
        if (issue <= 0) throw new ArgumentOutOfRangeException(nameof(issue));

        WebsiteMaterialPolarity polarity = InferPolarity(materialName + " " + rawText);
        string[] zodiacs = Zodiac.Where(rawText.Contains)
            .OrderBy(z => rawText.IndexOf(z, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        int[] numbers = NumberRegex.Matches(rawText)
            .Select(match => int.Parse(match.Value))
            .Where(number => number is >= 1 and <= 49)
            .Distinct()
            .ToArray();
        WebsiteMaterialTarget target = zodiacs.Length > 0 && numbers.Length > 0
            ? WebsiteMaterialTarget.Mixed
            : zodiacs.Length > 0 ? WebsiteMaterialTarget.Zodiac
            : numbers.Length > 0 ? WebsiteMaterialTarget.Number
            : WebsiteMaterialTarget.Unknown;

        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (WebsiteResearchSeedHint seed in WebsiteResearchSeedCatalog.All)
        {
            if (!materialName.Contains(seed.Name, StringComparison.Ordinal)) continue;
            foreach (string tag in seed.Tags) tags.Add(tag);
            if (polarity == WebsiteMaterialPolarity.Unknown) polarity = seed.Polarity;
            if (target == WebsiteMaterialTarget.Unknown) target = seed.Target;
        }
        if (materialName.Contains("肖", StringComparison.Ordinal)) tags.Add("zodiac");
        if (materialName.Contains("码", StringComparison.Ordinal)) tags.Add("number");
        if (polarity == WebsiteMaterialPolarity.Negative) tags.Add("negative");
        if (polarity == WebsiteMaterialPolarity.Conditional) tags.Add("conditional");

        string materialKey = NormalizeKey(sourceId) + "::" + NormalizeKey(materialName);
        return new WebsiteResearchMaterial(materialKey, sourceId, materialName, issue, polarity, target,
            zodiacs.ToImmutableArray(), numbers.ToImmutableArray(), tags.Order().ToImmutableArray(), capturedAt, rawText);
    }

    public static WebsiteResearchMaterial FromLegacyEvidence(WebsiteLearningEvidence evidence)
    {
        string materialName = evidence.SourceId switch
        {
            "6x.js" => "六肖中特",
            "x3x6m.js" => "三肖六码",
            "6x18mm.js" => "六肖18码",
            "3bds.js" => "大小单双资料",
            _ => evidence.SourceId
        };
        WebsiteResearchMaterial interpreted = InterpretSection(evidence.SourceId, materialName,
            evidence.Issue, evidence.RawText, evidence.CapturedAt);
        if (evidence.Zodiacs.Count > 0)
            interpreted = interpreted with { Zodiacs = evidence.Zodiacs.Distinct(StringComparer.Ordinal).ToImmutableArray() };
        return interpreted;
    }

    private static WebsiteMaterialPolarity InferPolarity(string text)
    {
        if (text.Contains("绝杀", StringComparison.Ordinal) || text.Contains("杀肖", StringComparison.Ordinal) ||
            text.Contains("排除", StringComparison.Ordinal) || text.Contains("不要", StringComparison.Ordinal))
            return WebsiteMaterialPolarity.Negative;
        if (text.Contains("单双", StringComparison.Ordinal) || text.Contains("天地", StringComparison.Ordinal) ||
            text.Contains("家野", StringComparison.Ordinal) || text.Contains("波色", StringComparison.Ordinal))
            return WebsiteMaterialPolarity.Conditional;
        if (text.Contains("中特", StringComparison.Ordinal) || text.Contains("必中", StringComparison.Ordinal) ||
            text.Contains("稳", StringComparison.Ordinal) || text.Contains("精选", StringComparison.Ordinal) ||
            text.Contains("推荐", StringComparison.Ordinal))
            return WebsiteMaterialPolarity.Positive;
        return WebsiteMaterialPolarity.Unknown;
    }

    private static string NormalizeKey(string value) => Regex.Replace(value.Trim().ToLowerInvariant(), @"\s+", "-");
}

public static class WebsiteResearchExperienceEngine
{
    public static ImmutableArray<WebsiteResearchSettlement> Settle(
        IEnumerable<WebsiteResearchMaterial> materials,
        IReadOnlyDictionary<long, WebsiteResearchOutcomeContext> outcomes)
    {
        var rows = new List<WebsiteResearchSettlement>();
        foreach (WebsiteResearchMaterial material in materials)
        {
            if (!outcomes.TryGetValue(material.Issue, out WebsiteResearchOutcomeContext? outcome)) continue;
            if (material.CapturedAt >= outcome.TrainingCutoff) continue; // hard anti-leakage gate
            if (material.Target is WebsiteMaterialTarget.Number or WebsiteMaterialTarget.Unknown) continue;
            if (material.Zodiacs.Length == 0 || material.Zodiacs.Length >= 12) continue;
            if (material.Polarity is WebsiteMaterialPolarity.Unknown or WebsiteMaterialPolarity.Conditional) continue;

            int coverage = material.Zodiacs.Length;
            bool contains = material.Zodiacs.Contains(outcome.ActualZodiac, StringComparer.Ordinal);
            bool success = material.Polarity == WebsiteMaterialPolarity.Negative ? !contains : contains;
            double positiveBaseline = material.Zodiacs
                .Distinct(StringComparer.Ordinal)
                .Sum(zodiac => outcome.ZodiacRandomBaseline.GetValueOrDefault(zodiac));
            positiveBaseline = Math.Clamp(positiveBaseline, 0d, 1d);
            double baseline = material.Polarity == WebsiteMaterialPolarity.Negative
                ? 1d - positiveBaseline
                : positiveBaseline;
            rows.Add(new WebsiteResearchSettlement(material.MaterialKey, material.SourceId, material.MaterialName,
                material.Issue, outcome.ActualZodiac, coverage, success, baseline,
                (success ? 1d : 0d) - baseline, material.CapturedAt, outcome.MappingVersion));
        }
        return rows.OrderBy(row => row.Issue).ThenBy(row => row.MaterialKey, StringComparer.Ordinal).ToImmutableArray();
    }

    public static ImmutableArray<WebsiteMaterialExperience> Learn(
        IEnumerable<WebsiteResearchMaterial> materials,
        IEnumerable<WebsiteResearchSettlement> settlements,
        int minimumDecisionSamples = 30)
    {
        var materialMap = materials.GroupBy(x => x.MaterialKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(x => x.Issue).First(), StringComparer.Ordinal);
        var result = new List<WebsiteMaterialExperience>();
        foreach (IGrouping<string, WebsiteResearchSettlement> group in settlements.GroupBy(x => x.MaterialKey, StringComparer.Ordinal))
        {
            WebsiteResearchSettlement[] all = group.OrderBy(row => row.Issue).ToArray();
            if (all.Length == 0 || !materialMap.TryGetValue(group.Key, out WebsiteResearchMaterial? material)) continue;
            WebsiteResearchSettlement[] recent20 = all.TakeLast(20).ToArray();
            WebsiteResearchSettlement[] recent50 = all.TakeLast(50).ToArray();
            double longRate = Rate(all);
            double recent20Rate = Rate(recent20);
            double recent50Rate = Rate(recent50);
            double baseline = all.Average(row => row.Baseline);
            double baseline20 = recent20.Length == 0 ? baseline : recent20.Average(row => row.Baseline);
            double baseline50 = recent50.Length == 0 ? baseline : recent50.Average(row => row.Baseline);
            double confidence = Math.Clamp(Math.Sqrt(all.Length / 50d), 0d, 1d);
            double longEdge = longRate - baseline;
            double edge20 = recent20Rate - baseline20;
            double edge50 = recent50Rate - baseline50;
            bool eligible = all.Length >= minimumDecisionSamples && confidence >= .70d;
            string status = !eligible ? "OBSERVE" :
                longEdge <= 0 ? "NO_PROVEN_EDGE" :
                edge20 < longEdge - .15d ? "RECENT_DEGRADING" :
                edge20 > longEdge + .15d ? "RECENT_STRENGTHENING" : "STABLE_EDGE";
            result.Add(new WebsiteMaterialExperience(group.Key, material.SourceId, material.MaterialName,
                material.Polarity, material.Target, all.Length, recent20.Length, recent50.Length,
                longRate, recent20Rate, recent50Rate, baseline, longEdge, edge20, edge50,
                confidence, eligible, status));
        }
        return result.OrderByDescending(x => x.EligibleForDecision)
            .ThenByDescending(x => x.LongEdge)
            .ThenByDescending(x => x.Samples)
            .ThenBy(x => x.MaterialKey, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    public static ImmutableArray<WebsiteMaterialCorrelation> FindCorrelations(
        IEnumerable<WebsiteResearchMaterial> materials,
        IReadOnlyDictionary<long, WebsiteResearchOutcomeContext> outcomes,
        int minimumOverlapIssues = 12,
        double duplicateThreshold = .80d)
    {
        // Correlations use only genuinely pre-draw snapshots, otherwise a post-result page copy can create false agreement.
        var groups = materials
            .Where(x => x.Zodiacs.Length > 0 && outcomes.TryGetValue(x.Issue, out WebsiteResearchOutcomeContext? outcome) && x.CapturedAt < outcome.TrainingCutoff)
            .GroupBy(x => x.MaterialKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.GroupBy(x => x.Issue).ToDictionary(g => g.Key, g => g.OrderBy(x => x.CapturedAt).First()),
                StringComparer.Ordinal);
        string[] keys = groups.Keys.Order(StringComparer.Ordinal).ToArray();
        var result = new List<WebsiteMaterialCorrelation>();
        for (int i = 0; i < keys.Length; i++)
        {
            for (int j = i + 1; j < keys.Length; j++)
            {
                long[] overlap = groups[keys[i]].Keys.Intersect(groups[keys[j]].Keys).Order().ToArray();
                if (overlap.Length < minimumOverlapIssues) continue;
                double mean = overlap.Average(issue => Jaccard(groups[keys[i]][issue].Zodiacs, groups[keys[j]][issue].Zodiacs));
                result.Add(new WebsiteMaterialCorrelation(keys[i], keys[j], overlap.Length, mean, mean >= duplicateThreshold));
            }
        }
        return result.OrderByDescending(x => x.LikelyDuplicateFamily)
            .ThenByDescending(x => x.MeanJaccard)
            .ThenByDescending(x => x.OverlapIssues)
            .ToImmutableArray();
    }

    public static WebsiteResearchBrainSnapshot BuildSnapshot(
        IEnumerable<WebsiteResearchMaterial> materials,
        IReadOnlyDictionary<long, WebsiteResearchOutcomeContext> outcomes)
    {
        WebsiteResearchMaterial[] uniqueMaterials = materials
            .GroupBy(x => (x.MaterialKey, x.Issue))
            .Select(group => group.OrderBy(x => x.CapturedAt).First())
            .OrderBy(x => x.Issue)
            .ThenBy(x => x.MaterialKey, StringComparer.Ordinal)
            .ToArray();
        int trainingEligible = uniqueMaterials.Count(material =>
            outcomes.TryGetValue(material.Issue, out WebsiteResearchOutcomeContext? outcome) && material.CapturedAt < outcome.TrainingCutoff);
        int rejectedPostDraw = uniqueMaterials.Count(material =>
            outcomes.TryGetValue(material.Issue, out WebsiteResearchOutcomeContext? outcome) && material.CapturedAt >= outcome.TrainingCutoff);
        ImmutableArray<WebsiteResearchSettlement> settlements = Settle(uniqueMaterials, outcomes);
        ImmutableArray<WebsiteMaterialExperience> experiences = Learn(uniqueMaterials, settlements);
        ImmutableArray<WebsiteMaterialCorrelation> correlations = FindCorrelations(uniqueMaterials, outcomes);
        return new WebsiteResearchBrainSnapshot(
            "website-research-brain-v1",
            DateTimeOffset.Now,
            uniqueMaterials.Length,
            trainingEligible,
            rejectedPostDraw,
            settlements.Length,
            experiences,
            correlations,
            [
                "这是旁路研究快照，不生成或修改正式P25预测。",
                "只有开奖训练截止时间之前真实冻结的资料才允许学习；开奖后抓到的旧资料只归档，不训练。",
                "资料价值按实际命中减去当期49号码生肖映射的随机基线评估，不用固定N/12近似。",
                "未知资料允许进入观察池；种子目录只帮助理解，不是固定白名单或固定权重。",
                "相关性用于识别疑似转载/同源资料，后续元学习器不得把高度相关资料重复当成独立证据。",
                "Conditional与纯号码资料暂不自动判定优劣，等待专门语义解释器。"
            ]);
    }

    private static double Rate(IReadOnlyCollection<WebsiteResearchSettlement> rows) => rows.Count == 0 ? 0d : rows.Count(row => row.Success) / (double)rows.Count;

    private static double Jaccard(ImmutableArray<string> left, ImmutableArray<string> right)
    {
        var a = left.ToHashSet(StringComparer.Ordinal);
        var b = right.ToHashSet(StringComparer.Ordinal);
        int union = a.Union(b).Count();
        return union == 0 ? 0d : a.Intersect(b).Count() / (double)union;
    }
}

public interface IWebsiteResearchAnalyst
{
    WebsiteResearchMaterial Analyze(WebsiteResearchMaterial material);
}

public sealed class NoOpWebsiteResearchAnalyst : IWebsiteResearchAnalyst
{
    public WebsiteResearchMaterial Analyze(WebsiteResearchMaterial material) => material;
}

public static class WebsiteResearchBrainStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    public static void Save(string path, WebsiteResearchBrainSnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, JsonOptions));
            using JsonDocument _ = JsonDocument.Parse(File.ReadAllBytes(temporary));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

public static class WebsiteResearchDailyExamStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    public static WebsiteResearchDailyExamArchive Load(string path)
    {
        if (!File.Exists(path)) return Empty();
        WebsiteResearchDailyExamArchive? archive = JsonSerializer.Deserialize<WebsiteResearchDailyExamArchive>(File.ReadAllText(path), JsonOptions);
        return archive ?? Empty();
    }

    public static void Save(string path, WebsiteResearchDailyExamArchive archive)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(archive, JsonOptions));
            using JsonDocument _ = JsonDocument.Parse(File.ReadAllBytes(temporary));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static WebsiteResearchDailyExamArchive Empty() => new(
        "website-research-daily-exam-v1", DateTimeOffset.MinValue, [],
        new(0, 0, 0), new(0, 0, 0), new(0, 0, 0), 0, 0, []);
}

public static class WebsiteResearchShadowService
{
    private static readonly TimeSpan ChinaOffset = TimeSpan.FromHours(8);
    private const int ConservativeTrainingCutoffHour = 20;

    public static WebsiteResearchBrainSnapshot BuildFromLegacyArchive(string repositoryRoot)
    {
        string archivePath = Path.Combine(repositoryRoot, "site", "data", "predictions", "website-learning-archive.json");
        WebsiteLearningPersistentState state = WebsiteLearningPersistence.Load(archivePath);
        WebsiteResearchMaterial[] materials = state.Evidence
            // Settlement is a property of the capture, not a reason to erase that
            // capture from the learning set.  The captured-at gate below remains
            // the only training admission gate.
            .Where(evidence => evidence.Zodiacs.Count > 0)
            .Select(WebsiteResearchMaterialInterpreter.FromLegacyEvidence)
            .GroupBy(material => (material.MaterialKey, material.Issue))
            .Select(group => group.OrderBy(material => material.CapturedAt).First())
            .ToArray();

        Dictionary<long, WebsiteResearchOutcomeContext> outcomes = BuildOutcomeContexts();
        WebsiteResearchBrainSnapshot snapshot = WebsiteResearchExperienceEngine.BuildSnapshot(materials, outcomes);
        string outputPath = Path.Combine(repositoryRoot, "site", "data", "predictions", "website-research-brain.json");
        WebsiteResearchBrainStore.Save(outputPath, snapshot);
        BuildOrSettleDailyExam(repositoryRoot, state, materials, outcomes, snapshot);
        return snapshot;
    }

    private static void BuildOrSettleDailyExam(string repositoryRoot, WebsiteLearningPersistentState state,
        WebsiteResearchMaterial[] materials, IReadOnlyDictionary<long, WebsiteResearchOutcomeContext> outcomes,
        WebsiteResearchBrainSnapshot snapshot)
    {
        string path = Path.Combine(repositoryRoot, "site", "data", "predictions", "website-research-daily-exams.json");
        WebsiteResearchDailyExamArchive existing = WebsiteResearchDailyExamStore.Load(path);
        long targetIssue = ResolveNextIssue();
        var exams = existing.Exams.ToDictionary(exam => exam.Issue);

        // Freeze once.  Later web captures, source edits, and the draw itself can
        // only affect Settlement; they can never change this ranking or evidence.
        if (!exams.ContainsKey(targetIssue))
            exams[targetIssue] = CreateFrozenExam(targetIssue, state, materials, snapshot);

        foreach ((long issue, WebsiteResearchDailyExam exam) in exams.ToArray())
        {
            if (exam.Settlement is not null || !outcomes.TryGetValue(issue, out WebsiteResearchOutcomeContext? outcome)) continue;
            int rank = exam.Ranking.IndexOf(outcome.ActualZodiac);
            if (rank < 0) throw new InvalidDataException($"网页研究考试第{issue}期缺少实际生肖 {outcome.ActualZodiac}");
            exams[issue] = exam with
            {
                Settlement = new(outcome.ActualZodiac, rank + 1, rank < 3, rank < 6, DateTimeOffset.Now)
            };
        }

        WebsiteResearchDailyExam[] frozen = exams.Values.OrderBy(exam => exam.Issue).ToArray();
        WebsiteResearchDailyExamStore.Save(path, BuildArchive(frozen));
    }

    private static WebsiteResearchDailyExam CreateFrozenExam(long issue, WebsiteLearningPersistentState state,
        IEnumerable<WebsiteResearchMaterial> materials, WebsiteResearchBrainSnapshot snapshot)
    {
        WebsiteResearchMaterial[] issueMaterials = materials.Where(material => material.Issue == issue)
            .OrderBy(material => material.MaterialKey, StringComparer.Ordinal).ToArray();
        Dictionary<string, WebsiteMaterialExperience> experience = snapshot.Experiences
            .ToDictionary(item => item.MaterialKey, StringComparer.Ordinal);
        var evidence = new List<WebsiteResearchEvidenceContribution>();
        var score = Zodiac.ToDictionary(zodiac => zodiac, _ => 0d, StringComparer.Ordinal);
        foreach (WebsiteResearchMaterial material in issueMaterials)
        {
            experience.TryGetValue(material.MaterialKey, out WebsiteMaterialExperience? learned);
            bool apply = learned is not null && learned.EligibleForDecision && learned.LongEdge > 0 &&
                material.Polarity is WebsiteMaterialPolarity.Positive or WebsiteMaterialPolarity.Negative;
            double edge = apply ? learned!.LongEdge * learned.Confidence : 0d;
            if (apply)
            {
                foreach (string zodiac in material.Zodiacs)
                    score[zodiac] += material.Polarity == WebsiteMaterialPolarity.Negative ? -edge : edge;
            }
            evidence.Add(new(material.MaterialKey, material.SourceId, material.MaterialName,
                state.Evidence.FirstOrDefault(item => item.Issue == issue && item.SourceId == material.SourceId &&
                    item.CapturedAt == material.CapturedAt)?.SourceHash ?? "", material.Zodiacs,
                learned?.Samples ?? 0, apply, edge,
                apply ? "已通过样本门槛，按已验证超额优势计入" : "低样本观察：不计入排序"));
        }
        // With no validated source there is intentionally no disguised vote.  The
        // deterministic tie-break is an auditable neutral observation baseline.
        string[] ranking = Zodiac.OrderByDescending(zodiac => score[zodiac])
            .ThenBy(zodiac => NeutralTieBreak(issue, zodiac)).ToArray();
        int effective = evidence.Count(item => item.AppliedToRanking);
        return new(issue, DateTimeOffset.Now, "website-research-brain-v1",
            effective == 0 ? "低样本观察期" : "前瞻学习期", issueMaterials.Length, effective,
            ranking.ToImmutableArray(), ranking.Take(3).ToImmutableArray(), ranking.Take(6).ToImmutableArray(),
            evidence.ToImmutableArray(), null);
    }

    private static WebsiteResearchDailyExamArchive BuildArchive(IReadOnlyList<WebsiteResearchDailyExam> exams)
    {
        WebsiteResearchDailyExam[] settled = exams.Where(exam => exam.Settlement is not null).OrderBy(exam => exam.Issue).ToArray();
        int currentMiss = 0, maximumMiss = 0, runningMiss = 0;
        foreach (WebsiteResearchDailyExam exam in settled)
        {
            if (exam.Settlement!.Top6Hit) runningMiss = 0;
            else { runningMiss++; maximumMiss = Math.Max(maximumMiss, runningMiss); }
        }
        for (int i = settled.Length - 1; i >= 0 && !settled[i].Settlement!.Top6Hit; i--) currentMiss++;
        return new("website-research-daily-exam-v1", DateTimeOffset.Now, exams.ToImmutableArray(),
            Window(settled.TakeLast(20)), Window(settled.TakeLast(50)), Window(settled), maximumMiss, currentMiss,
            ["每期排名和证据在开奖前首次生成后冻结；开奖后仅回填结算字段。",
             "没有达到样本门槛的网页资料只展示，不参与排序，因此不会变相按资料数量投票。",
             "训练仅使用开奖前抓到的资料；开奖后首次抓到的旧资料不进入训练。"]);
    }

    private static WebsiteResearchPerformanceWindow Window(IEnumerable<WebsiteResearchDailyExam> exams)
    {
        WebsiteResearchDailyExam[] rows = exams.ToArray();
        return new(rows.Length,
            rows.Length == 0 ? 0d : rows.Count(row => row.Settlement!.Top3Hit) / (double)rows.Length,
            rows.Length == 0 ? 0d : rows.Count(row => row.Settlement!.Top6Hit) / (double)rows.Length);
    }

    private static long ResolveNextIssue()
    {
        if (!long.TryParse(DatabaseHelper.GetLatestPeriod(), out long latest) || latest <= 0)
            throw new InvalidDataException("无法从开奖历史确定网页研究考试目标期号");
        int currentYear = DateTimeOffset.UtcNow.ToOffset(ChinaOffset).Year;
        return latest / 1000 < currentYear ? currentYear * 1000L + 1 : latest + 1;
    }

    private static ulong NeutralTieBreak(long issue, string zodiac)
    {
        ulong hash = 1469598103934665603UL;
        foreach (char c in issue.ToString() + zodiac) { hash ^= c; hash *= 1099511628211UL; }
        return hash;
    }

    private static readonly string[] Zodiac = ["鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪"];

    private static Dictionary<long, WebsiteResearchOutcomeContext> BuildOutcomeContexts()
    {
        var result = new Dictionary<long, WebsiteResearchOutcomeContext>();
        foreach (DatabaseHelper.HistoryRecord row in DatabaseHelper.GetLatestHistory(int.MaxValue))
        {
            if (!long.TryParse(row.Period, out long issue) || issue <= 0 || string.IsNullOrWhiteSpace(row.SpecialZodiac)) continue;
            DateTime? date = ParseDate(row.Date) ?? ParseDate(row.OpenTime);
            if (!date.HasValue) continue;
            DateOnly drawDate = DateOnly.FromDateTime(date.Value);
            var cutoff = new DateTimeOffset(drawDate.Year, drawDate.Month, drawDate.Day,
                ConservativeTrainingCutoffHour, 0, 0, ChinaOffset);
            int lunarYear = V65MappingService.GetLunarYear(drawDate.ToDateTime(new TimeOnly(12, 0)));
            IReadOnlyDictionary<string, IReadOnlyList<string>> map = V65MappingService.GetZodiacNumberMap(lunarYear);
            ImmutableDictionary<string, double> baseline = map.ToImmutableDictionary(
                pair => pair.Key,
                pair => pair.Value.Count / 49d,
                StringComparer.Ordinal);
            result[issue] = new WebsiteResearchOutcomeContext(issue, row.SpecialZodiac, drawDate, cutoff,
                baseline, V65MappingService.ZodiacNumberMappingVersion);
        }
        return result;
    }

    private static DateTime? ParseDate(string? value)
    {
        return DateTime.TryParse(value, out DateTime parsed) ? parsed.Date : null;
    }
}
