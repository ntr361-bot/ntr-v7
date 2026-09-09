public static class P4AcceptanceTests
{
    public static int Run()
    {
        string root = FindRoot();
        string mainProject = File.ReadAllText(Path.Combine(root, "六合分析软件.csproj"));
        string testProject = File.ReadAllText(Path.Combine(root, "Tests", "六合分析软件.SmokeTests.csproj"));
        string program = File.ReadAllText(Path.Combine(root, "Tests", "Program.cs"));

        Check(mainProject.Contains("<Compile Remove=\"OnlineLearningPipeline.cs\"", StringComparison.Ordinal),
            "旧 OnlineLearningPipeline 保持排除主程序编译");
        Check(testProject.Contains("<Compile Remove=\"LearningPipelineTests.cs\"", StringComparison.Ordinal),
            "旧 LearningPipelineTests 保持排除测试编译");
        Check(program.Contains("return RejectLegacyLearningPipelineAlias();", StringComparison.Ordinal),
            "废弃别名必须明确拒绝，不能静默运行另一套学习测试");

        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}Tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !Path.GetFileName(path).Equals("OnlineLearningPipeline.cs", StringComparison.OrdinalIgnoreCase)))
            Check(!File.ReadAllText(file).Contains("OnlineLearningPipeline.", StringComparison.Ordinal),
                $"正式源码不调用旧管线：{Path.GetFileName(file)}");

        int result = IndependentLearningTests.Run();
        Check(result == 0, "V7独立学习完整行为验收");
        Console.WriteLine("P4 ACCEPTANCE PASS");
        return 0;
    }

    private static string FindRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "六合分析软件.csproj"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("找不到V7项目根目录");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("FAIL " + name);
        Console.WriteLine("PASS " + name);
    }
}
