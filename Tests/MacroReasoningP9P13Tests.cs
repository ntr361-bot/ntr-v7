using System.Collections.Immutable;
using 六合分析软件;
using 六合分析软件.MacroReasoning;

public static class MacroReasoningP9P13Tests
{
    public static int Run()
    {
        P9SupportingEvidence();
        P10CounterEvidence();
        P11Critic();
        P12Confidence();
        P13Decision();
        Console.WriteLine("P9-P13 REASONING PASS");
        return 0;
    }

    private static void P13Decision()
    {
        Fixture x=BuildFixture();
        var engine=new MacroDecisionEngine();
        Hypothesis hot=x.Hypotheses.Candidates.Single(h=>h.Type==HypothesisType.HotPersistenceIncreasing);
        EvidenceSearch cleanSupport=CleanSupport(hot);
        var cleanCounter=new EvidenceSearch(true,ImmutableArray.Create("counter-check"),ImmutableArray<Evidence>.Empty,ImmutableArray<string>.Empty);
        var pass=new CriticResult(CriticVerdict.Pass,ImmutableArray.Create("ALL_CHECKS_PASSED"),ImmutableArray.Create("checks"),.05);
        var caution=new CriticResult(CriticVerdict.Caution,ImmutableArray.Create("SMALL_EFFECTIVE_SAMPLE"),ImmutableArray.Create("checks"),.01);
        var reject=pass with{Result=CriticVerdict.Reject,Reasons=ImmutableArray.Create("FUTURE_OR_TARGET_DATA"),MaximumMagnitude=0};
        ConfidenceResult high=Confidence(.70),low=Confidence(.59);

        MacroDecision noHypothesis=engine.Decide(200,new HypothesisSet(ImmutableArray<Hypothesis>.Empty,ImmutableArray<string>.Empty),
            new EvidenceSearch(true,ImmutableArray<string>.Empty,ImmutableArray<Evidence>.Empty,ImmutableArray<string>.Empty),cleanCounter,
            Proposal(.5,.5,.51,.49),pass,high);
        Check(noHypothesis.DecisionType==DecisionType.Hold&&Unchanged(noHypothesis),"P13没有假设时HOLD且权重不变");
        var missingNulls=new HypothesisSet(ImmutableArray.Create(hot),ImmutableArray.Create(hot.HypothesisId));
        MacroDecision missingNullDecision=engine.Decide(200,missingNulls,cleanSupport,cleanCounter,Proposal(.5,.5,.51,.49),pass,high);
        Check(missingNullDecision.DecisionType==DecisionType.Hold&&Unchanged(missingNullDecision),"P13缺少随机波动或无明显变化候选时不得APPLY");

        MacroDecision incomplete=engine.Decide(200,x.Hypotheses,cleanSupport,cleanCounter with{Completed=false},Proposal(.5,.5,.51,.49),pass,high);
        Check(incomplete.DecisionType==DecisionType.Hold&&Unchanged(incomplete),"P13主动反证未完成时不得APPLY");
        MacroDecision veto=engine.Decide(200,x.Hypotheses,cleanSupport,cleanCounter,Proposal(.5,.5,.51,.49),reject,high);
        Check(veto.DecisionType==DecisionType.Reject&&Unchanged(veto),"P13 Critic拒绝具有最终否决权");
        MacroDecision lowConfidence=engine.Decide(200,x.Hypotheses,cleanSupport,cleanCounter,Proposal(.5,.5,.51,.49),pass,low);
        Check(lowConfidence.DecisionType==DecisionType.Hold&&Unchanged(lowConfidence),"P13低于0.60置信评分时HOLD");

        MacroDecision cautiousApply=engine.Decide(200,x.Hypotheses,cleanSupport,cleanCounter,Proposal(.5,.5,.52,.48),caution,high);
        Check(cautiousApply.DecisionType==DecisionType.Apply&&Close(cautiousApply.ActionMagnitude,.01)
            &&Close(cautiousApply.ExpertWeightsApplied["A"],.51)&&Close(cautiousApply.ExpertWeightsApplied["B"],.49),"P13 CAUTION动作缩小到1%");
        MacroDecision passApply=engine.Decide(200,x.Hypotheses,cleanSupport,cleanCounter,Proposal(.5,.5,.51,.49),pass,high);
        Check(passApply.DecisionType==DecisionType.Apply&&Close(passApply.ActionMagnitude,.01)
            &&passApply.SelectedHypothesisIds.Contains(hot.HypothesisId,StringComparer.Ordinal),"P13 PASS应用已获支持的小幅动作");
        Check(passApply.ExpertWeightsProposed["A"]==.51&&passApply.ReasonSummary.Length>0,"P13保留提议权重和结构化原因摘要");
        MacroDecision repeated=engine.Decide(200,x.Hypotheses,cleanSupport,cleanCounter,Proposal(.5,.5,.51,.49),pass,high);
        Check(SameDecision(passApply,repeated),"P13相同冻结输入产生可复现决策");

        Hypothesis random=x.Hypotheses.Candidates.Single(h=>h.Type==HypothesisType.RandomFluctuation);
        Evidence randomEvidence=new(H('5'),random.HypothesisId,"MaterialShiftCount",0,0,1,1,1,"RandomPlausible",new IssueRange(100,199,100),100,"fixture","aggregate");
        EvidenceSearch nullCompetes=cleanSupport with{Items=cleanSupport.Items.Add(randomEvidence)};
        MacroDecision nullHold=engine.Decide(200,x.Hypotheses,nullCompetes,cleanCounter,Proposal(.5,.5,.51,.49),pass,high);
        Check(nullHold.DecisionType==DecisionType.Hold&&Unchanged(nullHold),"P13空解释不弱于动作解释时HOLD");

        Reject(()=>engine.Decide(200,x.Hypotheses,cleanSupport,cleanCounter,
            new ActionProposal(Weights(.5,.5),ImmutableDictionary<string,double>.Empty.Add("A",1.1).Add("B",-.1),.6,"bad"),pass,high),"P13拒绝非法权重提议");

        EvidenceSearch liveSupport=new MacroEvidenceEngine().Search(x.Observation,x.Hypotheses,x.Prefix);
        EvidenceSearch liveCounter=new MacroCounterEvidenceEngine().Search(x.Observation,x.Hypotheses,liveSupport,x.Prefix);
        ActionProposal liveProposal=Proposal(.5,.5,.505,.495);
        CriticResult liveCritic=new MacroReasoningCritic().Review(x.Prefix,x.Hypotheses,liveSupport,liveCounter,liveProposal);
        ConfidenceResult liveConfidence=new MacroConfidenceEngine().Estimate(x.Hypotheses,liveSupport,liveCounter,liveCritic,x.Memory);
        MacroDecision integrated=engine.Decide(200,x.Hypotheses,liveSupport,liveCounter,liveProposal,liveCritic,liveConfidence);
        Check(integrated.DecisionType==DecisionType.Hold&&Unchanged(integrated),"P9到P13证据不足时完整链选择HOLD");

        var history=Enumerable.Range(1,80).Select(i=>new DatabaseHelper.HistoryRecord{Period=(2026000+i).ToString(),SpecialZodiac=Zodiac[i%12],SpecialNumber=((i%49)+1).ToString("00"),OpenTime=new DateTime(2026,1,1).AddDays(i).ToString("yyyy-MM-dd HH:mm:ss")}).ToArray();
        string before=string.Join(",",V7Engine.Predict(history).Top6);
        _=engine.Decide(200,x.Hypotheses,liveSupport,liveCounter,liveProposal,liveCritic,liveConfidence);
        string after=string.Join(",",V7Engine.Predict(history).Top6);
        Check(before==after,"P9到P13执行前后正式V7 Top6不变");
    }

    private static void P12Confidence()
    {
        Fixture x=BuildFixture();
        EvidenceSearch support=new MacroEvidenceEngine().Search(x.Observation,x.Hypotheses,x.Prefix);
        EvidenceSearch counter=new MacroCounterEvidenceEngine().Search(x.Observation,x.Hypotheses,support,x.Prefix);
        CriticResult caution=new MacroReasoningCritic().Review(x.Prefix,x.Hypotheses,support,counter,Proposal(.5,.5,.505,.495));
        var engine=new MacroConfidenceEngine();
        ConfidenceResult first=engine.Estimate(x.Hypotheses,support,counter,caution,x.Memory);
        ConfidenceResult second=engine.Estimate(x.Hypotheses,support,counter,caution,x.Memory);
        Check(Close(first.Value,second.Value)&&first.CalibrationVersion==second.CalibrationVersion
            &&first.CalibrationSamples==second.CalibrationSamples
            &&first.Components.OrderBy(x=>x.Key).SequenceEqual(second.Components.OrderBy(x=>x.Key)),"P12相同冻结输入产生可复现置信评分");
        Check(first.Value>=0&&first.Value<=.6&&first.Components.Values.All(v=>!v.HasValue||v.Value is >=0 and <=1),"P12 CAUTION评分有界且不超过0.60");
        Check(first.Components["HistoricalReliability"] is null&&first.Components["CalibrationQuality"] is null,"P12缺失历史组成保持null而不是伪造0分");

        ConfidenceResult incomplete=engine.Estimate(x.Hypotheses,support,counter with{Completed=false},caution,x.Memory);
        Check(Close(incomplete.Value,0),"P12主动反证未完成时评分为0");
        ConfidenceResult rejected=engine.Estimate(x.Hypotheses,support,counter,caution with{Result=CriticVerdict.Reject,MaximumMagnitude=0},x.Memory);
        Check(Close(rejected.Value,0),"P12 Critic拒绝时评分为0");

        Hypothesis hot=x.Hypotheses.Candidates.Single(h=>h.Type==HypothesisType.HotPersistenceIncreasing);
        EvidenceSearch cleanSupport=CleanSupport(hot);
        var cleanCounter=new EvidenceSearch(true,ImmutableArray.Create("counter-check"),ImmutableArray<Evidence>.Empty,ImmutableArray<string>.Empty);
        CriticResult pass=new MacroReasoningCritic().Review(x.Prefix,x.Hypotheses,cleanSupport,cleanCounter,Proposal(.5,.5,.51,.49));
        ConfidenceResult uncalibrated=engine.Estimate(x.Hypotheses,cleanSupport,cleanCounter,pass,x.Memory);
        Check(uncalibrated.Value<=.6&&uncalibrated.CalibrationSamples==0,"P12无成熟校准时严格使用0.60上限");
        var calibrated=x.Memory with{Calibration=ImmutableArray.Create(new ConfidenceCalibrationStats(.6,.8,100,.7,.65,.05)),MemoryHash=H('6')};
        ConfidenceResult withCalibration=engine.Estimate(x.Hypotheses,cleanSupport,cleanCounter,pass,calibrated);
        Check(withCalibration.Value<=.8&&withCalibration.CalibrationSamples==100&&withCalibration.Components["CalibrationQuality"]>.9,"P12成熟校准进入评分但仍限制在0.80");
        Reject(()=>engine.Estimate(x.Hypotheses,cleanSupport,cleanCounter,pass,x.Memory with{Calibration=ImmutableArray.Create(
            new ConfidenceCalibrationStats(.8,.6,10,.7,.6,1.5))}),"P12拒绝区间或误差越界的校准统计");
    }

    private static void P11Critic()
    {
        Fixture x=BuildFixture();
        EvidenceSearch support=new MacroEvidenceEngine().Search(x.Observation,x.Hypotheses,x.Prefix);
        EvidenceSearch counter=new MacroCounterEvidenceEngine().Search(x.Observation,x.Hypotheses,support,x.Prefix);
        var critic=new MacroReasoningCritic();
        ActionProposal small=Proposal(.5,.5,.505,.495);

        CriticResult cautious=critic.Review(x.Prefix,x.Hypotheses,support,counter,small);
        Check(cautious.Result==CriticVerdict.Caution&&Close(cautious.MaximumMagnitude,.01),"P11对短窗和强反证给出CAUTION及1%上限");
        Check(cautious.CompletedChecks.Length==10,"P11完整执行10项批判检查");
        Check(small.Before["A"]==.5&&small.Proposed["A"]==.505,"P11不修改待审权重");

        CriticResult incomplete=critic.Review(x.Prefix,x.Hypotheses,support,counter with{Completed=false},small);
        Check(incomplete.Result==CriticVerdict.Reject&&Close(incomplete.MaximumMagnitude,0),"P11拒绝未完成的主动反证");
        CriticResult leakage=critic.Review(x.Prefix with{PastResults=x.Prefix.PastResults.Add(
            new ClosedResult(200,"鼠",x.Prefix.AsOf,x.Prefix.AsOf,H('7')))},x.Hypotheses,support,counter,small);
        Check(leakage.Result==CriticVerdict.Reject&&leakage.Reasons.Contains("FUTURE_OR_TARGET_DATA",StringComparer.Ordinal),"P11泄漏检查具有否决权");
        CriticResult futureMemory=critic.Review(x.Prefix with{Memory=x.Prefix.Memory with{AvailableAt=x.Prefix.AsOf.AddMinutes(1)}},x.Hypotheses,support,counter,small);
        Check(futureMemory.Result==CriticVerdict.Reject&&futureMemory.Reasons.Contains("FUTURE_OR_TARGET_DATA",StringComparer.Ordinal),"P11拒绝预测时尚不可用的推理记忆");
        CriticResult invalid=critic.Review(x.Prefix,x.Hypotheses,support,counter,
            new ActionProposal(Weights(.5,.5),ImmutableDictionary<string,double>.Empty.Add("A",1.1).Add("B",-.1),.6,"fixture"));
        Check(invalid.Result==CriticVerdict.Reject,"P11拒绝负权重或错误动作幅度");
        CriticResult excessive=critic.Review(x.Prefix,x.Hypotheses,support,counter,Proposal(.5,.5,.56,.44));
        Check(excessive.Result==CriticVerdict.Reject,"P11拒绝超过5%的权重转移");

        Hypothesis hot=x.Hypotheses.Candidates.Single(h=>h.Type==HypothesisType.HotPersistenceIncreasing);
        var longEvidence=new Evidence(H('6'),hot.HypothesisId,"ZodiacConcentration",50,.20,.10,.10,1,"Increasing",
            new IssueRange(100,199,100),100,"fixture","independent-long");
        var cleanSupport=new EvidenceSearch(true,ImmutableArray.Create("long-check"),ImmutableArray.Create(longEvidence),ImmutableArray<string>.Empty);
        var cleanCounter=new EvidenceSearch(true,ImmutableArray.Create("counter-check"),ImmutableArray<Evidence>.Empty,ImmutableArray<string>.Empty);
        CriticResult awaitingP14=critic.Review(x.Prefix,x.Hypotheses,cleanSupport,cleanCounter,Proposal(.5,.5,.51,.49));
        Check(awaitingP14.Result==CriticVerdict.Caution&&Close(awaitingP14.MaximumMagnitude,.01)
            &&awaitingP14.Reasons.Contains("ENVIRONMENT_SIMILARITY_UNAVAILABLE",StringComparer.Ordinal)
            &&awaitingP14.Reasons.Contains("RANK_IMPACT_PREVIEW_UNAVAILABLE",StringComparer.Ordinal),"P11在P14排名预览和环境相似度缺失时不得伪造PASS");
    }

    private static void P10CounterEvidence()
    {
        Fixture x=BuildFixture();
        EvidenceSearch support=new MacroEvidenceEngine().Search(x.Observation,x.Hypotheses,x.Prefix);
        var engine=new MacroCounterEvidenceEngine();
        EvidenceSearch first=engine.Search(x.Observation,x.Hypotheses,support,x.Prefix);
        EvidenceSearch second=engine.Search(x.Observation,x.Hypotheses,support,x.Prefix);

        Check(first.Completed,"P10完成主动反证检查");
        Check(first.Items.SequenceEqual(second.Items)&&first.CheckedSignalIds.SequenceEqual(second.CheckedSignalIds),"P10相同输入产生可复现反证");
        Check(Has(first,x,HypothesisType.HotPersistenceIncreasing,"ZodiacConcentration"),"P10记录热集中度在50期未确认");
        Check(Has(first,x,HypothesisType.RepeatRegimeIncreasing,"ImmediateRepeatRate"),"P10记录重复增强在50期未确认");
        Check(Has(first,x,HypothesisType.ModelPerformanceShift,"Top6Rate:A"),"P10记录专家变化在50期不足");
        Check(Has(first,x,HypothesisType.RandomFluctuation,"MaterialStructuralShiftCount")
            &&Has(first,x,HypothesisType.NoMeaningfulChange,"MaterialStructuralShiftCount"),"P10用已观察结构变化反驳两个空解释");
        Check(first.MissingInputs.Any(v=>v.Contains("ColdReturnRate",StringComparison.Ordinal))
            &&first.MissingInputs.Any(v=>v.Contains("TrendReliability",StringComparison.Ordinal)),"P10明确记录无法执行的冷回归与趋势反证");
        Reject(()=>engine.Search(x.Observation,x.Hypotheses,support with{Completed=false},x.Prefix),"P10拒绝未完成的支持证据搜索");
        Reject(()=>engine.Search(x.Observation,x.Hypotheses,support,x.Prefix with{PastResults=x.Prefix.PastResults.Add(
            new ClosedResult(200,"鼠",x.Prefix.AsOf,x.Prefix.AsOf,H('8')))}),"P10拒绝目标期结果进入反证");
    }

    private static void P9SupportingEvidence()
    {
        Fixture x=BuildFixture();
        var engine=new MacroEvidenceEngine();
        EvidenceSearch first=engine.Search(x.Observation,x.Hypotheses,x.Prefix);
        EvidenceSearch second=engine.Search(x.Observation,x.Hypotheses,x.Prefix);

        Check(first.Completed,"P9完成全部预注册支持证据查找");
        Check(first.Items.SequenceEqual(second.Items)&&first.CheckedSignalIds.SequenceEqual(second.CheckedSignalIds),"P9相同输入产生可复现证据");
        Check(Has(first,x,HypothesisType.HotPersistenceIncreasing,"ZodiacConcentration"),"P9识别集中度上升的热持续证据");
        Check(Has(first,x,HypothesisType.RepeatRegimeIncreasing,"ImmediateRepeatRate"),"P9识别20对100期重复增强证据");
        Check(Has(first,x,HypothesisType.ModelPerformanceShift,"Top6Rate:A"),"P9识别专家20对100期表现变化");
        Check(!first.Items.Any(e=>e.HypothesisId==Id(x,HypothesisType.ColdReturnIncreasing)),"P9不从遗漏猜测冷回归证据");
        Check(!first.Items.Any(e=>e.HypothesisId==Id(x,HypothesisType.TrendSignalDegrading)),"P9不从排名猜测趋势证据");
        Check(first.MissingInputs.Any(v=>v.Contains("ColdReturnRate@20",StringComparison.Ordinal))
            &&first.MissingInputs.Any(v=>v.Contains("TrendReliability@20",StringComparison.Ordinal)),"P9明确记录当前缺失的冷回归与趋势输入");
        Check(!first.Items.Any(e=>e.HypothesisId==Id(x,HypothesisType.RandomFluctuation)
            ||e.HypothesisId==Id(x,HypothesisType.NoMeaningfulChange)),"已有结构变化时P9不支持空解释");
        Check(first.Items.All(e=>e.EffectiveSamples>0&&e.Reliability>=0&&e.Reliability<=1),"P9证据保存有效样本与有界样本支持度");
        Reject(()=>engine.Search(x.Observation,x.Hypotheses,x.Prefix with{PastResults=x.Prefix.PastResults.Add(
            new ClosedResult(200,"鼠",x.Prefix.AsOf,x.Prefix.AsOf,H('9')))}),"P9拒绝目标期结果进入支持证据");
        Reject(()=>engine.Search(x.Observation with{Issue=201},x.Hypotheses,x.Prefix),"P9拒绝观察与Prefix期号不一致");
    }

    private static bool Has(EvidenceSearch search,Fixture x,HypothesisType type,string signal)=>search.Items.Any(e=>e.HypothesisId==Id(x,type)&&e.SignalName==signal);
    private static string Id(Fixture x,HypothesisType type)=>x.Hypotheses.Candidates.Single(h=>h.Type==type).HypothesisId;

    private static Fixture BuildFixture()
    {
        long issue=200,cutoff=199;
        DateTimeOffset asOf=new(2026,9,9,12,0,0,TimeSpan.Zero);
        var pool=new ExpertPoolSnapshot(issue,asOf,"registry",ImmutableArray.Create("A","B"),ImmutableArray.Create("A","B"),
            ImmutableArray.Create("A","B"),ImmutableArray<string>.Empty,ImmutableArray<string>.Empty,
            ImmutableDictionary<string,ImmutableArray<string>>.Empty,ImmutableDictionary<string,string>.Empty.Add("A",H('a')).Add("B",H('b')),
            HistoricalEvaluationMode.HistoricalAvailability,ImmutableDictionary<string,string>.Empty.Add("A","A@r1").Add("B","B@r1"));
        IssueRange range20=new(180,199,20),range100=new(100,199,100);
        var facts=ImmutableArray.Create(
            Fact("ZodiacConcentration","__environment__",20,.18,.14,20,range20),
            Fact("ZodiacConcentration","__environment__",50,.15,.14,50,new IssueRange(150,199,50)),
            Fact("ZodiacConcentration","__environment__",100,.14,null,100,range100),
            Fact("ImmediateRepeatRate","__environment__",20,.20,null,19,range20),
            Fact("ImmediateRepeatRate","__environment__",50,.12,null,49,new IssueRange(150,199,50)),
            Fact("ImmediateRepeatRate","__environment__",100,.10,null,99,range100),
            Fact("Gap1RepeatRate","__environment__",20,.05,null,18,range20),
            Fact("Gap1RepeatRate","__environment__",100,.05,null,98,range100),
            Fact("Gap2RepeatRate","__environment__",20,.04,null,17,range20),
            Fact("Gap2RepeatRate","__environment__",100,.05,null,97,range100),
            Fact("TrendReliability","__missing_signal__",20,null,null,0,new IssueRange(0,0,0)));
        var metrics=ImmutableArray.Create(
            Fact("Top6Rate","A",20,.70,null,20,range20),
            Fact("Top6Rate","A",50,.54,null,50,new IssueRange(150,199,50)),
            Fact("Top6Rate","A",100,.50,null,100,range100));
        var marginal=new MarginalContributionStats("A","A@r1",0,0,0,0,"none","none",ExpertActionMode.IGNORE,"none",HistoricalEvaluationMode.HistoricalAvailability,"none");
        var expert=new ExpertObservation("A","Family-A","group-A",metrics,.5,.7,null,null,null,null,6.5,null,null,null,null,null,0,0,
            ImmutableArray<string>.Empty,"p7","A@r1",marginal,new SignedReliability(null,100,range100,"p7","global"),ExpertState.NeutralExpert);
        var stableMetrics=ImmutableArray.Create(
            Fact("Top6Rate","B",20,.50,null,20,range20),Fact("Top6Rate","B",50,.50,null,50,new IssueRange(150,199,50)),
            Fact("Top6Rate","B",100,.50,null,100,range100));
        var marginalB=marginal with{ExpertId="B",ExpertRevisionId="B@r1"};
        var expertB=new ExpertObservation("B","Family-B","group-B",stableMetrics,.5,.5,null,null,null,null,6.5,null,null,null,null,null,0,0,
            ImmutableArray<string>.Empty,"p7","B@r1",marginalB,new SignedReliability(null,100,range100,"p7","global"),ExpertState.NeutralExpert);
        var dependencies=new ExpertDependencySnapshot(ImmutableArray<ExpertPairDependency>.Empty,
            ImmutableDictionary<string,ImmutableArray<string>>.Empty.Add("group-A",ImmutableArray.Create("A")).Add("group-B",ImmutableArray.Create("B")),2,"p7",range100,
            ImmutableArray<ExpertDependencyEdge>.Empty);
        var observation=new MacroObservationSnapshot(issue,cutoff,facts,H('1'),pool,ImmutableArray.Create(expert,expertB),dependencies);
        var memory=Memory(cutoff,H('2'));
        var hypotheses=new MacroHypothesisEngine().Build(observation,memory);
        var prefix=new PrefixContext(new RunIdentity("p9-p13","v1","test",H('3'),"code",6501),issue,cutoff,asOf,
            ImmutableArray<ClosedResult>.Empty,ImmutableArray<ExpertSnapshot>.Empty,H('4'),memory,pool);
        return new Fixture(observation,memory,hypotheses,prefix);
    }

    private static ObservationFact Fact(string name,string expert,int window,double? value,double? baseline,int samples,IssueRange range)=>
        new(name,expert,window,value,baseline,samples,range,"p7-observation-v1");
    private static ReasoningMemorySnapshot Memory(long last,string hash)=>new(1,last,new DateTimeOffset(2026,9,9,0,0,0,TimeSpan.Zero),
        ImmutableDictionary<string,ReliabilityStats>.Empty,ImmutableDictionary<string,ReliabilityStats>.Empty,
        ImmutableDictionary<string,ReliabilityStats>.Empty,ImmutableDictionary<string,ReliabilityStats>.Empty,
        ImmutableDictionary<string,ReliabilityStats>.Empty,ImmutableArray<ConfidenceCalibrationStats>.Empty,hash);
    private static string H(char value)=>new(value,64);
    private static ImmutableDictionary<string,double> Weights(double a,double b)=>ImmutableDictionary<string,double>.Empty.Add("A",a).Add("B",b);
    private static ActionProposal Proposal(double beforeA,double beforeB,double proposedA,double proposedB)=>new(
        Weights(beforeA,beforeB),Weights(proposedA,proposedB),.5*(Math.Abs(proposedA-beforeA)+Math.Abs(proposedB-beforeB)),"fixture");
    private static EvidenceSearch CleanSupport(Hypothesis hypothesis)=>new(true,ImmutableArray.Create("long-check"),
        ImmutableArray.Create(new Evidence(H('6'),hypothesis.HypothesisId,"ZodiacConcentration",50,.20,.10,.10,1,"Increasing",
            new IssueRange(100,199,100),100,"fixture","independent-long")),ImmutableArray<string>.Empty);
    private static bool Close(double value,double expected)=>Math.Abs(value-expected)<1e-10;
    private static ConfidenceResult Confidence(double value)=>new(value,ImmutableDictionary<string,double?>.Empty,"fixture",100);
    private static bool Unchanged(MacroDecision decision)=>Close(decision.ActionMagnitude,0)
        &&decision.ExpertWeightsBefore.OrderBy(x=>x.Key).SequenceEqual(decision.ExpertWeightsApplied.OrderBy(x=>x.Key));
    private static bool SameDecision(MacroDecision left,MacroDecision right)=>left.DecisionType==right.DecisionType
        &&Close(left.ActionMagnitude,right.ActionMagnitude)&&left.SelectedHypothesisIds.SequenceEqual(right.SelectedHypothesisIds)
        &&left.RejectedHypothesisIds.SequenceEqual(right.RejectedHypothesisIds)
        &&left.ExpertWeightsApplied.OrderBy(x=>x.Key).SequenceEqual(right.ExpertWeightsApplied.OrderBy(x=>x.Key))
        &&left.ReasonSummary==right.ReasonSummary;
    private static readonly string[] Zodiac={"鼠","牛","虎","兔","龙","蛇","马","羊","猴","鸡","狗","猪"};
    private sealed record Fixture(MacroObservationSnapshot Observation,ReasoningMemorySnapshot Memory,HypothesisSet Hypotheses,PrefixContext Prefix);
    private static void Reject(Action action,string name){try{action();}catch(InvalidDataException){Console.WriteLine("PASS "+name);return;}throw new InvalidOperationException("FAIL "+name);}
    private static void Check(bool condition,string name){if(!condition)throw new InvalidOperationException("FAIL "+name);Console.WriteLine("PASS "+name);}
}
