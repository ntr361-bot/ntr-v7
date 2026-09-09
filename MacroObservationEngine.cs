using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace 六合分析软件.MacroReasoning;

/// <summary>
/// P7 sidecar observer. It consumes a caller-frozen prefix and never predicts, learns,
/// changes weights, writes history, or resolves production eligibility.
/// </summary>
public sealed class MacroObservationEngine : IMacroObservationEngine
{
    private static readonly int[] Windows={10,20,50,100};
    private static readonly string[] Zodiac={"鼠","牛","虎","兔","龙","蛇","马","羊","猴","鸡","狗","猪"};
    private static readonly HashSet<string> ZodiacSet=new(Zodiac,StringComparer.Ordinal);
    private const string Definition="p7-observation-v1";
    private readonly VersionedExpertRegistry registry;

    public MacroObservationEngine(VersionedExpertRegistry registry)
    {
        this.registry=registry??throw new ArgumentNullException(nameof(registry));
    }

    public MacroObservationSnapshot Observe(PrefixContext prefix)
    {
        ValidatePrefix(prefix);
        DateTimeOffset registryAsOf=prefix.ExpertPool.EvaluationMode==HistoricalEvaluationMode.CausalReconstruction
            ?prefix.ExpertSnapshots.Select(x=>x.Reconstruction?.ReconstructedAt??x.AvailableAt).DefaultIfEmpty(prefix.AsOf).Max()
            :prefix.AsOf;
        ExpertRegistrySnapshot registrySnapshot=registry.ReadAsOf(registryAsOf);
        if(registrySnapshot.Version!=prefix.ExpertPool.RegistryVersion)throw new InvalidDataException("ExpertPool注册表版本与观察时点不一致");
        var registrations=ResolveRegistrations(prefix,registrySnapshot);
        var included=prefix.ExpertPool.IncludedExpertIds.Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        var snapshots=prefix.ExpertSnapshots
            .Where(x=>included.Contains(x.ExpertId,StringComparer.Ordinal)&&prefix.ExpertPool.ExpertRevisionIds[x.ExpertId]==x.ExpertRevisionId)
            .OrderBy(x=>x.TargetIssue).ThenBy(x=>x.ExpertId,StringComparer.Ordinal).ToImmutableArray();
        var results=prefix.PastResults.OrderBy(x=>x.Issue).ToImmutableArray();
        ExpertDependencySnapshot dependencies=BuildDependencies(prefix,included,snapshots,results);
        ImmutableArray<ExpertObservation> expertObservations=BuildExpertObservations(prefix,included,registrations,snapshots,results,dependencies);
        ImmutableArray<ObservationFact> facts=BuildEnvironmentFacts(prefix,included,snapshots,results);
        string hash=Hash(prefix,facts,expertObservations,dependencies);
        return new MacroObservationSnapshot(prefix.TargetIssue,prefix.CutoffIssue,facts,hash,prefix.ExpertPool,expertObservations,dependencies);
    }

    private void ValidatePrefix(PrefixContext prefix)
    {
        if(prefix.TargetIssue<=0||prefix.CutoffIssue>=prefix.TargetIssue) throw new InvalidDataException("TargetIssue必须严格晚于CutoffIssue");
        if(prefix.ExpertPool.TargetIssue!=prefix.TargetIssue||prefix.ExpertPool.AsOf>prefix.AsOf) throw new InvalidDataException("冻结Pool与PrefixContext不一致");
        if(prefix.PastResults.GroupBy(x=>x.Issue).Any(g=>g.Count()>1)) throw new InvalidDataException("重复期开奖结果");
        foreach(ClosedResult result in prefix.PastResults)
        {
            if(result.Issue>=prefix.TargetIssue) throw new InvalidDataException("目标期或未来期开奖结果不得进入Observation");
            if(result.AvailableAt>prefix.AsOf||result.OpenedAt>result.AvailableAt) throw new InvalidDataException("期开奖结果在AsOf时不可用");
            if(!ZodiacSet.Contains(result.ActualZodiac)) throw new InvalidDataException("期开奖结果生肖无效");
        }
        if(prefix.ExpertSnapshots.GroupBy(x=>(x.ExpertId,x.ExpertRevisionId,x.TargetIssue)).Any(g=>g.Count()>1)) throw new InvalidDataException("重复专家期快照");
        if(prefix.ExpertSnapshots.GroupBy(x=>(x.ExpertId,x.TargetIssue)).Any(g=>g.Select(x=>x.ExpertRevisionId).Distinct(StringComparer.Ordinal).Count()>1)) throw new InvalidDataException("同一专家同一期存在冲突Revision");
        foreach(ExpertSnapshot snapshot in prefix.ExpertSnapshots)
        {
            if(snapshot.TargetIssue>prefix.TargetIssue) throw new InvalidDataException("未来目标快照不得进入Observation");
            if(snapshot.HistoryCutoffIssue>=snapshot.TargetIssue||snapshot.HistoryCutoffIssue>prefix.CutoffIssue) throw new InvalidDataException("快照HistoryCutoff不是合法前缀");
            ValidateRanking(snapshot.Ranking);
            if(!ExpertSnapshotIntegrity.Verify(snapshot))throw new InvalidDataException("专家快照PayloadHash校验失败");
            ClosedResult? result=prefix.PastResults.SingleOrDefault(x=>x.Issue==snapshot.TargetIssue);
            if(prefix.ExpertPool.EvaluationMode==HistoricalEvaluationMode.HistoricalAvailability)
            {
                if(snapshot.Origin!=SnapshotOrigin.LiveFrozen||snapshot.Reconstruction is not null)throw new InvalidDataException("HistoricalAvailability只能读取LiveFrozen快照");
                if(snapshot.GeneratedAt>prefix.AsOf||snapshot.AvailableAt>prefix.AsOf) throw new InvalidDataException("快照在AsOf以后才可用");
                if(result is not null&&(snapshot.GeneratedAt>result.OpenedAt||snapshot.AvailableAt>result.OpenedAt)) throw new InvalidDataException("历史专家快照不是开奖前冻结");
            }
            else
            {
                ReconstructionProvenance reconstruction=snapshot.Reconstruction??throw new InvalidDataException("CausalReconstruction缺少重建证明");
                if(snapshot.Origin!=SnapshotOrigin.CausalReconstruction||!reconstruction.Reconstructed||!reconstruction.MemoryRebuiltFromScratch
                    ||reconstruction.HistoryCutoff!=snapshot.HistoryCutoffIssue||reconstruction.SimulatedAsOf>prefix.AsOf
                    ||string.IsNullOrWhiteSpace(reconstruction.TrainingPrefixHash))throw new InvalidDataException("CausalReconstruction重建证明无效");
                if(result is not null&&reconstruction.SimulatedAsOf>result.OpenedAt)throw new InvalidDataException("重建模拟时点晚于历史开奖");
            }
        }
        string[] included=prefix.ExpertPool.IncludedExpertIds.ToArray();
        if(included.Distinct(StringComparer.Ordinal).Count()!=included.Length) throw new InvalidDataException("IncludedExperts重复");
        foreach(string expertId in included)
        {
            if(!prefix.ExpertPool.ExpertRevisionIds.ContainsKey(expertId)||string.IsNullOrWhiteSpace(prefix.ExpertPool.ExpertRevisionIds[expertId]))
                throw new InvalidDataException("Included专家缺少冻结Revision");
            if(!prefix.ExpertPool.IncludedSnapshotHashes.TryGetValue(expertId,out string? expectedHash))throw new InvalidDataException("Included专家缺少冻结快照哈希");
            string revision=prefix.ExpertPool.ExpertRevisionIds[expertId];
            ExpertSnapshot[] current=prefix.ExpertSnapshots.Where(x=>x.ExpertId==expertId&&x.ExpertRevisionId==revision&&x.TargetIssue==prefix.TargetIssue).ToArray();
            if(current.Length!=1)throw new InvalidDataException("Included专家缺少唯一同目标期快照");
            if(current[0].PayloadHash!=expectedHash)throw new InvalidDataException("ExpertPool与实际专家快照哈希不一致");
        }
    }

    private static void ValidateRanking(ImmutableArray<string> ranking)
    {
        if(ranking.Length!=12||ranking.Distinct(StringComparer.Ordinal).Count()!=12||ranking.Any(x=>!ZodiacSet.Contains(x)))
            throw new InvalidDataException("专家快照必须包含无重无漏的完整12生肖排名");
    }

    private static Dictionary<string,ExpertRegistration> ResolveRegistrations(PrefixContext prefix,ExpertRegistrySnapshot snapshot)
    {
        var result=new Dictionary<string,ExpertRegistration>(StringComparer.Ordinal);
        foreach(string expertId in prefix.ExpertPool.IncludedExpertIds.Distinct(StringComparer.Ordinal))
        {
            string revision=prefix.ExpertPool.ExpertRevisionIds[expertId];
            ExpertRegistration? registration=snapshot.Experts.SingleOrDefault(x=>x.ExpertId==expertId&&x.ExpertRevisionId==revision);
            if(registration is null) throw new InvalidDataException("冻结专家Revision在AsOf注册表中不存在");
            result.Add(expertId,registration);
        }
        return result;
    }

    private static ImmutableArray<ExpertObservation> BuildExpertObservations(PrefixContext prefix,string[] included,
        IReadOnlyDictionary<string,ExpertRegistration> registrations,ImmutableArray<ExpertSnapshot> snapshots,
        ImmutableArray<ClosedResult> results,ExpertDependencySnapshot dependencies)
    {
        var observations=ImmutableArray.CreateBuilder<ExpertObservation>();
        foreach(string expertId in included)
        {
            string revision=prefix.ExpertPool.ExpertRevisionIds[expertId];
            var samples=SamplesFor(expertId,revision,snapshots,results);
            var metrics=ImmutableArray.CreateBuilder<ObservationFact>();
            foreach(int window in Windows)
            {
                var rows=samples.TakeLast(window).ToArray();
                IssueRange range=Range(rows.Select(x=>x.Issue));
                metrics.Add(Fact("Top3Rate",expertId,window,rows.Length==0?null:rows.Count(x=>x.Rank<=3)/(double)rows.Length,rows.Length,range));
                metrics.Add(Fact("Top6Rate",expertId,window,rows.Length==0?null:rows.Count(x=>x.Rank<=6)/(double)rows.Length,rows.Length,range));
                metrics.Add(Fact("MRR",expertId,window,rows.Length==0?null:rows.Average(x=>1d/x.Rank),rows.Length,range));
                metrics.Add(Fact("MeanRank",expertId,window,rows.Length==0?null:rows.Average(x=>(double)x.Rank),rows.Length,range));
            }
            int opportunities=0,uniqueRescue=0;
            foreach(var sample in samples)
            {
                var peers=included.Where(x=>x!=expertId)
                    .SelectMany(peer=>SamplesFor(peer,prefix.ExpertPool.ExpertRevisionIds[peer],snapshots,results).Where(y=>y.Issue==sample.Issue))
                    .ToArray();
                if(peers.Length==0)continue;
                opportunities++;
                if(sample.Rank<=6&&peers.All(x=>x.Rank>6))uniqueRescue++;
            }
            double? rankingChange=RankingChangeRate(snapshots.Where(x=>x.ExpertId==expertId&&x.ExpertRevisionId==revision&&x.TargetIssue<prefix.TargetIssue));
            var related=dependencies.Pairs.Where(x=>x.LeftExpertId==expertId||x.RightExpertId==expertId).ToArray();
            double? diversity=related.Where(x=>x.ResidualDiversity.HasValue).Select(x=>x.ResidualDiversity!.Value).DefaultIfEmpty().Average();
            if(!related.Any(x=>x.ResidualDiversity.HasValue))diversity=null;
            string group=dependencies.DependencyGroups.Single(x=>x.Value.Contains(expertId,StringComparer.Ordinal)).Key;
            IssueRange allRange=Range(samples.Select(x=>x.Issue));
            double? top6=samples.Length==0?null:samples.Count(x=>x.Rank<=6)/(double)samples.Length;
            double? mean=samples.Length==0?null:samples.Average(x=>(double)x.Rank);
            var marginal=new MarginalContributionStats(expertId,revision,0,0,0,0,"unavailable-p7-no-frozen-control","unavailable-p7-no-frozen-candidate",ExpertActionMode.IGNORE,"p7-unavailable",prefix.ExpertPool.EvaluationMode,"unavailable");
            observations.Add(new ExpertObservation(expertId,registrations[expertId].ModelFamily,group,
                metrics.OrderBy(x=>x.Window).ThenBy(x=>x.SignalName,StringComparer.Ordinal).ToImmutableArray(),top6,
                Value(metrics,"Top6Rate",20),null,null,null,null,mean,diversity,null,rankingChange,
                opportunities==0?null:uniqueRescue/(double)opportunities,diversity,uniqueRescue,opportunities,
                included.Where(x=>x!=expertId).ToImmutableArray(),Definition,revision,marginal,
                new SignedReliability(null,samples.Length,allRange,Definition,"global"),ExpertState.NeutralExpert));
        }
        return observations.OrderBy(x=>x.ExpertId,StringComparer.Ordinal).ToImmutableArray();
    }

    private ExpertDependencySnapshot BuildDependencies(PrefixContext prefix,string[] included,
        ImmutableArray<ExpertSnapshot> snapshots,ImmutableArray<ClosedResult> results)
    {
        var includedSet=included.ToHashSet(StringComparer.Ordinal);
        var edges=registry.ReadDependencies().Where(e=>includedSet.Contains(e.FromExpertId)&&includedSet.Contains(e.ToExpertId)
            &&prefix.ExpertPool.ExpertRevisionIds[e.FromExpertId]==e.FromExpertRevisionId
            &&prefix.ExpertPool.ExpertRevisionIds[e.ToExpertId]==e.ToExpertRevisionId)
            .OrderBy(e=>e.FromExpertId,StringComparer.Ordinal).ThenBy(e=>e.ToExpertId,StringComparer.Ordinal).ThenBy(e=>e.DependencyType).ToImmutableArray();
        var pairs=ImmutableArray.CreateBuilder<ExpertPairDependency>();
        var allIssues=new List<long>();
        for(int i=0;i<included.Length;i++)for(int j=i+1;j<included.Length;j++)
        {
            string left=included[i],right=included[j];
            string lr=prefix.ExpertPool.ExpertRevisionIds[left],rr=prefix.ExpertPool.ExpertRevisionIds[right];
            var matched=MatchedSnapshots(left,lr,right,rr,snapshots,results).ToArray();
            allIssues.AddRange(matched.Select(x=>x.Issue));
            var correlations=Windows.ToImmutableDictionary(window=>window,window=>NullableAverage(matched.TakeLast(window).Select(x=>Spearman(x.Left.Ranking,x.Right.Ranking))));
            double? overlap=NullableAverage(matched.Select(x=>Top6Overlap(x.Left.Ranking,x.Right.Ranking)));
            bool parent=edges.Any(e=>IsConsuming(e.DependencyType)&&((e.FromExpertId==left&&e.ToExpertId==right)||(e.FromExpertId==right&&e.ToExpertId==left)));
            pairs.Add(new ExpertPairDependency(left,right,parent,null,correlations,overlap,overlap.HasValue?1-overlap:null,null,null,matched.Length,Definition));
        }
        ImmutableDictionary<string,ImmutableArray<string>> groups=BuildGroups(included,edges);
        return new ExpertDependencySnapshot(pairs.ToImmutable(),groups,null,Definition,Range(allIssues),edges);
    }

    private static ImmutableDictionary<string,ImmutableArray<string>> BuildGroups(string[] experts,ImmutableArray<ExpertDependencyEdge> edges)
    {
        var remaining=new HashSet<string>(experts,StringComparer.Ordinal);
        var groups=new SortedDictionary<string,ImmutableArray<string>>(StringComparer.Ordinal);
        while(remaining.Count>0)
        {
            string start=remaining.OrderBy(x=>x,StringComparer.Ordinal).First();
            var component=new HashSet<string>(StringComparer.Ordinal){start};
            var queue=new Queue<string>();queue.Enqueue(start);remaining.Remove(start);
            while(queue.Count>0)
            {
                string current=queue.Dequeue();
                foreach(string next in edges.Where(e=>e.FromExpertId==current||e.ToExpertId==current).Select(e=>e.FromExpertId==current?e.ToExpertId:e.FromExpertId))
                    if(component.Add(next)){remaining.Remove(next);queue.Enqueue(next);}
            }
            string[] members=component.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
            groups.Add("dependency-group:"+string.Join("|",members),members.ToImmutableArray());
        }
        return groups.ToImmutableDictionary(StringComparer.Ordinal);
    }

    private static ImmutableArray<ObservationFact> BuildEnvironmentFacts(PrefixContext prefix,string[] included,
        ImmutableArray<ExpertSnapshot> snapshots,ImmutableArray<ClosedResult> results)
    {
        var facts=ImmutableArray.CreateBuilder<ObservationFact>();
        foreach(int window in Windows)
        {
            var rows=results.TakeLast(window).ToArray();
            IssueRange range=Range(rows.Select(x=>x.Issue));
            facts.Add(Fact("ImmediateRepeatRate","__environment__",window,Repeat(rows,1),Math.Max(0,rows.Length-1),range));
            facts.Add(Fact("Gap1RepeatRate","__environment__",window,Repeat(rows,2),Math.Max(0,rows.Length-2),range));
            facts.Add(Fact("Gap2RepeatRate","__environment__",window,Repeat(rows,3),Math.Max(0,rows.Length-3),range));
            facts.Add(Fact("ZodiacConcentration","__environment__",window,Concentration(rows),rows.Length,range,
                window==100?null:Concentration(results.TakeLast(100).ToArray())));
            foreach(string zodiac in Zodiac)
            {
                double? frequency=rows.Length==0?null:rows.Count(x=>x.ActualZodiac==zodiac)/(double)rows.Length;
                double? baseline=results.Length==0?null:results.TakeLast(100).Count(x=>x.ActualZodiac==zodiac)/(double)Math.Min(100,results.Length);
                facts.Add(Fact("Frequency:"+zodiac,"__environment__",window,frequency,rows.Length,range,baseline));
                if(window<100)facts.Add(Fact("FrequencyDeltaVs100:"+zodiac,"__environment__",window,frequency.HasValue&&baseline.HasValue?frequency-baseline:null,rows.Length,range,0));
            }
            facts.Add(Fact("TrendReliability","__missing_signal__",window,null,0,new IssueRange(0,0,0)));
        }
        foreach(string zodiac in Zodiac)
        {
            int omission=0;bool found=false;
            foreach(ClosedResult row in results.Reverse())
            {
                if(row.ActualZodiac==zodiac){found=true;break;}
                omission++;
            }
            facts.Add(Fact("Omission:"+zodiac,"__environment__",0,results.Length==0?null:omission,results.Length==0?0:1,Range(results.Select(x=>x.Issue)),found?null:results.Length));
        }
        var current=snapshots.Where(x=>x.TargetIssue==prefix.TargetIssue&&included.Contains(x.ExpertId,StringComparer.Ordinal)).ToArray();
        var currentPairs=(from left in current from right in current where string.CompareOrdinal(left.ExpertId,right.ExpertId)<0 select (left,right)).ToArray();
        facts.Add(Fact("CurrentExpertMeanSpearman","__expert_pool__",0,NullableAverage(currentPairs.Select(x=>Spearman(x.left.Ranking,x.right.Ranking))),currentPairs.Length,new IssueRange(prefix.TargetIssue,prefix.TargetIssue,currentPairs.Length)));
        facts.Add(Fact("CurrentExpertMeanTop6Overlap","__expert_pool__",0,NullableAverage(currentPairs.Select(x=>Top6Overlap(x.left.Ranking,x.right.Ranking))),currentPairs.Length,new IssueRange(prefix.TargetIssue,prefix.TargetIssue,currentPairs.Length)));
        return facts.OrderBy(x=>x.ExpertId,StringComparer.Ordinal).ThenBy(x=>x.SignalName,StringComparer.Ordinal).ThenBy(x=>x.Window).ToImmutableArray();
    }

    private static (long Issue,int Rank)[] SamplesFor(string expert,string revision,ImmutableArray<ExpertSnapshot> snapshots,ImmutableArray<ClosedResult> results)
    {
        var actual=results.ToDictionary(x=>x.Issue,x=>x.ActualZodiac);
        return snapshots.Where(x=>x.ExpertId==expert&&x.ExpertRevisionId==revision&&actual.ContainsKey(x.TargetIssue))
            .Select(x=>(x.TargetIssue,x.Ranking.IndexOf(actual[x.TargetIssue])+1)).OrderBy(x=>x.TargetIssue).ToArray();
    }

    private static IEnumerable<(long Issue,ExpertSnapshot Left,ExpertSnapshot Right)> MatchedSnapshots(string left,string leftRevision,string right,string rightRevision,ImmutableArray<ExpertSnapshot> snapshots,ImmutableArray<ClosedResult> results)
    {
        var revealed=results.Select(x=>x.Issue).ToHashSet();
        var rightByIssue=snapshots.Where(x=>x.ExpertId==right&&x.ExpertRevisionId==rightRevision&&revealed.Contains(x.TargetIssue)).ToDictionary(x=>x.TargetIssue);
        foreach(ExpertSnapshot l in snapshots.Where(x=>x.ExpertId==left&&x.ExpertRevisionId==leftRevision&&revealed.Contains(x.TargetIssue)).OrderBy(x=>x.TargetIssue))
            if(rightByIssue.TryGetValue(l.TargetIssue,out ExpertSnapshot? r))yield return(l.TargetIssue,l,r);
    }

    private static double Spearman(ImmutableArray<string> left,ImmutableArray<string> right)
    {
        double sum=0;
        for(int i=0;i<left.Length;i++){int d=(i+1)-(right.IndexOf(left[i])+1);sum+=d*d;}
        return 1-6*sum/(12d*(12*12-1));
    }
    private static double Top6Overlap(ImmutableArray<string> left,ImmutableArray<string> right)=>left.Take(6).Intersect(right.Take(6),StringComparer.Ordinal).Count()/6d;
    private static bool IsConsuming(MetaDependencyType type)=>type is MetaDependencyType.ConsumesExpertRanking or MetaDependencyType.ConsumesExpertScore or MetaDependencyType.ConsumesMetaOutput;
    private static double? NullableAverage(IEnumerable<double> values){double[] a=values.ToArray();return a.Length==0?null:a.Average();}
    private static double? RankingChangeRate(IEnumerable<ExpertSnapshot> source){ExpertSnapshot[] rows=source.OrderBy(x=>x.TargetIssue).ToArray();return rows.Length<2?null:rows.Zip(rows.Skip(1),(a,b)=>!a.Ranking.SequenceEqual(b.Ranking,StringComparer.Ordinal)).Count(x=>x)/(double)(rows.Length-1);}
    private static double? Repeat(ClosedResult[] rows,int lag)=>rows.Length<=lag?null:Enumerable.Range(lag,rows.Length-lag).Count(i=>rows[i].ActualZodiac==rows[i-lag].ActualZodiac)/(double)(rows.Length-lag);
    private static double? Concentration(ClosedResult[] rows)=>rows.Length==0?null:Zodiac.Sum(z=>Math.Pow(rows.Count(x=>x.ActualZodiac==z)/(double)rows.Length,2));
    private static ObservationFact Fact(string name,string expert,int window,double? value,int samples,IssueRange range,double? baseline=null)=>new(name,expert,window,value,baseline,samples,range,Definition);
    private static double? Value(ImmutableArray<ObservationFact>.Builder metrics,string name,int window)=>metrics.Single(x=>x.SignalName==name&&x.Window==window).Value;
    private static IssueRange Range(IEnumerable<long> source){long[] values=source.Distinct().OrderBy(x=>x).ToArray();return values.Length==0?new IssueRange(0,0,0):new IssueRange(values[0],values[^1],values.Length);}

    private static string Hash(PrefixContext prefix,ImmutableArray<ObservationFact> facts,ImmutableArray<ExpertObservation> experts,ExpertDependencySnapshot dependencies)
    {
        var b=new StringBuilder();
        b.Append("RUN|").Append(prefix.Run.ExperimentId).Append('|').Append(prefix.Run.SchemaVersion).Append('|').Append(prefix.Run.AlgorithmVersion)
            .Append('|').Append(prefix.Run.ParametersHash).Append('|').Append(prefix.Run.CodeCommit).Append('|').Append(prefix.Run.RandomSeed)
            .Append('|').Append(prefix.TargetIssue).Append('|').Append(prefix.CutoffIssue).Append('|').Append(prefix.AsOf.ToUniversalTime().ToString("O"))
            .Append('|').Append(prefix.SourceManifestHash);
        AppendPool(b,prefix.ExpertPool);
        foreach(var f in facts)AppendFact(b,"F",f);
        foreach(var e in experts)
        {
            b.Append("\nE|").Append(e.ExpertId).Append('|').Append(e.ExpertRevisionId).Append('|').Append(e.FamilyId).Append('|').Append(e.DependencyGroup)
                .Append('|').Append(Number(e.GlobalReliability)).Append('|').Append(Number(e.RecentReliability)).Append('|').Append(Number(e.ContextReliability))
                .Append('|').Append(Number(e.RescueRate)).Append('|').Append(Number(e.HarmRate)).Append('|').Append(Number(e.NetImpact))
                .Append('|').Append(Number(e.MeanRank)).Append('|').Append(Number(e.DiversityValue)).Append('|').Append(Number(e.DependencyPenalty))
                .Append('|').Append(Number(e.RankingChangeRate)).Append('|').Append(Number(e.Top6UniqueContribution)).Append('|').Append(Number(e.DiversityContribution))
                .Append('|').Append(e.UniqueRescue).Append('|').Append(e.UniqueRescueOpportunities).Append('|').Append(e.ContextDefinitionVersion).Append('|').Append((int)e.State)
                .Append('|').Append(string.Join(',',e.ReferenceExpertIds.OrderBy(x=>x,StringComparer.Ordinal)))
                .Append('|').Append(Number(e.SignedReliability.Value)).Append('|').Append(e.SignedReliability.Samples).Append('|').Append(e.SignedReliability.EstimatorVersion)
                .Append('|').Append(e.MarginalContribution.ControlEnsembleId).Append('|').Append(e.MarginalContribution.CandidateEnsembleId)
                .Append('|').Append(e.MarginalContribution.MarginalRescue).Append('|').Append(e.MarginalContribution.MarginalHarm).Append('|').Append(e.MarginalContribution.CommonSamples);
            foreach(var f in e.WindowMetrics)AppendFact(b,"EF",f);
        }
        foreach(var p in dependencies.Pairs)b.Append("\nP|").Append(p.LeftExpertId).Append('|').Append(p.RightExpertId).Append('|').Append(p.ParentDependency)
            .Append('|').Append(Number(p.SharedInputRatio)).Append('|').Append(Number(p.Top6Overlap)).Append('|').Append(Number(p.ResidualDiversity))
            .Append('|').Append(Number(p.DependencyPenalty)).Append('|').Append(Number(p.RedundancyPenalty)).Append('|').Append(p.CommonSamples).Append('|').Append(p.EstimatorVersion)
            .Append('|').Append(string.Join(',',p.RankingCorrelation.OrderBy(x=>x.Key).Select(x=>$"{x.Key}:{Number(x.Value)}")));
        foreach(var group in dependencies.DependencyGroups.OrderBy(x=>x.Key,StringComparer.Ordinal))b.Append("\nG|").Append(group.Key).Append('|').Append(string.Join(',',group.Value.OrderBy(x=>x,StringComparer.Ordinal)));
        b.Append("\nDS|").Append(Number(dependencies.EffectiveIndependentExpertCount)).Append('|').Append(dependencies.EstimatorVersion).Append('|')
            .Append(dependencies.SourceIssues.First).Append('|').Append(dependencies.SourceIssues.Last).Append('|').Append(dependencies.SourceIssues.Samples);
        foreach(var e in dependencies.MetaDependencyEdges)b.Append("\nD|").Append(e.FromExpertId).Append('|').Append(e.ToExpertId).Append('|')
            .Append(e.FromExpertRevisionId).Append('|').Append(e.ToExpertRevisionId).Append('|').Append((int)e.DependencyType).Append('|')
            .Append(e.EvidenceReference).Append('|').Append(e.Version).Append('|').Append(string.Join(',',e.SharedInputIds.OrderBy(x=>x,StringComparer.Ordinal)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(b.ToString())));
    }
    private static void AppendFact(StringBuilder b,string prefix,ObservationFact f)=>b.Append("\n").Append(prefix).Append('|').Append(f.ExpertId).Append('|').Append(f.SignalName).Append('|').Append(f.Window)
        .Append('|').Append(Number(f.Value)).Append('|').Append(Number(f.BaselineValue)).Append('|').Append(f.EffectiveSamples).Append('|')
        .Append(f.SourceIssueRange.First).Append('|').Append(f.SourceIssueRange.Last).Append('|').Append(f.SourceIssueRange.Samples).Append('|').Append(f.DefinitionVersion);
    private static void AppendPool(StringBuilder b,ExpertPoolSnapshot pool)
    {
        b.Append("\nPOOL|").Append(pool.TargetIssue).Append('|').Append(pool.AsOf.ToUniversalTime().ToString("O")).Append('|').Append(pool.RegistryVersion).Append('|').Append((int)pool.EvaluationMode);
        AppendList("eligible",pool.EligibleExpertIds);AppendList("available",pool.AvailableExpertIds);AppendList("included",pool.IncludedExpertIds);AppendList("missing",pool.MissingExpertIds);AppendList("excluded",pool.ExcludedExpertIds);
        foreach(var x in pool.ExpertRevisionIds.OrderBy(x=>x.Key,StringComparer.Ordinal))b.Append("\nPR|").Append(x.Key).Append('|').Append(x.Value);
        foreach(var x in pool.IncludedSnapshotHashes.OrderBy(x=>x.Key,StringComparer.Ordinal))b.Append("\nPS|").Append(x.Key).Append('|').Append(x.Value);
        foreach(var x in pool.ExclusionReasons.OrderBy(x=>x.Key,StringComparer.Ordinal))b.Append("\nPX|").Append(x.Key).Append('|').Append(string.Join(',',x.Value.OrderBy(y=>y,StringComparer.Ordinal)));
        void AppendList(string name,IEnumerable<string> values)=>b.Append("\nPL|").Append(name).Append('|').Append(string.Join(',',values.OrderBy(x=>x,StringComparer.Ordinal)));
    }
    private static string Number(double? value)=>value.HasValue?value.Value.ToString("R",CultureInfo.InvariantCulture):"null";
}

/// <summary>Adapter required by the frozen contract; analysis is delegated to the P7 observer.</summary>
public sealed class RegisteredExpertDependencyAnalyzer : IExpertDependencyAnalyzer
{
    private readonly MacroObservationEngine observer;
    public RegisteredExpertDependencyAnalyzer(VersionedExpertRegistry registry)=>observer=new MacroObservationEngine(registry);
    public ExpertDependencySnapshot Analyze(PrefixContext prefix)=>observer.Observe(prefix).Dependencies;
}
