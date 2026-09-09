using System.Collections.Immutable;

namespace 六合分析软件.MacroReasoning;

/// <summary>P6 audit resolver only. It never enables an expert or chooses a prediction weight.</summary>
public sealed class ExpertPoolResolver : IExpertPoolResolver
{
    public ExpertPoolSnapshot Resolve(ExpertRegistrySnapshot registry,long targetIssue,DateTimeOffset asOf,
        ImmutableArray<ExpertSnapshot> snapshots,HistoricalEvaluationMode mode)
    {
        if(targetIssue<=0)throw new InvalidDataException("TargetIssue无效");
        var candidates=snapshots.Where(x=>x.TargetIssue==targetIssue).ToArray();
        foreach(var group in candidates.GroupBy(x=>x.ExpertId,StringComparer.Ordinal))
        {
            if(group.Select(x=>x.ExpertRevisionId).Distinct(StringComparer.Ordinal).Count()>1)throw new InvalidDataException("同一期同Expert出现冲突Revision");
            if(group.Select(x=>x.PayloadHash).Distinct(StringComparer.Ordinal).Count()>1)throw new InvalidDataException("同一期同Revision出现冲突内容");
        }
        candidates=candidates.GroupBy(x=>(x.ExpertId,x.ExpertRevisionId,x.PayloadHash)).Select(x=>x.First()).ToArray();
        var registrationGroups=registry.Experts.GroupBy(x=>x.ExpertId,StringComparer.Ordinal).ToArray();
        var eligible=new List<ExpertRegistration>();
        foreach(var group in registrationGroups)
        {
            ExpertRegistration[] active=group.Where(x=>Eligible(x,asOf,mode)).ToArray();
            if(active.Length>1)throw new InvalidDataException("同一Expert存在多个有效Eligible Revision");
            if(active.Length==1)eligible.Add(active[0]);
        }
        var available=candidates.Select(x=>x.ExpertId).Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal).ToImmutableArray();
        var included=new List<string>();var missing=new List<string>();var excluded=new HashSet<string>(StringComparer.Ordinal);
        var reasons=new Dictionary<string,List<string>>(StringComparer.Ordinal);
        var hashes=new Dictionary<string,string>(StringComparer.Ordinal);var revisions=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var group in registrationGroups)
        {
            ExpertRegistration latest=group.OrderByDescending(x=>x.EffectiveFrom).ThenByDescending(x=>x.RegisteredAt).ThenBy(x=>x.ExpertRevisionId,StringComparer.Ordinal).First();
            revisions[group.Key]=eligible.SingleOrDefault(x=>x.ExpertId==group.Key)?.ExpertRevisionId??latest.ExpertRevisionId;
            string[] eligibilityReasons=EligibilityReasons(latest,asOf,mode).ToArray();
            if(!eligible.Any(x=>x.ExpertId==group.Key))
            {
                excluded.Add(group.Key);foreach(string reason in eligibilityReasons)AddReason(group.Key,reason);
            }
        }
        foreach(ExpertRegistration registration in eligible.OrderBy(x=>x.ExpertId,StringComparer.Ordinal))
        {
            ExpertSnapshot[] exact=candidates.Where(x=>x.ExpertId==registration.ExpertId&&x.ExpertRevisionId==registration.ExpertRevisionId).ToArray();
            if(exact.Length==0){missing.Add(registration.ExpertId);continue;}
            string[] errors=SnapshotErrors(exact[0],registration,registry,targetIssue,asOf,mode).ToArray();
            if(errors.Length>0){excluded.Add(registration.ExpertId);foreach(string error in errors)AddReason(registration.ExpertId,error);continue;}
            included.Add(registration.ExpertId);hashes.Add(registration.ExpertId,exact[0].PayloadHash);
        }
        foreach(ExpertSnapshot unknown in candidates.Where(x=>!registrationGroups.Any(g=>g.Key==x.ExpertId)))
        {excluded.Add(unknown.ExpertId);revisions[unknown.ExpertId]=unknown.ExpertRevisionId;AddReason(unknown.ExpertId,"UnregisteredExpertRevision");}
        return new ExpertPoolSnapshot(targetIssue,asOf,registry.Version,
            eligible.Select(x=>x.ExpertId).OrderBy(x=>x,StringComparer.Ordinal).ToImmutableArray(),available,
            included.OrderBy(x=>x,StringComparer.Ordinal).ToImmutableArray(),missing.OrderBy(x=>x,StringComparer.Ordinal).ToImmutableArray(),
            excluded.OrderBy(x=>x,StringComparer.Ordinal).ToImmutableArray(),
            reasons.OrderBy(x=>x.Key,StringComparer.Ordinal).ToImmutableDictionary(x=>x.Key,x=>x.Value.Distinct().OrderBy(y=>y,StringComparer.Ordinal).ToImmutableArray(),StringComparer.Ordinal),
            hashes.ToImmutableDictionary(StringComparer.Ordinal),mode,revisions.ToImmutableDictionary(StringComparer.Ordinal));
        void AddReason(string id,string reason){if(!reasons.TryGetValue(id,out List<string>? list))reasons[id]=list=[];list.Add(reason);}
    }

    private static bool Eligible(ExpertRegistration x,DateTimeOffset asOf,HistoricalEvaluationMode mode)=>EligibilityReasons(x,asOf,mode).Count()==0;
    private static IEnumerable<string> EligibilityReasons(ExpertRegistration x,DateTimeOffset asOf,HistoricalEvaluationMode mode)
    {
        if(!x.EligibleForMacro)yield return "EligibleForMacro=false";
        if(!x.Enabled)yield return "Enabled=false";
        if(!x.HasFullRanking12)yield return "HasFullRanking12=false";
        if(x.LeakageAuditStatus!=ExpertAuditStatus.Passed)yield return "LeakageAuditStatus!=Passed";
        if(x.SnapshotIntegrityStatus!=ExpertAuditStatus.Passed)yield return "SnapshotIntegrityStatus!=Passed";
        if(mode==HistoricalEvaluationMode.HistoricalAvailability&&x.EffectiveFrom>asOf)yield return "EffectiveFrom>AsOf";
    }

    private static IEnumerable<string> SnapshotErrors(ExpertSnapshot x,ExpertRegistration registration,
        ExpertRegistrySnapshot registry,long targetIssue,DateTimeOffset asOf,HistoricalEvaluationMode mode)
    {
        if(x.TargetIssue!=targetIssue)yield return "TargetIssueMismatch";
        if(x.HistoryCutoffIssue>=targetIssue)yield return "HistoryCutoffNotPrefix";
        if(!HasValidRanking(x.Ranking))yield return "IncompleteRanking12";
        if(!ExpertSnapshotIntegrity.Verify(x))yield return "PayloadHashInvalid";
        if(x.AlgorithmVersion!=registration.AlgorithmVersion||x.CodeVersion!=registration.CodeVersion)yield return "RevisionVersionMismatch";
        if(x.RegistryVersion!=registry.Version)yield return "RegistryVersionMismatch";
        if(registration.InputDependencyIds.Any(id=>!x.InputDependencyIds.Contains(id,StringComparer.Ordinal)))yield return "InputDependencyMissing";
        if(mode==HistoricalEvaluationMode.HistoricalAvailability)
        {
            if(x.Origin!=SnapshotOrigin.LiveFrozen)yield return "OriginMustBeLiveFrozen";
            if(x.GeneratedAt>asOf||x.AvailableAt>asOf)yield return "SnapshotUnavailableAtAsOf";
            if(x.Reconstruction is not null)yield return "LiveSnapshotHasReconstruction";
        }
        else
        {
            if(x.Origin!=SnapshotOrigin.CausalReconstruction)yield return "OriginMustBeCausalReconstruction";
            if(x.Reconstruction is not {Reconstructed:true,MemoryRebuiltFromScratch:true} r)yield return "ReconstructionProvenanceMissing";
            else if(r.SimulatedAsOf>asOf||r.HistoryCutoff!=x.HistoryCutoffIssue||string.IsNullOrWhiteSpace(r.TrainingPrefixHash))yield return "ReconstructionProvenanceInvalid";
        }
    }
    private static bool HasValidRanking(ImmutableArray<string> ranking){try{ExpertSnapshotIntegrity.ValidateRanking(ranking);return true;}catch(InvalidDataException){return false;}}
}
