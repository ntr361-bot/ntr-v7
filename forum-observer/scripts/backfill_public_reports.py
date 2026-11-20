#!/usr/bin/env python3
"""One-time copy of existing archive report.md into site/data public feed.

Run from repo root:
    python forum-observer/scripts/backfill_public_reports.py

After this runs, the next freeze/settle will keep new reports in sync automatically.
"""
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent.parent
PAIRS = [
    (ROOT / "forum-observer/archive/issues", ROOT / "site/data/forum-observer/issues"),
    (ROOT / "forum-observer/forum49/issues", ROOT / "site/data/forum49/issues"),
]

copied = 0
for src_root, dest_root in PAIRS:
    if not src_root.exists():
        continue
    for issue_dir in sorted(src_root.glob("20?????")):
        report = issue_dir / "report.md"
        if not report.exists():
            continue
        dest = dest_root / issue_dir.name / "report.md"
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(report, dest)
        copied += 1
        print(f"copied {report.relative_to(ROOT)} -> {dest.relative_to(ROOT)}")

print(f"total copied: {copied}")
