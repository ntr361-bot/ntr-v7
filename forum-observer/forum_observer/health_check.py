"""Independent health checks and last-30 ledger-manual reliability report.

Read-only forum probes and GitHub archive audits; never starts collection,
scrapes posts, changes ranking, touches D1 or overwrites frozen evidence.
The only writes are health telemetry under forum-observer/status/health/
and a deduplicated GitHub issue when attention is required.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
from datetime import datetime, timedelta, timezone
from pathlib import Path
from urllib.parse import urlparse
from zoneinfo import ZoneInfo

from .forum49 import DEFAULT_URL as FORUM49_URL
from .runtime import GitHub
from .schedule import current_issue

TZ = ZoneInfo("Asia/Shanghai")
ALERT_TITLE = "论坛采集稳定性告警（118/49）"
MANUAL_TITLE = re.compile(
    r"^Manual forum (both|118|49) - (collect|freeze) · request#[0-9a-f-]{36}$", re.I)
HEALTH_DIR = "forum-observer/status/health/"
MAX_SAMPLES = 30
PROBE_RETRIES = 2
PROBE_TIMEOUT_SECONDS = 12


def model_urls(config_path=Path("collector-config.json")):
    config = json.loads(Path(config_path).read_text(encoding="utf-8"))
    return {"118": config["forum_url"], "49": FORUM49_URL}


def probe_once(url, timeout=PROBE_TIMEOUT_SECONDS):
    """One small HTTP fetch, NOT rendered browsing or forum post extraction."""
    host = urlparse(url).hostname
    if urlparse(url).scheme != "https" or not host:
        raise ValueError("Forum health probe requires a configured HTTPS host")
    request = urllib.request.Request(
        url.split("#", 1)[0],
        headers={"User-Agent": "Forum-Readiness-Check/1.0",
                 "Accept": "text/html,*/*;q=0.5",
                 "Range": "bytes=0-32767"})
    with urllib.request.urlopen(request, timeout=timeout) as response:
        status = response.status
        redirect_host = urlparse(response.geturl()).hostname
        body = response.read(32768)
    if redirect_host != host:
        return {"state": "unexpected_redirect", "host": host, "http_status": status}
    if status not in (200, 206):
        return {"state": "unexpected_status", "host": host, "http_status": status}
    text = body.decode("utf-8", "replace").lower()
    if "<html" not in text and "<!doctype html" not in text:
        return {"state": "unexpected_document", "host": host, "http_status": status}
    return {"state": "reachable", "host": host, "http_status": status}


def probe_with_retry(url, sleep=time.sleep):
    """Retry transient network probes twice, never retry the formal collector."""
    result = None
    for attempt in range(PROBE_RETRIES + 1):
        try:
            result = probe_once(url)
            if result["state"] == "reachable":
                break
            # A valid response with a changed page/redirect is not a transient
            # connection failure; more requests cannot repair it.
            break
        except urllib.error.HTTPError as exc:
            result = {"state": "http_denied" if exc.code in (401, 403, 429)
                      else "http_error", "host": urlparse(url).hostname,
                      "http_status": exc.code}
            if exc.code not in (429, 500, 502, 503, 504) or attempt == PROBE_RETRIES:
                break
        except (urllib.error.URLError, OSError, TimeoutError) as exc:
            result = {"state": "network_error", "host": urlparse(url).hostname,
                      "error_type": type(exc).__name__}
            if attempt == PROBE_RETRIES:
                break
        sleep(attempt + 1)
    return result


def _dt(value):
    if not isinstance(value, str):
        return None
    try:
        return datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        return None


def manual_sample(runs):
    """A successful parent is the only proven all-selected-model archive pass.

    Avoid counting a green collector without its committed evidence.
    Distinguish incomplete runs from completed attempts in the denominator.
    """
    eligible = [r for r in runs if
                r.get("event") == "workflow_dispatch" and
                MANUAL_TITLE.fullmatch(str(r.get("display_title") or "")) and
                str(r.get("path") or "").endswith("/manual-forum-operations.yml")]
    eligible.sort(key=lambda r: str(r.get("created_at") or ""), reverse=True)
    selected = eligible[:MAX_SAMPLES]
    completed = [r for r in selected if r.get("status") == "completed"]
    verified = [r for r in completed if r.get("conclusion") == "success"]
    failures = [r for r in completed if r.get("conclusion") != "success"]
    lengths = []
    for run in completed:
        start = _dt(run.get("run_started_at") or run.get("created_at"))
        end = _dt(run.get("updated_at"))
        if start and end and end >= start:
            lengths.append((end - start).total_seconds() / 60)
    by_target = {}
    for run in selected:
        target = MANUAL_TITLE.fullmatch(run["display_title"]).group(1).lower()
        bucket = by_target.setdefault(target, {"total": 0, "verified": 0})
        bucket["total"] += 1
        if run.get("status") == "completed" and run.get("conclusion") == "success":
            bucket["verified"] += 1
    denominator = len(completed)
    return {
        "scope": "last_30_ledger_manual_dispatches; success=parent_verified_committed_evidence",
        "sample_size": len(selected),
        "completed": denominator,
        "verified": len(verified),
        "failed_or_cancelled": len(failures),
        "in_progress": len(selected) - denominator,
        "verified_rate_pct": round(100 * len(verified) / denominator, 1) if denominator else None,
        "mean_completed_minutes": round(sum(lengths) / len(lengths), 1) if lengths else None,
        "by_target": by_target,
        "sample_complete": len(selected) >= MAX_SAMPLES,
    }


def _recent_runs(github, workflow, needed, pages=3):
    items = []
    for page in range(1, pages + 1):
        body = github.request(f"actions/workflows/{workflow}/runs?"
                              f"event=workflow_dispatch&per_page=100&page={page}")
        rows = body.get("workflow_runs", [])
        items.extend(rows)
        if len(items) >= needed or len(rows) < 100:
            break
    return items


def run_model_receipt(github, model, child, issue):
    """Verify matching current-run archive receipt and report via GitHub API."""
    archive = "archive" if model == "118" else "forum49"
    base = f"forum-observer/{archive}/issues/{issue}"
    attempt = int(child.get("run_attempt") or 1)
    try:
        _, doc = github.content(
            f"{base}/collection-runs/{child['id']}-{attempt}.json")
    except urllib.error.HTTPError as exc:
        if exc.code == 404:
            return False, False
        raise
    if not doc or doc.get("issue") != issue or str(doc.get("run_id")) != str(child["id"]):
        return False, False
    if (doc.get("run_attempt") != attempt or
            int(doc.get("valid_materials") or 0) <= 0 or
            not doc.get("draft_published")):
        return False, False
    if not doc.get("report_published"):
        return True, False
    try:
        report = github.request(f"contents/{base}/report.md?ref=main")
    except urllib.error.HTTPError as exc:
        if exc.code == 404:
            return True, False
        raise
    return True, bool(report.get("sha"))


def _model_quality(github, parent_rows):
    """Separate startup, usable collection and report archiving for 30 runs."""
    sample = sorted((r for r in parent_rows if
                     r.get("event") == "workflow_dispatch" and
                     MANUAL_TITLE.fullmatch(str(r.get("display_title") or "")) and
                     str(r.get("path") or "").endswith("/manual-forum-operations.yml")),
                    key=lambda r: str(r.get("created_at") or ""), reverse=True)[:MAX_SAMPLES]
    targets = []
    for parent in sample:
        m = MANUAL_TITLE.fullmatch(parent["display_title"])
        for model in ("118", "49") if m.group(1).lower() == "both" else (m.group(1),):
            targets.append((parent, model, m.group(2).lower()))
    child_runs = {}
    for model, workflow in (("118", "forum-observer.yml"), ("49", "forum49-model.yml")):
        needed = sum(model == target for _, target, _ in targets)
        if not needed:
            continue
        for run in _recent_runs(github, workflow, max(100, needed)):
            key = re.fullmatch(rf"Forum {model} (collect|freeze) manual#(\d+)",
                               str(run.get("display_title") or ""))
            if key and str(run.get("path") or "").endswith("/" + workflow):
                child_runs[(model, key.group(1), key.group(2))] = run
    results = {model: {"requested": 0, "started": 0, "unmatched": 0,
                       "collection_attempts": 0, "effective_collections": 0,
                       "archived_reports": 0} for model in ("118", "49")}
    for parent, model, mode in targets:
        entry = results[model]
        entry["requested"] += 1
        if mode == "collect":
            entry["collection_attempts"] += 1
        child = child_runs.get((model, mode, str(parent["id"])))
        if child is None:
            entry["unmatched"] += 1
            continue
        if child.get("status") in ("in_progress", "completed") and child.get("conclusion") != "skipped":
            entry["started"] += 1
        if mode != "collect" or child.get("status") != "completed":
            continue
        date = _dt(parent.get("created_at"))
        if date is None:
            continue
        issue = current_issue(date.astimezone(TZ))
        collected, archived = run_model_receipt(github, model, child, issue)
        entry["effective_collections"] += int(collected)
        entry["archived_reports"] += int(archived)
    for entry in results.values():
        requested = entry["requested"]
        collected = entry["collection_attempts"]
        entry["startup_rate_pct"] = (round(100 * entry["started"] / requested, 1)
                                     if requested else None)
        entry["effective_collection_rate_pct"] = (
            round(100 * entry["effective_collections"] / collected, 1)
            if collected else None)
        entry["report_archive_rate_pct"] = (
            round(100 * entry["archived_reports"] / collected, 1)
            if collected else None)
    return results


def fetch_manual_sample(github, pages=4):
    all_runs = _recent_runs(github, "manual-forum-operations.yml", MAX_SAMPLES, pages)
    result = manual_sample(all_runs)
    result["by_model"] = _model_quality(github, all_runs)
    return result


def snapshot_audit(github, issue):
    """Check today's snapshot SHA256 and report, without touching any files."""
    results = {}
    for model, archive in (("118", "archive"), ("49", "forum49")):
        try:
            root = f"forum-observer/{archive}/issues/{issue}"
            _, snapshot = github.content(root + "/snapshot.json")
            report = github.request("contents/" + root + "/report.md?ref=main")
            # GitHub.content returns ({}, {}) for a genuine 404, but the report
            # can be absent before a successful collection.
            if not snapshot:
                _, draft = github.content(root + "/draft-status.json")
                results[model] = {
                    "state": "draft_not_frozen" if draft and report.get("sha")
                             else "no_verified_snapshot",
                    "report_present": bool(report.get("sha")),
                    "draft_present": bool(draft),
                }
                continue
            core = dict(snapshot)
            actual_digest = core.pop("digest", None)
            expected = hashlib.sha256(
                json.dumps(core, ensure_ascii=False, sort_keys=True).encode()).hexdigest()
            good = (snapshot.get("issue") == issue and actual_digest == expected
                    and bool(snapshot.get("frozen_at")) and bool(report.get("sha")))
            results[model] = {
                "state": "frozen_verified" if good else "invalid_snapshot_or_report",
                "report_present": bool(report.get("sha")),
                "digest_verified": actual_digest == expected,
            }
        except (OSError, ValueError, KeyError, TypeError) as error:
            results[model] = {"state": "archive_read_error", "error_type": type(error).__name__}
    return results


def _missing_report_as_none(github, path):
    try:
        return github.request(path)
    except urllib.error.HTTPError as exc:
        if exc.code == 404:
            return {}
        raise


def safe_snapshot_audit(github, issue):
    # Report is Markdown, not JSON; only its verified existence/sha is needed.
    class ReadOnlyGitHub:
        def __init__(self, original):
            self.original = original
        def content(self, path):
            return self.original.content(path)
        def request(self, path):
            return _missing_report_as_none(self.original, path)
    return snapshot_audit(ReadOnlyGitHub(github), issue)


def findings_for(mode, probes, audits, stats):
    findings = []
    if mode in ("probe", "all"):
        for model, result in probes.items():
            if result["state"] != "reachable":
                findings.append(f"{model}_probe_{result['state']}")
    if mode in ("audit", "all"):
        for model, result in audits.items():
            if result["state"] != "frozen_verified":
                findings.append(f"{model}_audit_{result['state']}")
    # A low rate becomes meaningful only after at least 10 completed samples.
    if stats["completed"] >= 10 and stats["verified_rate_pct"] < 80:
        findings.append("manual_verified_rate_below_80_pct")
    return sorted(findings)


def merge_findings(mode, new_findings, previous_findings):
    """Keep untested dimensions in alert state, so audit != probe recovery.

    The daytime HTTP probe cannot clear a missing nightly snapshot, and the
    nightly archive audit cannot declare an earlier unreachable forum healthy.
    A check can only clear findings that it actually rechecks.
    """
    if mode == "all":
        return sorted(set(new_findings))
    preserve = ("118_audit_", "49_audit_", "settlement_audit_") if mode == "probe" else ("118_probe_", "49_probe_")
    return sorted(set(new_findings) |
                  {f for f in previous_findings if f.startswith(preserve)})


def update_alert(github, findings, previous_findings, report):
    """One open issue for current problems, no repeated identical comments."""
    issues = github.request("issues?state=open&per_page=100")
    open_alert = next((item for item in issues if
                       item.get("title") == ALERT_TITLE and "pull_request" not in item), None)
    text = ("论坛健康检查结果（仅检查，不启动采集或冻结）：\n\n"
            + "\n".join("- " + finding for finding in findings)
            + "\n\n详见本仓库 forum-observer/status/health/latest.json")
    if findings:
        if open_alert is None:
            github.request("issues", method="POST",
                           data={"title": ALERT_TITLE,
                                 "body": "@ntr361-bot\n\n" + text})
        elif findings != previous_findings:
            github.request(f"issues/{open_alert['number']}/comments", method="POST",
                           data={"body": text})
    elif open_alert is not None:
        github.request(f"issues/{open_alert['number']}/comments", method="POST",
                       data={"body": "✅ 健康检查已恢复正常；保留历史记录以供排查。"})
        github.request(f"issues/{open_alert['number']}", method="PATCH",
                       data={"state": "closed"})


def settlement_audit(path, issue):
    if not path.exists():
        return {"state": "receipt_missing"}
    try:
        saved = json.loads(path.read_text(encoding="utf-8"))
        if saved.get("issue") != issue:
            return {"state": "receipt_stale", "issue": saved.get("issue")}
        return {
            "state": "verified",
            "issue": issue, "run_url": saved["run_url"],
            "settled_count": saved["settled_count"], "expected_count": saved["expected_count"],
            "ledger_sync": saved["ledger_sync"]["state"],
            "pending": {row["id"]: row.get("reason", row["state"]) for row in saved["models"]
                        if row["state"] != "settled"},
            "historical_findings": saved.get("historical_findings", {}),
        }
    except (OSError, ValueError, KeyError, TypeError):
        return {"state": "invalid_receipt"}


def run(mode, publish=True, github=None, now=None, root=Path("."), settlement_receipt=None):
    if mode not in ("probe", "audit", "all"):
        raise ValueError("mode must be probe, audit, all")
    now = (now or datetime.now(TZ)).astimezone(TZ)
    today = now.date().isoformat()
    # A delayed overnight audit belongs to the last completed draw day.
    # Preflight always checks today's sources; never freeze here.
    audit_day = now - timedelta(days=1) if mode == "audit" and (now.hour, now.minute) < (21, 32) else now
    issue = current_issue(audit_day)
    github = github or GitHub()
    probes = {}
    if mode in ("probe", "all"):
        for model, url in model_urls(root / "collector-config.json").items():
            probes[model] = probe_with_retry(url)
    audits = safe_snapshot_audit(github, issue) if mode in ("audit", "all") else {}
    stats = fetch_manual_sample(github)
    current_findings = findings_for(mode, probes, audits, stats)
    settlement = {}
    if settlement_receipt and mode in ("audit", "all"):
        settlement = settlement_audit(settlement_receipt, issue)
        if settlement["state"] != "verified":
            current_findings.append("settlement_audit_" + settlement["state"])
        else:
            if settlement["ledger_sync"] != "verified":
                current_findings.append("settlement_audit_ledger_" + settlement["ledger_sync"])
            current_findings.extend("settlement_audit_" + model + "_" + why
                                    for model, why in settlement["pending"].items())
    previous_record = {}
    if publish:
        _, previous_record = github.content(HEALTH_DIR + "latest.json")
    previous_findings = previous_record.get("findings", [])
    findings = merge_findings(mode, current_findings, previous_findings)
    report = {
        "schema_version": 1,
        "checked_at": now.isoformat(),
        "updated_at": now.isoformat(),
        "issue": issue,
        "mode": mode,
        "state": "attention" if findings else "healthy",
        "probes": probes,
        "archive_audit": audits,
        "settlement_audit": settlement,
        "last_30_manual_operations": stats,
        "current_check_findings": current_findings,
        "findings": findings,
        "notes": "Probe only checks HTTP shell, not actual DOM parsing; "
                 "reliability counts only completed verified parent workflows. "
                 "No collector, freeze, V7 or D1 writes.",
    }
    print(json.dumps(report, indent=2, ensure_ascii=False), flush=True)
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as file:
            file.write(f"## 论坛健康检查 · {today} · {mode}\n\n"
                       f"- 状态：{report['state']}\n"
                       f"- 本期：{issue}\n"
                       f"- 最近{stats['sample_size']}次账本手动运行，"
                       f"完成{stats['completed']}次，验收成功{stats['verified']}次，"
                       f"失败{stats['failed_or_cancelled']}次；"
                       f"成功率{stats['verified_rate_pct']}%（仅已完成样本）\n"
                       f"- 平均已完成任务耗时：{stats['mean_completed_minutes']}分钟\n"
                       f"- 异常：{', '.join(findings) if findings else '无'}\n\n")
    if publish:
        # Status-only paths; never touch archive, frozen data or site/data.
        github.write(HEALTH_DIR + f"{today}-{mode}.json", report)
        github.write(HEALTH_DIR + "latest.json", report, latest=True)
        update_alert(github, findings, previous_findings, report)
    return report


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", choices=("probe", "audit", "all"), default="all")
    parser.add_argument("--publish", action="store_true",
                        help="Write health-only status and deduplicated issue")
    parser.add_argument("--settlement-receipt", type=Path)
    args = parser.parse_args()
    report = run(args.mode, publish=args.publish, settlement_receipt=args.settlement_receipt)
    if report["findings"]:
        print("::warning::Forum health requires attention: " + ", ".join(report["findings"]))
        return 1 if args.publish else 0
    return 0


if __name__ == "__main__":
    sys.exit(main())
