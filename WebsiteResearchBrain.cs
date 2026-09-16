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
    DateTimeOffset CapturedAt);

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
    int SettledCount,
    ImmutableArray<WebsiteMaterialExperience> Experiences,
    ImmutableArray<WebsiteMaterialCorrelation> Correlations,
    ImmutableArray<string> Notes);

public sealed record WebsiteResearchSeedHint(
    string Name,
    WebsiteMaterialPolarity Polarity,
    WebsiteMaterialTarget Target,
    ImmutableArray<string> Tags);

public static class WebsiteResearchSeedCatalog
{
    // Seed hints are not a whitelist. Unknown materials must remain discoverable and learnable.
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
        if (!evidence.Zodiacs.IsNullOrEmpty())
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

    private static bool IsNullOrEmpty<T>(this IReadOnlyCollection<T>? values) => values is null || values.Count == 0;
}

public static class WebsiteResearchExperienceEngine
{
    public static ImmutableArray<WebsiteResearchSettlement> Settle(
        IEnumerable<WebsiteResearchMaterial> materials,
        IReadOnlyDictionary<long, string> actualZodiacByIssue)
    {
        var rows = new List<WebsiteResearchSettlement>();
        foreach (WebsiteResearchMaterial material in materials)
        {
            if (!actualZodiacByIssue.TryGetValue(material.Issue, out string? actual) || string.IsNullOrWhiteSpace(actual)) continue;
            if (material.Target is WebsiteMaterialTarget.Number or WebsiteMaterialTarget.Unknown) continue;
            if (material.Zodiacs.Length == 0 || material.Zodiacs.Length >= 12) continue;
            if (material.Polarity is WebsiteMaterialPolarity.Unknown or WebsiteMaterialPolarity.Conditional) continue;

            int coverage = material.Zodiacs.Length;
            bool contains = material.Zodiacs.Contains(actual, StringComparer.Ordinal);
            bool success = material.Polarity == WebsiteMaterialPolarity.Negative ? !contains : contains;
            double baseline = material.Polarity == WebsiteMaterialPolarity.Negative
                ? 1d - coverage / 12d
                : coverage / 12d;
            rows.Add(new WebsiteResearchSettlement(material.MaterialKey, material.SourceId, material.MaterialName,
                material.Issue, actual, coverage, success, baseline, (success ? 1d : 0d) - baseline,
                material.CapturedAt));
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
        int minimumOverlapIssues = 12,
        double duplicateThreshold = .80d)
    {
        var groups = materials.Where(x => x.Zodiacs.Length > 0)
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
        IReadOnlyDictionary<long, string> actualZodiacByIssue)
    {
        WebsiteResearchMaterial[] uniqueMaterials = materials
            .GroupBy(x => (x.MaterialKey, x.Issue))
            .Select(group => group.OrderBy(x => x.CapturedAt).First())
            .OrderBy(x => x.Issue)
            .ThenBy(x => x.MaterialKey, StringComparer.Ordinal)
            .ToArray();
        ImmutableArray<WebsiteResearchSettlement> settlements = Settle(uniqueMaterials, actualZodiacByIssue);
        ImmutableArray<WebsiteMaterialExperience> experiences = Learn(uniqueMaterials, settlements);
        ImmutableArray<WebsiteMaterialCorrelation> correlations = FindCorrelations(uniqueMaterials);
        return new WebsiteResearchBrainSnapshot(
            "website-research-brain-v1",
            DateTimeOffset.Now,
            uniqueMaterials.Length,
            settlements.Length,
            experiences,
            correlations,
            [
                "这是旁路研究快照，不生成或修改正式P25预测。",
                "资料价值按实际命中减去自身覆盖规模随机基线评估，避免用高覆盖率制造虚假高命中。",
                "未知资料允许进入观察池；种子目录仅用于帮助理解，不是固定白名单。",
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

public static class WebsiteResearchShadowService
{
    public static WebsiteResearchBrainSnapshot BuildFromLegacyArchive(string repositoryRoot)
    {
        string archivePath = Path.Combine(repositoryRoot, "site", "data", "predictions", "website-learning-archive.json");
        WebsiteLearningPersistentState state = WebsiteLearningPersistence.Load(archivePath);
        WebsiteResearchMaterial[] materials = state.Evidence
            .Where(evidence => evidence.WebsiteResultZodiac is null && evidence.Zodiacs.Length > 0)
            .Select(WebsiteResearchMaterialInterpreter.FromLegacyEvidence)
            .GroupBy(material => (material.MaterialKey, material.Issue))
            .Select(group => group.OrderBy(material => material.CapturedAt).First())
            .ToArray();

        var actualByIssue = DatabaseHelper.GetLatestHistory(int.MaxValue)
            .Where(row => long.TryParse(row.Period, out _) && !string.IsNullOrWhiteSpace(row.SpecialZodiac))
            .GroupBy(row => long.Parse(row.Period))
            .ToDictionary(group => group.Key, group => group.First().SpecialZodiac);

        WebsiteResearchBrainSnapshot snapshot = WebsiteResearchExperienceEngine.BuildSnapshot(materials, actualByIssue);
        string outputPath = Path.Combine(repositoryRoot, "site", "data", "predictions", "website-research-brain.json");
        WebsiteResearchBrainStore.Save(outputPath, snapshot);
        return snapshot;
    }
}
