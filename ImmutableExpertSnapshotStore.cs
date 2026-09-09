using System.Collections.Immutable;
using System.Data.SQLite;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace 六合分析软件.MacroReasoning;

public sealed record StoredExpertPool(ExpertPoolSnapshot Snapshot,string PayloadHash,DateTimeOffset FrozenAt);

public static class ExpertSnapshotIntegrity
{
    private static readonly string[] Zodiac={"鼠","牛","虎","兔","龙","蛇","马","羊","猴","鸡","狗","猪"};
    private static readonly HashSet<string> ZodiacSet=new(Zodiac,StringComparer.Ordinal);

    public static ExpertSnapshot Seal(ExpertSnapshot snapshot)
    {
        ValidateRanking(snapshot.Ranking);
        return snapshot with{PayloadHash=ComputeHash(snapshot)};
    }

    public static bool Verify(ExpertSnapshot snapshot)=>!string.IsNullOrWhiteSpace(snapshot.PayloadHash)
        &&string.Equals(snapshot.PayloadHash,ComputeHash(snapshot),StringComparison.Ordinal);

    public static string ComputeHash(ExpertSnapshot snapshot)
    {
        var b=new StringBuilder();
        b.Append(snapshot.ExpertId).Append('|').Append(snapshot.ExpertRevisionId).Append('|').Append(snapshot.TargetIssue).Append('|')
            .Append(snapshot.HistoryCutoffIssue).Append('|').Append(snapshot.GeneratedAt.ToUniversalTime().ToString("O")).Append('|')
            .Append(snapshot.AvailableAt.ToUniversalTime().ToString("O")).Append('|').Append(snapshot.AlgorithmVersion).Append('|')
            .Append(snapshot.CodeVersion).Append('|').Append(snapshot.RegistryVersion).Append('|').Append((int)snapshot.Origin).Append('|')
            .Append(string.Join(',',snapshot.Ranking)).Append('|').Append(string.Join(',',snapshot.ParentSnapshotHashes.OrderBy(x=>x,StringComparer.Ordinal)))
            .Append('|').Append(string.Join(',',snapshot.InputDependencyIds.OrderBy(x=>x,StringComparer.Ordinal)));
        if(snapshot.Reconstruction is { } r)b.Append('|').Append(r.Reconstructed).Append('|').Append(r.SimulatedAsOf.ToUniversalTime().ToString("O"))
            .Append('|').Append(r.ReconstructedAt.ToUniversalTime().ToString("O")).Append('|').Append(r.ReconstructionCodeVersion)
            .Append('|').Append(r.HistoryCutoff).Append('|').Append(r.MemoryRebuiltFromScratch).Append('|').Append(r.TrainingPrefixHash);
        else b.Append("|no-reconstruction");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(b.ToString())));
    }

    public static void ValidateRanking(ImmutableArray<string> ranking)
    {
        if(ranking.Length!=12||ranking.Distinct(StringComparer.Ordinal).Count()!=12||ranking.Any(x=>!ZodiacSet.Contains(x)))
            throw new InvalidDataException("专家快照必须包含无重无漏的完整12生肖排名");
    }
}

public static class ExpertPoolIntegrity
{
    public static string ComputeHash(ExpertPoolSnapshot pool)
    {
        var b=new StringBuilder();
        b.Append(pool.TargetIssue).Append('|').Append(pool.AsOf.ToUniversalTime().ToString("O")).Append('|').Append(pool.RegistryVersion).Append('|').Append((int)pool.EvaluationMode);
        Add("eligible",pool.EligibleExpertIds);Add("available",pool.AvailableExpertIds);Add("included",pool.IncludedExpertIds);Add("missing",pool.MissingExpertIds);Add("excluded",pool.ExcludedExpertIds);
        foreach(var x in pool.ExpertRevisionIds.OrderBy(x=>x.Key,StringComparer.Ordinal))b.Append("\nR|").Append(x.Key).Append('|').Append(x.Value);
        foreach(var x in pool.IncludedSnapshotHashes.OrderBy(x=>x.Key,StringComparer.Ordinal))b.Append("\nS|").Append(x.Key).Append('|').Append(x.Value);
        foreach(var x in pool.ExclusionReasons.OrderBy(x=>x.Key,StringComparer.Ordinal))b.Append("\nX|").Append(x.Key).Append('|').Append(string.Join(',',x.Value.OrderBy(y=>y,StringComparer.Ordinal)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(b.ToString())));
        void Add(string name,IEnumerable<string> values)=>b.Append("\nL|").Append(name).Append('|').Append(string.Join(',',values.OrderBy(x=>x,StringComparer.Ordinal)));
    }
}

/// <summary>Append-only P6 sidecar. It has no formal prediction or learning entry point.</summary>
public sealed class ImmutableExpertSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions=new(){PropertyNameCaseInsensitive=true};
    private readonly string path;
    private readonly VersionedExpertRegistry registry;

    public ImmutableExpertSnapshotStore(string databasePath,VersionedExpertRegistry registry)
    {
        path=Path.GetFullPath(databasePath);this.registry=registry;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var c=Open();Execute(c,"""
            CREATE TABLE IF NOT EXISTS ExpertPredictionSnapshot(
              SnapshotKey TEXT PRIMARY KEY,ExpertRevisionId TEXT NOT NULL,TargetIssue INTEGER NOT NULL,
              Origin INTEGER NOT NULL,AvailableAt TEXT NOT NULL,SimulatedAsOf TEXT NULL,
              PayloadHash TEXT NOT NULL,PayloadJson TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_expert_snapshot_prefix ON ExpertPredictionSnapshot(TargetIssue,Origin,AvailableAt);
            CREATE TABLE IF NOT EXISTS FrozenExpertPool(
              PoolKey TEXT PRIMARY KEY,TargetIssue INTEGER NOT NULL,AsOf TEXT NOT NULL,EvaluationMode INTEGER NOT NULL,
              RegistryVersion TEXT NOT NULL,PayloadHash TEXT NOT NULL,FrozenAt TEXT NOT NULL,PayloadJson TEXT NOT NULL);
            """);
    }

    public ExpertSnapshot FreezeAndAppend(ExpertSnapshot proposed,DateTimeOffset freezeAt)
    {
        ExpertRegistrySnapshot registrySnapshot=registry.ReadAsOf(freezeAt);
        ExpertRegistration registration=registrySnapshot.Experts.SingleOrDefault(x=>x.ExpertId==proposed.ExpertId&&x.ExpertRevisionId==proposed.ExpertRevisionId)
            ??throw new InvalidDataException("未知ExpertRevisionId");
        ExpertSnapshot? prior=ReadSnapshot(proposed.ExpertRevisionId,proposed.TargetIssue,proposed.Origin);
        if(prior is not null)
        {
            ExpertSnapshot normalized=proposed with{RegistryVersion=prior.RegistryVersion};
            ValidateForStorage(normalized,registration,freezeAt);
            ExpertSnapshot resealed=ExpertSnapshotIntegrity.Seal(normalized);
            if(resealed.PayloadHash!=prior.PayloadHash)throw new InvalidDataException("同一快照身份内容冲突，禁止覆盖");
            return prior;
        }
        ValidateForStorage(proposed,registration,freezeAt);
        ExpertSnapshot sealedSnapshot=ExpertSnapshotIntegrity.Seal(proposed with{RegistryVersion=registrySnapshot.Version});
        string key=SnapshotKey(sealedSnapshot);
        using var c=Open();using var tx=c.BeginTransaction();
        ExpertSnapshot? existing=ReadSnapshot(c,key);
        if(existing is not null)
        {
            if(existing.PayloadHash!=sealedSnapshot.PayloadHash)throw new InvalidDataException("同一快照身份内容冲突，禁止覆盖");
            tx.Commit();return existing;
        }
        Execute(c,"INSERT INTO ExpertPredictionSnapshot(SnapshotKey,ExpertRevisionId,TargetIssue,Origin,AvailableAt,SimulatedAsOf,PayloadHash,PayloadJson) VALUES(@k,@r,@i,@o,@a,@s,@h,@j)",
            ("@k",key),("@r",sealedSnapshot.ExpertRevisionId),("@i",sealedSnapshot.TargetIssue),("@o",(int)sealedSnapshot.Origin),
            ("@a",sealedSnapshot.AvailableAt.ToUniversalTime().ToString("O")),("@s",sealedSnapshot.Reconstruction?.SimulatedAsOf.ToUniversalTime().ToString("O")),
            ("@h",sealedSnapshot.PayloadHash),("@j",JsonSerializer.Serialize(sealedSnapshot,JsonOptions)));
        tx.Commit();return sealedSnapshot;
    }

    public ExpertSnapshot? ReadSnapshot(string expertRevisionId,long targetIssue,SnapshotOrigin origin)
    {
        using var c=Open();return ReadSnapshot(c,$"{(int)origin}|{expertRevisionId}|{targetIssue}");
    }

    public ImmutableArray<ExpertSnapshot> ReadPrefixSnapshots(long targetIssue,DateTimeOffset asOf,HistoricalEvaluationMode mode)
    {
        using var c=Open();
        string sql=mode==HistoricalEvaluationMode.HistoricalAvailability
            ?"SELECT PayloadJson FROM ExpertPredictionSnapshot WHERE TargetIssue<=@i AND Origin=@o AND AvailableAt<=@a ORDER BY TargetIssue,ExpertRevisionId"
            :"SELECT PayloadJson FROM ExpertPredictionSnapshot WHERE TargetIssue<=@i AND Origin=@o AND SimulatedAsOf<=@a ORDER BY TargetIssue,ExpertRevisionId";
        return Rows<ExpertSnapshot>(c,sql,("@i",targetIssue),("@o",mode==HistoricalEvaluationMode.HistoricalAvailability?(int)SnapshotOrigin.LiveFrozen:(int)SnapshotOrigin.CausalReconstruction),("@a",asOf.ToUniversalTime().ToString("O"))).ToImmutableArray();
    }

    public StoredExpertPool FreezePool(ExpertPoolSnapshot pool,DateTimeOffset frozenAt)
    {
        ValidatePool(pool);
        if(pool.AsOf>frozenAt)throw new InvalidDataException("Pool AsOf晚于冻结时间");
        if(registry.ReadAsOf(frozenAt).Version!=pool.RegistryVersion)throw new InvalidDataException("Pool注册表版本与冻结时点不一致");
        SnapshotOrigin requiredOrigin=pool.EvaluationMode==HistoricalEvaluationMode.HistoricalAvailability?SnapshotOrigin.LiveFrozen:SnapshotOrigin.CausalReconstruction;
        foreach(string expertId in pool.IncludedExpertIds)
        {
            string revision=pool.ExpertRevisionIds[expertId];
            ExpertSnapshot snapshot=ReadSnapshot(revision,pool.TargetIssue,requiredOrigin)??throw new InvalidDataException("Pool引用的同目标期快照不存在");
            if(snapshot.ExpertId!=expertId||snapshot.PayloadHash!=pool.IncludedSnapshotHashes[expertId])throw new InvalidDataException("Pool引用快照身份或哈希不一致");
        }
        string hash=ExpertPoolIntegrity.ComputeHash(pool),key=PoolKey(pool);
        using var c=Open();using var tx=c.BeginTransaction();
        StoredExpertPool? existing=ReadPool(c,key);
        if(existing is not null)
        {
            if(existing.PayloadHash!=hash)throw new InvalidDataException("同一Pool身份内容冲突，禁止覆盖");
            tx.Commit();return existing;
        }
        var stored=new StoredExpertPool(pool,hash,frozenAt);
        Execute(c,"INSERT INTO FrozenExpertPool(PoolKey,TargetIssue,AsOf,EvaluationMode,RegistryVersion,PayloadHash,FrozenAt,PayloadJson) VALUES(@k,@i,@a,@m,@r,@h,@f,@j)",
            ("@k",key),("@i",pool.TargetIssue),("@a",pool.AsOf.ToUniversalTime().ToString("O")),("@m",(int)pool.EvaluationMode),("@r",pool.RegistryVersion),("@h",hash),("@f",frozenAt.ToUniversalTime().ToString("O")),("@j",JsonSerializer.Serialize(stored,JsonOptions)));
        tx.Commit();return stored;
    }

    public StoredExpertPool? ReadPool(long targetIssue,DateTimeOffset asOf,HistoricalEvaluationMode mode,string registryVersion)
    {
        using var c=Open();return ReadPool(c,$"{(int)mode}|{targetIssue}|{asOf.ToUniversalTime():O}|{registryVersion}");
    }

    private void ValidateForStorage(ExpertSnapshot x,ExpertRegistration registration,DateTimeOffset freezeAt)
    {
        if(x.TargetIssue<=0||x.HistoryCutoffIssue>=x.TargetIssue)throw new InvalidDataException("HistoryCutoff必须早于TargetIssue");
        ExpertSnapshotIntegrity.ValidateRanking(x.Ranking);
        if(x.AlgorithmVersion!=registration.AlgorithmVersion||x.CodeVersion!=registration.CodeVersion)throw new InvalidDataException("快照算法或代码版本与注册Revision不符");
        if(registration.InputDependencyIds.Any(id=>!x.InputDependencyIds.Contains(id,StringComparer.Ordinal)))throw new InvalidDataException("快照缺少注册输入依赖");
        if(x.Origin==SnapshotOrigin.LiveFrozen)
        {
            if(x.Reconstruction is not null)throw new InvalidDataException("LiveFrozen不能携带重建证明");
            if(x.GeneratedAt>freezeAt||x.AvailableAt>freezeAt)throw new InvalidDataException("LiveFrozen快照在冻结时刻尚不可用");
        }
        else
        {
            ReconstructionProvenance r=x.Reconstruction??throw new InvalidDataException("CausalReconstruction缺少重建证明");
            if(!r.Reconstructed||!r.MemoryRebuiltFromScratch||r.HistoryCutoff!=x.HistoryCutoffIssue||string.IsNullOrWhiteSpace(r.TrainingPrefixHash)||string.IsNullOrWhiteSpace(r.ReconstructionCodeVersion))throw new InvalidDataException("重建证明不完整");
            if(r.ReconstructedAt>freezeAt||x.GeneratedAt>freezeAt||x.AvailableAt>freezeAt||r.SimulatedAsOf>=x.AvailableAt)throw new InvalidDataException("重建真实时间或模拟时间无效");
        }
        var parents=registry.ReadDependencies().Where(e=>e.FromExpertRevisionId==x.ExpertRevisionId&&IsConsuming(e.DependencyType)).ToArray();
        var expected=new List<string>();
        foreach(ExpertDependencyEdge edge in parents)
        {
            ExpertSnapshot parent=ReadSnapshot(edge.ToExpertRevisionId,x.TargetIssue,x.Origin)??throw new InvalidDataException("消费专家缺少同目标期父快照");
            expected.Add(parent.PayloadHash);
        }
        if(!expected.OrderBy(v=>v,StringComparer.Ordinal).SequenceEqual(x.ParentSnapshotHashes.OrderBy(v=>v,StringComparer.Ordinal),StringComparer.Ordinal))throw new InvalidDataException("父快照哈希集合与注册消费依赖不一致");
    }

    private static void ValidatePool(ExpertPoolSnapshot pool)
    {
        string[] included=pool.IncludedExpertIds.ToArray();
        if(included.Distinct(StringComparer.Ordinal).Count()!=included.Length||included.Any(x=>!pool.ExpertRevisionIds.ContainsKey(x)||!pool.IncludedSnapshotHashes.ContainsKey(x)))throw new InvalidDataException("Pool Included身份、Revision或快照哈希不完整");
        if(pool.MissingExpertIds.Intersect(included,StringComparer.Ordinal).Any()||pool.ExcludedExpertIds.Intersect(included,StringComparer.Ordinal).Any())throw new InvalidDataException("Pool分类互相冲突");
    }
    private static bool IsConsuming(MetaDependencyType type)=>type is MetaDependencyType.ConsumesExpertRanking or MetaDependencyType.ConsumesExpertScore or MetaDependencyType.ConsumesMetaOutput;
    private static string SnapshotKey(ExpertSnapshot x)=>$"{(int)x.Origin}|{x.ExpertRevisionId}|{x.TargetIssue}";
    private static string PoolKey(ExpertPoolSnapshot x)=>$"{(int)x.EvaluationMode}|{x.TargetIssue}|{x.AsOf.ToUniversalTime():O}|{x.RegistryVersion}";
    private static ExpertSnapshot? ReadSnapshot(SQLiteConnection c,string key)=>Rows<ExpertSnapshot>(c,"SELECT PayloadJson FROM ExpertPredictionSnapshot WHERE SnapshotKey=@k",("@k",key)).SingleOrDefault();
    private static StoredExpertPool? ReadPool(SQLiteConnection c,string key)=>Rows<StoredExpertPool>(c,"SELECT PayloadJson FROM FrozenExpertPool WHERE PoolKey=@k",("@k",key)).SingleOrDefault();
    private SQLiteConnection Open(){var c=new SQLiteConnection($"Data Source={path};Version=3;");c.Open();return c;}
    private static void Execute(SQLiteConnection c,string sql,params (string,object?)[] args){using var q=new SQLiteCommand(sql,c);foreach(var a in args)q.Parameters.AddWithValue(a.Item1,a.Item2??DBNull.Value);q.ExecuteNonQuery();}
    private static IEnumerable<string> Strings(SQLiteConnection c,string sql,params (string,object?)[] args){using var q=new SQLiteCommand(sql,c);foreach(var a in args)q.Parameters.AddWithValue(a.Item1,a.Item2??DBNull.Value);using var r=q.ExecuteReader();while(r.Read())yield return r.GetString(0);}
    private static IEnumerable<T> Rows<T>(SQLiteConnection c,string sql,params (string,object?)[] args){foreach(string json in Strings(c,sql,args))yield return JsonSerializer.Deserialize<T>(json,JsonOptions)??throw new InvalidDataException("P6存储JSON无效");}
}
