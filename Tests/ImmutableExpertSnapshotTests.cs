using System.Collections.Immutable;
using 六合分析软件;
using 六合分析软件.MacroReasoning;

public static class ImmutableExpertSnapshotTests
{
    private static readonly string[] Zodiac={"鼠","牛","虎","兔","龙","蛇","马","羊","猴","鸡","狗","猪"};

    public static int Run()
    {
        string dir=Path.Combine(Path.GetTempPath(),"liuhe-p6-snapshot-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        DateTimeOffset registered=new(2026,1,1,0,0,0,TimeSpan.Zero);
        DateTimeOffset freezeAt=new(2026,1,10,12,0,0,TimeSpan.Zero);
        var registry=new VersionedExpertRegistry(Path.Combine(dir,"registry.db"));
        registry.AppendRevision(Reg("A","A@r1",registered));
        registry.AppendRevision(Reg("B","B@r1",registered));
        registry.AppendRevision(Reg("C","C@r1",registered,ExpertModelType.Meta,true));
        registry.AppendDependency(new ExpertDependencyEdge("C","A","C@r1","A@r1",MetaDependencyType.ConsumesExpertRanking,"fixture","v1",ImmutableArray<string>.Empty));
        var store=new ImmutableExpertSnapshotStore(Path.Combine(dir,"snapshots.db"),registry);

        ExpertSnapshot a=store.FreezeAndAppend(Draft("A","A@r1",110,109,freezeAt.AddHours(-1)),freezeAt);
        Check(!string.IsNullOrWhiteSpace(a.PayloadHash)&&ExpertSnapshotIntegrity.Verify(a),"快照生成稳定规范哈希");
        ExpertSnapshot same=store.FreezeAndAppend(Draft("A","A@r1",110,109,freezeAt.AddHours(-1)),freezeAt);
        Check(same.PayloadHash==a.PayloadHash,"完全相同快照重复写入幂等");
        Reject(()=>store.FreezeAndAppend(Draft("A","A@r1",110,109,freezeAt.AddHours(-1),Zodiac.Reverse()),freezeAt),"同身份不同内容拒绝覆盖");
        Reject(()=>store.FreezeAndAppend(Draft("X","X@r1",110,109,freezeAt.AddHours(-1)),freezeAt),"未知Revision拒绝");
        Reject(()=>store.FreezeAndAppend(Draft("A","A@r1",111,110,freezeAt.AddHours(-1)) with{AlgorithmVersion="wrong"},freezeAt),"算法版本错配拒绝");
        Reject(()=>store.FreezeAndAppend(Draft("A","A@r1",111,110,freezeAt.AddHours(-1),Zodiac.Take(11)),freezeAt),"11生肖快照拒绝");
        Reject(()=>store.FreezeAndAppend(Draft("A","A@r1",111,110,freezeAt.AddHours(1)),freezeAt),"LiveFrozen未来时间拒绝");
        Reject(()=>store.FreezeAndAppend(Draft("C","C@r1",110,109,freezeAt.AddHours(-1)),freezeAt),"消费专家缺父快照哈希拒绝");
        ExpertSnapshot c=store.FreezeAndAppend(Draft("C","C@r1",110,109,freezeAt.AddHours(-1)) with{ParentSnapshotHashes=ImmutableArray.Create(a.PayloadHash)},freezeAt);
        Check(c.ParentSnapshotHashes.Single()==a.PayloadHash,"消费专家绑定同目标期父快照");
        Reject(()=>store.FreezeAndAppend(Draft("A","A@r1",50,49,freezeAt.AddHours(-1)) with{Origin=SnapshotOrigin.CausalReconstruction},freezeAt),"重建快照缺Provenance拒绝");
        DateTimeOffset simulated=new(2025,2,1,12,0,0,TimeSpan.Zero);
        ExpertSnapshot reconstructed=store.FreezeAndAppend(Draft("B","B@r1",50,49,freezeAt.AddHours(-1)) with{
            Origin=SnapshotOrigin.CausalReconstruction,
            Reconstruction=new ReconstructionProvenance(true,simulated,freezeAt.AddHours(-1),"rebuild-v1",49,true,"prefix-hash")},freezeAt);
        Check(store.ReadPrefixSnapshots(50,simulated,HistoricalEvaluationMode.CausalReconstruction).Single().PayloadHash==reconstructed.PayloadHash,"因果重建按SimulatedAsOf读取并保留真实重建时间");
        Check(store.ReadPrefixSnapshots(110,freezeAt,HistoricalEvaluationMode.HistoricalAvailability).All(x=>x.Origin==SnapshotOrigin.LiveFrozen),"历史实盘读取不混入重建快照");

        var resolver=new ExpertPoolResolver();
        ExpertRegistration eligibleA=Reg("A","A@r1",registered) with{EligibleForMacro=true,Enabled=true,LeakageAuditStatus=ExpertAuditStatus.Passed,SnapshotIntegrityStatus=ExpertAuditStatus.Passed};
        ExpertRegistration eligibleB=Reg("B","B@r1",registered) with{EligibleForMacro=true,Enabled=true,LeakageAuditStatus=ExpertAuditStatus.Passed,SnapshotIntegrityStatus=ExpertAuditStatus.Passed};
        ExpertRegistration disabledC=Reg("C","C@r1",registered,ExpertModelType.Meta,true);
        var registrySnapshot=registry.ReadAsOf(freezeAt) with{Experts=ImmutableArray.Create(eligibleA,eligibleB,disabledC)};
        ExpertPoolSnapshot pool=resolver.Resolve(registrySnapshot,110,freezeAt,ImmutableArray.Create(a,c),HistoricalEvaluationMode.HistoricalAvailability);
        Check(pool.EligibleExpertIds.SequenceEqual(new[]{"A","B"}),"Eligible只含启用且双审计通过专家");
        Check(pool.AvailableExpertIds.SequenceEqual(new[]{"A","C"}),"Available记录实际提供快照且不等同合法");
        Check(pool.IncludedExpertIds.SequenceEqual(new[]{"A"})&&pool.MissingExpertIds.SequenceEqual(new[]{"B"}),"Included与Missing严格区分且不补位");
        Check(pool.ExcludedExpertIds.Contains("C")&&pool.ExclusionReasons["C"].Any(x=>x.Contains("Enabled=false")),"禁用专家保留明确排除原因");
        Check(pool.IncludedSnapshotHashes["A"]==a.PayloadHash&&pool.ExpertRevisionIds["A"]=="A@r1","Pool冻结Included哈希和Revision");
        Reject(()=>resolver.Resolve(registrySnapshot with{Experts=registrySnapshot.Experts.Add(eligibleA with{ExpertRevisionId="A@r2"})},110,freezeAt,ImmutableArray.Create(a),HistoricalEvaluationMode.HistoricalAvailability),"同Expert双有效Revision拒绝");
        ExpertPoolSnapshot causalPool=resolver.Resolve(registrySnapshot,50,simulated,ImmutableArray.Create(reconstructed),HistoricalEvaluationMode.CausalReconstruction);
        Check(causalPool.AvailableExpertIds.SequenceEqual(new[]{"B"})&&causalPool.IncludedExpertIds.SequenceEqual(new[]{"B"}),"重建模式只纳入合法重建快照");
        ExpertPoolSnapshot wrongMode=resolver.Resolve(registrySnapshot,110,freezeAt,ImmutableArray.Create(a),HistoricalEvaluationMode.CausalReconstruction);
        Check(wrongMode.IncludedExpertIds.Length==0&&wrongMode.ExclusionReasons["A"].Any(x=>x.Contains("Origin")),"Live快照不能混入重建Pool");

        StoredExpertPool stored=store.FreezePool(pool,freezeAt);
        Check(stored.PayloadHash==store.FreezePool(pool,freezeAt.AddMinutes(1)).PayloadHash,"相同冻结Pool重复保存幂等");
        Reject(()=>store.FreezePool(pool with{MissingExpertIds=ImmutableArray<string>.Empty},freezeAt.AddMinutes(2)),"同Pool身份不同内容拒绝覆盖");
        Reject(()=>store.FreezePool(pool with{TargetIssue=111,AsOf=freezeAt.AddMinutes(3)},freezeAt.AddMinutes(3)),"Pool引用不存在的同目标期快照拒绝");
        Check(store.ReadPool(110,freezeAt,HistoricalEvaluationMode.HistoricalAvailability,registrySnapshot.Version)?.PayloadHash==stored.PayloadHash,"冻结Pool可按完整身份读取");

        PrefixContext livePrefix=Prefix(registry,a,pool,freezeAt);
        MacroObservationSnapshot observation=new MacroObservationEngine(registry).Observe(livePrefix);
        Check(observation.ExpertPool.IncludedSnapshotHashes["A"]==a.PayloadHash&&observation.ExpertObservations.Single().ExpertRevisionId=="A@r1","P7读取P6冻结Pool与快照");
        Reject(()=>new MacroObservationEngine(registry).Observe(livePrefix with{ExpertPool=pool with{IncludedSnapshotHashes=pool.IncludedSnapshotHashes.SetItem("A","wrong")}}),"P7拒绝Pool与实际快照哈希不一致");
        Reject(()=>new MacroObservationEngine(registry).Observe(livePrefix with{ExpertSnapshots=ImmutableArray<ExpertSnapshot>.Empty}),"P7拒绝Included专家缺少实际快照");
        PrefixContext causalPrefix=Prefix(registry,reconstructed,causalPool,simulated);
        Check(new MacroObservationEngine(registry).Observe(causalPrefix).ExpertPool.EvaluationMode==HistoricalEvaluationMode.CausalReconstruction,"P7按重建证明而非真实重建时间审核前缀");

        var history=Enumerable.Range(1,80).Select(i=>new DatabaseHelper.HistoryRecord{Period=(2026000+i).ToString(),SpecialZodiac=Zodiac[i%12],SpecialNumber=((i%49)+1).ToString("00"),OpenTime=new DateTime(2026,1,1).AddDays(i).ToString("yyyy-MM-dd HH:mm:ss")}).ToArray();
        string before=string.Join(",",V7Engine.Predict(history).Top6);_=store.ReadPrefixSnapshots(110,freezeAt,HistoricalEvaluationMode.HistoricalAvailability);string after=string.Join(",",V7Engine.Predict(history).Top6);
        Check(before==after,"P6冻结读取前后正式V7 Top6不变");
        registry.AppendRevision(Reg("D","D@r1",freezeAt.AddMinutes(1)));
        Check(store.FreezeAndAppend(Draft("A","A@r1",110,109,freezeAt.AddHours(-1)),freezeAt.AddMinutes(2)).PayloadHash==a.PayloadHash,"注册表追加无关Revision后原快照仍保持幂等");
        Console.WriteLine("P6 SNAPSHOT PASS");
        return 0;
    }

    private static ExpertRegistration Reg(string id,string revision,DateTimeOffset at,ExpertModelType type=ExpertModelType.Base,bool derived=false)=>new(){ExpertId=id,ExpertRevisionId=revision,DisplayName=id,ModelFamily=id,ModelType=type,AlgorithmVersion="algo-v1",CodeVersion="code-v1",IsBaseExpert=type==ExpertModelType.Base,IsMetaExpert=type==ExpertModelType.Meta,DerivedFromExpertOutputs=derived,ParentExpertIds=derived?ImmutableArray.Create("A"):ImmutableArray<string>.Empty,InputDependencyIds=ImmutableArray.Create("history"),PredictionSnapshotType="FullRanking12",HasFullRanking12=true,RegisteredAt=at,EffectiveFrom=at,DependencyEvidenceReference="fixture"};
    private static ExpertSnapshot Draft(string id,string revision,long issue,long cutoff,DateTimeOffset at,IEnumerable<string>? ranking=null)=>new(id,issue,cutoff,at,(ranking??Zodiac).ToImmutableArray(),"","algo-v1","code-v1","fixture-registry",at,SnapshotOrigin.LiveFrozen,ImmutableArray<string>.Empty,ImmutableArray.Create("history"),revision,null);
    private static PrefixContext Prefix(VersionedExpertRegistry registry,ExpertSnapshot snapshot,ExpertPoolSnapshot pool,DateTimeOffset asOf)
    {
        long target=snapshot.TargetIssue;
        var memory=new ReasoningMemorySnapshot(0,0,asOf,ImmutableDictionary<string,ReliabilityStats>.Empty,ImmutableDictionary<string,ReliabilityStats>.Empty,ImmutableDictionary<string,ReliabilityStats>.Empty,ImmutableDictionary<string,ReliabilityStats>.Empty,ImmutableDictionary<string,ReliabilityStats>.Empty,ImmutableArray<ConfidenceCalibrationStats>.Empty,"empty");
        return new PrefixContext(new RunIdentity("p6-test","v1","p6","parameters","code",6501),target,snapshot.HistoryCutoffIssue,asOf,ImmutableArray<ClosedResult>.Empty,ImmutableArray.Create(snapshot),"p6-source",memory,pool);
    }
    private static void Reject(Action action,string name){try{action();}catch(InvalidDataException){Console.WriteLine("PASS "+name);return;}throw new InvalidOperationException("FAIL "+name);}
    private static void Check(bool condition,string name){if(!condition)throw new InvalidOperationException("FAIL "+name);Console.WriteLine("PASS "+name);}
}
