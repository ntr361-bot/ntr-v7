"""Idempotent schedule gate for the independent forum observer only.

GitHub cron delivery is best-effort. Backups collect only when the current
local-day issue lacks an immutable observation snapshot; no old issue is
recreated or re-frozen by this gate.
"""
import argparse
import hashlib
import json
import os
from datetime import datetime
from pathlib import Path
from zoneinfo import ZoneInfo

TZ = ZoneInfo("Asia/Shanghai")
CUTOFF_HOUR, CUTOFF_MINUTE = 20, 25
MAX_SECONDS = 1800
PUBLISH_RESERVE_SECONDS = 90


def current_issue(now):
    local = now.astimezone(TZ)
    return f"{local.year}{local.timetuple().tm_yday:03d}"


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


def decide(now, archive):
    """Return (collect, issue, seconds, reason). Fail closed after cutoff."""
    local = now.astimezone(TZ)
    issue = current_issue(now)
    if snapshot_exists(archive, issue):
        return False, issue, 0, "immutable_snapshot_exists"
    cutoff = local.replace(hour=CUTOFF_HOUR, minute=CUTOFF_MINUTE,
                           second=0, microsecond=0)
    remaining = int((cutoff - local).total_seconds()) - PUBLISH_RESERVE_SECONDS
    if remaining < 60:
        return False, issue, 0, "daily_collection_cutoff_passed"
    return True, issue, min(MAX_SECONDS, remaining), "missing_snapshot_retry"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--archive", default="archive")
    parser.add_argument("--github-output", default=os.environ.get("GITHUB_OUTPUT"))
    parser.add_argument("--require-snapshot", action="store_true")
    a = parser.parse_args()
    now = datetime.now(TZ)
    issue = current_issue(now)
    if a.require_snapshot:
        if not snapshot_exists(a.archive, issue):
            raise SystemExit(f"ERROR: {issue} has no immutable forum snapshot at daily audit")
        print(f"PASS: {issue} immutable forum snapshot exists")
        return 0
    collect, issue, seconds, reason = decide(now, a.archive)
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
