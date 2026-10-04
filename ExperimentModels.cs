namespace 六合分析软件;

/// <summary>Stable identities for the four independent V6.5 experiments.</summary>
public static class ExperimentModels
{
    public const string Period50 = "v65_50";
    public const string Period100 = "v65_100";
    public const string AllHistory = "v65_all";
    public const string AutoLearning = "v65_auto";
    public const string V7 = "v7";
    public const string V7Auto = "v7_auto";

    public static string DisplayName(string modelId) => modelId switch
    {
        Period50 => "50期",
        Period100 => "100期",
        AllHistory => "全历史",
        AutoLearning => "自学习",
        V7 => "V7",
        V7Auto => "V7学习",
        _ => modelId
    };

    public static string Canonicalize(string value, int analysisPeriods = 0) => value switch
    {
        "v65-50" => Period50,
        "v65-100" => Period100,
        "v65-all" => AllHistory,
        "v65-auto" => AutoLearning,
        "V6.5" when analysisPeriods == 50 => Period50,
        "V6.5" when analysisPeriods == 100 => Period100,
        "V6.5" => AllHistory,
        "V6.5 AutoLearning" => AutoLearning,
        "AI生肖预测 V7" => V7,
        "V7" => V7,
        "V7 AutoLearning" => V7Auto,
        _ => value
    };

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
