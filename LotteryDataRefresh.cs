namespace 六合分析软件;

public sealed record LotteryRefreshResult(
    string LocalIssueBefore,
    string RemoteLatestIssue,
    string LocalIssueAfter,
    int SourceRecordCount,
    int NewRecordCount,
    bool DryRun);

public static class LotteryDataRefresh
{
    public static async Task<LotteryRefreshResult> RefreshAsync(
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine("[INFO] 开始连接开奖数据源");
        DatabaseHelper.InitializeDatabase();
        string before = DatabaseHelper.GetLatestPeriod();
        Console.WriteLine($"[INFO] 更新前最新开奖期号：{before}");

        DataCrawler.CrawlResult crawl = await DataCrawler.FetchAndSaveAsync(
            periods: 500,
            isTest: dryRun,
            cancellationToken: cancellationToken);
        if (!crawl.Success)
            throw new InvalidOperationException($"开奖数据抓取失败：{crawl.Message}");
        if (!long.TryParse(crawl.RemoteLatestPeriod, out long remoteIssue))
            throw new InvalidDataException($"数据源最新期号无效：{crawl.RemoteLatestPeriod}");
        if (long.TryParse(before, out long localIssue) && remoteIssue < localIssue)
            throw new InvalidDataException($"数据源期号 {remoteIssue} 落后于本地期号 {localIssue}，已停止更新");

        string after = dryRun ? before : DatabaseHelper.GetLatestPeriod();
        if (!dryRun)
        {
            IReadOnlyList<string> warnings = PredictionAutomation.ValidateHistory(
                DatabaseHelper.GetLatestHistory(int.MaxValue));
            foreach (string warning in warnings)
                Console.WriteLine($"[WARNING] {warning}");
            if (after != crawl.RemoteLatestPeriod)
                throw new InvalidDataException($"数据库更新后期号 {after} 与数据源 {crawl.RemoteLatestPeriod} 不一致");
        }

        Console.WriteLine($"[INFO] 数据源最新开奖期号：{crawl.RemoteLatestPeriod}");
        Console.WriteLine($"[INFO] 数据源返回记录：{crawl.TotalCount}");
        Console.WriteLine($"[INFO] 新增开奖记录：{(dryRun ? 0 : crawl.NewCount)}");
        Console.WriteLine(dryRun
            ? "[SUCCESS] 开奖数据源检查通过（dry-run 未写入数据库）"
            : "[SUCCESS] 历史开奖数据更新完成");

        return new LotteryRefreshResult(
            before,
            crawl.RemoteLatestPeriod,
            after,
            crawl.TotalCount,
            dryRun ? 0 : crawl.NewCount,
            dryRun);
    }
}
