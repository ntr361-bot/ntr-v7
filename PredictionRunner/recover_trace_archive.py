"""One-time recovery of genuine pre-draw Live traces from original Actions artifacts.

Does not generate predictions, reconstruct a trace from later draws, or write a database.
Fails closed if the original artifact or its payload hash cannot be verified.
"""
from __future__ import annotations

import gzip
import hashlib
import io
import json
import os
from pathlib import Path
import sqlite3
import sys
import tempfile
import urllib.request
import zipfile

REPO = "ntr361-bot/ntr-v7"
SOURCES = {2026280: 11438147089, 2026281: 11506929526}
OUTPUT = Path(__file__).resolve().parents[1] / "site/data/prediction-traces/history.json.gz"


def extract_trace(issue: int, zip_bytes: bytes) -> dict:
    with zipfile.ZipFile(io.BytesIO(zip_bytes)) as archive:
        with tempfile.TemporaryDirectory(prefix="v7-live-evidence-") as directory:
            database = Path(directory) / "history.db"
            database.write_bytes(archive.read("history.db"))
            connection = sqlite3.connect(f"file:{database.as_posix()}?mode=ro", uri=True)
            try:
                rows = connection.execute(
                    """SELECT Issue, PayloadJson, PayloadHash, HistoryCutoffIssue
                       FROM PredictionTrace WHERE Issue=? AND CaptureKind='Live'
                       AND TraceSchemaVersion='trace-v1'""", (str(issue),)
                ).fetchall()
            finally:
                connection.close()
    if len(rows) != 1:
        raise ValueError(f"Expected one genuine Live trace for {issue}; got {len(rows)}")
    actual_issue, payload, stored_hash, cutoff = rows[0]
    digest = hashlib.sha256(payload.encode("utf-8")).hexdigest().upper()
    if actual_issue != str(issue) or cutoff != str(issue - 1) or digest != stored_hash.upper():
        raise ValueError(f"Invalid original Live evidence for {issue}")
    snapshot = json.loads(payload)
    if snapshot.get("issue") != str(issue) or snapshot.get("captureKind") != "Live":
        raise ValueError(f"Original Live snapshot identity mismatch: {issue}")
    return {"issue": str(issue), "payloadJson": payload, "payloadHash": stored_hash}


def recover() -> None:
    if OUTPUT.exists():
        print("Original trace archive already exists; no recovery or overwrite.")
        return
    token = os.environ.get("GITHUB_TOKEN", "")
    if not token:
        raise RuntimeError("GITHUB_TOKEN is required to read historical artifact downloads")
    entries = []
    for issue, artifact_id in sorted(SOURCES.items()):
        url = f"https://api.github.com/repos/{REPO}/actions/artifacts/{artifact_id}/zip"
        request = urllib.request.Request(url, headers={
            "Authorization": f"Bearer {token}",
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28",
        })
        with urllib.request.urlopen(request, timeout=60) as response:
            entries.append(extract_trace(issue, response.read()))
    body = json.dumps({
        "schemaVersion": "v1", "traces": entries, "outcomes": [],
    }, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    tmp = OUTPUT.with_name(OUTPUT.name + ".tmp")
    try:
        tmp.write_bytes(gzip.compress(body, mtime=0))
        if OUTPUT.exists():
            raise FileExistsError("Archive appeared concurrently; never overwrite it")
        tmp.rename(OUTPUT)
    finally:
        tmp.unlink(missing_ok=True)
    print(f"Recovered {len(entries)} original pre-draw traces, without recalculation.")


if __name__ == "__main__":
    try:
        recover()
    except Exception as exc:
        print(f"Unable to recover original archived traces: {exc}", file=sys.stderr)
        raise
