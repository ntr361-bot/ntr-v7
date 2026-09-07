using System.Globalization;
using System.Text.Json;

namespace 六合分析软件;

/// <summary>Daily sidecar: caller supplies read-only production snapshots; all writes belong to the third learner.</summary>
public static class IndependentLearningDaily
{
    private static readonly string[] Keys={"v65-50","v65-100","v65-all"};

    public static string Run(string dataRoot,long target,IReadOnlyList<DatabaseHelper.HistoryRecord> history,
        IReadOnlyList<DatabaseHelper.PredictionRecord> records)
    {
        var draws=history.OrderBy(h=>long.Parse(h.Period)).ToArray();
        if(draws.Length==0 || draws.Select(d=>d.Period).Distinct().Count()!=draws.Length || long.Parse(draws[^1].Period)>=target)
            throw new InvalidDataException("旁路日更只接受目标期之前的真实开奖，禁止历史补造");
        var model=new IndependentLearningModel(dataRoot);
        foreach(string pending in model.PendingPredictions()) {
            using var doc=JsonDocument.Parse(pending); var input=doc.RootElement.GetProperty("Input");
            long issue=input.GetProperty("Issue").GetInt64();
            var draw=draws.SingleOrDefault(d=>d.Period==issue.ToString());
            if(draw is null) continue;
            if(!TryTime(draw.OpenTime,out var opened) || opened>DateTimeOffset.UtcNow || opened<=doc.RootElement.GetProperty("GeneratedAt").GetDateTimeOffset())
                throw new InvalidDataException("开奖时间缺失或预测晚于开奖，禁止把补造预测计入学习");
            model.Learn(issue,draw.SpecialZodiac,input.GetProperty("HistoryCutoffIssue").GetInt64());
        }
        string? existing=model.ReadPredictionJson(target);
        if(existing is not null) return existing;
        long cutoff=long.Parse(draws[^1].Period);
        if(!TryTime(draws[^1].OpenTime,out var cutoffTime)) throw new InvalidDataException("最新开奖缺少可验证时间");
        var bases=records.Where(r=>r.Issue==target.ToString() && r.ModelVersion=="V6.5")
            .GroupBy(r=>ExperimentModels.ForPeriods(r.AnalysisPeriods)).ToDictionary(g=>g.Key,g=>g.Single());
        if(bases.Count!=3 || Keys.Any(k=>!bases.ContainsKey(k))) throw new InvalidDataException("旁路日更缺少三条基础快照");
        var times=Keys.Select(k=>TryTime(bases[k].PredictTime,out var t)?t:throw new InvalidDataException("基础预测时间无效")).ToArray();
        if(times.Any(t=>t<cutoffTime || t>DateTimeOffset.UtcNow)) throw new InvalidDataException("基础快照不是最新开奖后的可用预测");
        return model.Predict(JsonSerializer.Serialize(new {
            Issue=target,HistoryCutoffIssue=cutoff,SourceGeneratedAt=times.Max(),
            SourceRankings=Keys.ToDictionary(k=>k,k=>JsonSerializer.Deserialize<string[]>(bases[k].FinalRankingJson))
        }));
    }

    // Legacy timestamps without an offset are China local time, regardless of the cloud host timezone.
    private static bool TryTime(string text,out DateTimeOffset value)
    {
        if(DateTimeOffset.TryParseExact(text,new[]{"O","yyyy-MM-dd'T'HH:mm:sszzz","yyyy-MM-dd'T'HH:mm:ss'Z'"},CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out value)) return true;
        if(DateTime.TryParseExact(text,new[]{"yyyy-MM-dd HH:mm:ss","yyyy/MM/dd HH:mm:ss"},CultureInfo.InvariantCulture,DateTimeStyles.None,out var local)) {
            value=new DateTimeOffset(local,TimeSpan.FromHours(8));return true;
        }
        return false;
    }

    public static void TryRunDesktop(long target)
    {
        try { Run(AppPaths.DataDirectory,target,DatabaseHelper.GetHistory(),DatabaseHelper.GetPredictionHistory(int.MaxValue)); }
        catch(Exception ex) { AppLogger.Error("独立学习旁路日更",ex); }
    }
}
