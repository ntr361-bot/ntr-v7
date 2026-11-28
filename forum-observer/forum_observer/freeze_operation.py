"""Shared manual and scheduled freeze transaction for the two forum archives."""
import argparse
import hashlib
import json
import os
import subprocess
import time
from datetime import datetime, timedelta
from pathlib import Path
from urllib.request import Request, urlopen

from .core import SCORING_RULE_VERSION, ZODIACS, verify_scored_result
from .cycle import atomic_bytes, sync
from .runtime import GitHub
from .schedule import FREEZE_END, TZ, current_issue, snapshot_exists

ARCHIVES = {"forum-observer": "archive", "forum49": "forum49"}
HISTORY = Path("../site/data/history.json")
DRAW_SOURCE = "https://api.00853lhc.com/api/HistoryOpenInfo"
RAW = "https://raw.githubusercontent.com/ntr361-bot/ntr-v7/main/"
LEDGER = "https://smart-ledger-cf.5rmwf2d5ff.workers.dev"
EXCLUDED = "2026327"


class FreezeError(ValueError):
    pass


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


def sha(payload):
    return hashlib.sha256(payload).hexdigest()


def timestamp(value):
    dt = datetime.fromisoformat(value)
    if dt.utcoffset() is None:
        raise FreezeError("timestamp_requires_timezone")
    return dt.astimezone(TZ)


def check_window(issue, now, manual):
    if current_issue(now) != issue or issue == EXCLUDED:
        raise FreezeError("wrong_or_excluded_issue")
    now = now.astimezone(TZ)
    minute = (now.hour, now.minute)
    if minute < (21, 0) or minute >= ((21, 31) if manual else FREEZE_END):
        raise FreezeError("outside_safe_freeze_window")


def load_draft(folder, issue, now):
    return validate_draft(folder, issue, now, read_json(folder / "draft-status.json"),
                          (folder / "report.md").read_bytes())


def validate_draft(folder, issue, now, status, report):
    fetched = timestamp(status["fetched_at"])
    draw = timestamp(status["draw_at"])
    if (status.get("issue") != issue or fetched.date() != now.date()
            or fetched >= now.replace(hour=21, minute=0, second=0, microsecond=0)
            or draw.date() != now.date() or (draw.hour, draw.minute) != (21, 32)
            or status.get("pre_draw_verified") is not True):
        raise FreezeError("draft_issue_or_time_mismatch")
    count = status["valid_materials"]
    if (status.get("scoring_rule_version") != SCORING_RULE_VERSION or type(count) is not int
            or count < 1 or not (status["status"] == "ready_observation"
                or (status["status"] == "incomplete_observation" and count < 25))):
        raise FreezeError("draft_not_eligible")
    verify_scored_result(status, min_materials=1 if count < 25 else 25)
    text = report.decode("utf-8")
    expected = (f"# 论坛 {issue} 采集成绩单", f"抓取时间：{status['fetched_at']}",
                f"**有效资料：{count}份**（经期号核对、去重且实际计分）",
                f"观察Top6：{'、'.join(status['top6'])}")
    if not all(line in text.splitlines() for line in expected) or '本期冻结状态：' in text:
        raise FreezeError("draft_report_mismatch")
    run_id = str(status["collection_run_id"])
    if not run_id.isdecimal() or status["collection_round"] not in (1, 2):
        raise FreezeError("missing_collection_provenance")
    name = f"round{status['collection_round']}-{fetched:%Y%m%dT%H%M%S%f}-{run_id}"
    round_dir = folder / "rounds" / name
    round_summary = read_json(round_dir / "summary.json")
    for key in ("issue", "fetched_at", "valid_materials", "top6", "status"):
        if round_summary.get(key) != status[key]:
            raise FreezeError("archived_round_mismatch")
    if (round_dir / "report.md").read_bytes() != report:
        raise FreezeError("archived_report_mismatch")
    return status, report


def restore_draft(folder, issue, now):
    """Recover a pointer from its newest verified, committed collection copy."""
    try:
        return load_draft(folder, issue, now)
    except (OSError, ValueError, KeyError, TypeError) as original:
        for candidate in sorted((folder / "rounds").glob("*/draft-status.json"),
                                key=lambda path: path.parent.name.split("-", 1)[-1], reverse=True):
            try:
                content = candidate.read_bytes()
                status = json.loads(content)
                report = (candidate.parent / "report.md").read_bytes()
                run_id = status["collection_run_id"]
                attempt = status["collection_run_attempt"]
                if not str(run_id).isdecimal() or type(attempt) is not int:
                    continue
                receipt = read_json(folder / "collection-runs" / f"{run_id}-{attempt}.json")
                if (receipt["issue"] != issue or receipt["round"] != candidate.parent.name
                        or receipt["run_id"] != run_id or receipt["run_attempt"] != attempt
                        or receipt["draft_sha256"] != sha(content)
                        or receipt["report_sha256"] != sha(report)):
                    continue
                validate_draft(folder, issue, now, status, report)
            except (OSError, ValueError, KeyError, TypeError):
                continue
            atomic_bytes(folder / "report.md", report)
            atomic_bytes(folder / "draft-status.json", content)
            return status, report
        raise original


def live_draw_state(now=None):
    """Read the same Macau results source used by the ledger's /api/results."""
    now = now or datetime.now(TZ)
    request = Request(f"{DRAW_SOURCE}?issueNum={now.date()}&lotteryId=2032",
                      headers={"User-Agent": "Mozilla/5.0", "Cache-Control": "no-cache"})
    with urlopen(request, timeout=8) as response:
        raw = response.read(1_000_001)
    if len(raw) > 1_000_000:
        raise FreezeError("live_results_too_large")
    payload = json.loads(raw)
    if payload.get("code") != 0 or not isinstance(payload.get("data"), list):
        raise FreezeError("live_results_unavailable")
    records = []
    for row in payload["data"]:
        if row.get("lotteryId") != 2032:
            continue
        issue = str(row.get("issue"))
        if issue == current_issue(now):
            # A current-period result or placeholder is sufficient to stop
            # normal freeze; never filter it out on malformed result fields.
            records.append({"issue": issue})
            continue
        try:
            when = datetime.fromisoformat(row["openTime"])
            if when.utcoffset() is None:
                when = when.replace(tzinfo=TZ)
            numbers = [int(value) for value in row["openCode"].split(",")]
            if (current_issue(when) == issue and when <= now
                    and len(set(numbers)) == len(numbers) == 7
                    and all(1 <= n <= 49 for n in numbers)):
                records.append({"issue": issue})
        except (KeyError, TypeError, ValueError):
            continue
    return records


def guard(issue, manual, draw_at, clock, live):
    now = clock()
    check_window(issue, now, manual)
    records = read_json(HISTORY)["records"]
    if not isinstance(records, list) or not records:
        raise FreezeError("draw_history_unavailable")
    if any(str(row["issue"]) == issue for row in records):
        raise FreezeError("draw_already_recorded")
    live_records = live()
    now = clock()
    check_window(issue, now, manual)
    if now >= draw_at or any(str(row["issue"]) == issue for row in live_records):
        raise FreezeError("draw_already_recorded_or_started")
    previous_issue = current_issue(now - timedelta(days=1))
    if not any(str(row["issue"]) == previous_issue for row in live_records):
        raise FreezeError("live_results_not_current")
    return now


def read_url(url):
    req = Request(url, headers={"Cache-Control": "no-cache",
                  "User-Agent": "ntr-v7-forum-freeze/1"})
    with urlopen(req, timeout=8) as response:
        value = response.read(2_000_001)
    if len(value) > 2_000_000:
        raise FreezeError("ledger_source_too_large")
    return value


def read_remote(path):
    return read_url(RAW + path)


def verify_ledger_freeze(model, issue, snapshot, read=read_url):
    report = json.loads(read(f"{LEDGER}/api/settlement?model={model}&issue={issue}&phase=frozen"))
    if report != {key: snapshot[key] for key in ("issue", "digest", "frozen_at", "top6")}:
        raise FreezeError("production_ledger_freeze_mismatch")


def verify_feed(model, issue, snap, report, read=read_remote):
    """Verify archive, generated projection and deployed ledger read-back."""
    prefix = f"site/data/{model}"
    saved = json.loads(read(f"forum-observer/{ARCHIVES[model]}/issues/{issue}/snapshot.json"))
    latest = json.loads(read(f"{prefix}/latest.json"))
    item = json.loads(read(f"{prefix}/issues/{issue}.json"))
    if (saved != snap or item.get("digest") != snap["digest"]
            or item.get("issue") != issue or item.get("ranking") != snap["ranking"]
            or item.get("frozen_at") != snap["frozen_at"]
            or latest.get("model") != model or str(latest.get("issue") or "") < issue
            or (latest.get("issue") == issue and
                (latest.get("frozen") is not True or latest.get("snapshot_digest") != snap["digest"]))):
        raise FreezeError("ledger_feed_mismatch")
    if read(f"forum-observer/{ARCHIVES[model]}/issues/{issue}/report.md") != report:
        raise FreezeError("ledger_report_mismatch")
    if read is read_remote:
        verify_ledger_freeze(model, issue, snap)


def check_snapshot(archive, issue, folder):
    if not snapshot_exists(archive, issue):
        raise FreezeError("snapshot_missing")
    snap = read_json(folder / "snapshot.json")
    fetched, frozen = timestamp(snap["fetched_at"]), timestamp(snap["frozen_at"])
    ranking = snap["ranking"]
    order = [row["zodiac"] for row in ranking]
    if (len(order) != len(ZODIACS) or set(order) != set(ZODIACS)
            or any(row["rank"] != i for i, row in enumerate(ranking, 1))
            or any(snap.get(f"top{size}") != order[:size] for size in (1, 3, 6))):
        raise FreezeError("snapshot_ranking_mismatch")
    if (current_issue(fetched) != issue or current_issue(frozen) != issue
            or not (fetched < frozen < frozen.replace(hour=21, minute=32, second=0, microsecond=0))
            or frozen.hour != 21):
        raise FreezeError("snapshot_time_mismatch")
    report = (folder / "report.md").read_bytes()
    if (not report.startswith(f"# 论坛 {issue} ".encode())
            or f"不可变摘要：{snap['digest']}".encode() not in report
            or snap["frozen_at"].encode() not in report):
        raise FreezeError("frozen_report_mismatch")
    return snap, report


def git(*args):
    return subprocess.run(["git", *args], check=True, text=True, stdout=subprocess.PIPE,
                          timeout=20).stdout.strip()


def create_snapshot(model, archive, folder, issue, manual, journal, clock, live):
    check_window(issue, clock(), manual)
    status, report = restore_draft(folder, issue, clock())
    provenance = {"source": "manual" if manual else "automatic",
                  "run_id": journal["run_id"], "run_attempt": journal["run_attempt"],
                  "report_generated_at": status["fetched_at"],
                  "report_sha256": sha(report)}
    journal.update(provenance)

    def before_write():
        return guard(issue, manual, timestamp(status["draw_at"]), clock, live)

    sync("unused-freeze-output", archive, HISTORY, Path("../site/data") / model,
         finalize=True, model_id=model, allow_settlement=False, before_snapshot=before_write, freeze_receipt=provenance)
    snap, _ = check_snapshot(archive, issue, folder)
    git("add", "--", str(archive), f"../site/data/{model}")
    git("commit", "-m", f"chore(forum): freeze {model} {issue} run {journal['run_id']}")
    before_write()
    git("push", "origin", "HEAD:refs/heads/main")
    return snap


def operate(model, phase, issue, journal, *, clock=lambda: datetime.now(TZ),
            live=live_draw_state, read=read_remote):
    archive = Path(ARCHIVES[model])
    folder = archive / "issues" / issue
    journal["snapshot_path"] = str(Path("forum-observer") / folder / "snapshot.json")
    journal["ledger_sync"] = "pending"
    manual = phase == "manual"
    for attempt in range(2):
        journal["retry_count"] = attempt
        git("fetch", "origin", "main")
        # Fresh main is the transaction's base. Never rebase or force a
        # snapshot commit, so a concurrent writer cannot be overwritten.
        git("reset", "--hard", "origin/main")
        try:
            if not (folder / "snapshot.json").exists():
                create_snapshot(model, archive, folder, issue, manual, journal, clock, live)
            break
        except (OSError, ValueError, TypeError, KeyError, subprocess.SubprocessError) as exc:
            journal["failures"].append(str(exc))
            if attempt:
                raise
            check_window(issue, clock(), manual)
            time.sleep(3)

    snap, report = check_snapshot(archive, issue, folder)
    provenance = snap.get("freeze_provenance") or {}
    journal.update(source=provenance.get("source", "legacy"),
                   frozen_by_run_id=provenance.get("run_id"),
                   report_generated_at=snap["fetched_at"],
                   report_sha256=provenance.get("report_sha256"))
    journal.update(frozen_at=snap["frozen_at"], digest=snap["digest"],
                   frozen_report_sha256=sha(report))
    if phase == "verify":
        journal["ledger_sync"] = "ledger_readback_failed"
        # GitHub archive and public feed are normally one commit. Repair a
        # stale generated feed independently, keeping the frozen archive intact.
        def local_read(path):
            return (Path("..") / path).read_bytes()

        try:
            verify_feed(model, issue, snap, report, local_read)
        except (OSError, ValueError, KeyError):
            if read_json(archive / "latest.json").get("issue") != issue:
                raise FreezeError("ledger_feed_repair_would_replace_newer_issue")
            public = Path("../site/data") / model
            sync("unused-freeze-output", archive, HISTORY, public,
                 defer_freeze=True, model_id=model, allow_settlement=False)
            verify_feed(model, issue, snap, report, local_read)
            git("add", "--", str(public))
            git("commit", "-m", f"chore(forum): repair {model} ledger feed {issue}")
            git("push", "origin", "HEAD:refs/heads/main")
            journal["ledger_feed_repaired"] = True
    for attempt in range(3):
        journal["ledger_retry_count"] = attempt
        try:
            verify_feed(model, issue, snap, report, read)
            journal["ledger_sync"] = "verified_ledger_source"
            if read is read_remote:
                journal["production_ledger_verified"] = True
            return
        except (ValueError, KeyError, OSError) as exc:
            journal["failures"].append(str(exc))
            journal["ledger_sync"] = "ledger_readback_failed"
            if attempt == 2:
                raise
            time.sleep(8)


def alert(github, journal):
    issue, model = journal["issue"], journal["model"]
    title = f"论坛冻结复核异常 · {model} · {issue}"
    # One notification per model, issue and distinct operational impact.
    impact = journal["ledger_sync"] if journal.get("digest") else "snapshot_unverified"
    marker = f"<!-- freeze-impact:{impact} -->"
    body = (f"{marker}\n@ntr361-bot\n\n{issue} {model} · {impact}\n\n"
            f"运行 {journal['run_id']} · {journal['failures'][-1]}")
    issues = github.request("issues?state=all&per_page=100")
    existing = next((item for item in issues if item.get("title") == title), None)
    if not existing:
        github.request("issues", "POST", {"title": title, "body": body})
    elif marker not in (existing.get("body") or ""):
        comments = github.request(f"issues/{existing['number']}/comments?per_page=100")
        if not any(marker in (comment.get("body") or "") for comment in comments):
            github.request(f"issues/{existing['number']}/comments", "POST", {"body": body})


def requested_freeze_issue(created_at, phase, scheduled):
    scheduled_minute = 5 if phase == "verify" else 0
    if scheduled and (created_at.hour, created_at.minute) < (21, scheduled_minute):
        created_at -= timedelta(days=1)
    return current_issue(created_at)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("model", choices=ARCHIVES)
    parser.add_argument("phase", choices=("manual", "auto", "verify"))
    args = parser.parse_args()
    run_id = os.environ["GITHUB_RUN_ID"]
    attempt = int(os.environ["GITHUB_RUN_ATTEMPT"])
    github = GitHub()
    run = github.request(f"actions/runs/{run_id}")
    issue = requested_freeze_issue(timestamp(run["created_at"]), args.phase,
                                   run.get("event") == "schedule")
    requested = os.environ.get("REQUESTED_ISSUE")
    if requested and requested != issue:
        raise FreezeError("freeze_request_issue_mismatch")
    journal = {"issue": issue, "model": args.model, "phase": args.phase,
               "run_id": run_id, "run_attempt": attempt, "failures": []}
    try:
        operate(args.model, args.phase, issue, journal)
    except Exception as exc:
        if not journal["failures"] or journal["failures"][-1] != str(exc):
            journal["failures"].append(str(exc))
        if args.phase != "manual" and issue != EXCLUDED:
            alert(github, journal)
        raise
    finally:
        path = f"forum-observer/status/freeze/{issue}/{args.model}/{run_id}-{attempt}.json"
        print(json.dumps(journal, ensure_ascii=False))
        github.write(path, journal)


if __name__ == "__main__":
    main()
