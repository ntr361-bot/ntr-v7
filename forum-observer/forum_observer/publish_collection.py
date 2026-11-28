"""Commit one verified collection against fresh main, retrying non-fast-forwards."""
import argparse
import hashlib
import json
import os
import re
import subprocess
import time
from pathlib import Path

from .cycle import sync, write_once
from .freeze_operation import ARCHIVES, git, load_draft, timestamp


def verify_publication(folder, issue, run_id, attempt, read):
    receipt_path = folder / "collection-runs" / f"{run_id}-{attempt}.json"
    receipt = json.loads(read(receipt_path))
    if (receipt["issue"] != issue or receipt["run_id"] != run_id
            or receipt["run_attempt"] != attempt or receipt["valid_authors"] <= 0
            or receipt["valid_authors"] != receipt["valid_materials"]
            or receipt["draft_published"] is not True or receipt["report_published"] is not True):
        raise ValueError("invalid collection receipt")
    name = receipt["round"]
    if not re.fullmatch(r"round[12]-[0-9]{8}T[0-9]{12}-[0-9]+", name):
        raise ValueError("invalid collection round")
    for path, key in (("draft-status.json", "draft_sha256"), ("report.md", "report_sha256")):
        if hashlib.sha256(read(folder / "rounds" / name / path)).hexdigest() != receipt[key]:
            raise ValueError("collection checksum mismatch: " + path)
    return receipt


def publish(model, out, issue):
    archive = Path(ARCHIVES[model])
    public = Path("../site/data") / model
    folder = archive / "issues" / issue
    run_id = os.environ["GITHUB_RUN_ID"]
    attempt = int(os.environ["GITHUB_RUN_ATTEMPT"])
    status = json.loads((out / "status.json").read_bytes())
    if status.get("issue") != issue or (out / "collection-error.json").exists():
        raise ValueError("collector failed or returned a different issue")
    pathspec = [str(archive), str(public)]
    source = git("rev-parse", "HEAD")
    for retry in range(5):
        git("fetch", "origin", "main")
        if git("diff", "--name-only", source, "origin/main", "--", "forum_observer", "collector-config.json"):
            raise ValueError("collector revision changed before publication")
        git("reset", "--hard", "origin/main")
        if not (folder / "collection-runs" / f"{run_id}-{attempt}.json").exists():
            sync(out, archive, "../site/data/history.json", public, defer_freeze=True,
                 model_id=model, allow_settlement=False)
            receipt = verify_publication(folder, issue, run_id, attempt, Path.read_bytes)
            draft, _ = load_draft(folder, issue, timestamp(status["fetched_at"]))
            if str(draft["collection_run_id"]) != run_id or draft["collection_run_attempt"] != attempt:
                raise ValueError("published pointer does not identify this collection")
            if model == 'forum49':
                name = f"run-{run_id}-{attempt}.json"
                record = json.loads((out / "run-evidence" / "issues" / issue / name).read_bytes())
                write_once(folder / name, record)
            git("add", "--", *pathspec)
            git("commit", "-m", f"chore(forum): archive {model} {issue}, run {run_id}-{attempt}")
            try:
                git("push", "origin", "HEAD:refs/heads/main")
            except subprocess.SubprocessError:
                if retry == 4:
                    raise
                time.sleep(2 * (retry + 1))
                continue
        git("fetch", "origin", "main")

        def read_committed(path):
            return subprocess.check_output(
                ["git", "show", "origin/main:forum-observer/" + path.as_posix()], timeout=25)

        receipt = verify_publication(folder, issue, run_id, attempt, read_committed)
        print(f"Committed {model} {issue}: {receipt['valid_materials']} materials, "
              f"{receipt['valid_authors']} authors, run {run_id}-{attempt}", flush=True)
        return


def verify_observation(out, issue):
    status = json.loads((out / "status.json").read_bytes())
    if (status.get("issue") != issue or status.get("valid_materials", 0) <= 0
            or not (out / issue / "report.md").stat().st_size):
        raise ValueError("isolated collection has no verified evidence for the requested issue")
    print(f"Verified isolated observation for {issue}", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("model", choices=ARCHIVES)
    parser.add_argument("out", type=Path)
    parser.add_argument("issue")
    parser.add_argument("--observation", action="store_true")
    args = parser.parse_args()
    if args.observation:
        verify_observation(args.out, args.issue)
    else:
        publish(args.model, args.out, args.issue)
