using System.Collections.Immutable;

namespace 六合分析软件.MacroReasoning;

/// <summary>P12 bounded confidence score. The value is not advertised as a calibrated probability.</summary>
public sealed class MacroConfidenceEngine : IMacroConfidenceEngine
{
    private const string Version="p12-confidence-score-v1";

    public ConfidenceResult Estimate(HypothesisSet hypotheses,EvidenceSearch support,EvidenceSearch counter,
        CriticResult critic,ReasoningMemorySnapshot memory)
    {
        ArgumentNullException.ThrowIfNull(hypotheses);
        ArgumentNullException.ThrowIfNull(support);
        ArgumentNullException.ThrowIfNull(counter);
        ArgumentNullException.ThrowIfNull(critic);
        ArgumentNullException.ThrowIfNull(memory);
        Validate(hypotheses,support,counter,memory);

        double supportStrength=Math.Clamp(MacroReasoningCritic.Strength(support.Items),0,1);
        double counterResistance=1-Math.Clamp(MacroReasoningCritic.Strength(counter.Items),0,1);
        double sampleConfidence=Math.Clamp(support.Items.Select(x=>x.EffectiveSamples).DefaultIfEmpty(0).Max()/100d,0,1);
        double? historical=HistoricalReliability(hypotheses,memory);
        double criticFactor=critic.Result switch{CriticVerdict.Pass=>1,CriticVerdict.Caution=>.5,_=>0};
        (double? calibrationQuality,int calibrationSamples)=Calibration(memory);

        var components=ImmutableDictionary<string,double?>.Empty
            .Add("SupportingEvidenceStrength",supportStrength)
            .Add("CounterEvidenceResistance",counterResistance)
            .Add("SampleConfidence",sampleConfidence)
            .Add("HistoricalReliability",historical)
            .Add("EnvironmentSimilarity",null)
            .Add("ModelAgreement",null)
            .Add("CriticFactor",criticFactor)
            .Add("CalibrationQuality",calibrationQuality);

        double value=0;
        if(support.Completed&&counter.Completed&&critic.Result!=CriticVerdict.Reject)
        {
            double[] available=components.Values.Where(x=>x.HasValue).Select(x=>x!.Value).ToArray();
            value=available.Length==0?0:available.Average();
            double cap=calibrationSamples>=50?.8:.6;
            if(critic.Result==CriticVerdict.Caution)cap=Math.Min(cap,.6);
            value=Math.Clamp(value,0,cap);
        }
        return new ConfidenceResult(value,components,Version,calibrationSamples);
    }

    private static double? HistoricalReliability(HypothesisSet hypotheses,ReasoningMemorySnapshot memory)
    {
        var values=hypotheses.Candidates.Select(x=>memory.Hypotheses.TryGetValue(x.Type.ToString(),out ReliabilityStats? stats)?stats:null)
            .Where(x=>x is {Matured:>=20,EstimatedReliability:not null})
            .Select(x=>x!.EstimatedReliability!.Value).ToArray();
        return values.Length==0?null:values.Average();
    }

    private static (double? Quality,int Samples) Calibration(ReasoningMemorySnapshot memory)
    {
        ConfidenceCalibrationStats[] usable=memory.Calibration.Where(x=>x.MaturedSamples>0&&x.CalibrationError.HasValue).ToArray();
        if(usable.Length==0)return (null,0);
        long total=usable.Sum(x=>(long)x.MaturedSamples);
        double error=usable.Sum(x=>Math.Abs(x.CalibrationError!.Value)*x.MaturedSamples)/total;
        return (1-Math.Clamp(error,0,1),(int)Math.Min(int.MaxValue,total));
    }

    private static void Validate(HypothesisSet hypotheses,EvidenceSearch support,EvidenceSearch counter,ReasoningMemorySnapshot memory)
    {
        var ids=hypotheses.Candidates.Select(x=>x.HypothesisId).ToHashSet(StringComparer.Ordinal);
        if(ids.Count==0||support.Items.Any(x=>!ids.Contains(x.HypothesisId))||counter.Items.Any(x=>!ids.Contains(x.HypothesisId)))
            throw new InvalidDataException("P12证据引用未知假设");
        foreach(Evidence evidence in support.Items.Concat(counter.Items))
            if(!double.IsFinite(evidence.Value)||!double.IsFinite(evidence.BaselineValue)||!double.IsFinite(evidence.EffectSize)
                ||!double.IsFinite(evidence.Reliability)||evidence.EffectSize<0||evidence.Reliability<0||evidence.Reliability>1||evidence.EffectiveSamples<0)
                throw new InvalidDataException("P12证据数值无效");
        foreach(ReliabilityStats stats in memory.Hypotheses.Values)
            if(stats.EstimatedReliability is double r&&(!double.IsFinite(r)||r<0||r>1))throw new InvalidDataException("P12历史可靠度无效");
        foreach(ConfidenceCalibrationStats stats in memory.Calibration)
            if(stats.LowerInclusive<0||stats.Upper>1||stats.LowerInclusive>=stats.Upper||stats.MaturedSamples<0
                ||stats.MeanConfidence is double mean&&(!double.IsFinite(mean)||mean<0||mean>1)
                ||stats.ReasoningCorrectRate is double correct&&(!double.IsFinite(correct)||correct<0||correct>1)
                ||stats.CalibrationError is double error&&(!double.IsFinite(error)||error<0||error>1))
                throw new InvalidDataException("P12校准统计无效");
    }
}
