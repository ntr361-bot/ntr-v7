# P25 Target Issue Parsing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ensure P25 website signals contain only the requested issue block and never absorb prior-issue recommendations or revealed results.

**Architecture:** Bind the website target issue at the fetch boundary, isolate one issue block before extracting values, and keep the existing ranking and first-write database behavior. Retain the existing three-argument parser as a compatibility wrapper for callers and tests that parse the first issue.

**Tech Stack:** C# 13, .NET 10 Windows, `System.Text.RegularExpressions`, existing console smoke-test project.

## Global Constraints

- Do not add an HTML parsing dependency.
- Do not change prediction models, database schema, or source-weight algorithms.
- Do not delete or overwrite the existing affected 2026256 frozen record.
- A source with no requested issue, a revealed result, or no target-issue zodiac must be skipped.

---

### Task 1: Isolate the requested issue block

**Files:**
- Modify: `Tests/MacroP21P25Tests.cs`
- Modify: `WebsiteLearningService.cs`

**Interfaces:**
- Consumes: normalized website script text, source id, source hash, requested short issue.
- Produces: `WebsiteLearningParser.Parse(string text, string sourceId, string sourceHash, int expectedIssue)` returning one `WebsiteParsedSignal` for exactly `expectedIssue`.

- [ ] **Step 1: Write failing regression tests**

Add literal multi-issue fixtures proving that the existing parser does not absorb 255-period zodiac values into period 256. Add a reflection-based contract check for the new four-argument parser so the test project still compiles before the API exists. Assert that parsing issue 256 from a fixture containing issue 257 and 256 returns only the requested issue block.

- [ ] **Step 2: Run the P21/P25 smoke suite and verify RED**

Run:

```powershell
$env:NUGET_PACKAGES='C:\Users\Administrator\.nuget\packages'
dotnet run --project Tests\六合分析软件.SmokeTests.csproj --no-restore
```

Expected: FAIL because the existing parser extracts zodiac values from later issue blocks and the four-argument target-issue API does not exist.

- [ ] **Step 3: Implement minimal block isolation**

In `WebsiteLearningParser`, locate issue markers with `(?<!\d)(\d{1,4})期`. For the requested marker, slice from its start to the next issue marker start or end of text. Run result detection and zodiac extraction only on that slice. Throw `InvalidDataException` if the requested issue is absent. Keep the three-argument `Parse` overload by resolving the first issue marker and delegating to the four-argument overload.

- [ ] **Step 4: Verify GREEN**

Run the same smoke command. Expected: `P21_P25_SMOKE_PASS` and exit code 0.

- [ ] **Step 5: Commit**

```powershell
git add WebsiteLearningService.cs Tests\MacroP21P25Tests.cs
git commit -m "fix: isolate P25 website signals by issue"
```

### Task 2: Bind fetching to the database target issue

**Files:**
- Modify: `Tests/MacroP21P25Tests.cs`
- Modify: `WebsiteLearningService.cs`

**Interfaces:**
- Consumes: `FetchAsync(Uri page, int expectedIssue, CancellationToken cancellationToken = default)`.
- Produces: signals whose `Issue` equals `expectedIssue`; `WebsiteLearningIntegration.Publish(long targetIssue)` passes `targetIssue % 1000` at the fetch boundary.

- [ ] **Step 1: Write failing behavior tests**

Add tests proving that target-issue content with `开：？00` is accepted, target-issue content with `开：猪44` is rejected, and a no-zodiac target block cannot import zodiac values from a prior issue.

- [ ] **Step 2: Verify RED**

Run the smoke project. Expected: FAIL on at least one new target-block assertion under the old full-text result detector/extractor.

- [ ] **Step 3: Implement target-bound fetch and result normalization**

Change `FetchAsync` to accept `expectedIssue` and call the four-argument parser. Strip HTML tags and decode text before checking the `开` value. Treat `？`, `?`, and `？00`/`?00` as placeholders; treat a non-placeholder value as revealed. Update `Publish` to calculate `shortIssue` before fetching and pass it to `FetchAsync`. Preserve per-source skip behavior for `InvalidDataException`.

- [ ] **Step 4: Verify GREEN and build**

Run:

```powershell
$env:NUGET_PACKAGES='C:\Users\Administrator\.nuget\packages'
dotnet run --project Tests\六合分析软件.SmokeTests.csproj --no-restore
dotnet build 六合分析软件.csproj -c Release --no-restore --verbosity quiet /clp:ErrorsOnly
```

Expected: smoke suite exit 0 and release build exit 0.

- [ ] **Step 5: Commit**

```powershell
git add WebsiteLearningService.cs Tests\MacroP21P25Tests.cs
git commit -m "fix: bind website learning to target issue"
```

### Task 3: Verify runtime safety without rewriting the frozen record

**Files:**
- Verify: `WebsiteLearningService.cs`
- Verify: `C:\Users\Administrator\AppData\Local\六合分析软件-V7\history.db`

**Interfaces:**
- Consumes: current runtime DB, existing `P25-Web` row for 2026256.
- Produces: audit evidence only; no mutation of the existing row.

- [ ] **Step 1: Record pre-verification state**

Read the database SHA256 and the existing 2026256 P25-Web row. Confirm its first-write snapshot remains present.

- [ ] **Step 2: Run focused parser verification with current issue fixtures**

Run the smoke suite and confirm the literal 256-period fixture yields only current-period zodiac values.

- [ ] **Step 3: Record post-verification state**

Re-read database SHA256 and row values. Expected: identical hash and unchanged frozen record.

- [ ] **Step 4: Final repository verification**

Run `git diff --check`, `git status --short`, the smoke project, and the release build. Report exact outcomes and the remaining status of the affected historical row.
