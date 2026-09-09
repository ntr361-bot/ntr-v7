using System.Collections.Immutable;
using 六合分析软件;
using 六合分析软件.MacroReasoning;

public static class MacroObservationEngineTests
{
    private static readonly string[] Zodiac={"鼠","牛","虎","兔","龙","蛇","马","羊","猴","鸡","狗","猪"};

    public static int Run()
    {
        string dir=Path.Combine(Path.GetTempPath(),"liuhe-p7-observation-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var registry=new VersionedExpertRegistry(Path.Combine(dir,"registry.db"));
        DateTimeOffset registered=new(2026,1,1,0,0,0,TimeSpan.Zero);
        registry.AppendRevision(Reg("A","A@r1",registered,"Family-A"));
        registry.AppendRevision(Reg("B","B@r1",registered,"Family-B"));
        registry.AppendDependency(new ExpertDependencyEdge("A","B","A@r1","B@r1",MetaDependencyType.SharedHistory,"fixture","v1",ImmutableArray<string>.Empty));
        registry.AppendDependency(new ExpertDependencyEdge("A","B","A@r1","B@r1",MetaDependencyType.ConsumesExpertRanking,"fixture-consumes","v1",ImmutableArray<string>.Empty));
        var engine=new MacroObservationEngine(registry);
        PrefixContext prefix=BuildPrefix(registry,registered);

        MacroObservationSnapshot first=engine.Observe(prefix);
        MacroObservationSnapshot second=engine.Observe(prefix);
        Check(first.SnapshotHash==second.SnapshotHash,"相同前缀产生确定性Observation哈希");
        Check(first.SnapshotHash!=engine.Observe(prefix with{SourceManifestHash="fixture-changed"}).SnapshotHash,"Observation哈希绑定输入来源清单");
        Check(first.ExpertObservations.Length==2,"动态N专家均进入观察结果");

        ExpertObservation a=first.ExpertObservations.Single(x=>x.ExpertId=="A");
        Check(Close(Fact(a,"Top3Rate",10).Value,.75),"A最近窗口Top3手算一致");
        Check(Close(Fact(a,"Top6Rate",10).Value,1),"A最近窗口Top6手算一致");
        Check(Close(Fact(a,"MeanRank",10).Value,2.5),"A最近窗口平均名次手算一致");
        Check(Close(Fact(a,"MRR",10).Value,(1+1d/2+1d/3+1d/4)/4),"A最近窗口MRR手算一致");
        ExpertObservation b=first.ExpertObservations.Single(x=>x.ExpertId=="B");
        Check(Close(Fact(b,"Top6Rate",10).Value,0),"B最近窗口Top6手算一致");
        Check(a.ExpertRevisionId=="A@r1"&&b.ExpertRevisionId=="B@r1","专家统计按Revision隔离");

        ExpertPairDependency pair=first.Dependencies.Pairs.Single();
        Check(pair.CommonSamples==4&&Close(pair.RankingCorrelation[10],-1),"两专家完整反序Spearman为-1");
        Check(Close(pair.Top6Overlap,0)&&pair.ResidualDiversity==1,"Top6重叠和多样性可重复计算");
        Check(pair.ParentDependency&&first.Dependencies.DependencyGroups.Count==1&&first.Dependencies.MetaDependencyEdges.Length==2,"共享关系与排名消费边分开保留且消费边标记父子");
        Check(first.Dependencies.EffectiveIndependentExpertCount is null,"有效独立专家数无验证公式时保持未知");
        Check(a.DependencyPenalty is null&&a.RescueRate is null&&a.HarmRate is null&&a.NetImpact is null,"不虚构依赖惩罚或Rescue/Harm");
        Check(a.MarginalContribution.CommonSamples==0&&a.MarginalContribution.ControlEnsembleId.Contains("unavailable"),"没有冻结对照组合时边际贡献明确不可用");
        Check(a.State==ExpertState.NeutralExpert&&a.SignedReliability.Value is null,"P7不擅自给专家定性或方向分");

        Check(Close(Global(first,"ImmediateRepeatRate",10).Value,0),"Immediate Repeat只读已揭晓前缀");
        Check(Close(Global(first,"Gap1RepeatRate",10).Value,0),"Gap1 Repeat计算正确");
        Check(Close(Global(first,"Gap2RepeatRate",10).Value,0),"Gap2 Repeat计算正确");
        Check(Close(Global(first,"ZodiacConcentration",10).Value,.25),"生肖分布集中度使用HHI");
        Check(Global(first,"Omission:鼠",0).Value==3&&Global(first,"Omission:虎",0).Value==1,"当前遗漏按最近已揭晓顺序计算");
        Check(Global(first,"TrendReliability",20).Value is null&&Global(first,"TrendReliability",20).EffectiveSamples==0,"缺少冻结趋势贡献时显式记录missing");

        Reject(()=>engine.Observe(prefix with{PastResults=prefix.PastResults.Add(Result(105,"龙",prefix.AsOf.AddMinutes(-1))) }),"目标期开奖不得进入Observation");
        Reject(()=>engine.Observe(prefix with{PastResults=prefix.PastResults.Add(Result(106,"蛇",prefix.AsOf.AddMinutes(-1))) }),"未来期开奖不得进入Observation");
        Reject(()=>engine.Observe(prefix with{ExpertSnapshots=prefix.ExpertSnapshots.Add(Snapshot("A","A@r1",106,105,Zodiac,prefix.AsOf.AddMinutes(-1))) }),"未来目标快照不得进入Observation");
        Reject(()=>engine.Observe(prefix with{ExpertSnapshots=prefix.ExpertSnapshots.SetItem(0,prefix.ExpertSnapshots[0] with{AvailableAt=prefix.AsOf.AddMinutes(1)})}),"AsOf以后才可用的快照被拒绝");
        Reject(()=>engine.Observe(prefix with{ExpertSnapshots=prefix.ExpertSnapshots.SetItem(0,prefix.ExpertSnapshots[0] with{HistoryCutoffIssue=prefix.ExpertSnapshots[0].TargetIssue})}),"非前缀HistoryCutoff被拒绝");
        Reject(()=>engine.Observe(prefix with{ExpertSnapshots=prefix.ExpertSnapshots.SetItem(0,prefix.ExpertSnapshots[0] with{Ranking=Zodiac.Take(11).ToImmutableArray()})}),"不完整12生肖排名被拒绝");
        Reject(()=>engine.Observe(prefix with{ExpertSnapshots=prefix.ExpertSnapshots.Add(prefix.ExpertSnapshots[0])}),"重复专家期快照被拒绝");

        var history=Enumerable.Range(1,80).Select(i=>new DatabaseHelper.HistoryRecord{Period=(2026000+i).ToString(),SpecialZodiac=Zodiac[i%12],SpecialNumber=((i%49)+1).ToString("00"),OpenTime=new DateTime(2026,1,1).AddDays(i).ToString("yyyy-MM-dd HH:mm:ss")}).ToArray();
        string before=string.Join(",",V7Engine.Predict(history).Top6);
        _=engine.Observe(prefix);
        string after=string.Join(",",V7Engine.Predict(history).Top6);
        Check(before==after,"P7观察前后正式V7 Top6不变");

        Console.WriteLine("P7 OBSERVATION PASS");
        return 0;
    }

    private static PrefixContext BuildPrefix(VersionedExpertRegistry registry,DateTimeOffset registered)
    {
        DateTimeOffset asOf=new(2026,1,6,12,0,0,TimeSpan.Zero);
        var results=ImmutableArray.Create(
            Result(101,"鼠",new DateTimeOffset(2026,1,2,0,0,0,TimeSpan.Zero)),
            Result(102,"牛",new DateTimeOffset(2026,1,3,0,0,0,TimeSpan.Zero)),
            Result(103,"虎",new DateTimeOffset(2026,1,4,0,0,0,TimeSpan.Zero)),
            Result(104,"兔",new DateTimeOffset(2026,1,5,0,0,0,TimeSpan.Zero)));
        string registryVersion=registry.ReadAsOf(asOf).Version;
        var snapshots=ImmutableArray.CreateBuilder<ExpertSnapshot>();
        foreach(long issue in new long[]{101,102,103,104,105})
        {
            DateTimeOffset available=new(2026,1,(int)(issue-100),12,0,0,TimeSpan.Zero);
            snapshots.Add(ExpertSnapshotIntegrity.Seal(Snapshot("A","A@r1",issue,issue-1,Zodiac,available) with{RegistryVersion=registryVersion}));
            snapshots.Add(ExpertSnapshotIntegrity.Seal(Snapshot("B","B@r1",issue,issue-1,Zodiac.Reverse().ToArray(),available) with{RegistryVersion=registryVersion}));
        }
        ExpertSnapshot currentA=snapshots.Single(x=>x.ExpertId=="A"&&x.TargetIssue==105);
        ExpertSnapshot currentB=snapshots.Single(x=>x.ExpertId=="B"&&x.TargetIssue==105);
        var pool=new ExpertPoolSnapshot(105,asOf,registry.ReadAsOf(asOf).Version,
            ImmutableArray.Create("A","B"),ImmutableArray.Create("A","B"),ImmutableArray.Create("A","B"),
            ImmutableArray<string>.Empty,ImmutableArray<string>.Empty,
            ImmutableDictionary<string,ImmutableArray<string>>.Empty,
            ImmutableDictionary<string,string>.Empty.Add("A",currentA.PayloadHash).Add("B",currentB.PayloadHash),HistoricalEvaluationMode.HistoricalAvailability,
            ImmutableDictionary<string,string>.Empty.Add("A","A@r1").Add("B","B@r1"));
        var memory=new ReasoningMemorySnapshot(0,0,registered,ImmutableDictionary<string,ReliabilityStats>.Empty,
            ImmutableDictionary<string,ReliabilityStats>.Empty,ImmutableDictionary<string,ReliabilityStats>.Empty,
            ImmutableDictionary<string,ReliabilityStats>.Empty,ImmutableDictionary<string,ReliabilityStats>.Empty,
            ImmutableArray<ConfidenceCalibrationStats>.Empty,"empty");
        return new PrefixContext(new RunIdentity("p7-test","v1","p7","parameters","code",6501),105,104,asOf,
            results,snapshots.ToImmutable(),"fixture",memory,pool);
    }

    private static ExpertRegistration Reg(string id,string revision,DateTimeOffset at,string family)=>new(){ExpertId=id,ExpertRevisionId=revision,DisplayName=id,ModelFamily=family,ModelType=ExpertModelType.Base,AlgorithmVersion="test",CodeVersion="test",PredictionSnapshotType="FullRanking12",HasFullRanking12=true,RegisteredAt=at,EffectiveFrom=at,DependencyEvidenceReference="fixture"};
    private static ClosedResult Result(long issue,string zodiac,DateTimeOffset opened)=>new(issue,zodiac,opened,opened.AddMinutes(1),"r"+issue);
    private static ExpertSnapshot Snapshot(string id,string revision,long issue,long cutoff,IEnumerable<string> ranking,DateTimeOffset at)=>new(id,issue,cutoff,at,ranking.ToImmutableArray(),id+"-"+issue,"test","test","p5",at,SnapshotOrigin.LiveFrozen,ImmutableArray<string>.Empty,ImmutableArray<string>.Empty,revision,null);
    private static ObservationFact Fact(ExpertObservation x,string name,int window)=>x.WindowMetrics.Single(f=>f.SignalName==name&&f.Window==window);
    private static ObservationFact Global(MacroObservationSnapshot x,string name,int window)=>x.Facts.Single(f=>f.SignalName==name&&f.Window==window);
    private static bool Close(double? value,double expected)=>value.HasValue&&Math.Abs(value.Value-expected)<1e-10;
    private static void Reject(Action action,string name){try{action();}catch(InvalidDataException){Console.WriteLine("PASS "+name);return;}throw new InvalidOperationException("FAIL "+name);}
    private static void Check(bool condition,string name){if(!condition)throw new InvalidOperationException("FAIL "+name);Console.WriteLine("PASS "+name);}
}
