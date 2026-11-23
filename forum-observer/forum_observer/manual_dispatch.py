"""Manually dispatch and verify both independent forum pipelines.

Runs only from an explicit GitHub workflow_dispatch. Never scrapes, scores,
freezes, edits snapshots or touches V7/D1 itself. A downstream success is
accepted only when its run-specific committed evidence is verified.
"""
import base64
import hashlib
import json
import os
import subprocess
import sys
import time
from datetime import datetime, timedelta
from pathlib import Path
from zoneinfo import ZoneInfo

from .schedule import current_issue, decide, forum49_collect_gate, freeze_window_allowed, snapshot_exists

TZ = ZoneInfo("Asia/Shanghai")
MODELS = {
    "118": ("forum-observer.yml", "Forum observer daily cycle", "archive", "forum-observer"),
    "49": ("forum49-model.yml", "49论坛模型 collection", "forum49", "forum49"),
}
MIN_START_LEAD_SECONDS = 300  # Leave room for browser startup before the 21:00 cutoff.
POLL_SECONDS = 12
MAX_WAIT_SECONDS = 65 * 60


def gh(*args):
    result = subprocess.run(["gh", *args], capture_output=True, text=True, timeout=35)
    if result.returncode:
        raise RuntimeError("GitHub operation failed: " + " ".join(args[:3]) +
                           ": " + (result.stderr or result.stdout).strip()[:500])
    return result.stdout


def api(path):
    return json.loads(gh("api", path))


def github_contents(repo, path):
    item = api(f"repos/{repo}/contents/{path}?ref=main")
    if item.get("encoding") != "base64":
        raise RuntimeError("Unexpected GitHub contents encoding for " + path)
    return base64.b64decode(item["content"]).decode("utf-8")


def checked_targets(target):
    if target == "both":
        return ["118", "49"]
    if target not in MODELS:
        raise ValueError("target must be 118, 49, or both")
    return [target]


def preflight(target, mode, root, now=None):
    """Fail closed before dispatch. The downstream gates are also mandatory."""
    if mode not in ("collect", "freeze"):
        raise ValueError("mode must be collect or freeze")
    now = (now or datetime.now(TZ)).astimezone(TZ)
    today = current_issue(now)
    selected = checked_targets(target)
    cutoff = now.replace(hour=21, minute=0, second=0, microsecond=0)
    for model in selected:
        _, _, folder, _ = MODELS[model]
        archive = Path(root) / folder
        if snapshot_exists(archive, today):
            raise ValueError(f"{model} issue {today} is already frozen; refusing {mode}")
        if mode == "collect":
            remaining = (cutoff - now).total_seconds()
            if remaining < MIN_START_LEAD_SECONDS:
                raise ValueError(f"{model} collect: fewer than 5 minutes remain before the 21:00 cutoff")
            if model == "118":
                allowed, _, _, reason = decide(now, archive, manual=True)
                if not allowed or reason != "manual_rerun":
                    raise ValueError(f"118 collection gate refused: {reason}")
            else:
                allowed, reason, test_only = forum49_collect_gate(archive, now, False)
                if not allowed or test_only:
                    raise ValueError(f"49 collection gate refused: {reason}")
        else:
            if not freeze_window_allowed(now, archive, manual=True):
                raise ValueError(f"{model} freeze: no eligible same-day draft after 21:00")
            p = archive / "issues" / today / "draft-status.json"
            status = json.loads(p.read_text(encoding="utf-8"))
            if not (int(status.get("valid_materials") or 0) > 0
                    and len(status.get("ranking") or []) == 12
                    and len(status.get("top6") or []) == 6
                    and (archive / "issues" / today / "report.md").is_file()):
                raise ValueError(f"{model} freeze: incomplete report or ranking")
            draw = status.get("draw_at")
            try:
                deadline = datetime.fromisoformat(draw)
            except (TypeError, ValueError):
                raise ValueError(f"{model} freeze: verified draw cutoff is missing")
            if deadline.tzinfo is None or now >= deadline:
                raise ValueError(f"{model} freeze: verified draw cutoff has passed")
    return selected, today


def assert_no_active_runs(repo, selected):
    runs = api(f"repos/{repo}/actions/runs?per_page=100").get("workflow_runs", [])
    for model in selected:
        name = MODELS[model][1]
        active = [v for v in runs if v.get("name") == name
                  and v.get("status") not in ("completed", None)]
        if active:
            v = active[0]
            raise RuntimeError(f"{model}: another run is still {v.get('status')}: {v.get('html_url')}")


def find_runs(repo, request_id, selected):
    items = api(f"repos/{repo}/actions/runs?per_page=100").get("workflow_runs", [])
    found = {}
    for item in items:
        if item.get("event") != "workflow_dispatch":
            continue
        if f"manual#{request_id}" not in item.get("display_title", ""):
            continue
        for model in selected:
            if item.get("name") == MODELS[model][1]:
                found[model] = item
    return found


def verify(repo, model, mode, issue, run):
    """The Actions conclusion alone is insufficient proof."""
    folder = MODELS[model][2]
    root = f"forum-observer/{folder}/issues/{issue}"
    if mode == "collect":
        attempt = int(run.get("run_attempt") or 1)
        doc = json.loads(github_contents(repo, f"{root}/collection-runs/{run['id']}-{attempt}.json"))
        if (doc.get("issue") != issue
                or str(doc.get("run_id")) != str(run["id"])
                or doc.get("run_attempt") != attempt
                or int(doc.get("valid_materials") or 0) <= 0
                or not doc.get("draft_published")
                or not doc.get("report_published")):
            raise RuntimeError(f"{model}: committed collection receipt is invalid")
        report = api(f"repos/{repo}/contents/{root}/report.md?ref=main")
        if not report.get("sha"):
            raise RuntimeError(f"{model}: report is missing")
        return f"{model}: verified {doc['valid_materials']} materials and report, issue {issue}"
    snap = json.loads(github_contents(repo, f"{root}/snapshot.json"))
    core = dict(snap)
    digest = core.pop("digest", None)
    actual = hashlib.sha256(json.dumps(core, ensure_ascii=False, sort_keys=True).encode()).hexdigest()
    if snap.get("issue") != issue or not digest or digest != actual or not snap.get("frozen_at"):
        raise RuntimeError(f"{model}: immutable snapshot or digest invalid")
    return f"{model}: verified immutable snapshot, issue {issue}, frozen_at {snap['frozen_at']}"


def summary(message):
    print(message, flush=True)
    target = os.environ.get("GITHUB_STEP_SUMMARY")
    if target:
        with open(target, "a", encoding="utf-8") as f:
            f.write(message + "\n\n")


def main():
    repo = os.environ["GITHUB_REPOSITORY"]
    target = os.environ.get("FORUM_TARGET", "both")
    mode = os.environ.get("FORUM_MODE", "collect")
    request_id = os.environ["GITHUB_RUN_ID"]
    try:
        selected, issue = preflight(target, mode, Path("."))
        assert_no_active_runs(repo, selected)
        summary(f"### Manual forum operation: {mode}, issue {issue}, target {target}")
        for model in selected:
            workflow = MODELS[model][0]
            gh("workflow", "run", workflow, "--ref", "main",
               "-f", f"mode={mode}", "-f", f"request_id={request_id}")
            summary(f"Dispatched {model}; awaiting downstream verification (not yet successful).")
        started = time.monotonic()
        done = {}
        while len(done) != len(selected) and time.monotonic() - started < MAX_WAIT_SECONDS:
            matched = find_runs(repo, request_id, selected)
            for model, run in matched.items():
                if model in done or run.get("status") != "completed":
                    continue
                link = run.get("html_url", "")
                if run.get("conclusion") != "success":
                    raise RuntimeError(f"{model} downstream run did not succeed: {link} ({run.get('conclusion')})")
                # GitHub reports a completed run after its archive push. Retry
                # eventual-consistency gaps without accepting older drafts.
                last_error = None
                for _ in range(6):
                    try:
                        evidence = verify(repo, model, mode, issue, run)
                        break
                    except Exception as exc:
                        last_error = exc
                        time.sleep(5)
                else:
                    raise RuntimeError(f"{model}: Actions green but committed evidence missing: {last_error}; {link}")
                done[model] = evidence
                summary(f"- {evidence} | {link}")
            if len(done) < len(selected):
                time.sleep(POLL_SECONDS)
        if len(done) != len(selected):
            raise RuntimeError("Timed out awaiting real results: " + ", ".join(m for m in selected if m not in done))
        summary("✅ All selected models passed the run-specific committed evidence checks.")
        return 0
    except Exception as exc:
        summary("❌ Manual forum operation failed: " + str(exc))
        return 1


if __name__ == "__main__":
    sys.exit(main())
