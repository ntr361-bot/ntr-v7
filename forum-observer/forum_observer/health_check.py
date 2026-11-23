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
from datetime import datetime, timezone
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


def fetch_manual_sample(github, pages=4):
    all_runs = []
    for page in range(1, pages + 1):
        body = github.request(
            "actions/workflows/manual-forum-operations.yml/runs?"
            f"event=workflow_dispatch&per_page=100&page={page}")
        rows = body.get("workflow_runs", [])
        all_runs.extend(rows)
        count = sum(bool(MANUAL_TITLE.fullmatch(str(r.get("display_title") or "")))
                    for r in all_runs)
        if count >= MAX_SAMPLES or len(rows) < 100:
            break
    return manual_sample(all_runs)


def snapshot_audit(github, issue):
    """Check today's snapshot SHA256 and report, without touching any files."""
    results = {}
    for model, archive in (("118", "archive"), ("49", "forum49")):
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


def run(mode, publish=True, github=None, now=None, root=Path(".")):
    if mode not in ("probe", "audit", "all"):
        raise ValueError("mode must be probe, audit, all")
    now = (now or datetime.now(TZ)).astimezone(TZ)
    today = now.date().isoformat()
    issue = current_issue(now)
    github = github or GitHub()
    probes = {}
    if mode in ("probe", "all"):
        for model, url in model_urls(root / "collector-config.json").items():
            probes[model] = probe_with_retry(url)
    audits = safe_snapshot_audit(github, issue) if mode in ("audit", "all") else {}
    stats = fetch_manual_sample(github)
    findings = findings_for(mode, probes, audits, stats)
    report = {
        "schema_version": 1,
        "checked_at": now.isoformat(),
        "issue": issue,
        "mode": mode,
        "state": "attention" if findings else "healthy",
        "probes": probes,
        "archive_audit": audits,
        "last_30_manual_operations": stats,
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
        previous, prev_data = github.content(HEALTH_DIR + "latest.json")
        prev_findings = prev_data.get("findings", []) if previous else []
        # Status-only paths; never touch archive, frozen data or site/data.
        github.write(HEALTH_DIR + f"{today}-{mode}.json", report)
        github.write(HEALTH_DIR + "latest.json", report, latest=True)
        update_alert(github, findings, prev_findings, report)
    return report


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", choices=("probe", "audit", "all"), default="all")
    parser.add_argument("--publish", action="store_true",
                        help="Write health-only status and deduplicated issue")
    args = parser.parse_args()
    report = run(args.mode, publish=args.publish)
    if report["findings"]:
        print("::warning::Forum health requires attention: " + ", ".join(report["findings"]))
        return 1 if args.publish else 0
    return 0


if __name__ == "__main__":
    sys.exit(main())
