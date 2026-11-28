"""Idempotent schedule gate for the independent forum observer only.

Collection may start at any time. Schedule events use the actual Beijing date
at process start, and an existing frozen snapshot sends later collection to
an isolated test path. Freeze remains separately gated at 21:00.
"""
import argparse
import hashlib
import json
import os
from datetime import datetime
from pathlib import Path
from zoneinfo import ZoneInfo

TZ = ZoneInfo("Asia/Shanghai")
CUTOFF_HOUR, CUTOFF_MINUTE = 21, 0
# Existing backup cron identity is retained for compatibility; it is not a time gate.
BACKUP_COLLECT_CRON = '25 12 * * *'
EVENING_START = (20, 0)
FREEZE_START = (21, 0)
FREEZE_END = (21, 31)
MAX_SECONDS = 1800


def current_issue(now):
    local = now.astimezone(TZ)
    return f"{local.year}{local.timetuple().tm_yday:03d}"


def collection_window_allowed(now):
    """Collection has no time-of-day gate; retained for API compatibility."""
    return True


def gpt_trigger_wait_seconds(now, round_number=1):
    """Compatibility helper: every GPT trigger starts immediately."""
    return 0


def draft_is_current_evening(data, issue, now, since=None):
    local = now.astimezone(TZ)
    if data.get("issue") != issue or not data.get("fetched_at"):
        return False
    try:
        captured = datetime.fromisoformat(data["fetched_at"])
    except (TypeError, ValueError):
        return False
    if captured.tzinfo is None:
        return False
    captured_local = captured.astimezone(TZ)
    return (
        captured_local.date() == local.date()
        and current_issue(captured_local) == issue
        and (captured_local.hour, captured_local.minute) >= (EVENING_START if since is None else since)
    )


def freeze_window_allowed(now, archive=None, manual=False):
    local = now.astimezone(TZ)
    minute = (local.hour, local.minute)
    # Even an explicit manual freeze must not happen before 21:00 Beijing.
    # Manual retries after the scheduled window still need a verified
    # current-issue draft and the cycle's separate pre-draw cutoff check.
    if minute < FREEZE_START:
        return False
    if not manual and not (FREEZE_START <= minute < FREEZE_END):
        return False
    if archive is None:
        return True
    issue = current_issue(local)
    draft = Path(archive) / "issues" / issue / "draft-status.json"
    try:
        data = json.loads(draft.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return False
    # Manual freeze accepts the current-issue draft at any time. The cycle
    # still checks the 21:00 evidence cutoff and refuses snapshot overwrites.
    return draft_is_current_evening(data, issue, local, since=(0, 0))


def snapshot_exists(archive, issue):
    path = Path(archive) / "issues" / issue / "snapshot.json"
    if not path.exists():
        return False
    data = json.loads(path.read_text(encoding="utf-8"))
    digest = data.get("digest")
    body = dict(data)
    body.pop("digest", None)
    check = hashlib.sha256(json.dumps(
        body, ensure_ascii=False, sort_keys=True).encode()).hexdigest()
    if data.get("issue") != issue or not digest or digest != check:
        raise ValueError(f"Existing snapshot is invalid; refusing overwrite: {path}")
    return True


def decide(now, archive, manual=False, scheduled=False, slot=None, requested_round=None):
    """Gate an all-day collection or backup retry without duplicate scans.

    All-day retries are allowed. A completed current-issue automatic draft
    suppresses redundant backups; manual requests can always refresh it.
    """
    local = now.astimezone(TZ)
    issue = current_issue(now)
    minute = (local.hour, local.minute)
    # Cron slots and trigger markers identify a request, but never limit its
    # execution time. Delayed events are evaluated against the current issue.
    frozen = snapshot_exists(archive, issue)
    after_freeze = minute >= FREEZE_START
    if frozen or after_freeze:
        # Post-freeze observations run in test mode and never publish to the
        # formal archive, even if a delayed freeze has not produced a snapshot.
        return True, issue, MAX_SECONDS, "manual_test_after_freeze"
    draft = Path(archive) / 'issues' / issue / 'draft-status.json'
    if not manual and draft.exists():
        try:
            previous = json.loads(draft.read_text(encoding='utf-8'))
        except (OSError, ValueError):
            previous = {}
        if (isinstance(previous, dict) and previous.get('issue') == issue
                and previous.get('status') == 'ready_observation'
                and type(previous.get('valid_materials')) is int and previous['valid_materials'] > 0
                and (draft.parent / 'report.md').is_file()
                and draft_is_current_evening(previous, issue, local)):
            # Tonight's collection (automatic or a manual run from 20:00)
            # already produced a draft: automatic triggers never repeat it.
            return False, issue, 0, "current_round_already_completed"
    return True, issue, MAX_SECONDS, ("manual_rerun" if manual else "round_collection_or_retry")


def forum49_collect_gate(archive, now, skip_if_collected):
    """Decide collection vs. post-freeze isolated observation for forum 49.

    Automatic backup retries run without a time gate and skip when a valid
    current-issue draft already exists. Returns (collect, reason, test_only).
    """
    local = now.astimezone(TZ)
    minute = (local.hour, local.minute)
    issue = current_issue(local)
    frozen = snapshot_exists(archive, issue)
    if minute >= FREEZE_START or frozen:
        return True, "manual_test_after_freeze", True
    if not skip_if_collected:
        return True, "manual_collection", False
    draft = Path(archive) / "issues" / issue / "draft-status.json"
    try:
        data = json.loads(draft.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        data = None
    if (isinstance(data, dict) and data.get("status") == "ready_observation"
            and type(data.get("valid_materials")) is int and data["valid_materials"] > 0
            and (draft.parent / "report.md").is_file()
            and draft_is_current_evening(data, issue, local)):
        return False, "already_collected_tonight", False
    return True, "automatic_collection", False


def ensure_daily_report(archive, now):
    """Publish an honest issue-scoped nightly report even without a collector run.

    Never fabricate qualifying evidence or produce a freeze in this path.
    """
    issue=current_issue(now)
    root=Path(archive)
    folder=root/"issues"/issue
    report=folder/"report.md"
    if report.exists():
        print("PASS: nightly report exists:",report)
        return True
    snap_path=folder/"snapshot.json"
    snapshot=json.loads(snap_path.read_text(encoding="utf-8")) if snap_path.exists() else None
    lines=[f"# 论坛 {issue} 本期报告","",
           f"核查时间：{now.astimezone(TZ).isoformat()}",
           "状态："+("冻结快照存在，但原采集报告缺失" if snapshot else "未获得可冻结资料；没有本期冻结预测"),
           "这是自动核查生成的状态报告，不是采集成功报告，也不包含虚构排名。"]
    if snapshot:
        lines += [
            f"有效资料：{snapshot.get('valid_materials','未知')}份",
            f"冻结质量：{snapshot.get('quality_status',snapshot.get('freeze_classification','历史快照'))}",
            "冻结Top3："+'、'.join(snapshot.get('top3',[])),
            "冻结Top6："+'、'.join(snapshot.get('top6',[]))]
    else:
        lines += ["有效资料：未核实", "冻结状态：无", "缺失原因：本期未生成可验证的采集报告或模型任务未正常完成"]
    folder.mkdir(parents=True,exist_ok=True)
    report.write_text("\n".join(lines)+"\n",encoding="utf-8")
    latest=root/"latest.json"
    if not snapshot:
        prev=json.loads(latest.read_text(encoding="utf-8")) if latest.exists() else {}
        if str(prev.get("issue",""))<issue:
            latest.write_text(json.dumps({
                "issue":issue,"status":"missing_collection_report",
                "raw_count":0,"valid_materials":0,"valid_prediction_authors":0,
                "top3":[],"top6":[],"updated_at":now.isoformat(),
                "notes":"No verified current-issue collector data; nightly failure report only."
            },ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
    print("Nightly report generated:",report)
    return False


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--archive", default="archive")
    parser.add_argument("--github-output", default=os.environ.get("GITHUB_OUTPUT"))
    parser.add_argument("--require-snapshot", action="store_true")
    parser.add_argument("--ensure-report", action="store_true")
    parser.add_argument("--manual", action="store_true",help="Allow an explicit current-issue collection")
    parser.add_argument("--scheduled", action="store_true",help="Mark a scheduled collection request")
    parser.add_argument("--align-gpt-trigger", action="store_true",
                        help="Compatibility no-op; GPT collection triggers start immediately")
    parser.add_argument("--check-freeze-window", action="store_true",help="Check the current Beijing nightly freeze window")
    parser.add_argument("--manual-freeze", action="store_true",
                        help="Allow an explicit freeze outside the nightly window for today's current-issue draft")
    parser.add_argument("--gpt-second-round", action="store_true",help="Compatibility flag for older triggers")
    parser.add_argument("--forum49-collect-gate", action="store_true",
                        help="Decide whether a 49论坛模型 collect run should collect (env F49_SKIP_IF_COLLECTED)")
    a = parser.parse_args()
    now = datetime.now(TZ)
    if a.align_gpt_trigger:
        print("GPT collection trigger accepted; collection is available 24 hours.", flush=True)
        return 0
    if a.forum49_collect_gate:
        skip = os.environ.get("F49_SKIP_IF_COLLECTED", "false").lower() == "true"
        collect, reason, test = forum49_collect_gate(a.archive, now, skip)
        print(json.dumps({"collect": collect, "reason": reason, "test_only": test}, ensure_ascii=False), flush=True)
        if a.github_output:
            with open(a.github_output, "a", encoding="utf-8") as f:
                f.write(f"collect={str(collect).lower()}\nreason={reason}\ntest={str(test).lower()}\nissue={current_issue(now)}\n")
        return 0
    if a.check_freeze_window:
        allowed = freeze_window_allowed(now, a.archive, manual=a.manual_freeze)
        if allowed:
            reason = "manual_current_draft" if a.manual_freeze else "within_freeze_window"
        elif a.manual_freeze:
            reason = "manual_freeze_missing_current_draft"
        elif not freeze_window_allowed(now):
            reason = "scheduled_freeze_window_missed"
        else:
            reason = "scheduled_freeze_missing_evening_collection"
        print(json.dumps({"allowed": allowed, "reason": reason}, ensure_ascii=False), flush=True)
        if a.github_output:
            with open(a.github_output, "a", encoding="utf-8") as f:
                f.write(f"allowed={str(allowed).lower()}\n")
                f.write(f"reason={reason}\n")
        return 0
    issue = current_issue(now)
    if a.ensure_report:
        ensure_daily_report(a.archive, now)
        return 0
    if a.require_snapshot:
        if (now.hour,now.minute)<(CUTOFF_HOUR,CUTOFF_MINUTE):
            print(f"PENDING: {issue} final freeze is scheduled at 21:00 Asia/Shanghai")
            return 0
        if not snapshot_exists(a.archive, issue):
            raise SystemExit(f"ERROR: {issue} has no immutable forum snapshot at daily audit")
        print(f"PASS: {issue} immutable forum snapshot exists")
        return 0
    scheduled=a.scheduled or os.environ.get("SCHEDULED_RUN") == "true"
    manual=a.manual or (int(os.environ.get("GITHUB_RUN_ATTEMPT","1"))>1 and not scheduled)
    requested_round = os.environ.get('FORUM_REQUESTED_ROUND')
    collect, issue, seconds, reason = decide(now, a.archive,manual=manual,scheduled=scheduled,
                                             slot=os.environ.get('FORUM_SCHEDULE_CRON'),
                                             requested_round=requested_round)
    local = now.astimezone(TZ)
    collection_round = 1
    print(json.dumps({"issue":issue, "collect":collect,
                      "collection_round":collection_round,
                      "max_seconds":seconds, "reason":reason},
                     ensure_ascii=False), flush=True)
    if a.github_output:
        with open(a.github_output, "a", encoding="utf-8") as f:
            f.write(f"collection_round={collection_round}\n")
            f.write(f"collect={str(collect).lower()}\n")
            f.write(f"issue={issue}\n")
            f.write(f"max_seconds={seconds}\n")
            f.write(f"reason={reason}\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
