"""Idempotent schedule gate for the independent forum observer only.

User decision 2026-10-26: ONE nightly collection at 20:20 Asia/Shanghai
(trigger(forum-collect-1) marker), running until the 21:00 cutoff. The old
20:00 first round and 20:30 second round are retired; their cron slots and
the trigger(forum-collect-2) marker are rejected. Once tonight's collection
has a valid draft, automatic repeats skip. Old snapshots stay immutable.
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
COLLECTION_START = (20, 20)  # the single nightly collection (2026-10-26)
COLLECTION_END = (20, 58)
# A manual collection started from 20:00 also counts as tonight's draft
# (it can be frozen, and automatic collections then skip).
EVENING_START = (20, 0)
# Independent GitHub cron backup for the 20:20 collection; it skips when
# tonight's draft already exists (user decision 2026-10-26).
BACKUP_COLLECT_CRON = '20,25 12 * * *'
FORUM49_AUTO_COLLECT_END = (20, 50)
# Retired cron slots (20:00 first round, 20:30 second round): never collect.
ROUND_ONE_CRON = '0,5,10 12 * * *'
ROUND_TWO_CRON = '30,35,40 12 * * *'
ROUND_ONE_END = COLLECTION_END
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


def gpt_trigger_wait_seconds(now, round_number=1):
    """Wait up to five minutes for the 20:20 collection; round two is retired."""
    if round_number != 1:
        return None
    local = now.astimezone(TZ)
    target = COLLECTION_START
    start = local.replace(hour=target[0], minute=target[1],
                          second=0, microsecond=0)
    delay = math.ceil((start - local).total_seconds())
    if delay <= 0:
        return 0
    if delay <= GPT_TRIGGER_EARLY_TOLERANCE_SECONDS:
        return delay
    return None


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
    # Manual collections may run at any time of the day before 21:00
    # (user decision 2026-10-26); the latest same-day draft is frozen.
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
    """Gate the two independent daily rounds without duplicate backup scans.

    First round opens 20:00; a valid first draft never blocks round two.
    The last verified completed draft before 21:00 is the freeze candidate.
    """
    local = now.astimezone(TZ)
    issue = current_issue(now)
    minute = (local.hour, local.minute)
    # Push markers expand ${{ github.event.schedule }} to an empty string, not
    # an unset variable: "" means "no cron slot", never a retired cron.
    slot = slot or None
    # Since 2026-10-26 only the 20:20 backup cron may collect on a schedule;
    # the 20:00/20:30 slots and the second-round marker are retired.
    if (slot is not None and slot != BACKUP_COLLECT_CRON) or requested_round == '2':
        return False, issue, 0, "retired_schedule_trigger"
    if scheduled and not manual and not collection_window_allowed(local):
        return False, issue, 0, "scheduled_event_outside_collection_window"
    if requested_round == '1' and not (COLLECTION_START <= minute < ROUND_ONE_END):
        return False, issue, 0, "requested_round_outside_window"
    deadline = (CUTOFF_HOUR, CUTOFF_MINUTE)
    cutoff = local.replace(hour=deadline[0], minute=deadline[1],
                           second=0, microsecond=0)
    remaining = int((cutoff - local).total_seconds()) - PUBLISH_RESERVE_SECONDS
    frozen = snapshot_exists(archive, issue)
    if manual and (frozen or remaining < 60):
        # User decision 2026-10-26: manual runs after the freeze time are
        # test collections. They never publish a draft or touch the archive.
        # Until tonight's freeze is written (21:00-21:20) they wait, so a
        # test can never delay the freeze past its window.
        if not frozen and (FREEZE_START <= minute < FREEZE_END):
            return False, issue, 0, "manual_test_waits_for_freeze"
        return True, issue, MAX_SECONDS, "manual_test_after_freeze"
    if frozen:
        return False, issue, 0, "immutable_snapshot_exists"
    round_number = 1
    draft = Path(archive) / 'issues' / issue / 'draft-status.json'
    if not manual and draft.exists():
        previous = json.loads(draft.read_text(encoding='utf-8'))
        if (previous.get('issue') == issue
                and previous.get('status') == 'ready_observation'
                and int(previous.get('valid_materials') or 0) > 0
                and draft_is_current_evening(previous, issue, local)):
            # Tonight's collection (automatic or a manual run from 20:00)
            # already produced a draft: automatic triggers never repeat it.
            return False, issue, 0, "current_round_already_completed"
    if remaining < 60:
        return False, issue, 0, "daily_collection_cutoff_passed"
    return True, issue, min(MAX_SECONDS, remaining), ("manual_rerun" if manual else "round_collection_or_retry")


def forum49_collect_gate(archive, now, skip_if_collected):
    """49论坛模型: automatic collections (backup cron, scheduled task) run once
    per night inside 20:20-20:50 and skip when tonight's draft exists.
    A manual dispatch (skip_if_collected=False) always collects: before
    21:00 it publishes a replaceable draft; from 21:00 (or once frozen) it is
    a test that uses an isolated copy of the archive and publishes nothing.
    Returns (collect, reason, test_only)."""
    local = now.astimezone(TZ)
    minute = (local.hour, local.minute)
    issue = current_issue(local)
    if not skip_if_collected:
        if minute >= FREEZE_START or snapshot_exists(archive, issue):
            return True, "manual_test_after_freeze", True
        return True, "manual_collection", False
    if not (COLLECTION_START <= minute < FORUM49_AUTO_COLLECT_END):
        return False, "automatic_collection_outside_window", False
    if snapshot_exists(archive, issue):
        return False, "immutable_snapshot_exists", False
    draft = Path(archive) / "issues" / issue / "draft-status.json"
    try:
        data = json.loads(draft.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        data = None
    if data and data.get("status") == "ready_observation" and draft_is_current_evening(data, issue, local):
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
    parser.add_argument("--manual", action="store_true",help="Allow an explicit rerun before the freeze deadline")
    parser.add_argument("--scheduled", action="store_true",help="Require the scheduled collection window")
    parser.add_argument("--align-gpt-trigger", action="store_true",
                        help="Wait for 20:20 only when a GPT trigger arrives up to five minutes early")
    parser.add_argument("--check-freeze-window", action="store_true",help="Check the current Beijing nightly freeze window")
    parser.add_argument("--gpt-second-round", action="store_true",help="Retired 20:30 second round: always rejected")
    parser.add_argument("--forum49-collect-gate", action="store_true",
                        help="Decide whether a 49论坛模型 collect run should collect (env F49_SKIP_IF_COLLECTED)")
    a = parser.parse_args()
    now = datetime.now(TZ)
    if a.align_gpt_trigger:
        if a.gpt_second_round:
            raise SystemExit("ERROR: the 20:30 second collection round is retired (single 20:20 collection "
                             "since 2026-10-26). No collection was started.")
        delay = gpt_trigger_wait_seconds(now,1)
        if delay is None:
            raise SystemExit("ERROR: GPT collection trigger arrived more than five minutes before 20:20. "
                             "No early collection was started.")
        if delay:
            print(f"GPT collection trigger arrived early; waiting {delay}s for 20:20 Asia/Shanghai.",
                  flush=True)
            time.sleep(delay)
        return 0
    if a.forum49_collect_gate:
        skip = os.environ.get("F49_SKIP_IF_COLLECTED", "false").lower() == "true"
        collect, reason, test = forum49_collect_gate(a.archive, now, skip)
        print(json.dumps({"collect": collect, "reason": reason, "test_only": test}, ensure_ascii=False), flush=True)
        if a.github_output:
            with open(a.github_output, "a", encoding="utf-8") as f:
                f.write(f"collect={str(collect).lower()}\nreason={reason}\ntest={str(test).lower()}\n")
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
