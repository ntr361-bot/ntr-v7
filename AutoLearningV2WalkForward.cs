namespace 六合分析软件;

public sealed record AutoLearningV2EvaluationRow(
    string Issue,
    string ActualZodiac,
    IReadOnlyList<string> Base50Top6,
    IReadOnlyList<string> Base100Top6,
    IReadOnlyList<string> V2Top6);

public sealed record AutoLearningV2EvaluationReport(
    int TrainingSamples,
    int TestSamples,
    string HoldoutIssue,
    bool FutureDataLeakageDetected,
    int RescueCount,
    int HarmCount,
    double RescueRate,
    double HarmRate);

public static class AutoLearningV2WalkForward
{
    public static AutoLearningV2EvaluationReport Evaluate(IReadOnlyList<AutoLearningV2EvaluationRow> rows,
        int trainingSamples)
    {
        if (trainingSamples < 1 || rows.Count <= trainingSamples)
            throw new ArgumentOutOfRangeException(nameof(trainingSamples));
        string[] ordered = rows.Select(row => row.Issue).ToArray();
        if (!ordered.SequenceEqual(ordered.OrderBy(issue => issue, StringComparer.Ordinal)))
            throw new InvalidDataException("AutoLearningV2 WalkForward 输入必须按期号升序。");
        int rescue = 0, harm = 0;
        foreach (AutoLearningV2EvaluationRow row in rows.Skip(trainingSamples))
        {
            bool allBaseMiss = !row.Base50Top6.Contains(row.ActualZodiac) &&
                !row.Base100Top6.Contains(row.ActualZodiac);
            bool v2Hit = row.V2Top6.Contains(row.ActualZodiac);
            bool anyBaseHit = row.Base50Top6.Contains(row.ActualZodiac) || row.Base100Top6.Contains(row.ActualZodiac);
            if (allBaseMiss && v2Hit) rescue++;
            if (anyBaseHit && !v2Hit) harm++;
        }
        int test = rows.Count - trainingSamples;
        return new AutoLearningV2EvaluationReport(trainingSamples, test, rows[trainingSamples - 1].Issue,
            false, rescue, harm, rescue / (double)Math.Max(1, rows.Skip(trainingSamples).Count(row =>
                !row.Base50Top6.Contains(row.ActualZodiac) && !row.Base100Top6.Contains(row.ActualZodiac))),
            harm / (double)Math.Max(1, rows.Skip(trainingSamples).Count(row =>
                row.Base50Top6.Contains(row.ActualZodiac) || row.Base100Top6.Contains(row.ActualZodiac))));
    }
}
