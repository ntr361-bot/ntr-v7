using System.Collections.Immutable;
using 六合分析软件;
using 六合分析软件.MacroReasoning;

public static class MacroHypothesisEngineTests
{
    private static readonly HypothesisType[] ExpectedTypes={HypothesisType.ColdReturnIncreasing,
        HypothesisType.HotPersistenceIncreasing,HypothesisType.TrendSignalDegrading,
        HypothesisType.RepeatRegimeIncreasing,HypothesisType.ModelPerformanceShift,
        HypothesisType.RandomFluctuation,HypothesisType.NoMeaningfulChange};

    public static int Run()
    {
        var engine=new MacroHypothesisEngine();
        MacroObservationSnapshot observation=Observation(200,199,H('a'));
        ReasoningMemorySnapshot empty=Memory(12,199,H('b'));

        HypothesisSet first=engine.Build(observation,empty);
        HypothesisSet second=engine.Build(observation,empty);
        Check(first.Candidates.Select(x=>x.Type).SequenceEqual(ExpectedTypes),"固定生成7类Macro V1假设");
        Check(first.Candidates.Any(x=>x.Type==HypothesisType.RandomFluctuation)
            &&first.Candidates.Any(x=>x.Type==HypothesisType.NoMeaningfulChange),"随机波动与无明显变化始终存在");
        Check(first.AlternativeHypothesisIds.SequenceEqual(first.Candidates.Select(x=>x.HypothesisId)),"替代解释集合包含全部候选");
        Check(first.Candidates.SequenceEqual(second.Candidates)
            &&first.AlternativeHypothesisIds.SequenceEqual(second.AlternativeHypothesisIds),"相同观察和记忆生成完全可复现结果");
        Check(!first.Candidates.Select(x=>x.HypothesisId).SequenceEqual(engine.Build(observation with{SnapshotHash=H('c')},empty).Candidates.Select(x=>x.HypothesisId)),"假设身份绑定Observation哈希");
        Check(!first.Candidates.Select(x=>x.HypothesisId).SequenceEqual(engine.Build(observation,empty with{MemoryHash=H('d')}).Candidates.Select(x=>x.HypothesisId)),"假设身份绑定Memory哈希");
        Check(first.Candidates.All(x=>x.SupportingEvidenceIds.IsEmpty&&x.CounterEvidenceIds.IsEmpty),"P8不伪造支持或反证");
        Check(first.Candidates.All(x=>x.Status==HypothesisStatus.Active&&x.EvaluationDueIssue==210),"候选状态与10期成熟边界冻结");
        Check(first.Candidates.All(x=>Close(x.PriorProbability,.5)&&Close(x.CurrentProbability,.5)&&Close(x.HypothesisScore,0)),"无可靠记忆时使用中性先验");

        var insufficient=empty with{Hypotheses=ImmutableDictionary<string,ReliabilityStats>.Empty.Add(
            HypothesisType.ColdReturnIncreasing.ToString(),new ReliabilityStats(19,19,15,0,0,.8,"fixture")),MemoryHash=H('e')};
        Check(Close(engine.Build(observation,insufficient).Candidates[0].PriorProbability,.5),"少于20个成熟样本不改变先验");

        var learned=empty with{Hypotheses=ImmutableDictionary<string,ReliabilityStats>.Empty.Add(
            HypothesisType.ColdReturnIncreasing.ToString(),new ReliabilityStats(40,20,16,3,2,.8,"fixture")),MemoryHash=H('f')};
        Hypothesis learnedCold=engine.Build(observation,learned).Candidates.Single(x=>x.Type==HypothesisType.ColdReturnIncreasing);
        Check(Close(learnedCold.PriorProbability,.65)&&Close(learnedCold.CurrentProbability,.65),"可靠记忆按样本量向中性先验收缩");

        var bounded=empty with{Hypotheses=ImmutableDictionary<string,ReliabilityStats>.Empty.Add(
            HypothesisType.ColdReturnIncreasing.ToString(),new ReliabilityStats(2000,2000,2000,0,0,1,"fixture")),MemoryHash=H('1')};
        Check(Close(engine.Build(observation,bounded).Candidates[0].PriorProbability,.9),"历史先验严格限制在0.10到0.90");

        var veryLarge=empty with{Hypotheses=ImmutableDictionary<string,ReliabilityStats>.Empty.Add(
            HypothesisType.ColdReturnIncreasing.ToString(),new ReliabilityStats(int.MaxValue,int.MaxValue,int.MaxValue,0,0,1,"fixture")),MemoryHash=H('5')};
        Check(Close(engine.Build(observation,veryLarge).Candidates[0].PriorProbability,.9),"极大样本计数不会造成先验计算溢出");

        Reject(()=>engine.Build(observation,empty with{LastTrainingIssue=200,MemoryHash=H('2')}),"拒绝读取目标期或未来训练记忆");
        Reject(()=>engine.Build(observation,empty with{Hypotheses=ImmutableDictionary<string,ReliabilityStats>.Empty.Add(
            HypothesisType.ColdReturnIncreasing.ToString(),new ReliabilityStats(3,4,2,0,0,.5,"bad")),MemoryHash=H('3')}),"拒绝不可能的记忆计数");
        Reject(()=>engine.Build(observation,empty with{Hypotheses=ImmutableDictionary<string,ReliabilityStats>.Empty.Add(
            HypothesisType.ColdReturnIncreasing.ToString(),new ReliabilityStats(20,20,10,0,0,double.NaN,"bad")),MemoryHash=H('4')}),"拒绝非有限可靠度");
        Reject(()=>engine.Build(observation with{ExpertPool=observation.ExpertPool with{TargetIssue=201}},empty),"拒绝Observation与冻结Pool期号不一致");
        Reject(()=>engine.Build(observation with{SnapshotHash="not-a-hash"},empty),"拒绝无效Observation哈希");

        var history=Enumerable.Range(1,80).Select(i=>new DatabaseHelper.HistoryRecord{Period=(2026000+i).ToString(),SpecialZodiac=Zodiac[i%12],SpecialNumber=((i%49)+1).ToString("00"),OpenTime=new DateTime(2026,1,1).AddDays(i).ToString("yyyy-MM-dd HH:mm:ss")}).ToArray();
        string before=string.Join(",",V7Engine.Predict(history).Top6);
        _=engine.Build(observation,empty);
        string after=string.Join(",",V7Engine.Predict(history).Top6);
        Check(before==after,"P8构建前后正式V7 Top6不变");

        Console.WriteLine("P8 HYPOTHESIS PASS");
        return 0;
    }

    private static MacroObservationSnapshot Observation(long issue,long cutoff,string hash)
    {
        DateTimeOffset asOf=new(2026,9,9,0,0,0,TimeSpan.Zero);
        var pool=new ExpertPoolSnapshot(issue,asOf,"registry",ImmutableArray<string>.Empty,
            ImmutableArray<string>.Empty,ImmutableArray<string>.Empty,ImmutableArray<string>.Empty,
            ImmutableArray<string>.Empty,ImmutableDictionary<string,ImmutableArray<string>>.Empty,
            ImmutableDictionary<string,string>.Empty,HistoricalEvaluationMode.HistoricalAvailability,
            ImmutableDictionary<string,string>.Empty);
        var dependencies=new ExpertDependencySnapshot(ImmutableArray<ExpertPairDependency>.Empty,
            ImmutableDictionary<string,ImmutableArray<string>>.Empty,null,"p7",new IssueRange(0,0,0),
            ImmutableArray<ExpertDependencyEdge>.Empty);
        return new MacroObservationSnapshot(issue,cutoff,ImmutableArray<ObservationFact>.Empty,hash,pool,
            ImmutableArray<ExpertObservation>.Empty,dependencies);
    }

    private static ReasoningMemorySnapshot Memory(long version,long lastIssue,string hash)=>new(version,lastIssue,
        new DateTimeOffset(2026,9,8,0,0,0,TimeSpan.Zero),ImmutableDictionary<string,ReliabilityStats>.Empty,
        ImmutableDictionary<string,ReliabilityStats>.Empty,ImmutableDictionary<string,ReliabilityStats>.Empty,
        ImmutableDictionary<string,ReliabilityStats>.Empty,ImmutableDictionary<string,ReliabilityStats>.Empty,
        ImmutableArray<ConfidenceCalibrationStats>.Empty,hash);
    private static string H(char value)=>new(value,64);
    private static readonly string[] Zodiac={"鼠","牛","虎","兔","龙","蛇","马","羊","猴","鸡","狗","猪"};
    private static bool Close(double value,double expected)=>Math.Abs(value-expected)<1e-10;
    private static void Reject(Action action,string name){try{action();}catch(InvalidDataException){Console.WriteLine("PASS "+name);return;}throw new InvalidOperationException("FAIL "+name);}
    private static void Check(bool condition,string name){if(!condition)throw new InvalidOperationException("FAIL "+name);Console.WriteLine("PASS "+name);}
}
