# V7 GitHub 云端同步 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 V7 桌面端优先从正式 GitHub 数据档案同步开奖、预测与运行状态，并在 GitHub 临时不可用时回退到现有 V7 云端接口。

**Architecture:** 将同步资源地址解析和下载行为从单一 Cloudflare 接口拆为可顺序尝试的数据源。同步循环继续使用同一套完整性校验、缓存和数据库合并逻辑，只记录本次成功的来源并向界面反馈。GitHub 原始内容服务是主源；`/api/v7-sync/desktop` 是只读备用源。

**Tech Stack:** C# / .NET 10 Windows Forms、HttpClient、System.Text.Json、现有控制台冒烟测试。

## Global Constraints

- 只修改 `D:\projects\六合分析软v7正式版` 主目录。
- 不修改预测算法、V6.5 自动学习显示、模型结果或 Cloudflare 网站规则。
- 只读 GitHub 发布档案；不得下载或写入仓库外的预测数据。
- GitHub 主源成功时不请求备用接口；所有导入继续经过现有完整快照校验与合并去重。

---

### Task 1: 定义可回退的 V7 档案数据源

**Files:**
- Modify: `CloudPredictionSyncService.cs`
- Test: `Tests/Program.cs`

**Interfaces:**
- Consumes: 资源名 `history`、`manifest`、`prediction?file=<issue>.json`、`runtime-state`。
- Produces: `CloudSyncResult(..., string Source)`，以及供测试调用的 `CreateGitHubSyncRequest(string resource)`。

- [ ] **Step 1: Write the failing test**

在 `Tests/Program.cs` 增加 `GitHubDesktopCloudSyncUsesPublishedV7Archive()`，断言：

```csharp
using HttpRequestMessage history = CloudPredictionSyncService.CreateGitHubSyncRequest("history");
using HttpRequestMessage prediction = CloudPredictionSyncService.CreateGitHubSyncRequest("prediction?file=2026239.json");
Assert(history.RequestUri?.ToString() == "https://raw.githubusercontent.com/ntr361-bot/ntr-v7/main/site/data/history.json",
    "GitHub 主同步源没有读取 V7 正式开奖档案");
Assert(prediction.RequestUri?.ToString() == "https://raw.githubusercontent.com/ntr361-bot/ntr-v7/main/site/data/daily-records/2026239.json",
    "GitHub 主同步源没有读取对应预测档案");
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project Tests/六合分析软件.SmokeTests.csproj --configuration Release`

Expected: 编译失败，提示 `CreateGitHubSyncRequest` 不存在；其他既有失败单独记录。

- [ ] **Step 3: Write minimal implementation**

在 `CloudPredictionSyncService` 中新增 `CreateGitHubSyncRequest`，将资源严格映射到：

```text
history       -> site/data/history.json
manifest      -> site/data/daily-records/manifest.json
runtime-state -> site/data/runtime-state.json
prediction?file=<safe-json-name> -> site/data/daily-records/<safe-json-name>
```

资源名或预测文件名无效时抛出 `ArgumentException`，不得把查询字符串拼入路径。将 `CloudSyncResult` 增加 `Source` 字段，供界面展示。

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project Tests/六合分析软件.SmokeTests.csproj --configuration Release`

Expected: 新增测试通过；若出现已有的 `DailyPredictionAutomation.cs` 定位失败，记录为既有独立问题。

- [ ] **Step 5: Commit**

```powershell
git add CloudPredictionSyncService.cs Tests/Program.cs
git commit -m "feat: add V7 GitHub sync source"
```

### Task 2: 让同步按主源、备用源顺序执行

**Files:**
- Modify: `CloudPredictionSyncService.cs`
- Test: `Tests/Program.cs`

**Interfaces:**
- Consumes: `CreateGitHubSyncRequest` 和现有 `CreateMachineSyncRequest`。
- Produces: `SyncAsync` 使用单一成功来源完成整轮同步；`CloudSyncResult.Source` 为 `GitHub V7 正式数据` 或 `V7 备用云端`。

- [ ] **Step 1: Write the failing test**

将下载地址选择提取为内部可测试的 `CloudSyncSource` 值，并在测试中用可控 `HttpMessageHandler` 构造主源返回成功、备用源返回失败的场景。断言同步请求均以 GitHub 原始内容地址发出，且结果来源为 `GitHub V7 正式数据`；再构造 GitHub 抛出 `HttpRequestException`、备用源返回成功的场景，断言结果来源为 `V7 备用云端`。

```csharp
Assert(result.Source == "GitHub V7 正式数据", "主源成功时同步结果没有标明 GitHub 来源");
Assert(fallbackResult.Source == "V7 备用云端", "主源失败时没有回退到 V7 备用云端");
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project Tests/六合分析软件.SmokeTests.csproj --configuration Release`

Expected: 新增来源断言失败，因为当前下载逻辑只使用一个固定接口。

- [ ] **Step 3: Write minimal implementation**

将 `DownloadAsync<T>` 改为在整轮同步开始时确定一个 `CloudSyncSource`：先用 GitHub 拉取 `history`，主源任一请求失败则在尚未写入预测数据前改用备用源重新开始该轮；若两个来源都失败，抛出包含 `GitHub` 与 `备用云端` 摘要的异常。禁止在同一轮中混用两个来源的历史、清单或预测文件。

使用现有 `ImportHistoryArchive`、`ImportPrediction`、`SymmetricRuntimeStateSync.MergeIntoLocal`；不要复制导入逻辑。

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet run --project Tests/六合分析软件.SmokeTests.csproj --configuration Release`

Expected: 新增主源与回退测试通过，且完整云端预测快照导入测试继续通过。

- [ ] **Step 5: Commit**

```powershell
git add CloudPredictionSyncService.cs Tests/Program.cs
git commit -m "fix: fall back when V7 GitHub sync is unavailable"
```

### Task 3: 在界面显示实际同步来源并完成发布验证

**Files:**
- Modify: `Form1.cs`
- Test: `Tests/Program.cs`

**Interfaces:**
- Consumes: `CloudSyncResult.Source`。
- Produces: 成功状态文字包含成功来源；失败状态保留现有 15 分钟重试行为。

- [ ] **Step 1: Write the failing test**

增加一个界面契约测试，调用一个只负责格式化同步状态的内部方法：

```csharp
string status = Form1.FormatCloudSyncSuccess(new CloudSyncResult("2026239", 2026239, 1, 1, 3, "GitHub V7 正式数据"));
Assert(status.Contains("GitHub V7 正式数据", StringComparison.Ordinal),
    "同步成功状态没有展示实际数据来源");
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet run --project Tests/六合分析软件.SmokeTests.csproj --configuration Release`

Expected: 编译失败，提示 `FormatCloudSyncSuccess` 不存在。

- [ ] **Step 3: Write minimal implementation**

在 `Form1` 中新增不访问控件的 `internal static string FormatCloudSyncSuccess(CloudSyncResult result)`，并让 `SyncCloudDataAsync` 使用它。文案应包含来源、最新开奖期号、最新预测期号、预测档案数量和导入行数；保留原失败处理、日志和 15 分钟定时器。

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet build 六合分析软件.csproj --configuration Release`

Expected: Release 生成成功且无编译错误。

Run: `dotnet run --project Tests/六合分析软件.SmokeTests.csproj --configuration Release`

Expected: 新增状态测试通过；报告任何不属于本次变更的既有测试失败。

- [ ] **Step 5: Commit**

```powershell
git add Form1.cs Tests/Program.cs
git commit -m "feat: show V7 sync source in desktop status"
```

