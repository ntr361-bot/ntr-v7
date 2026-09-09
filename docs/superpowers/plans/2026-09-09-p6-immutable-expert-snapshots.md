# P6 Immutable Expert Snapshots Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement an append-only sidecar store for versioned expert rankings and an auditable resolver for each issue's dynamic expert pool.

**Architecture:** `ImmutableExpertSnapshotStore` canonicalizes, validates, hashes, and transactionally stores expert snapshots and frozen pool records. `ExpertPoolResolver` classifies registered experts and candidate snapshots without substituting missing data; live and causal-reconstruction timelines are isolated. P7 consumes the resulting frozen pool and prefix snapshots but remains outside production.

**Tech Stack:** C# 14 / .NET 10, immutable Macro contracts, System.Data.SQLite, console smoke tests.

## Global Constraints

- No Gating, COUNTER, Macro reasoning, learning weights, production integration, cloud publication, or UI.
- Existing formal V7/V6.5 algorithms, Top3/Top6, daily workflow, and the 17-issue experiment remain unchanged.
- Current six P5 candidates remain disabled and ineligible.
- Exact duplicate writes are idempotent; conflicting writes are rejected and never overwrite prior rows.
- HistoricalAvailability and CausalReconstruction remain separate.

---

### Task 1: Canonical immutable expert snapshots

**Files:**
- Create: `Tests/ImmutableExpertSnapshotTests.cs`
- Modify: `Tests/Program.cs`
- Create: `ImmutableExpertSnapshotStore.cs`

**Interfaces:**
- Consumes: `ExpertSnapshot`, `VersionedExpertRegistry`, P5 dependency edges.
- Produces: `FreezeAndAppend`, `ReadSnapshot`, and `ReadPrefixSnapshots` with canonical payload hashes.

- [x] **Step 1: Write failing tests** for deterministic hashing, idempotence, conflicting content, unknown Revision, version mismatch, incomplete ranking, future LiveFrozen time, invalid reconstruction, and missing parent snapshot.
- [x] **Step 2: Run `--p6-snapshot-smoke`** and verify failure because the store is absent.
- [x] **Step 3: Implement canonical validation and append-only SQLite storage.**
- [x] **Step 4: Re-run P6 tests and keep all snapshot cases green.**

### Task 2: Dynamic pool resolution and immutable pool records

**Files:**
- Modify: `Tests/ImmutableExpertSnapshotTests.cs`
- Create: `ExpertPoolResolver.cs`
- Modify: `ImmutableExpertSnapshotStore.cs`

**Interfaces:**
- Consumes: `ExpertRegistrySnapshot`, target issue, AsOf, candidate snapshots, evaluation mode.
- Produces: `ExpertPoolSnapshot` and immutable `StoredExpertPool` records.

- [x] **Step 1: Add failing tests** for Eligible/Available/Included/Missing/Excluded, exclusion reasons, no substitution, conflicting active Revision, live/reconstruction isolation, pool idempotence, and conflict rejection.
- [x] **Step 2: Run P6 smoke and confirm resolver assertions fail before implementation.**
- [x] **Step 3: Implement strict resolver classification and frozen pool hashing/storage.**
- [x] **Step 4: Re-run P6 smoke and confirm all pool cases pass.**

### Task 3: P7 compatibility and regression gate

**Files:**
- Modify: `Tests/ImmutableExpertSnapshotTests.cs`
- Modify: `MacroObservationEngine.cs`
- Create: `docs/P6-不可变专家快照-验收报告.md`

**Interfaces:**
- Consumes: P6 snapshots and frozen pool.
- Produces: a valid `PrefixContext` input path for P7 in both declared historical modes.

- [x] **Step 1: Add failing integration tests** proving P7 reads stored LiveFrozen snapshots, reconstruction provenance is not treated as live time, and formal V7 output is unchanged.
- [x] **Step 2: Implement only the compatibility validation required by the two frozen modes.**
- [x] **Step 3: Run the focused P6 Release build and 30-case P6 acceptance suite.** Full-suite rerun was omitted at the user's direction before repository upload.
- [x] **Step 4: Document formulas, storage keys, classifications, remaining non-production boundary, and test evidence.**
