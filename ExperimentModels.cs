namespace 六合分析软件;

/// <summary>Stable identities for the four independent V6.5 experiments.</summary>
public static class ExperimentModels
{
    public const string Period50 = "v65-50";
    public const string Period100 = "v65-100";
    public const string AllHistory = "v65-all";
    public const string AutoLearning = "v65-auto";
    // 智能预测历史是另一套独立模型，不属于 V6.5 四模型实验。
    public const string IntelligentHistory = "intelligent-history";

    public static IReadOnlyList<string> AllKeys { get; } =
        new[] { Period50, Period100, AllHistory, AutoLearning };

    public static string ForPeriods(int periods) => periods switch
    {
        50 => Period50,
        100 => Period100,
        _ => AllHistory
    };

    public static string MemoryKey(string experimentKey) => $"auto-learning-meta-v2|{experimentKey}";
}
