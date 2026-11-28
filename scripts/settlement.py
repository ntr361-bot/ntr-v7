"""Reconcile committed forecasts against one verified draw archive."""
from collections import defaultdict
from datetime import datetime, timedelta, timezone
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
from tempfile import TemporaryDirectory
from urllib.request import Request, urlopen

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "forum-observer"))
from forum_observer import author_profiles, cycle, live_draw
from forum_observer.core import ZODIACS

DATA = ROOT / "site" / "data"
STATUS = DATA / "settlement" / "latest.json"
LEDGER = "https://smart-ledger-cf.5rmwf2d5ff.workers.dev"
ISSUE_PATTERN = re.compile(r"20\d{5}")


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def issue_at(when):
    local = when.astimezone(cycle.TZ)
    return f"{local.year}{local.timetuple().tm_yday:03d}"


def stamp():
    return datetime.now(timezone.utc).isoformat()


def digest(value):
    return hashlib.sha256(json.dumps(value, ensure_ascii=False, sort_keys=True).encode()).hexdigest()


def registry():
    value = load(ROOT / "scripts" / "settlement_models.json")
    ids = [model["id"] for model in value["models"]]
    if len(set(ids)) != len(ids):
        raise ValueError("duplicate formal model registration")
    return value


def draw_identity(row):
    return row["special_number"], row["special_zodiac"], row["numbers"]


def verified_draws(history, live, now):
    if history.get("status") != "success":
        raise ValueError("committed_draw_archive_unverified")
    confirmed = {}
    conflicts = set()
    for rows, is_live in ((history["records"], False), (live, True)):
        for row in rows:
            issue = str(row["issue"])
            when = cycle.draw_time(row)
            if (not ISSUE_PATTERN.fullmatch(issue) or not when or when > now
                    or issue_at(when) != issue or row.get("special_zodiac") not in ZODIACS
                    or not re.fullmatch(r"\d{2}", str(row.get("special_number", "")))
                    or not 1 <= int(row["special_number"]) <= 49):
                continue
            prior = confirmed.get(issue)
            if prior:
                if draw_identity(prior) != draw_identity(row):
                    conflicts.add(issue)
                continue
            if is_live or issue not in confirmed:
                confirmed[issue] = row
    return {issue: row for issue, row in confirmed.items() if issue not in conflicts}, conflicts


def update_history(history, draws):
    original = {str(row["issue"]) for row in history["records"]}
    additions = [dict(draws[issue]) for issue in sorted(draws.keys() - original)]
    if not additions:
        return False
    for row in additions:
        row.pop("actual_source", None)
        row["open_time"] = cycle.draw_time(row).astimezone(cycle.TZ).strftime("%Y-%m-%d %H:%M:%S")
        row["date"] = row["open_time"]
        row["wave_color_source"] = "WebPageLunarYear" if row.get("special_wave_color") else ""
    history["records"] = sorted(history["records"] + additions, key=lambda row: str(row["issue"]))
    history["latest_issue"] = str(history["records"][-1]["issue"])
    history["updated_at"] = stamp()
    cycle.atomic(DATA / "history.json", history)
    return True


def entry(state, reason=None, **details):
    return {"state": state, **({"reason": reason} if reason else {}), **details}


def reconcile_forum(model, issues, draws, conflicts, history_file, *, apply):
    archive = ROOT / "forum-observer" / model["archive"]
    public = DATA / model["id"]
    directories = {p.name: p for p in (archive / "issues").glob("20?????") if p.is_dir()}
    outcomes = {}
    valid = {}
    new_count = 0
    for issue in sorted(set(directories) | set(issues)):
        if issue < model["first_issue"]:
            continue
        folder = archive / "issues" / issue
        try:
            if not (folder / "snapshot.json").is_file():
                outcomes[issue] = entry("failed", "snapshot_missing")
                continue
            snapshot = cycle.verified_snapshot(folder)
            if issue in conflicts:
                outcomes[issue] = entry("failed", "draw_source_conflict")
                continue
            actual = draws.get(issue)
            result_path = folder / "settlement.json"
            if not actual:
                if result_path.exists():
                    result = load(result_path)
                    if (str(result.get("issue")) != issue or result.get("snapshot_digest") != snapshot["digest"]
                            or result.get("actual") not in ZODIACS):
                        raise ValueError("existing_settlement_conflict")
                valid[issue] = snapshot
                outcomes[issue] = entry("waiting", "draw_unconfirmed")
                continue
            result = cycle.settlement_record(snapshot, actual, strict=not result_path.exists())
            if result_path.exists():
                saved = load(result_path)
                fields = ("issue", "actual", "snapshot_digest", "rank", "top1_hit", "top3_hit", "top6_hit")
                if any(saved.get(field) != result[field] for field in fields):
                    raise ValueError("existing_settlement_conflict")
            elif apply:
                new_count += cycle.write_once(result_path, result)
                if load(result_path) != result:
                    raise ValueError("settlement_readback_mismatch")
            valid[issue] = snapshot
            outcomes[issue] = entry("settled" if result_path.exists() else "ready",
                                    digest=snapshot["digest"], actual=result["actual"],
                                    rank=result["rank"], top6_hit=result["top6_hit"])
        except (OSError, ValueError, KeyError, TypeError, AttributeError) as error:
            outcomes[issue] = entry("failed", str(error).splitlines()[0])
    if apply:
        with TemporaryDirectory() as no_collection:
            cycle.sync(no_collection, archive, history_file, public,
                       live_draws=[r for r in draws.values() if r.get("actual_source")],
                       defer_freeze=True, model_id=model["id"], allow_settlement=False,
                       valid_snapshots=valid)
        author_profiles.build(archive)
        checkpoint = load(archive / "author-profiles" / "statistics-state.json")
        for issue, outcome in outcomes.items():
            if outcome["state"] == "settled":
                outcome["author_statistics"] = checkpoint.get("issues", {}).get(issue, {}).get("status")
                if load(public / "issues" / (issue + ".json")).get("settlement") != load(
                        archive / "issues" / issue / "settlement.json"):
                    outcome.update(entry("failed", "forum_feed_readback_mismatch"))
    return outcomes, new_count


def targets(model, daily, runtime):
    daily_key = model.get("daily")
    daily_model = (daily.get("ai_zodiac", {}).get(daily_key) or
                   daily.get("ai_zodiac", {}).get(model.get("legacy_daily"))) if daily_key else None
    if model.get("daily_section"):
        daily_model = daily.get(model["daily_section"])
    selected = [row for row in runtime if row["modelVersion"].lower() == model.get("version", "").lower()
                and (not model.get("periods") or row.get("analysisPeriods") == model["periods"])
                and row.get("analysisPeriods", 0) >= model.get("periods_min", 0)]
    return daily_model, selected


def formal_status(models, first, runtime_first, issues, draws, conflicts):
    runtime = defaultdict(list)
    for row in load(DATA / "runtime-state.json")["predictions"]:
        runtime[str(row["issue"])].append(row)
    results = {model["id"]: {} for model in models}
    for issue in sorted(set(issues) | set(runtime)):
        if issue < first:
            continue
        path = DATA / "daily-records" / f"{issue}.json"
        try:
            daily = load(path) if path.is_file() else {}
            if daily and str(daily.get("issue")) != issue:
                raise ValueError("daily_issue_mismatch")
        except (OSError, ValueError) as error:
            for result in results.values():
                result[issue] = entry("failed", str(error))
            continue
        draw = draws.get(issue)
        for model in models:
            result = results[model["id"]]
            model_data, rows = targets(model, daily, runtime[issue])
            uses_daily = model.get("daily") or model.get("daily_section")
            uses_runtime = model.get("version") and issue >= runtime_first
            if not uses_daily and not uses_runtime:
                continue
            if issue in conflicts:
                result[issue] = entry("failed", "draw_source_conflict")
            elif (uses_daily and not model_data) or (uses_runtime and len(rows) != 1):
                result[issue] = entry("failed", "formal_snapshot_missing_or_ambiguous")
            elif not draw:
                result[issue] = entry("waiting", "draw_unconfirmed")
            else:
                number, zodiac, _ = draw_identity(draw)
                verification = daily.get("verification", {})
                verified = not (model.get("daily") or model.get("daily_section")) or (
                    verification.get("status") == "verified"
                    and verification.get("actual_number") == number
                    and verification.get("actual_zodiac") == zodiac)
                runtime_verified = all(row.get("actualNumber") == number and row.get("actualZodiac") == zodiac
                                       and row.get("hitResult") in ("命中", "未命中")
                                       and row.get("top6HitResult") in ("命中", "未命中") for row in rows)
                if verified and runtime_verified:
                    result[issue] = entry("settled", actual=zodiac, special_number=number)
                    continue
                try:
                    frozen = datetime.fromisoformat(daily["generated_at"])
                    if (daily.get("status") != "success" or frozen.utcoffset() is None or
                            frozen >= cycle.draw_time(draw)):
                        raise ValueError("formal_snapshot_not_provably_pre_draw")
                    if verification.get("status") == "verified" and (
                            verification.get("actual_number") != number or verification.get("actual_zodiac") != zodiac):
                        raise ValueError("formal_existing_settlement_conflict")
                except (ValueError, KeyError, TypeError) as error:
                    result[issue] = entry("failed", str(error))
                    continue
                result[issue] = entry("ready", actual=zodiac, special_number=number,
                                      freeze_digest=digest({"issue": issue, "generated_at": daily["generated_at"],
                                                            "model": model_data, "rows": [
                                          {key: row.get(key) for key in ("modelVersion", "analysisPeriods", "predictZodiac",
                                                                        "top6Zodiac", "finalRankingJson")} for row in rows]}))
    return results


def run_formal(eligible):
    command = ["dotnet", "run", "--project", "PredictionRunner/PredictionRunner.csproj",
               "--configuration", "Release", "--no-build", "--"]
    with TemporaryDirectory() as private_database:
        env = {**os.environ, "LIUHE_DATA_DIR": private_database}
        issues = Path(private_database) / "eligible-issues.txt"
        issues.write_text("\n".join(sorted(eligible)), encoding="utf-8")
        for args in (["--rebuild-db", "--rebuild-only"],
                     ["--settle-only", "--settlement-issues", str(issues)]):
            subprocess.run(command + args, cwd=ROOT, env=env, check=True)


def read_live(url, *, headers=None, body=None):
    payload = json.dumps(body, ensure_ascii=False).encode() if body is not None else None
    request = Request(url, data=payload, headers={"Accept": "application/json", "Cache-Control": "no-cache",
                                    "Content-Type": "application/json",
                                    "User-Agent": "ntr-v7-settlement/1", **(headers or {})})
    with urlopen(request, timeout=20) as response:
        return json.load(response)


def upstream_draws(now, query_date=None):
    from forum_observer.freeze_operation import DRAW_SOURCE
    date = query_date or now.astimezone(cycle.TZ).date()
    payload = read_live(f"{DRAW_SOURCE}?issueNum={date}&lotteryId=2032")
    if payload.get("code") != 0:
        raise ValueError("draw_source_unavailable")
    pets = str.maketrans("龍馬雞豬", "龙马鸡猪")
    rows = []
    for source in payload["data"]:
        try:
            if source["lotteryId"] != 2032:
                continue
            numbers = [int(v.strip()) for v in source["openCode"].split(",")]
            pet = source["pet"].translate(pets)
            special = numbers[-1]
            index = ZODIACS.index(pet)
            zodiac = pet if special == 49 else ZODIACS[(index - (special - 1)) % 12]
            rows.append(dict(issue=source["issue"], numbers=numbers, special=special,
                             yearPet=pet, specialZodiac=zodiac, openTime=source["openTime"]))
        except (ValueError, KeyError, TypeError, IndexError):
            continue
    return [{**row, "actual_source": "Macau:HistoryOpenInfo"}
            for row in live_draw.normalize_live_draws({"records": rows}, now)]


def target_date(now, scheduled=False):
    local = now.astimezone(cycle.TZ)
    if scheduled and (local.hour, local.minute) < (21, 40):
        return local - timedelta(days=1)
    return local


def save_status(report):
    issue = report["issue"]
    cycle.atomic(STATUS.parent / "issues" / (issue + ".json"), report)
    if not STATUS.exists() or load(STATUS)["issue"] <= issue:
        cycle.atomic(STATUS, report)


def run(args):
    now = datetime.now(timezone.utc)
    date = target_date(now, os.environ.get("SETTLEMENT_EVENT") == "schedule")
    target = args.issue or issue_at(date)
    config = registry()
    status = STATUS.parent / "issues" / (target + ".json")
    previous = load(status) if status.exists() else load(STATUS) if STATUS.exists() else {}
    history_file = DATA / "history.json"
    history = load(history_file)
    live = load(Path(args.live_fixture)) if args.live_fixture else []
    if not args.live_fixture:
        try:
            live = live_draw.fetch_live_draws(LEDGER + "/api/results")
        except (OSError, ValueError, KeyError, RuntimeError) as error:
            print(f"Production ledger draw unavailable ({type(error).__name__}); checking the Macau upstream.")
        if not any(row.get("issue") == target for row in live):
            try:
                query_date = (datetime(int(target[:4]), 1, 1) +
                              timedelta(days=int(target[-3:]) - 1)).date()
                upstream = upstream_draws(now, query_date)
                existing = {row["issue"] for row in live}
                live.extend(row for row in upstream if row["issue"] not in existing)
            except (OSError, ValueError, KeyError, TypeError) as fallback_error:
                print(f"Upstream draw unavailable ({type(fallback_error).__name__}); retaining verified draw evidence.")
    draws, conflicts = verified_draws(history, live, now)
    # Historical draw-backed issues plus the manual/current issue.
    issues = set(draws) | conflicts | {target}
    models = config["models"]
    counts = {}
    results = {}
    for model in (model for model in models if "archive" in model):
        try:
            results[model["id"]], counts[model["id"]] = reconcile_forum(
                model, issues, draws, conflicts, history_file, apply=args.apply)
        except (OSError, ValueError, KeyError, TypeError) as error:
            results[model["id"]] = {target: entry("failed", "forum_projection_or_author_update_failed",
                                                   detail=str(error))}
    formal = [model for model in models if "archive" not in model]
    first = config["daily_first_issue"]
    formal_results = formal_status(formal, first, config["runtime_first_issue"], issues, draws, conflicts)
    eligible = {issue for rows in formal_results.values() for issue, row in rows.items() if row["state"] == "ready"}
    if args.apply and eligible:
        original_formal = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
        try:
            update_history(history, draws)
            run_formal(eligible)
            newer = load(history_file)
            old = {row["issue"]: draw_identity(row) for row in history["records"]}
            if {row["issue"]: draw_identity(row) for row in newer["records"]} != old:
                raise ValueError("formal_runner_changed_verified_draws")
            formal_results = formal_status(formal, first, config["runtime_first_issue"], issues, draws, conflicts)
            for outcomes in formal_results.values():
                for outcome in outcomes.values():
                    if outcome["state"] == "ready":
                        outcome.update(entry("failed", "formal_row_retained_unsettled"))
        except (subprocess.CalledProcessError, OSError, ValueError, KeyError) as error:
            subprocess.run(["git", "restore", "--source", original_formal, "--",
                            "site/data/history.json", "site/data/daily-records", "site/data/runtime-state.json",
                            "site/data/prediction-traces"], cwd=ROOT, check=True)
            for rows in formal_results.values():
                for row in rows.values():
                    if row["state"] == "ready":
                        row.update(entry("failed", "formal_settlement_failed", detail=str(error)))
    results.update(formal_results)
    run_id = os.environ.get("GITHUB_RUN_ID")
    repository = os.environ.get("GITHUB_REPOSITORY")
    run_url = f"https://github.com/{repository}/actions/runs/{run_id}" if run_id and repository else None
    failures = {model["id"]: {issue: row["reason"] for issue, row in results[model["id"]].items()
                              if row["state"] == "failed"}
                for model in models}
    rows = [{"id": model["id"], "label": model["label"],
             **results[model["id"]].get(target, entry("failed", "model_not_active_for_issue"))}
            for model in models]
    attempts = previous.get("attempts", 0) + 1 if previous.get("issue") == target else 1
    if run_url and previous.get("run_url") == run_url:
        attempts -= 1
    report = {
        "schema_version": 1, "issue": target, "attempts": attempts, "run_url": run_url,
        "checked_at": stamp(), "last_settlement_at": stamp() if any(counts.values()) or any(
            row["state"] == "settled" for row in rows) and previous.get("issue") != target
            else previous.get("last_settlement_at"),
        "draw_confirmed": target in draws, "expected_count": len(rows),
        "settled_count": sum(row["state"] == "settled" for row in rows), "models": rows,
        "new_forum_settlements": counts, "historical_findings": failures,
        "ledger_sync": {"state": "pending"},
    }
    if args.apply:
        save_status(report)
    return report


def verify_ledger(report):
    # The current Cloudflare ledger reads the committed ntr-v7 files directly.
    # These are the production ledger's machine routes and its forum data sources.
    base = LEDGER
    issue = report["issue"]
    settled = {model["id"] for model in report["models"] if model["state"] == "settled"}
    if not settled:
        return {"state": "waiting", "verified_models": []}
    errors = {}
    verified = []
    config = registry()
    for model in config["models"]:
        key = model["id"]
        if key not in settled:
            continue
        if model.get("archive"):
            try:
                path = f"site/data/{key}/issues/{issue}.json"
                raw = "https://raw.githubusercontent.com/ntr361-bot/ntr-v7/main/"
                remote = read_live(raw + path)
                expected = load(ROOT / path)
                if remote.get("settlement") != expected.get("settlement") or remote.get("digest") != expected.get("digest"):
                    raise ValueError("forum_feed_readback_mismatch")
                # The GitHub projection has been published. The ledger machine
                # route confirms the deployed ledger successfully consumed it.
                live = read_live(base + f"/api/settlement?model={key}&issue={issue}")
                if live.get("settlement") != expected.get("settlement"):
                    raise ValueError("ledger_forum_readback_mismatch")
                verified.append(key)
            except (OSError, ValueError, KeyError) as error:
                errors[key] = f"{type(error).__name__}: {error}"
    formal = [model for model in config["models"] if "archive" not in model and model["id"] in settled]
    if formal:
        try:
            runtime = read_live(base + "/api/v7-sync/desktop/runtime-state")
            daily = read_live(base + f"/api/v7-sync/desktop/prediction?file={issue}.json")
            expected_daily = load(DATA / "daily-records" / (issue + ".json"))
            actual_rows = [row for row in runtime["predictions"] if str(row["issue"]) == issue]
            expected_rows = [row for row in load(DATA / "runtime-state.json")["predictions"]
                             if str(row["issue"]) == issue]
            for model in formal:
                live_model, live_rows = targets(model, daily, actual_rows)
                expected_model, target_rows = targets(model, expected_daily, expected_rows)
                fields = ("issue", "modelVersion", "analysisPeriods", "predictZodiac", "top6Zodiac",
                          "finalRankingJson", "actualNumber", "actualZodiac", "hitResult",
                          "top6HitResult", "actualRank")
                live_results = [tuple(row.get(field) for field in fields) for row in live_rows]
                expected_results = [tuple(row.get(field) for field in fields) for row in target_rows]
                if (live_model != expected_model or live_results != expected_results
                        or daily.get("verification") != expected_daily.get("verification")):
                    errors[model["id"]] = "formal_ledger_readback_mismatch"
                else:
                    verified.append(model["id"])
        except (OSError, ValueError, KeyError) as error:
            errors["formal"] = f"{type(error).__name__}: {error}"
    return {"state": "failed" if errors else "verified", "verified_models": verified,
            "checked_at": stamp(), "errors": errors}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--issue", type=lambda value: value if ISSUE_PATTERN.fullmatch(value) else parser.error("invalid issue"))
    parser.add_argument("--verify-ledger", action="store_true")
    parser.add_argument("--apply", action="store_true")
    parser.add_argument("--live-fixture")
    arguments = parser.parse_args()
    if arguments.apply and arguments.live_fixture:
        parser.error("fixture results are read-only")
    if arguments.verify_ledger:
        path = STATUS.parent / "issues" / (arguments.issue + ".json") if arguments.issue else STATUS
        report = load(path)
        report["ledger_sync"] = verify_ledger(report)
        save_status(report)
    else:
        report = run(arguments)
    print(json.dumps(report, ensure_ascii=False, indent=2))
