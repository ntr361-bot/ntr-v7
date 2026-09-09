using System.Collections.Immutable;
using System.Data.SQLite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace 六合分析软件.MacroReasoning;

/// <summary>Append-only P5 registry. It has no production prediction or weighting entry point.</summary>
public sealed class VersionedExpertRegistry : IExpertRegistry
{
    private readonly string path;
    private static readonly JsonSerializerOptions JsonOptions=new(){PropertyNameCaseInsensitive=true};
    private static readonly MetaDependencyType[] Consuming={MetaDependencyType.ConsumesExpertRanking,MetaDependencyType.ConsumesExpertScore,MetaDependencyType.ConsumesMetaOutput};

    public VersionedExpertRegistry(string databasePath)
    {
        path=Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var c=Open();
        Execute(c,"""
            CREATE TABLE IF NOT EXISTS ExpertRevision(ExpertRevisionId TEXT PRIMARY KEY,ExpertId TEXT NOT NULL,RegisteredAt TEXT NOT NULL,PayloadJson TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_expert_identity ON ExpertRevision(ExpertId,RegisteredAt);
            CREATE TABLE IF NOT EXISTS ExpertDependency(EdgeKey TEXT PRIMARY KEY,FromRevisionId TEXT NOT NULL,ToRevisionId TEXT NOT NULL,DependencyType INTEGER NOT NULL,PayloadJson TEXT NOT NULL);
            """);
    }

    public void AppendRevision(ExpertRegistration registration)
    {
        ValidateRegistration(registration);
        using var c=Open(); using var tx=c.BeginTransaction();
        if(Scalar(c,"SELECT 1 FROM ExpertRevision WHERE ExpertRevisionId=@r",("@r",registration.ExpertRevisionId)) is not null)
            throw new InvalidDataException("ExpertRevisionId已存在，注册表只允许追加新Revision");
        Execute(c,"INSERT INTO ExpertRevision(ExpertRevisionId,ExpertId,RegisteredAt,PayloadJson) VALUES(@r,@e,@t,@j)",
            ("@r",registration.ExpertRevisionId),("@e",registration.ExpertId),("@t",registration.RegisteredAt.ToUniversalTime().ToString("O")),("@j",JsonSerializer.Serialize(registration,JsonOptions)));
        tx.Commit();
    }

    public void AppendDependency(ExpertDependencyEdge edge)
    {
        using var c=Open(); using var tx=c.BeginTransaction();
        ExpertRegistration from=Require(c,edge.FromExpertRevisionId),to=Require(c,edge.ToExpertRevisionId);
        if(from.ExpertId!=edge.FromExpertId || to.ExpertId!=edge.ToExpertId) throw new InvalidDataException("依赖边ExpertId与Revision冲突");
        bool shared=!Consuming.Contains(edge.DependencyType);
        if(shared && string.CompareOrdinal(edge.FromExpertRevisionId,edge.ToExpertRevisionId)>0)
            throw new InvalidDataException("共享关系必须按RevisionId规范排序");
        string key=$"{edge.FromExpertRevisionId}|{(int)edge.DependencyType}|{edge.ToExpertRevisionId}";
        if(Scalar(c,"SELECT 1 FROM ExpertDependency WHERE EdgeKey=@k",("@k",key)) is not null) throw new InvalidDataException("重复依赖边");
        if(Consuming.Contains(edge.DependencyType) && CreatesCycle(c,edge.FromExpertRevisionId,edge.ToExpertRevisionId)) throw new InvalidDataException("消费依赖形成循环");
        Execute(c,"INSERT INTO ExpertDependency(EdgeKey,FromRevisionId,ToRevisionId,DependencyType,PayloadJson) VALUES(@k,@f,@t,@d,@j)",
            ("@k",key),("@f",edge.FromExpertRevisionId),("@t",edge.ToExpertRevisionId),("@d",(int)edge.DependencyType),("@j",JsonSerializer.Serialize(edge,JsonOptions)));
        tx.Commit();
    }

    public ExpertRegistrySnapshot ReadAsOf(DateTimeOffset asOf)
    {
        using var c=Open();
        var experts=Rows<ExpertRegistration>(c,"SELECT PayloadJson FROM ExpertRevision WHERE RegisteredAt<=@t ORDER BY RegisteredAt,ExpertRevisionId",("@t",asOf.ToUniversalTime().ToString("O")))
            .Select(x=>x with{DependencyDepth=Depth(c,x.ExpertRevisionId,new HashSet<string>(StringComparer.Ordinal))}).ToImmutableArray();
        string payload=JsonSerializer.Serialize(experts,JsonOptions);
        string hash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        return new ExpertRegistrySnapshot("p5-"+hash[..12],asOf,experts,hash);
    }

    public ImmutableArray<ExpertDependencyEdge> ReadDependencies()
    {
        using var c=Open();
        return Rows<ExpertDependencyEdge>(c,"SELECT PayloadJson FROM ExpertDependency ORDER BY EdgeKey").ToImmutableArray();
    }

    public int DependencyDepth(string expertRevisionId)
    {
        using var c=Open(); Require(c,expertRevisionId);
        return Depth(c,expertRevisionId,new HashSet<string>(StringComparer.Ordinal));
    }

    public ExpertPoolSnapshot FreezePool(long targetIssue,DateTimeOffset asOf,IEnumerable<string> revisionIds)
    {
        string[] revisions=revisionIds.Distinct(StringComparer.Ordinal).ToArray();
        using var c=Open();
        ExpertRegistration[] registrations=revisions.Select(r=>Require(c,r)).ToArray();
        if(registrations.GroupBy(r=>r.ExpertId,StringComparer.Ordinal).Any(g=>g.Count()>1)) throw new InvalidDataException("同一期Pool不能绑定同一ExpertId的多个Revision");
        var registry=ReadAsOf(asOf);
        if(registrations.Any(r=>!registry.Experts.Any(x=>x.ExpertRevisionId==r.ExpertRevisionId))) throw new InvalidDataException("Revision在Pool AsOf时尚不可用");
        string[] included=registrations.Where(r=>r.EligibleForMacro&&r.Enabled).Select(r=>r.ExpertId).ToArray();
        string[] excluded=registrations.Where(r=>!r.EligibleForMacro||!r.Enabled).Select(r=>r.ExpertId).ToArray();
        return new ExpertPoolSnapshot(targetIssue,asOf,registry.Version,registrations.Where(r=>r.EligibleForMacro).Select(r=>r.ExpertId).ToImmutableArray(),
            included.ToImmutableArray(),included.ToImmutableArray(),ImmutableArray<string>.Empty,excluded.ToImmutableArray(),
            excluded.ToImmutableDictionary(x=>x,x=>ImmutableArray.Create("EligibleForMacro=false或Enabled=false"),StringComparer.Ordinal),
            ImmutableDictionary<string,string>.Empty,HistoricalEvaluationMode.HistoricalAvailability,
            registrations.ToImmutableDictionary(x=>x.ExpertId,x=>x.ExpertRevisionId,StringComparer.Ordinal));
    }

    private static void ValidateRegistration(ExpertRegistration r)
    {
        if(string.IsNullOrWhiteSpace(r.ExpertId)||string.IsNullOrWhiteSpace(r.ExpertRevisionId)||string.IsNullOrWhiteSpace(r.AlgorithmVersion)||string.IsNullOrWhiteSpace(r.CodeVersion)||string.IsNullOrWhiteSpace(r.DependencyEvidenceReference)) throw new InvalidDataException("专家身份、版本或源码证据缺失");
        if(r.Enabled||r.EligibleForMacro) throw new InvalidDataException("P5注册禁止启用专家");
    }
    private static int Depth(SQLiteConnection c,string revision,HashSet<string> visiting)
    {
        if(!visiting.Add(revision)) throw new InvalidDataException("消费依赖形成循环");
        string[] parents=Strings(c,$"SELECT ToRevisionId FROM ExpertDependency WHERE FromRevisionId=@r AND DependencyType IN ({string.Join(',',Consuming.Select(x=>(int)x))})",("@r",revision)).ToArray();
        int result=parents.Length==0?0:parents.Max(p=>Depth(c,p,visiting))+1;
        visiting.Remove(revision); return result;
    }
    private static bool CreatesCycle(SQLiteConnection c,string from,string to)=>from==to||Reachable(c,to,from,new HashSet<string>(StringComparer.Ordinal));
    private static bool Reachable(SQLiteConnection c,string current,string target,HashSet<string> seen)
    {
        if(current==target)return true;if(!seen.Add(current))return false;
        return Strings(c,$"SELECT ToRevisionId FROM ExpertDependency WHERE FromRevisionId=@r AND DependencyType IN ({string.Join(',',Consuming.Select(x=>(int)x))})",("@r",current)).Any(next=>Reachable(c,next,target,seen));
    }
    private static ExpertRegistration Require(SQLiteConnection c,string revision)=>Rows<ExpertRegistration>(c,"SELECT PayloadJson FROM ExpertRevision WHERE ExpertRevisionId=@r",("@r",revision)).SingleOrDefault()??throw new InvalidDataException("未知父Revision或依赖Revision");
    private SQLiteConnection Open(){var c=new SQLiteConnection($"Data Source={path};Version=3;");c.Open();return c;}
    private static void Execute(SQLiteConnection c,string sql,params (string,object?)[] args){using var q=new SQLiteCommand(sql,c);foreach(var a in args)q.Parameters.AddWithValue(a.Item1,a.Item2??DBNull.Value);q.ExecuteNonQuery();}
    private static object? Scalar(SQLiteConnection c,string sql,params (string,object?)[] args){using var q=new SQLiteCommand(sql,c);foreach(var a in args)q.Parameters.AddWithValue(a.Item1,a.Item2??DBNull.Value);return q.ExecuteScalar();}
    private static IEnumerable<string> Strings(SQLiteConnection c,string sql,params (string,object?)[] args){using var q=new SQLiteCommand(sql,c);foreach(var a in args)q.Parameters.AddWithValue(a.Item1,a.Item2??DBNull.Value);using var r=q.ExecuteReader();while(r.Read())yield return r.GetString(0);}
    private static IEnumerable<T> Rows<T>(SQLiteConnection c,string sql,params (string,object?)[] args){foreach(string json in Strings(c,sql,args))yield return JsonSerializer.Deserialize<T>(json,JsonOptions)??throw new InvalidDataException("注册表JSON无效");}
}
