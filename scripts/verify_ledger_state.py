"""Read the deployed ledger and compare its GitHub-backed formal forecast."""
import json
import time
from pathlib import Path

from settlement import LEDGER, read_live


def verify(expected_manifest, expected_forecast, read=read_live):
    issue = expected_manifest["latest_issue"]
    filename = f"{issue}.json"
    base = LEDGER + "/api/v7-sync/desktop"
    manifest = read(base + "/manifest")
    if (manifest.get("latest_issue") != issue or filename not in manifest["records"]
            or read(base + f"/prediction?file={filename}") != expected_forecast):
        raise ValueError(f"production ledger does not serve the committed forecast for {issue}")


def main():
    root = Path("site/data/daily-records")
    manifest = json.loads((root / "manifest.json").read_bytes())
    forecast = json.loads((root / f"{manifest['latest_issue']}.json").read_bytes())
    for attempt in range(3):
        try:
            verify(manifest, forecast)
            print(f"Production ledger verified: {manifest['latest_issue']}", flush=True)
            return
        except (OSError, ValueError, KeyError) as error:
            print(f"Read-back #{attempt + 1}: {type(error).__name__}: {error}", flush=True)
            if attempt == 2:
                raise
            time.sleep(4 * (attempt + 1))


if __name__ == "__main__":
    main()
