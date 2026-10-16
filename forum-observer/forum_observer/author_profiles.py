"""Read-only, opt-in author memory derived solely from verified frozen forum evidence.

No collection, rescore, changed snapshot, D1 or formal V7 writes.
Author IDs are provisional nickname keys until the forum exposes a verified user ID.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import re
from datetime import datetime
from pathlib import Path

from .core import ZODIACS, SCORING_RULE_VERSION

SCHEMA = 1


def canonical(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True)


def sha256(value):
    return hashlib.sha256(canonical(value).encode("utf-8")).hexdigest()


def load(path):
    return json.loads(Path(path).read_text(encoding="utf-8"))


def timestamp(raw):
    dt = datetime.fromisoformat(str(raw))
    if dt.tzinfo is None or dt.utcoffset() is None:
        raise ValueError("timestamp without timezone")
    return dt


def nickname(value):
    if not isinstance(value, str):
        raise ValueError("missing author nickname")
    value = " ".join(value.split())
    if not value or len(value) > 120:
        raise ValueError("invalid author nickname")
    return value


def author_id(name):
    # A nickname is NOT a verified forum account identity.
    return "nickname-" + hashlib.sha256(name.encode("utf-8")).hexdigest()[:24]


def frozen_evidence(folder):
    """Verify digest, cutoff, exact ranked votes and one counted vote per nickname."""
    folder = Path(folder)
    snap_file, obs_file = folder / "snapshot.json", folder / "observation.json"
    if not snap_file.is_file() or not obs_file.is_file():
        raise ValueError("missing frozen snapshot or contemporaneous observation")
    snapshot, obs = load(snap_file), load(obs_file)
    issue = folder.name
    if not re.fullmatch(r"20\d{5}", issue):
        raise ValueError("invalid issue")
    if snapshot.get("issue") != issue or obs.get("issue") != issue:
        raise ValueError("issue mismatch")
    digest = snapshot.get("digest")
    check = dict(snapshot)
    check.pop("digest", None)
    if not isinstance(digest, str) or sha256(check) != digest:
        raise ValueError("snapshot digest mismatch")
    if snapshot.get("scoring_rule_version") != SCORING_RULE_VERSION:
        raise ValueError("unsupported scoring rule; no retroactive reinterpretation")
    if snapshot.get("scoring_rules", {}).get("max_materials_per_author_per_issue") != 1:
        raise ValueError("no single-author evidence guarantee")
    if snapshot.get("classification") != "experimental_unverified_leaderboard":
        raise ValueError("not an experimental frozen observation")
    if obs.get("fetched_at") != snapshot.get("fetched_at"):
        raise ValueError("observation was modified or not captured with the freeze")
    fetched, frozen = timestamp(snapshot["fetched_at"]), timestamp(snapshot["frozen_at"])
    if fetched > frozen:
        raise ValueError("freeze is earlier than evidence")
    rankings = snapshot.get("ranking", [])
    if len(rankings) != 12 or {r.get("zodiac") for r in rankings} != set(ZODIACS):
        raise ValueError("incomplete 12 zodiac ranking")
    if {r.get("rank") for r in rankings} != set(range(1, 13)):
        raise ValueError("invalid ranking positions")
    predictions = obs.get("parsed_predictions", [])
    if not isinstance(predictions, list):
        raise ValueError("missing parsed evidence")
    counted = [p for p in predictions if p.get("counted_for_ranking") is True]
    if not counted or len(counted) != snapshot.get("valid_materials"):
        raise ValueError("counted material count differs from frozen snapshot")
    names = set()
    totals = {z: 0.0 for z in ZODIACS}
    result = []
    counted_modes = {"include": 0, "exclude": 0}
    for row in counted:
        if row.get("issue") != issue:
            raise ValueError("prediction issue mismatch")
        name = nickname(row.get("author"))
        if name in names:
            # Legacy 287 has this anomaly: do not silently pick one or alter ranking.
            raise ValueError("multiple counted materials from same author")
        names.add(name)
        picks, votes = row.get("picks"), row.get("vote_zodiacs")
        mode = row.get("mode")
        if mode not in counted_modes:
            raise ValueError("unverified mode")
        if (not isinstance(picks, list) or not isinstance(votes, list) or
                len(set(picks)) != len(picks) or len(set(votes)) != len(votes) or
                any(z not in ZODIACS for z in picks + votes)):
            raise ValueError("invalid zodiac evidence")
        expected = (picks if mode == "include" else [z for z in ZODIACS if z not in picks])
        if (votes != expected or (mode == "include" and len(picks) < 6) or
                (mode == "exclude" and not (1 <= len(picks) <= 6))):
            raise ValueError("vote does not match frozen play")
        weight = row.get("weight_per_zodiac")
        if not isinstance(weight, (float, int)) or not math.isfinite(weight) or abs(weight - 1 / len(votes)) > 0.000001:
            raise ValueError("not equal per-author vote weight")
        for z in votes:
            totals[z] += weight
        counted_modes[mode] += 1
        url = row.get("source_url")
        if not isinstance(url, str) or not url.startswith(("https://", "http://")):
            raise ValueError("missing verified source URL")
        result.append({
            "id": author_id(name), "nickname": name,
            "identity_status": "nickname_only_unverified_account",
            "play": str(row.get("play") or ""), "mode": mode,
            "picks": picks, "vote_zodiacs": votes,
            "original_text": str(row.get("excerpt") or ""),
            "source_url": url, "published_at": row.get("published_at"),
            "random_hit_baseline": round(len(votes) / 12, 6),
        })
    for r in rankings:
        if abs(totals[r["zodiac"]] - float(r["score"])) > 0.00001:
            raise ValueError("frozen zodiac scores differ from counted evidence")
    material_counts = snapshot.get("material_counts", {})
    if material_counts and (material_counts.get("recommend") != counted_modes["include"] or
                            material_counts.get("kill_zodiac") != counted_modes["exclude"]):
        raise ValueError("frozen material counts differ from evidence")
    return {
        "schema_version": SCHEMA, "issue": issue,
        "snapshot_digest": digest,
        "observation_digest": sha256(obs),
        "fetched_at": snapshot["fetched_at"], "frozen_at": snapshot["frozen_at"],
        "rule_version": snapshot["scoring_rule_version"],
        "quality_status": snapshot.get("quality_status", "legacy_unclassified"),
        "materials": sorted(result, key=lambda x: x["id"]),
    }


def write_once(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    data = dict(value)
    data["record_digest"] = sha256(value)
    content = json.dumps(data, ensure_ascii=False, indent=2) + "\n"
    try:
        with path.open("x", encoding="utf-8") as handle:
            handle.write(content)
        return True
    except FileExistsError:
        existing = load(path)
        signature = existing.pop("record_digest", None)
        if signature != sha256(existing):
            raise ValueError("author record was altered: " + path.name)
        # An immutable record never gets replaced by a newer run.
        if existing.get("snapshot_digest") != value["snapshot_digest"]:
            raise ValueError("snapshot changed after author record: " + path.name)
        return False


def valid_settlement(folder, record):
    path = Path(folder) / "settlement.json"
    if not path.is_file():
        return None
    settle = load(path)
    if (settle.get("issue") != record["issue"] or
            settle.get("snapshot_digest") != record["snapshot_digest"] or
            settle.get("snapshot_at") != record["fetched_at"] or
            settle.get("actual") not in ZODIACS):
        return None
    try:
        if timestamp(settle["drawn_at"]) <= timestamp(record["frozen_at"]):
            return None
    except (KeyError, ValueError, TypeError):
        return None
    return settle["actual"]


def build(archive):
    """Append per-issue author evidence, rebuild only a separate derived index."""
    archive = Path(archive)
    destination = archive / "author-profiles"
    entries = []
    skipped = []
    created = 0
    for folder in sorted((archive / "issues").glob("20?????")):
        if not (folder / "snapshot.json").is_file():
            continue
        try:
            candidate = frozen_evidence(folder)
            record_path = destination / "records" / (folder.name + ".json")
            if write_once(record_path, candidate):
                created += 1
            record = load(record_path)
            actual = valid_settlement(folder, record)
            for item in record["materials"]:
                entries.append((record, item, actual))
        except (ValueError, TypeError, KeyError, OSError, json.JSONDecodeError) as error:
            skipped.append({"issue": folder.name, "reason": str(error)})
    profiles = {}
    for record, item, actual in entries:
        profile = profiles.setdefault(item["id"], {
            "id": item["id"], "nickname": item["nickname"],
            "identity_status": item["identity_status"], "records": [],
        })
        if profile["nickname"] != item["nickname"]:
            raise ValueError("nickname identity hash collision")
        profile["records"].append({
            "issue": record["issue"], "frozen_at": record["frozen_at"],
            "quality_status": record["quality_status"], "play": item["play"],
            "mode": item["mode"], "picks": item["picks"],
            "vote_zodiacs": item["vote_zodiacs"],
            "random_hit_baseline": item["random_hit_baseline"],
            "original_text": item["original_text"], "published_at": item["published_at"],
            "source_url": item["source_url"], "snapshot_digest": record["snapshot_digest"],
            "actual": actual, "outcome": ("pending" if actual is None else
                                        "hit" if actual in item["vote_zodiacs"] else "miss"),
        })
    for profile in profiles.values():
        profile["records"].sort(key=lambda row: row["issue"])
        profile["first_issue"] = profile["records"][0]["issue"]
        profile["last_issue"] = profile["records"][-1]["issue"]
        profile["observed_issue_count"] = len(profile["records"])
        profile["settled_issue_count"] = sum(r["outcome"] != "pending" for r in profile["records"])
    index = {
        "schema_version": SCHEMA,
        "classification": "read_only_author_memory_experimental",
        "used_for_predictions": False, "author_weighting_enabled": False,
        "identity_note": "Nickname-only provisional ID; a renamed account is not automatically merged.",
        "included_issues": sorted({e[0]["issue"] for e in entries}),
        "skipped_issues": skipped,
        "author_count": len(profiles),
        "authors": sorted(profiles.values(), key=lambda x: x["id"]),
    }
    destination.mkdir(parents=True, exist_ok=True)
    # Derived index may be rebuilt; immutable per-issue records may not.
    target = destination / "index.json"
    temp = destination / "index.json.tmp"
    temp.write_text(json.dumps(index, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    temp.replace(target)
    return {"created_issue_records": created, "profiles": len(profiles),
            "included_issues": index["included_issues"], "skipped_issues": skipped}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--archive", default="archive")
    args = parser.parse_args()
    print(json.dumps(build(args.archive), ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
