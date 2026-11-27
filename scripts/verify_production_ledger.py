"""Read-only verification of the current GitHub-backed production ledger."""
import json
import time
from urllib.request import Request, urlopen
from pipeline_guard import ROOT, read, validate

BASE = "https://smart-ledger-cf.5rmwf2d5ff.workers.dev/api/v7-sync/desktop/"


def remote(resource):
    request = Request(BASE + resource, headers={"Accept": "application/json",
                                                "Cache-Control": "no-cache"})
    with urlopen(request, timeout=30) as response:
        return json.load(response)


def verify(root=ROOT, fetch=remote):
    issue = validate(root)
    manifest = read(root / "daily-records" / "manifest.json")
    expected = read(root / "daily-records" / f"{issue}.json")
    history = read(root / "history.json")
    if fetch("manifest") != manifest:
        raise ValueError("Production manifest differs from committed manifest")
    if fetch(f"prediction?file={issue}.json") != expected:
        raise ValueError("Production latest forecast differs from committed snapshot")
    if fetch("history") != history:
        raise ValueError("Production draw history differs from committed history")
    expected_runtime = read(root / "runtime-state.json")
    if fetch("runtime-state") != expected_runtime:
        raise ValueError("Production runtime state differs from committed runtime")
    return issue


if __name__ == "__main__":
    for attempt in range(4):
        try:
            issue = verify()
            print(f"Production ledger read-back verified: {issue}; no D1 writes or model execution")
            break
        except (OSError, ValueError, KeyError, TypeError) as error:
            print(f"Read-back attempt {attempt + 1}/4: {type(error).__name__}: {error}")
            if attempt == 3:
                raise
            time.sleep((5, 10, 20)[attempt])
