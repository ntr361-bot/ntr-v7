"""Idempotent schedule gate for the independent forum observer only.

GitHub cron delivery is best-effort. Backups collect only when the current
local-day issue lacks an immutable observation snapshot; no old issue is
recreated or re-frozen by this gate.
"""
import argparse
import hashlib
import json
import math
import os
import time
from datetime import datetime
from pathlib import Path
from zoneinfo import ZoneInfo

TZ = ZoneInfo("Asia/Shanghai")
CUTOFF_HOUR, CUTOFF_MINUTE = 21, 0
COLLECTION_START = (19, 40)
COLLECTION_END = (20, 25)
FREEZE_START = (21, 0)
FREEZE_END = (21, 20)
MAX_SECONDS = 1800
PUBLISH_RESERVE_SECONDS = 90
GPT_TRIGGER_EARLY_TOLERANCE_SECONDS = 5 * 60


def current_issue(now):
    local = now.astimezone(TZ)
    return f"{local.year}{local.timetuple().tm_yday:03d}"


def collection_window_allowed(now):
    local = now.astimezone(TZ)
    minute = (local.hour, local.minute)
    return COLLECTION_START <= minute <= COLLECTION_END


def gpt_trigger_wait_seconds(now):
    """Return delay to 19:40 for a near-on-time GPT trigger, or None if stale."""
    local = now.astimezone(TZ)
    start = local.replace(hour=COLLECTION_START[0], minute=COLLECTION_START[1],
                          second=0, microsecond=0)
    delay = math.ceil((start - local).total_seconds())
    if delay <= 0:
        return 0
    if delay <= GPT_TRIGGER_EARLY_TOLERANCE_SECONDS:
        return delay
    return None


def draft_is_current_evening(data, issue, now):
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
        and (captured_local.hour, captured_local.minute) >= COLLECTION_START
    )


def freeze_window_allowed(now, archive=None):
    local = now.astimezone(TZ)
    minute = (local.hour, local.minute)
    if not (FREEZE_START <= minute < FREEZE_END):
        return False
    if archive is None:
        return True
    issue = current_issue(local)
    draft = Path(archive) / "issues" / issue / "draft-status.json"
    try:
        data = json.loads(draft.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return False
    return draft_is_current_evening(data, issue, local)


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


def decide(now, archive, manual=False, scheduled=False):
    """Return (collect, issue, seconds, reason); scheduled events fail closed outside the nightly window."""
    local = now.astimezone(TZ)
    issue = current_issue(now)
    if scheduled and not manual and not collection_window_allowed(local):
        return False, issue, 0, "scheduled_event_outside_collection_window"
    if snapshot_exists(archive, issue):
        return False, issue, 0, "immutable_snapshot_exists"
    draft=Path(archive)/'issues'/issue/'draft-status.json'
    if not manual and draft.exists():
        # Backups stop once sufficient real current-issue evidence has been
        # collected. Manual dispatch / GitHub rerun may still rescan until 21:00.
        previous=json.loads(draft.read_text(encoding='utf-8'))
        if (previous.get('issue')==issue and
                previous.get('status')=='ready_observation' and
                int(previous.get('valid_materials') or 0)>=25):
            # An early/overnight draft is not tonight's collection. Let the
            # first in-window scheduled run replace it before the freeze.
            if not scheduled or draft_is_current_evening(previous, issue, local):
                return False, issue, 0, "completed_draft_waiting_for_21_freeze"
    cutoff = local.replace(hour=CUTOFF_HOUR, minute=CUTOFF_MINUTE,
                           second=0, microsecond=0)
    remaining = int((cutoff - local).total_seconds()) - PUBLISH_RESERVE_SECONDS
    if remaining < 60:
        return False, issue, 0, "daily_collection_cutoff_passed"
    return True, issue, min(MAX_SECONDS, remaining), ("manual_rerun" if manual else "missing_or_incomplete_draft_retry")


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
    parser.add_argument("--manual", action="store_true",help="Allow an explicit rerun before the freeze deadline")
    parser.add_argument("--scheduled", action="store_true",help="Require the scheduled collection window")
    parser.add_argument("--align-gpt-trigger", action="store_true",
                        help="Wait for 19:40 only when a GPT trigger arrives up to five minutes early")
    parser.add_argument("--check-freeze-window", action="store_true",help="Check the current Beijing nightly freeze window")
    a = parser.parse_args()
    now = datetime.now(TZ)
    if a.align_gpt_trigger:
        delay = gpt_trigger_wait_seconds(now)
        if delay is None:
            raise SystemExit("ERROR: GPT collection trigger arrived more than five minutes before 19:40. "
                             "No early collection was started.")
        if delay:
            print(f"GPT collection trigger arrived early; waiting {delay}s for 19:40 Asia/Shanghai.",
                  flush=True)
            time.sleep(delay)
        return 0
    if a.check_freeze_window:
        allowed = freeze_window_allowed(now, a.archive)
        if allowed:
            reason = "within_freeze_window"
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
    collect, issue, seconds, reason = decide(now, a.archive,manual=manual,scheduled=scheduled)
    print(json.dumps({"issue":issue, "collect":collect,
                      "max_seconds":seconds, "reason":reason},
                     ensure_ascii=False), flush=True)
    if a.github_output:
        with open(a.github_output, "a", encoding="utf-8") as f:
            f.write(f"collect={str(collect).lower()}\n")
            f.write(f"issue={issue}\n")
            f.write(f"max_seconds={seconds}\n")
            f.write(f"reason={reason}\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
