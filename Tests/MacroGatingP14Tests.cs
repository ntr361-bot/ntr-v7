using System.Collections.Immutable;
using 六合分析软件.MacroReasoning;

public static class MacroGatingP14Tests
{
    private static readonly ImmutableArray<string> Zodiac =
        ImmutableArray.Create("鼠","牛","虎","兔","龙","蛇","马","羊","猴","鸡","狗","猪");

    public static int Run()
    {
        var gating=new MacroGatingModel();
        ExpertSnapshot a=Snapshot("A","A@r1",Zodiac);
        ExpertSnapshot b=Snapshot("B","B@r1",Zodiac.Reverse().ToImmutableArray());

        Check(gating.Rank(Arr(a),Weights(("A",1)),Arr(Decision("A","A@r1",ExpertActionMode.FOLLOW,1)),ImmutableArray<ExpertCounterView>.Empty)
            .SequenceEqual(Zodiac),"P14单专家保持原始完整排名");

        ImmutableArray<string> weighted=gating.Rank(Arr(a,b),Weights(("A",.75),("B",.25)),
            Arr(Decision("A","A@r1",ExpertActionMode.FOLLOW,.75),Decision("B","B@r1",ExpertActionMode.FOLLOW,.25)),ImmutableArray<ExpertCounterView>.Empty);
        Check(weighted.SequenceEqual(Zodiac),"P14按已批准权重执行动态N专家Borda聚合");

        ExpertSnapshot[] six=Enumerable.Range(1,6).Select(i=>Snapshot("N"+i,"N"+i+"@r1",Zodiac)).ToArray();
        Check(gating.Rank(six.ToImmutableArray(),six.ToImmutableDictionary(x=>x.ExpertId,_=>1d/6,StringComparer.Ordinal),
            six.Select(x=>Decision(x.ExpertId,x.ExpertRevisionId,ExpertActionMode.FOLLOW,1d/6)).ToImmutableArray(),
            ImmutableArray<ExpertCounterView>.Empty).SequenceEqual(Zodiac),"P14接口真实支持动态六专家而非固定四槽位");

        ImmutableArray<string> tie1=gating.Rank(Arr(a,b),Weights(("A",.5),("B",.5)),
            Arr(Decision("A","A@r1",ExpertActionMode.FOLLOW,.5),Decision("B","B@r1",ExpertActionMode.FOLLOW,.5)),ImmutableArray<ExpertCounterView>.Empty);
        ImmutableArray<string> tie2=gating.Rank(Arr(b,a),Weights(("B",.5),("A",.5)),
            Arr(Decision("B","B@r1",ExpertActionMode.FOLLOW,.5),Decision("A","A@r1",ExpertActionMode.FOLLOW,.5)),ImmutableArray<ExpertCounterView>.Empty);
        Check(tie1.SequenceEqual(tie2)&&tie1.SequenceEqual(Zodiac),"P14平分时使用固定生肖次序且不依赖输入顺序");

        Check(gating.Rank(Arr(a,b),Weights(("A",1),("B",0)),
            Arr(Decision("A","A@r1",ExpertActionMode.FOLLOW,1),Decision("B","B@r1",ExpertActionMode.IGNORE,0)),ImmutableArray<ExpertCounterView>.Empty)
            .SequenceEqual(Zodiac),"P14 IGNORE保留观察但不影响排名");

        ExpertCounterView counter=Counter(b);
        Check(gating.Rank(Arr(b),Weights(("B",1)),Arr(Decision("B","B@r1",ExpertActionMode.COUNTER,1)),Arr(counter))
            .SequenceEqual(Zodiac),"P14 COUNTER只消费显式匹配的冻结反向视图");

        Reject(()=>gating.Rank(Arr(a),Weights(("A",1),("B",0)),Arr(Decision("A","A@r1",ExpertActionMode.FOLLOW,1)),ImmutableArray<ExpertCounterView>.Empty),"P14拒绝权重支持集不一致");
        Reject(()=>gating.Rank(Arr(a),Weights(("A",.9)),Arr(Decision("A","A@r1",ExpertActionMode.FOLLOW,.9)),ImmutableArray<ExpertCounterView>.Empty),"P14拒绝权重和不为1");
        Reject(()=>gating.Rank(Arr(a),Weights(("A",1)),Arr(Decision("A","A@r2",ExpertActionMode.FOLLOW,1)),ImmutableArray<ExpertCounterView>.Empty),"P14拒绝Revision错配");
        Reject(()=>gating.Rank(Arr(a with { AvailableAt=new DateTimeOffset(2026,9,8,22,0,0,TimeSpan.FromHours(8)) }),Weights(("A",1)),
            Arr(Decision("A","A@r1",ExpertActionMode.FOLLOW,1)),ImmutableArray<ExpertCounterView>.Empty),"P14拒绝AsOf时尚不可见的专家快照");
        Reject(()=>gating.Rank(Arr(a,a),Weights(("A",1)),Arr(Decision("A","A@r1",ExpertActionMode.FOLLOW,1)),ImmutableArray<ExpertCounterView>.Empty),"P14拒绝重复专家");
        Reject(()=>gating.Rank(Arr(Snapshot("A","A@r1",Zodiac.RemoveAt(11))),Weights(("A",1)),Arr(Decision("A","A@r1",ExpertActionMode.FOLLOW,1)),ImmutableArray<ExpertCounterView>.Empty),"P14拒绝不完整排名");
        Reject(()=>gating.Rank(Arr(Snapshot("A","A@r1",Zodiac.SetItem(11,"鼠"))),Weights(("A",1)),Arr(Decision("A","A@r1",ExpertActionMode.FOLLOW,1)),ImmutableArray<ExpertCounterView>.Empty),"P14拒绝重复生肖排名");
        Reject(()=>gating.Rank(Arr(b),Weights(("B",1)),Arr(Decision("B","B@r1",ExpertActionMode.COUNTER,1)),ImmutableArray<ExpertCounterView>.Empty),"P14拒绝隐式COUNTER或缺失视图");
        Reject(()=>gating.Rank(Arr(b),Weights(("B",1)),Arr(Decision("B","B@r1",ExpertActionMode.COUNTER,1)),Arr(counter with { OriginalSnapshotHash="wrong" })),"P14拒绝CounterView来源哈希错配");
        Reject(()=>gating.Rank(Arr(b),Weights(("B",1)),Arr(Decision("B","B@r1",ExpertActionMode.COUNTER,1)),
            Arr(counter with { CounterRanking=Zodiac.Skip(1).Append(Zodiac[0]).ToImmutableArray() })),"P14拒绝不符合声明版本的反向视图");
        Reject(()=>gating.Rank(Arr(a,b),Weights(("A",0),("B",0)),
            Arr(Decision("A","A@r1",ExpertActionMode.IGNORE,0),Decision("B","B@r1",ExpertActionMode.IGNORE,0)),ImmutableArray<ExpertCounterView>.Empty),"P14全部IGNORE时拒绝除零和伪排名");

        Console.WriteLine("P14 GATING PASS");
        return 0;
    }

    private static ExpertSnapshot Snapshot(string id,string revision,ImmutableArray<string> ranking)=>new(id,300,299,
        new DateTimeOffset(2026,9,8,20,0,0,TimeSpan.FromHours(8)),ranking,"hash-"+id,"algo","code","registry",
        new DateTimeOffset(2026,9,8,20,0,1,TimeSpan.FromHours(8)),SnapshotOrigin.LiveFrozen,
        ImmutableArray<string>.Empty,ImmutableArray<string>.Empty,revision,null);

    private static ExpertDecision Decision(string id,string revision,ExpertActionMode mode,double weight)=>new(id,revision,mode,
        null,null,null,null,null,null,null,weight,weight,weight,.6,ImmutableArray.Create("fixture"),ImmutableArray<string>.Empty,
        new ExpertModeSelection(300,299,new DateTimeOffset(2026,9,8,21,0,0,TimeSpan.FromHours(8)),
            new DateTimeOffset(2026,9,8,20,45,0,TimeSpan.FromHours(8)),"fixture","evidence",
            mode==ExpertActionMode.COUNTER?CounterTransformDefinition.CounterTransformId:null,
            mode==ExpertActionMode.COUNTER?CounterTransformDefinition.Version:null));

    private static ExpertCounterView Counter(ExpertSnapshot snapshot)=>new(snapshot.ExpertId,snapshot.ExpertRevisionId,snapshot.TargetIssue,
        snapshot.PayloadHash,snapshot.Ranking,snapshot.Ranking.Reverse().ToImmutableArray(),CounterTransformDefinition.CounterTransformId,
        CounterTransformDefinition.Version,new DateTimeOffset(2026,9,8,20,31,0,TimeSpan.FromHours(8)),"view-hash","validated-fixture");
    private static ImmutableArray<T> Arr<T>(params T[] values)=>values.ToImmutableArray();
    private static ImmutableDictionary<string,double> Weights(params (string Id,double Value)[] values)=>
        values.ToImmutableDictionary(x=>x.Id,x=>x.Value,StringComparer.Ordinal);
    private static void Reject(Action action,string name){try{action();}catch(InvalidDataException){Console.WriteLine("PASS "+name);return;}throw new InvalidOperationException("FAIL "+name);}
    private static void Check(bool condition,string name){if(!condition)throw new InvalidOperationException("FAIL "+name);Console.WriteLine("PASS "+name);}
}
