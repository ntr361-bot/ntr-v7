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
from datetime import datetime
from pathlib import Path
from zoneinfo import ZoneInfo

from .freeze_operation import check_window
from .schedule import current_issue, decide, forum49_collect_gate, freeze_window_allowed, snapshot_exists

TZ = ZoneInfo("Asia/Shanghai")
MODELS = {
    "118": ("forum-observer.yml", "Forum observer daily cycle", "archive", "forum-observer"),
    "49": ("forum49-model.yml", "49论坛模型 collection", "forum49", "forum49"),
}
POLL_SECONDS = 12
MAX_WAIT_SECONDS = 65 * 60


def gh(*args):
    result = subprocess.run(["gh", *args], capture_output=True, text=True, timeout=35)
    if result.returncode:
        raise RuntimeError("GitHub operation failed: " + " ".join(args[:3]) +
                           ": " + (result.stderr or result.stdout).strip()[:500])
    return result.stdout


def api(path):
    """Retry only safe read-only GitHub requests after transient outages.

    Never retry the downstream workflow_dispatch action: an ambiguous POST
    response could have launched a real collection already.
    """
    for attempt in range(3):
        try:
            return json.loads(gh("api", path))
        except (RuntimeError, subprocess.TimeoutExpired, json.JSONDecodeError) as exc:
            message = str(exc).lower()
            transient = any(mark in message for mark in (
                "429", "502", "503", "504", "timeout", "timed out",
                "connection reset", "temporarily unavailable", "eof"))
            if attempt == 2 or not transient:
                raise
            time.sleep(attempt + 1)
    raise AssertionError("unreachable")


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
    for model in selected:
        _, _, folder, _ = MODELS[model]
        archive = Path(root) / folder
        if mode == "collect":
            # A frozen issue may still be observed, but downstream workflows
            # must isolate it from the formal archive and prediction history.
            if model == "118":
                allowed, _, _, reason = decide(now, archive, manual=True)
                if not allowed or reason not in ("manual_rerun", "manual_test_after_freeze"):
                    raise ValueError(f"118 collection gate refused: {reason}")
            else:
                allowed, reason, test_only = forum49_collect_gate(archive, now, False)
                if not allowed:
                    raise ValueError(f"49 collection gate refused: {reason}")
        else:
            if snapshot_exists(archive, today):
                raise ValueError(f"{model} issue {today} is already frozen; refusing {mode}")
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


def matches_model_run(item, model):
    """GitHub's run.name may equal the custom run-name, not workflow name.

    The workflow's stable path is authoritative for matching the actual job.
    Older API fixtures without path still support their fixed workflow name.
    """
    workflow, fixed_name, _, _ = MODELS[model]
    path = str(item.get("path") or "")
    return path.endswith("/" + workflow) or (not path and item.get("name") == fixed_name)


def matches_mode(item, mode):
    title = str(item.get("display_title") or "")
    if item.get("event") == "schedule" and title.startswith(("Forum 118 scheduled", "Forum 49 scheduled")):
        return title.endswith(" " + mode)
    if title.startswith(("Forum 118 ", "Forum 49 ")):
        return title.split()[2] == mode
    # Legacy branch markers identify a freeze. Unknown older workflows
    # conservatively reserve collection, while freeze has its own lock.
    return mode == ("freeze" if "freeze" in title.lower() else "collect")


def assert_no_active_runs(repo, selected, mode=None):
    runs = api(f"repos/{repo}/actions/runs?per_page=100").get("workflow_runs", [])
    for model in selected:
        active = [v for v in runs if matches_model_run(v, model)
                  and v.get("status") not in ("completed", None)
                  and (mode is None or matches_mode(v, mode))]
        if active:
            v = active[0]
            raise RuntimeError(f"{model}: another run is still {v.get('status')}: {v.get('html_url')}")


def find_runs(repo, request_id, selected):
    items = api(f"repos/{repo}/actions/runs?per_page=100").get("workflow_runs", [])
    found = {}
    for item in items:
        if item.get("event") != "workflow_dispatch":
            continue
        title = str(item.get("display_title") or "")
        for model in selected:
            if (matches_model_run(item, model)
                    and title.startswith(f"Forum {model} ")
                    and title.endswith(f" manual#{request_id}")):
                found[model] = item
    return found


def verify(repo, model, mode, issue, run):
    """The Actions conclusion alone is insufficient proof."""
    folder = MODELS[model][2]
    root = f"forum-observer/{folder}/issues/{issue}"
    if mode == "collect":
        attempt = int(run.get("run_attempt") or 1)
        try:
            doc = json.loads(github_contents(repo, f"{root}/collection-runs/{run['id']}-{attempt}.json"))
        except RuntimeError as exc:
            if "404" not in str(exc):
                raise
            ns = "forum49/" if model == "49" else ""
            telemetry = json.loads(github_contents(
                repo, f"forum-observer/status/{ns}tests/runs/{run['id']}-{attempt}.json"))
            if (telemetry.get("issue") == issue and telemetry.get("run_id") == str(run["id"])
                    and telemetry.get("attempt") == attempt and telemetry.get("test_only") is True
                    and telemetry.get("state") == "test_completed"
                    and (telemetry.get("valid_materials") or 0) > 0):
                return f"{model}: verified isolated observation, issue {issue}"
            raise
        if (doc.get("issue") != issue
                or str(doc.get("run_id")) != str(run["id"])
                or doc.get("run_attempt") != attempt
                or int(doc.get("valid_materials") or 0) <= 0
                or not doc.get("draft_published")
                or not doc.get("report_published")):
            raise RuntimeError(f"{model}: committed collection receipt is invalid")
        if doc.get("round"):
            if (doc.get("valid_authors") != doc["valid_materials"]
                    or not doc.get("draft_sha256") or not doc.get("report_sha256")):
                raise RuntimeError(f"{model}: incomplete collection integrity receipt")
            report_path = f"{root}/rounds/{doc['round']}/report.md"
        else:
            report_path = f"{root}/report.md"
        report = api(f"repos/{repo}/contents/{report_path}?ref=main")
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
        parent = api(f"repos/{repo}/actions/runs/{request_id}")
        now = datetime.now(TZ)
        issue = current_issue(datetime.fromisoformat(parent["created_at"]))
        if issue != current_issue(now):
            raise ValueError("manual request belongs to a previous issue; submit a new request")
        selected = checked_targets(target)
        summary(f"### Manual forum operation: {mode}, issue {issue}, target {target}")
        errors = {}
        dispatched = []
        for model in selected:
            try:
                if mode == "freeze":
                    folder = Path(MODELS[model][2]) / "issues" / issue
                    if (folder / "snapshot.json").exists():
                        summary(verify(repo, model, mode, issue, {}))
                        continue
                    check_window(issue, now, True)
                    # Validate or recover in the child from its fresh checkout.
                    # A prior failure in either model must not block the other.
                assert_no_active_runs(repo, [model], mode)
                gh("workflow", "run", MODELS[model][0], "--ref", "main",
                   "-f", f"mode={mode}", "-f", f"request_id={request_id}",
                   "-f", f"issue={issue}")
                dispatched.append(model)
                summary(f"Dispatched {model}; awaiting downstream verification.")
            except (RuntimeError, ValueError, OSError, subprocess.SubprocessError) as exc:
                errors[model] = str(exc)
                summary(f"- {model}: {exc}")
        started = time.monotonic()
        pending = set(dispatched)
        while pending and time.monotonic() - started < MAX_WAIT_SECONDS:
            matched = find_runs(repo, request_id, pending)
            for model, run in matched.items():
                if run.get("status") != "completed":
                    continue
                pending.remove(model)
                link = run.get("html_url", "")
                if run.get("conclusion") != "success":
                    errors[model] = f"downstream {run.get('conclusion')}: {link}"
                    summary(f"- {model}: {errors[model]}")
                    continue
                for retry in range(6):
                    try:
                        evidence = verify(repo, model, mode, issue, run)
                        summary(f"- {evidence} | {link}")
                        break
                    except (RuntimeError, ValueError, KeyError, OSError, subprocess.SubprocessError) as exc:
                        if retry == 5:
                            errors[model] = f"committed evidence missing: {exc}; {link}"
                            summary(f"- {model}: {errors[model]}")
                        else:
                            time.sleep(5)
            if pending:
                time.sleep(POLL_SECONDS)
        if pending:
            raise RuntimeError("Timed out awaiting real results: " + ", ".join(sorted(pending)))
        if errors:
            raise RuntimeError("; ".join(f"{model}: {reason}" for model, reason in errors.items()))
        summary("✅ All selected models passed the run-specific committed evidence checks.")
        return 0
    except Exception as exc:
        summary("❌ Manual forum operation failed: " + str(exc))
        return 1


if __name__ == "__main__":
    sys.exit(main())
