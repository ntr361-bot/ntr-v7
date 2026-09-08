using System.Text.Json;

namespace 六合分析软件;

/// <summary>Immutable pre-draw inputs for an isolated historical experiment. Never a production prediction record.</summary>
public sealed record HistoricalTrainingSample(
    long Issue,
    long HistoryCutoffIssue,
    DateTimeOffset SourceGeneratedAt,
    DateTimeOffset DrawOpenedAt,
    string ActualZodiac,
    Dictionary<string, string[]> SourceRankings,
    string EvidenceSource,
    string EvidenceReference);

public sealed record HistoricalTrainingEntry(
    long Issue,
    long HistoryCutoffIssue,
    string ActualZodiac,
    string[] Top3,
    string[] Top6,
    int ActualRank,
    bool Top3Hit,
    bool Top6Hit,
    Dictionary<string, double> BeforeWeights,
    Dictionary<string, double> AfterWeights,
    string EvidenceSource,
    string EvidenceReference,
    DateTimeOffset SourceGeneratedAt);

public sealed record HistoricalTrainingReport(
    string Model,
    string Kind,
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    HistoricalTrainingEntry[] Entries,
    Dictionary<string, double> FinalWeights)
{
    public long[] CompletedIssues => Entries.Select(entry => entry.Issue).ToArray();
}

/// <summary>Runs an auditable historical branch with frozen inputs. It has no production database dependency.</summary>
public static class IndependentLearningHistoricalTraining
{
    public const string Kind = "HistoricalExperiment";
    private static readonly string[] Sources = { "v65-50", "v65-100", "v65-all" };
    private static readonly string[] Zodiacs = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static HistoricalTrainingReport Train(string dataRoot, IReadOnlyList<HistoricalTrainingSample> samples)
    {
        if (string.IsNullOrWhiteSpace(dataRoot)) throw new ArgumentException("需要实验数据目录", nameof(dataRoot));
        HistoricalTrainingSample[] ordered = Validate(samples);
        string folder = Folder(dataRoot);
        if (Directory.Exists(folder)) throw new InvalidDataException("历史实验分支已存在，禁止覆盖或重复训练");
        Directory.CreateDirectory(folder);

        var model = new IndependentLearningModel(folder);
        try
        {
            var entries = new List<HistoricalTrainingEntry>(ordered.Length);
            foreach (HistoricalTrainingSample sample in ordered)
            {
                Dictionary<string, double> before = Weights(model.ReadStateJson());
                string prediction = model.Predict(JsonSerializer.Serialize(new
                {
                    sample.Issue,
                    sample.HistoryCutoffIssue,
                    sample.SourceGeneratedAt,
                    SourceRankings = sample.SourceRankings
                }));
                using var json = JsonDocument.Parse(prediction);
                string[] ranking = json.RootElement.GetProperty("Ranking").EnumerateArray().Select(x => x.GetString()!).ToArray();
                bool changed = model.Learn(sample.Issue, sample.ActualZodiac, sample.HistoryCutoffIssue);
                if (!changed) throw new InvalidDataException("历史实验不允许跳过学习收据");
                int actualRank = Array.IndexOf(ranking, sample.ActualZodiac) + 1;
                entries.Add(new HistoricalTrainingEntry(sample.Issue, sample.HistoryCutoffIssue, sample.ActualZodiac,
                    ranking.Take(3).ToArray(), ranking.Take(6).ToArray(), actualRank, actualRank <= 3, actualRank <= 6,
                    before, Weights(model.ReadStateJson()), sample.EvidenceSource, sample.EvidenceReference, sample.SourceGeneratedAt));
            }
            var report = new HistoricalTrainingReport(IndependentLearningModel.ModelKey, Kind, 1, DateTimeOffset.UtcNow,
                entries.ToArray(), Weights(model.ReadStateJson()));
            WriteAtomically(Path.Combine(folder, "inputs.json"), JsonSerializer.Serialize(ordered, JsonOptions));
            WriteAtomically(Path.Combine(folder, "archive.json"), model.ExportArchive());
            // Publish the report last; its existence is the completed-run marker.
            WriteAtomically(Path.Combine(folder, "report.json"), JsonSerializer.Serialize(report, JsonOptions));
            return report;
        }
        catch
        {
            // Retain partial runs for investigation. Never delete an existing directory on failure.
            throw;
        }
    }

    public static HistoricalTrainingReport? LoadReport(string dataRoot)
    {
        string report = Path.Combine(Folder(dataRoot), "report.json");
        return File.Exists(report) ? JsonSerializer.Deserialize<HistoricalTrainingReport>(File.ReadAllText(report), JsonOptions) : null;
    }

    public static string Folder(string dataRoot) => Path.Combine(Path.GetFullPath(dataRoot), "experiments",
        IndependentLearningModel.ModelKey, "history-pretraining");

    private static HistoricalTrainingSample[] Validate(IReadOnlyList<HistoricalTrainingSample> samples)
    {
        if (samples is null || samples.Count == 0) throw new InvalidDataException("历史实验没有冻结样本");
        HistoricalTrainingSample[] ordered = samples.OrderBy(sample => sample.Issue).ToArray();
        for (int index = 0; index < ordered.Length; index++)
        {
            HistoricalTrainingSample sample = ordered[index];
            if (sample.Issue <= 0 || sample.HistoryCutoffIssue <= 0 || sample.HistoryCutoffIssue >= sample.Issue ||
                sample.SourceGeneratedAt == default || sample.DrawOpenedAt == default || sample.SourceGeneratedAt >= sample.DrawOpenedAt || sample.DrawOpenedAt > DateTimeOffset.UtcNow ||
                !Zodiacs.Contains(sample.ActualZodiac) || string.IsNullOrWhiteSpace(sample.EvidenceSource) || string.IsNullOrWhiteSpace(sample.EvidenceReference) ||
                sample.SourceRankings is null || sample.SourceRankings.Count != Sources.Length || Sources.Any(source => !sample.SourceRankings.TryGetValue(source, out string[]? ranks) ||
                    ranks is null || ranks.Length != Zodiacs.Length || !ranks.Order().SequenceEqual(Zodiacs.Order())))
                throw new InvalidDataException("历史实验样本不完整、非开奖前快照或生肖排名无效");
            if (sample.Issue != sample.HistoryCutoffIssue + 1 || (index > 0 && (sample.HistoryCutoffIssue != ordered[index - 1].Issue || sample.SourceGeneratedAt < ordered[index - 1].DrawOpenedAt)))
                throw new InvalidDataException("历史实验样本必须连续且截止期等于上一期");
        }
        return ordered;
    }

    private static Dictionary<string, double> Weights(string stateJson)
    {
        using var state = JsonDocument.Parse(stateJson);
        double[] theta = state.RootElement.GetProperty("Theta").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        double max = theta.Max();
        double[] values = theta.Select(value => Math.Exp(value - max)).ToArray();
        double total = values.Sum();
        return Sources.Select((source, index) => new KeyValuePair<string, double>(source, values[index] / total))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    private static void WriteAtomically(string path, string content)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, content); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
