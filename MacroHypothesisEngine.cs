using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace 六合分析软件.MacroReasoning;

/// <summary>
/// P8 sidecar candidate generator. It does not search evidence, select hypotheses,
/// propose actions, change expert weights, rank zodiac signs, or write state.
/// </summary>
public sealed class MacroHypothesisEngine : IMacroHypothesisEngine
{
    private const string DefinitionVersion="p8-hypothesis-v1";
    private const string PriorVersion="p8-prior-v1";
    private const int MinimumMaturedSamples=20;
    private const int PriorStrength=20;
    private const int EvaluationHorizon=10;
    private static readonly HypothesisType[] Types=Enum.GetValues<HypothesisType>();
    private static readonly ImmutableDictionary<HypothesisType,string> Descriptions=
        new Dictionary<HypothesisType,string>
        {
            [HypothesisType.ColdReturnIncreasing]="冷回归兑现强度可能正在上升",
            [HypothesisType.HotPersistenceIncreasing]="热态持续性可能正在上升",
            [HypothesisType.TrendSignalDegrading]="趋势信号可靠度可能正在下降",
            [HypothesisType.RepeatRegimeIncreasing]="重复型环境强度可能正在上升",
            [HypothesisType.ModelPerformanceShift]="专家相对信息价值可能发生变化",
            [HypothesisType.RandomFluctuation]="当前变化可能只是随机波动",
            [HypothesisType.NoMeaningfulChange]="当前没有足够迹象表明环境发生实质变化"
        }.ToImmutableDictionary();

    public HypothesisSet Build(MacroObservationSnapshot observation,ReasoningMemorySnapshot memory)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(memory);
        Validate(observation,memory);

        var candidates=ImmutableArray.CreateBuilder<Hypothesis>(Types.Length);
        foreach(HypothesisType type in Types)
        {
            double prior=Prior(type,memory);
            string id=Id(observation,memory,type);
            candidates.Add(new Hypothesis(id,type,observation.Issue,Descriptions[type],prior,prior,
                Math.Log(prior/(1-prior)),HypothesisStatus.Active,ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,$"p8-falsification-v1/{type}",checked(observation.Issue+EvaluationHorizon)));
        }
        ImmutableArray<Hypothesis> frozen=candidates.ToImmutable();
        return new HypothesisSet(frozen,frozen.Select(x=>x.HypothesisId).ToImmutableArray());
    }

    private static double Prior(HypothesisType type,ReasoningMemorySnapshot memory)
    {
        const double neutral=.5;
        if(!memory.Hypotheses.TryGetValue(type.ToString(),out ReliabilityStats? stats)
            ||stats.Matured<MinimumMaturedSamples||stats.EstimatedReliability is null)
            return neutral;
        double weight=stats.Matured/((double)stats.Matured+PriorStrength);
        return Math.Clamp(neutral+weight*(stats.EstimatedReliability.Value-neutral),.1,.9);
    }

    private static void Validate(MacroObservationSnapshot observation,ReasoningMemorySnapshot memory)
    {
        if(observation.Issue<=0||observation.CutoffIssue>=observation.Issue)
            throw new InvalidDataException("P8需要严格的历史前缀");
        if(observation.Issue>long.MaxValue-EvaluationHorizon)
            throw new InvalidDataException("目标期号超出P8成熟边界");
        if(observation.ExpertPool.TargetIssue!=observation.Issue)
            throw new InvalidDataException("Observation与冻结ExpertPool期号不一致");
        if(!IsSha256(observation.SnapshotHash)||!IsSha256(memory.MemoryHash))
            throw new InvalidDataException("P8输入缺少合法内容哈希");
        if(memory.Version<0||memory.LastTrainingIssue<0||memory.LastTrainingIssue>=observation.Issue)
            throw new InvalidDataException("ReasoningMemory不是目标期之前的合法版本");
        foreach((string key,ReliabilityStats stats) in memory.Hypotheses)
        {
            if(string.IsNullOrWhiteSpace(key)||stats.Triggered<0||stats.Matured<0||stats.Correct<0
                ||stats.Rescue<0||stats.Harm<0||stats.Matured>stats.Triggered||stats.Correct>stats.Matured
                ||stats.Rescue>stats.Matured||stats.Harm>stats.Matured)
                throw new InvalidDataException("ReasoningMemory假设统计计数无效");
            if(stats.EstimatedReliability is double reliability
                &&(!double.IsFinite(reliability)||reliability<0||reliability>1))
                throw new InvalidDataException("ReasoningMemory假设可靠度无效");
        }
    }

    private static string Id(MacroObservationSnapshot observation,ReasoningMemorySnapshot memory,HypothesisType type)
    {
        string canonical=string.Join("\n",DefinitionVersion,PriorVersion,
            observation.Issue.ToString(CultureInfo.InvariantCulture),type.ToString(),
            observation.SnapshotHash.ToLowerInvariant(),memory.MemoryHash.ToLowerInvariant());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static bool IsSha256(string value)=>value is {Length:64}&&value.All(Uri.IsHexDigit);
}
