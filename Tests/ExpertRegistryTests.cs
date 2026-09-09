using System.Collections.Immutable;
using 六合分析软件;
using 六合分析软件.MacroReasoning;

public static class ExpertRegistryTests
{
    public static int Run()
    {
        string dir=Path.Combine(Path.GetTempPath(),"liuhe-p5-registry-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var registry=new VersionedExpertRegistry(Path.Combine(dir,"registry.db"));
        DateTimeOffset t=new(2026,9,9,0,0,0,TimeSpan.Zero);
        var a=Registration("A","A@r1",t);
        registry.AppendRevision(a);
        Reject(()=>registry.AppendRevision(a),"duplicate revision拒绝");
        Reject(()=>registry.AppendRevision(Registration("conflict","A@r1",t)),"conflicting revision identity拒绝");
        registry.AppendRevision(Registration("A","A@r2",t.AddMinutes(1)));
        Check(registry.ReadAsOf(t.AddHours(1)).Experts.Count(x=>x.ExpertId=="A")==2,"append-only revision");
        Reject(()=>registry.AppendDependency(Edge("A","A@r1","MISSING","MISSING@r1",MetaDependencyType.ConsumesExpertRanking)),"unknown dependency拒绝");
        registry.AppendRevision(Registration("B","B@r1",t));
        registry.AppendDependency(Edge("A","A@r1","B","B@r1",MetaDependencyType.ConsumesExpertRanking));
        Reject(()=>registry.AppendDependency(Edge("B","B@r1","A","A@r1",MetaDependencyType.ConsumesExpertScore)),"dependency cycle拒绝");
        registry.AppendRevision(Registration("C","C@r1",t));
        registry.AppendDependency(Edge("B","B@r1","C","C@r1",MetaDependencyType.SharedHistory));
        Check(registry.DependencyDepth("C@r1")==0 && registry.DependencyDepth("B@r1")==0,"shared relation不增加Depth");
        Check(registry.DependencyDepth("A@r1")==1,"消费边DependencyDepth计算");
        Check(registry.ReadAsOf(t.AddHours(1)).Experts.Single(x=>x.ExpertRevisionId=="A@r1").DependencyDepth==1,
            "读取注册表时Depth只由消费DAG派生");
        Reject(()=>registry.FreezePool(2026250,t.AddHours(1),new[]{"A@r1","A@r2"}),"同Expert双Revision不能进入一期Pool");
        Check(!a.EligibleForMacro && !a.Enabled,"默认Eligible和Enabled关闭");

        string[] zodiac={"鼠","牛","虎","兔","龙","蛇","马","羊","猴","鸡","狗","猪"};
        var history=Enumerable.Range(1,80).Select(i=>new DatabaseHelper.HistoryRecord{Period=(2026000+i).ToString(),SpecialZodiac=zodiac[i%12],SpecialNumber=((i%49)+1).ToString("00"),OpenTime=new DateTime(2026,1,1).AddDays(i).ToString("yyyy-MM-dd HH:mm:ss")}).ToArray();
        string before=string.Join(",",V7Engine.Predict(history).Top6);
        CurrentExpertCatalog.Seed(registry,t.AddMinutes(2),"p5-test-code");
        string after=string.Join(",",V7Engine.Predict(history).Top6);
        Check(before==after,"P5注册前后正式V7 Top6不变");
        var current=registry.ReadAsOf(t.AddHours(1));
        string[] ids={"V65-50","V65-100","V65-All","V65-Auto","Integrated-V7","V7-Auto"};
        Check(ids.All(id=>current.Experts.Any(x=>x.ExpertId==id && !x.EligibleForMacro && !x.Enabled)),"六个真实候选均登记且默认关闭");
        Check(current.Experts.Where(x=>ids.Contains(x.ExpertId)).All(x=>!string.IsNullOrWhiteSpace(x.AlgorithmVersion)&&!string.IsNullOrWhiteSpace(x.CodeVersion)&&!string.IsNullOrWhiteSpace(x.DependencyEvidenceReference)),"版本与源码证据完整");
        Check(current.Experts.Single(x=>x.ExpertId=="V65-Auto").DependencyDepth==1 && current.Experts.Where(x=>x.ExpertId!="V65-Auto"&&ids.Contains(x.ExpertId)).All(x=>x.DependencyDepth==0),"当前目录Depth来自真实消费DAG");
        var dependencies=registry.ReadDependencies();
        Check(dependencies.Count(x=>x.FromExpertId=="V65-Auto"&&x.DependencyType==MetaDependencyType.ConsumesExpertRanking)==4,"V65-Auto四条排名消费边来自源码");
        Check(!dependencies.Any(x=>x.FromExpertId=="V7-Auto"&&x.DependencyType is MetaDependencyType.ConsumesExpertRanking or MetaDependencyType.ConsumesExpertScore or MetaDependencyType.ConsumesMetaOutput),"V7-Auto不凭名称虚构专家消费边");
        Check(dependencies.Any(x=>new[]{x.FromExpertId,x.ToExpertId}.Contains("V7-Auto")&&x.DependencyType==MetaDependencyType.SharedFeatures),"V7-Auto与Integrated-V7共享特征关系单独登记");
        Console.WriteLine("P5 REGISTRY PASS");
        return 0;
    }

    private static ExpertRegistration Registration(string id,string revision,DateTimeOffset at)=>new(){ExpertId=id,ExpertRevisionId=revision,DisplayName=id,ModelFamily=id,ModelType=ExpertModelType.Base,AlgorithmVersion="test",CodeVersion="test",PredictionSnapshotType="FullRanking12",HasFullRanking12=true,RegisteredAt=at,EffectiveFrom=at,DependencyEvidenceReference="test"};
    private static ExpertDependencyEdge Edge(string from,string fromRevision,string to,string toRevision,MetaDependencyType type)=>new(from,to,fromRevision,toRevision,type,"test","v1",ImmutableArray<string>.Empty);
    private static void Reject(Action action,string name){try{action();}catch(InvalidDataException){Console.WriteLine("PASS "+name);return;}throw new InvalidOperationException("FAIL "+name);}
    private static void Check(bool condition,string name){if(!condition)throw new InvalidOperationException("FAIL "+name);Console.WriteLine("PASS "+name);}
}
