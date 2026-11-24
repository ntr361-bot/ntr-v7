"""Read-only, opt-in author memory derived solely from verified frozen forum evidence.

No collection, rescore, changed snapshot, D1 or formal V7 writes.
Author IDs are provisional nickname keys until the forum exposes a verified user ID.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import re
from collections import defaultdict
from tempfile import NamedTemporaryFile
from datetime import datetime
from pathlib import Path

from .core import ZODIACS

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
    # The frozen rule version is evidence, not an instruction to run today's rules.
    # Historical profiles must survive future, unrelated scoring-rule revisions.
    rules = snapshot.get("scoring_rules")
    if not isinstance(snapshot.get("scoring_rule_version"), str) or not isinstance(rules, dict):
        raise ValueError("missing frozen scoring rule")
    if rules.get("max_materials_per_author_per_issue") != 1 or rules.get("weight_per_material") != 1:
        raise ValueError("no verified single-author equal-weight rule")
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
    try:
        write_json(path, data, immutable=True)
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



def read_sealed_record(path, folder):
    """Read a prior immutable author record without trusting a mutable later observation."""
    record = load(path)
    digest = record.get("record_digest")
    payload = dict(record)
    payload.pop("record_digest", None)
    if digest != sha256(payload):
        raise ValueError("author record was altered: " + Path(path).name)
    snapshot = load(Path(folder) / "snapshot.json")
    snap_payload = dict(snapshot)
    snap_payload.pop("digest", None)
    if snapshot.get("digest") != sha256(snap_payload):
        raise ValueError("snapshot digest mismatch")
    if (record.get("issue") != Path(folder).name or
            record.get("snapshot_digest") != snapshot.get("digest") or
            record.get("fetched_at") != snapshot.get("fetched_at")):
        raise ValueError("sealed author record no longer matches immutable snapshot")
    return record


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


def write_json(path, value, *, immutable=False, compact=False):
    """Publish a complete file even if a run is interrupted during serialization."""
    with NamedTemporaryFile(mode="w", encoding="utf-8", dir=path.parent,
                            delete=False) as handle:
        temporary = Path(handle.name)
        try:
            json.dump(value, handle, ensure_ascii=False, indent=None if compact else 2)
            handle.write("\n")
            handle.close()
            if immutable:
                os.link(temporary, path)
            else:
                temporary.replace(path)
        finally:
            temporary.unlink(missing_ok=True)


def file_digest(path):
    try:
        return hashlib.sha256(path.read_bytes()).hexdigest()
    except FileNotFoundError:
        return None


def inputs(folder, record_path):
    return [
        file_digest(folder / "snapshot.json"),
        file_digest(record_path) if record_path.is_file() else
        file_digest(folder / "observation.json"),
        record_path.is_file(),
        file_digest(folder / "settlement.json"),
    ]


def profile_record(record, item, actual):
    return {
        "issue": record["issue"], "frozen_at": record["frozen_at"],
        "quality_status": record["quality_status"], "play": item["play"],
        "mode": item["mode"], "picks": item["picks"],
        "vote_zodiacs": item["vote_zodiacs"],
        "random_hit_baseline": item["random_hit_baseline"],
        "original_text": item["original_text"], "published_at": item["published_at"],
        "source_url": item["source_url"], "snapshot_digest": record["snapshot_digest"],
        "actual": actual, "outcome": ("pending" if actual is None else
                                    "hit" if actual in item["vote_zodiacs"] else "miss"),
    }


def rate(hits, count):
    return round(hits / count, 6) if count else None


def statistics(profile):
    rows = [r for r in profile["records"] if r["outcome"] != "pending"]
    hits = [r["outcome"] == "hit" for r in rows]
    recent = {
        str(size): {"count": len(hits[-size:]),
                    "hit_rate": rate(sum(hits[-size:]), len(hits[-size:]))}
        for size in (10, 20, 50)
    }
    # Use full rolling ten-participation windows to avoid mixing sample sizes.
    rolling = [sum(hits[i - 10:i]) for i in range(10, len(hits) + 1)]
    longest = {"hit": 0, "miss": 0}
    previous, length = None, 0
    for row in rows:
        outcome = row["outcome"]
        length = length + 1 if outcome == previous else 1
        longest[outcome] = max(longest[outcome], length)
        previous = outcome
    baseline = sum(len(r["vote_zodiacs"]) for r in rows)
    return {
        "nickname": profile["nickname"],
        "first_issue": profile["first_issue"], "last_issue": profile["last_issue"],
        "participations": len(profile["records"]), "settled": len(hits),
        "hits": sum(hits), "misses": len(hits) - sum(hits),
        "hit_rate": rate(sum(hits), len(hits)), "recent": recent,
        "rolling_10_range": rate(max(rolling) - min(rolling), 10) if rolling else None,
        "current_streak": {"outcome": previous, "count": length},
        "longest_streak": longest,
        "random_hit_baseline": rate(baseline, 12 * len(rows)),
    }


def build(archive, *, rebuild=False):
    """Refresh changed issues and recompute only the authors they affect."""
    archive = Path(archive)
    destination = archive / "author-profiles"
    destination.mkdir(parents=True, exist_ok=True)
    index_path = destination / "index.json"
    report_path = destination / "statistics.json"
    checkpoint = destination / "statistics-state.json"
    folders = {p.name: p for p in sorted((archive / "issues").glob("20?????"))
               if (p / "snapshot.json").is_file()}
    paths = {issue: destination / "records" / (issue + ".json") for issue in folders}
    signatures = {issue: inputs(folder, paths[issue]) for issue, folder in folders.items()}
    before = {}
    valid_checkpoint = False
    profiles, stats = {}, {}
    try:
        if not rebuild and checkpoint.is_file():
            state = load(checkpoint)
            digest = state.pop("digest")
            if (digest == sha256(state) and state["version"] == 1 and
                    state["index"] == file_digest(index_path) and
                    state["report"] == file_digest(report_path)):
                before = state["issues"]
                valid_checkpoint = True
    except (ValueError, TypeError, KeyError, OSError):
        pass
    changed = {issue for issue in signatures.keys() | before.keys()
               if issue not in before or signatures.get(issue) != before[issue]["inputs"]}
    if valid_checkpoint and not changed:
        return result(before, state["author_count"], 0, 0)
    if before:
        profiles = {p["id"]: p for p in load(index_path)["authors"]}
        stats = load(report_path)["authors"]
    additions = defaultdict(list)
    affected = set()
    states = {issue: entry for issue, entry in before.items() if issue not in changed}
    created = 0
    for issue in sorted(changed & folders.keys()):
        folder, path = folders[issue], paths[issue]
        entry = states[issue] = {"inputs": signatures[issue]}
        try:
            if not path.is_file():
                candidate = frozen_evidence(folder)
                created += write_once(path, candidate)
                entry["inputs"] = inputs(folder, path)
            record = read_sealed_record(path, folder)
            try:
                actual = valid_settlement(folder, record)
            except (ValueError, TypeError, KeyError):
                actual = None
            entry["status"] = "settled" if actual else "pending"
            if actual is None and entry["inputs"][-1] is not None:
                entry["settlement_error"] = "settlement fails frozen evidence validation"
            rows = [(item, profile_record(record, item, actual)) for item in record["materials"]]
            entry["authors"] = [item["id"] for item, _ in rows]
            if len(set(entry["authors"])) != len(rows):
                raise ValueError("duplicate author in sealed record")
            for item, row in rows:
                additions[item["id"]].append((item, row))
        except (ValueError, TypeError, KeyError, OSError) as error:
            entry.pop("authors", None)
            entry.pop("settlement_error", None)
            entry["status"] = "skipped"
            entry["reason"] = str(error)
        affected.update(entry.get("authors", []))
    for issue in changed & before.keys():
        affected.update(before[issue].get("authors", []))
    for identity in affected:
        profile = profiles.get(identity)
        rows = [] if profile is None else [
            row for row in profile["records"] if row["issue"] not in changed
        ]
        for item, row in additions[identity]:
            if profile is None:
                profile = {key: item[key] for key in ("id", "nickname", "identity_status")}
            if profile["nickname"] != item["nickname"]:
                raise ValueError("nickname identity hash collision")
            rows.append(row)
        if not rows:
            profiles.pop(identity, None)
            stats.pop(identity, None)
            continue
        rows.sort(key=lambda row: row["issue"])
        profile.update(records=rows, first_issue=rows[0]["issue"], last_issue=rows[-1]["issue"],
                       observed_issue_count=len(rows),
                       settled_issue_count=sum(row["outcome"] != "pending" for row in rows))
        profiles[identity] = profile
        stats[identity] = statistics(profile)
    summary = result(states, len(profiles), created, len(changed))
    index = {
        "schema_version": SCHEMA,
        "classification": "read_only_author_memory_experimental",
        "used_for_predictions": False, "author_weighting_enabled": False,
        "identity_note": "Nickname-only provisional ID; a renamed account is not automatically merged.",
        "included_issues": summary["included_issues"], "skipped_issues": summary["skipped_issues"],
        "author_count": len(profiles),
        "authors": [profiles[key] for key in sorted(profiles)],
    }
    report = {
        "schema_version": 1, "forum": {"archive": "118", "forum49": "49"}.get(archive.name, archive.name),
        "definitions": {
            "identity": "论坛内昵称，未验证为账号",
            "recent_and_streak": "按作者实际已结算参与期顺序，跳过缺席及待结算",
            "rolling_10_range": "完整滚动10次参与命中率的最高值减最低值，少于10次为空",
            "random_hit_baseline": "已结算各期有效覆盖生肖数除以12后取平均",
            "rates": "比例使用0至1，样本不足的近期窗口展示实际样本数",
        },
        "author_count": len(profiles), "included_issues": summary["included_issues"],
        "settled_issues": sorted(i for i, e in states.items() if e["status"] == "settled"),
        "pending_issues": sorted(i for i, e in states.items() if e["status"] == "pending"),
        "skipped_issues": summary["skipped_issues"],
        "invalid_settlements": sorted(i for i, e in states.items() if "settlement_error" in e),
        "participations": sum(row["participations"] for row in stats.values()),
        "settled_participations": sum(row["settled"] for row in stats.values()),
        "hits": sum(row["hits"] for row in stats.values()),
        "authors_with_full_windows": {
            str(size): sum(row["settled"] >= size for row in stats.values())
            for size in (10, 20, 50)
        },
        "authors": dict(sorted(stats.items())),
    }
    write_json(index_path, index)
    write_json(report_path, report, compact=True)
    state = {"version": 1, "issues": dict(sorted(states.items())), "author_count": len(profiles),
             "index": file_digest(index_path), "report": file_digest(report_path)}
    state["digest"] = sha256(state)
    write_json(checkpoint, state)
    return summary


def result(states, authors, created, changed):
    return {
        "created_issue_records": created, "profiles": authors, "reprocessed_issues": changed,
        "included_issues": sorted(i for i, e in states.items() if e["status"] != "skipped"),
        "skipped_issues": [{"issue": i, "reason": e["reason"]} for i, e in sorted(states.items())
                           if e["status"] == "skipped"],
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--archive", default="archive")
    parser.add_argument("--rebuild", action="store_true")
    args = parser.parse_args()
    print(json.dumps(build(args.archive, rebuild=args.rebuild), ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
