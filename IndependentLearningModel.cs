using System.Data.SQLite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace 六合分析软件;

/// <summary>Standalone experimental rank learner. No legacy database, memory or learning calls.</summary>
public sealed class IndependentLearningModel
{
    public const string ModelKey = "v7-independent-learning-v1";
    public const string CodeVersion = "independent-rank-softmax-1";
    private const double Rate = 0.05;
    private static readonly string[] Sources = { "v65-50", "v65-100", "v65-all" };
    private static readonly string[] Names = { "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪" };
    private readonly string path;

    public IndependentLearningModel(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot)) throw new ArgumentException("需要独立数据目录");
        path = Path.Combine(Path.GetFullPath(dataRoot), "experiments", ModelKey, "learning.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS IndependentState(Id INTEGER PRIMARY KEY CHECK(Id=1), Json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS IndependentPrediction(Issue INTEGER PRIMARY KEY, Json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS LearningReceipt(Issue INTEGER PRIMARY KEY, Version INTEGER NOT NULL UNIQUE, Json TEXT NOT NULL);
            """);
        Exec(c,"INSERT OR IGNORE INTO IndependentState VALUES(1,@j)",("@j",Json(new State())));
        ReadState(c);
    }

    private sealed record Input(long Issue, long HistoryCutoffIssue, DateTimeOffset SourceGeneratedAt,
        Dictionary<string,string[]> SourceRankings);
    private sealed record Prediction(string Model, string Code, int Schema, DateTimeOffset GeneratedAt,
        long UsedMemoryVersion, Input Input, string InputHash, double[] Weights, double[] Scores,
        double[] Probabilities, string[] Ranking);
    private sealed record State
    {
        public string Model { get; init; } = ModelKey;
        public string Code { get; init; } = CodeVersion;
        public int Schema { get; init; } = 1;
        public double LearningRate { get; init; } = Rate;
        public long Version { get; init; }
        public long LastIssue { get; init; }
        public double[] Theta { get; init; } = new double[3];
    }
    private sealed record Receipt(long Issue, string Actual, int ActualRank, string InputHash,
        DateTimeOffset LearnedAt, State Before, State After);

    private SQLiteConnection Open()
    {
        var c = new SQLiteConnection(new SQLiteConnectionStringBuilder {
            DataSource = path, Version = 3, Pooling = false, DefaultTimeout = 10 }.ToString());
        c.Open();
        return c;
    }
    private static int Exec(SQLiteConnection c,string sql,params (string Key,object Value)[] args)
    {
        using var cmd = new SQLiteCommand(sql,c);
        foreach(var p in args) cmd.Parameters.AddWithValue(p.Key,p.Value);
        return cmd.ExecuteNonQuery();
    }
    private static string? Scalar(SQLiteConnection c,string sql,params (string Key,object Value)[] args)
    {
        using var cmd = new SQLiteCommand(sql,c);
        foreach(var p in args) cmd.Parameters.AddWithValue(p.Key,p.Value);
        return cmd.ExecuteScalar() as string;
    }
    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json) ?? throw new InvalidDataException("实验数据为空");
    private static State ReadState(SQLiteConnection c)
    {
        var s = Read<State>(Scalar(c,"SELECT Json FROM IndependentState WHERE Id=1")!);
        if (s.Model != ModelKey || s.Code != CodeVersion || s.Schema != 1 || s.LearningRate != Rate ||
            s.Version < 0 || s.LastIssue < 0 || s.Theta.Length != 3 || s.Theta.Any(x=>!double.IsFinite(x) || Math.Abs(x)>5))
            throw new InvalidDataException("独立模型状态或版本不兼容，禁止静默重置");
        return s;
    }
    public string ReadStateJson() { using var c = Open(); return Json(ReadState(c)); }
    public string? ReadPredictionJson(long issue) { using var c = Open(); return Saved(c,issue); }
    public string? ReadReceiptJson(long issue)
    {
        using var c = Open();
        return Scalar(c,"SELECT Json FROM LearningReceipt WHERE Issue=@i",("@i",issue));
    }
    private static string? Saved(SQLiteConnection c,long issue) => Scalar(c,
        "SELECT Json FROM IndependentPrediction WHERE Issue=@i",("@i",issue));

    public string[] PendingPredictions()
    {
        using var c=Open();
        return Rows(c,"SELECT Json FROM IndependentPrediction WHERE Issue NOT IN (SELECT Issue FROM LearningReceipt) ORDER BY Issue");
    }
    private static string[] Rows(SQLiteConnection c,string sql)
    {
        using var cmd=new SQLiteCommand(sql,c); using var r=cmd.ExecuteReader(); var values=new List<string>();
        while(r.Read()) values.Add(r.GetString(0)); return values.ToArray();
    }
    private sealed record Archive(string Model,string Code,string State,string[] Predictions,string[] Receipts);
    public string ExportArchive()
    {
        using var c=Open(); Exec(c,"BEGIN");
        try {
            string json=Json(new Archive(ModelKey,CodeVersion,Json(ReadState(c)),
                Rows(c,"SELECT Json FROM IndependentPrediction ORDER BY Issue"),Rows(c,"SELECT Json FROM LearningReceipt ORDER BY Version")));
            Exec(c,"COMMIT"); return json;
        } catch { Exec(c,"ROLLBACK"); throw; }
    }
    // Validate in an isolated transaction; never merge divergent learning branches.
    public void RestoreArchive(string json)
    {
        var archive=Read<Archive>(json);
        if(archive.Model!=ModelKey || archive.Code!=CodeVersion) throw new InvalidDataException("独立学习档案版本不匹配");
        using var c=Open(); Exec(c,"BEGIN IMMEDIATE");
        try {
            if(ReadState(c).Version!=0 || Scalar(c,"SELECT Json FROM IndependentPrediction LIMIT 1") is not null)
                throw new InvalidDataException("档案只允许恢复至空的独立库，禁止覆盖本地学习分支");
            var expected=new State(); var predictions=archive.Predictions.Select(Read<Prediction>).ToArray();
            var receipts=archive.Receipts.Select(Read<Receipt>).ToArray();
            if(predictions.Select(p=>p.Input.Issue).Distinct().Count()!=predictions.Length || predictions.Length<receipts.Length || predictions.Length>receipts.Length+1)
                throw new InvalidDataException("学习档案记录数量异常");
            for(int i=0;i<predictions.Length;i++) {
                var p=predictions[i]; Validate(p.Input,p.GeneratedAt);
                var result=Evaluate(p.Input,expected);
                if(p.Model!=ModelKey || p.Code!=CodeVersion || p.Schema!=1 || p.InputHash!=Hash(p.Input) ||
                    p.UsedMemoryVersion!=expected.Version || (expected.LastIssue!=0 && p.Input.HistoryCutoffIssue!=expected.LastIssue) ||
                    !p.Ranking.SequenceEqual(result.Ranking) || !p.Weights.SequenceEqual(result.Weights) ||
                    !p.Scores.SequenceEqual(result.Scores) || !p.Probabilities.SequenceEqual(result.P))
                    throw new InvalidDataException("学习档案预测不可复现");
                Exec(c,"INSERT INTO IndependentPrediction VALUES(@i,@j)",("@i",p.Input.Issue),("@j",Json(p)));
                if(i<receipts.Length) {
                    var a=receipts[i];
                    if(a.Issue!=p.Input.Issue || !Names.Contains(a.Actual) || a.InputHash!=p.InputHash ||
                        a.ActualRank!=Array.IndexOf(p.Ranking,a.Actual)+1 || Json(a.Before)!=Json(expected) ||
                        Json(a.After)!=Json(Update(p.Input,expected,a.Actual)))
                        throw new InvalidDataException("学习档案审计链不可复现");
                    expected=a.After;
                    Exec(c,"INSERT INTO LearningReceipt VALUES(@i,@v,@j)",("@i",a.Issue),("@v",expected.Version),("@j",Json(a)));
                }
            }
            if(Json(expected)!=archive.State) throw new InvalidDataException("学习档案最终状态不一致");
            Exec(c,"UPDATE IndependentState SET Json=@j WHERE Id=1",("@j",archive.State)); Exec(c,"COMMIT");
        } catch { Exec(c,"ROLLBACK"); throw; }
    }

    private static void Validate(Input input,DateTimeOffset now)
    {
        if (input.Issue <= 0 || input.HistoryCutoffIssue <= 0 || input.HistoryCutoffIssue >= input.Issue ||
            input.SourceGeneratedAt == default || input.SourceGeneratedAt > now || input.SourceRankings is null ||
            input.SourceRankings.Count != 3 || Sources.Any(s=> !input.SourceRankings.TryGetValue(s,out var ranks) ||
                ranks is null || ranks.Length != 12 || !ranks.Order().SequenceEqual(Names.Order())))
            throw new InvalidDataException("输入必须包含目标期之前的截止期、生成时间和三模型完整12生肖排序");
        // Within-year issues must be contiguous. At year rollover require the first issue and previous year.
        if (input.Issue != input.HistoryCutoffIssue+1 &&
            !(input.Issue % 1000 == 1 && input.Issue/1000 == input.HistoryCutoffIssue/1000+1))
            throw new InvalidDataException("预测不能跳过历史截止期之后的期号");
    }
    private static string Hash(Input input) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Json(input))));
    private static double[][] Features(Input input) => Names.Select(z=>Sources.Select(s=>
        (11-Array.IndexOf(input.SourceRankings[s],z))/11d).ToArray()).ToArray();
    private static double[] Softmax(double[] values)
    {
        double max=values.Max(); var e=values.Select(v=>Math.Exp(v-max)).ToArray(); double sum=e.Sum();
        return e.Select(v=>v/sum).ToArray();
    }
    private static (double[] Weights,double[] Scores,double[] P,string[] Ranking) Evaluate(Input input,State state)
    {
        var w=Softmax(state.Theta); var x=Features(input);
        var scores=x.Select(row=>row.Select((v,i)=>v*w[i]).Sum()).ToArray();
        return (w,scores,Softmax(scores),Enumerable.Range(0,12).OrderByDescending(i=>scores[i]).ThenBy(i=>i).Select(i=>Names[i]).ToArray());
    }

    /// <summary>Consumes caller-verified pre-draw rankings only. It never computes or changes legacy predictions.</summary>
    public string Predict(string inputJson)
    {
        var input=Read<Input>(inputJson); var now=DateTimeOffset.UtcNow; Validate(input,now);
        // Canonical source order makes dictionary insertion order irrelevant to the immutable hash.
        input=input with { SourceRankings=Sources.ToDictionary(s=>s,s=>input.SourceRankings[s]) };
        using var c=Open(); Exec(c,"BEGIN IMMEDIATE");
        try
        {
            string? saved=Saved(c,input.Issue);
            if(saved is not null)
            {
                if(Read<Prediction>(saved).InputHash!=Hash(input)) throw new InvalidDataException("同一期输入冲突，不覆盖历史预测");
                Exec(c,"COMMIT"); return saved;
            }
            var state=ReadState(c);
            if ((state.LastIssue!=0 && state.LastIssue!=input.HistoryCutoffIssue) ||
                Scalar(c,"SELECT CAST(Issue AS TEXT) FROM IndependentPrediction WHERE Issue NOT IN (SELECT Issue FROM LearningReceipt) LIMIT 1") is not null)
                throw new InvalidDataException("上一期尚未学习或存在缺口，禁止跳期预测");
            var result=Evaluate(input,state);
            string json=Json(new Prediction(ModelKey,CodeVersion,1,now,state.Version,input,Hash(input),
                result.Weights,result.Scores,result.P,result.Ranking));
            Exec(c,"INSERT INTO IndependentPrediction VALUES(@i,@j)",("@i",input.Issue),("@j",json));
            Exec(c,"COMMIT"); return json;
        }
        catch { Exec(c,"ROLLBACK"); throw; }
    }

    /// <summary>Caller must verify the draw; feedback uses only the saved pre-draw input, not newly calculated ranks.</summary>
    public bool Learn(long issue,string actual,long previousIssue)
    {
        if(!Names.Contains(actual)) throw new InvalidDataException("实际生肖无效");
        using var c=Open(); Exec(c,"BEGIN IMMEDIATE");
        try
        {
            string? prior=Scalar(c,"SELECT Json FROM LearningReceipt WHERE Issue=@i",("@i",issue));
            if(prior is not null)
            {
                var receipt=Read<Receipt>(prior);
                if(receipt.Actual!=actual || Read<Prediction>(Saved(c,issue)!).Input.HistoryCutoffIssue!=previousIssue)
                    throw new InvalidDataException("已有学习结果与新反馈冲突");
                Exec(c,"COMMIT"); return false;
            }
            var prediction=Read<Prediction>(Saved(c,issue) ?? throw new InvalidDataException("缺少开奖前预测快照，禁止补造"));
            var before=ReadState(c);
            Validate(prediction.Input,prediction.GeneratedAt);
            if(prediction.InputHash!=Hash(prediction.Input) || prediction.UsedMemoryVersion!=before.Version ||
                previousIssue!=prediction.Input.HistoryCutoffIssue || (before.LastIssue!=0 && before.LastIssue!=previousIssue))
                throw new InvalidDataException("快照版本、截止期或学习顺序不一致");
            var after=Update(prediction.Input,before,actual);
            var audit=new Receipt(issue,actual,Array.IndexOf(prediction.Ranking,actual)+1,prediction.InputHash,DateTimeOffset.UtcNow,before,after);
            Exec(c,"UPDATE IndependentState SET Json=@j WHERE Id=1",("@j",Json(after)));
            Exec(c,"INSERT INTO LearningReceipt VALUES(@i,@v,@j)",("@i",issue),("@v",after.Version),("@j",Json(audit)));
            Exec(c,"COMMIT"); return true;
        }
        catch { Exec(c,"ROLLBACK"); throw; }
    }

    private static State Update(Input input,State before,string actual)
    {
        var (w,_,p,_)=Evaluate(input,before); var x=Features(input); int a=Array.IndexOf(Names,actual);
        var delta=Enumerable.Range(0,3).Select(s=>x[a][s]-Enumerable.Range(0,12).Sum(z=>p[z]*x[z][s])).ToArray();
        double mean=Enumerable.Range(0,3).Sum(s=>w[s]*delta[s]);
        var theta=Enumerable.Range(0,3).Select(s=>Math.Clamp(before.Theta[s]+Rate*w[s]*(delta[s]-mean),-5,5)).ToArray();
        return before with { Version=checked(before.Version+1),LastIssue=input.Issue,Theta=theta };
    }
}
