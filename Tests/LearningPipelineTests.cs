using System.Text.Json;
using 六合分析软件;

public static class LearningPipelineTests
{
    public static int Run()
    {
        string[] z = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
        var input = new MetaPredictionInput("100", z.Select((name, i) => new ZodiacMetaFeatures(name,
            new Dictionary<string, double> { ["AI"] = i, ["ML"] = 12-i, ["State"] = i/2d, ["V7"] = 2 },
            new Dictionary<string, double> { ["model_consensus"] = i/12d })).ToArray());
        var memory = new ModelMemoryState { LearnedSamples = 100, LastTrainingIssue = "99" };
        AutoLearningTrainer.LearnOne(input, z, "虎", memory);
        Check(memory.MemoryVersion == 1 && memory.LearnedSamples == 101 && memory.LastTrainingIssue == "100", "LearnOne increments version exactly once");
        string once = JsonSerializer.Serialize(memory);
        AutoLearningTrainer.LearnOne(input, z, "虎", memory);
        Check(JsonSerializer.Serialize(memory) == once, "duplicate cannot mutate meta coefficients");
        memory.RecentFeedback.Clear();
        once = JsonSerializer.Serialize(memory);
        AutoLearningTrainer.LearnOne(input, z, "虎", memory);
        Check(JsonSerializer.Serialize(memory) == once, "old issue rejected even after rolling feedback was trimmed");
        return 0;
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL " + name);
        Console.WriteLine("PASS " + name);
    }
}
