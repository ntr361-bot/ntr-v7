"""One-off read-only forum-285 continuation; no snapshot/archive/ledger writes."""
from __future__ import annotations

import argparse
import json
from collections import Counter
from datetime import datetime
from pathlib import Path
from .collection_policy import choose_comment_threads
from .page_collector import score_posts


ISSUE = "2026285"


def load_status(path: Path):
    obj = json.loads(path.read_text(encoding="utf-8"))
    if obj.get("issue") != ISSUE or not isinstance(obj.get("evidence"), list):
        raise ValueError(f"wrong period or missing evidence: {path}")
    return obj


def completed_keys(*statuses):
    """Skip only threads with confirmed end-of-comment-list, not truncated."""
    keys = set()
    for status in statuses:
        for row in status.get("comment_checks", []):
            if row.get("truncated") or row.get("end_confirmed") is not True:
                continue
            if not row.get("author") or not row.get("title"):
                continue
            values = [row["author"], ISSUE, row["title"]]
            keys.add(json.dumps(values, ensure_ascii=False, separators=(",", ":")))
    return sorted(keys)


def prepare(first, second, base_config, target):
    earlier = load_status(first)
    recent = load_status(second)
    config = json.loads(base_config.read_text(encoding="utf-8"))
    manifest = completed_keys(earlier, recent)
    if len(manifest) < 100:
        raise RuntimeError("refusing to restart from suspiciously short resume manifest")
    config.update({
        "issue": ISSUE,
        "issue_mode": "explicit",
        "freeze_enabled": False,
        "collection_mode": "current_feed",
        "collect_article_images": False,
        "collect_comment_images": False,
        "completed_thread_keys": manifest,
        "max_collection_seconds": 1800,
        "max_post_seconds": 75,
    })
    target.write_text(json.dumps(config, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({
        "issue": ISSUE,
        "completed_threads_from_prior_runs": len(manifest),
        "first_checked": len(earlier.get("comment_checks", [])),
        "second_checked": len(recent.get("comment_checks", [])),
        "limit_seconds": config["max_collection_seconds"],
        "freeze": config["freeze_enabled"],
        "archive": False,
    }, ensure_ascii=False, indent=2), flush=True)


def evidence_id(row):
    return row.get("evidence_id") or json.dumps([
        row.get("evidence_scope"), row.get("author"), row.get("issue"),
        row.get("title"), row.get("published_at"), row.get("comment_floor"),
    ], ensure_ascii=False)


def combined_evidence(*statuses):
    """Latest verified full article and latest comment text win; never sum duplicates."""
    merged = {}
    for status in statuses:
        for row in status.get("evidence", []):
            if row.get("issue") != ISSUE:
                continue
            key = evidence_id(row)
            old = merged.get(key)
            if old is None:
                merged[key] = dict(row)
                continue
            # Prefer verified full source article to a short feed-card snippet.
            full_old = old.get("detail_coverage") == "verified_article_text"
            full_new = row.get("detail_coverage") == "verified_article_text"
            if full_old and not full_new:
                continue
            if full_new and not full_old:
                merged[key] = dict(row)
                continue
            # When both have equivalent scope choose latest capture.
            merged[key] = dict(row)
    return list(merged.values())


def accepted_set(parsed):
    return {
        (row["author"], row["play"], row["mode"], tuple(row["picks"]))
        for row in parsed if row.get("counted_for_ranking")
    }


def summarize(first, second, next_file, out_dir):
    a = load_status(first)
    b = load_status(second)
    if not next_file.exists():
        raise RuntimeError("continuation collector produced no status")
    c = load_status(next_file)

    before = combined_evidence(a, b)
    after = combined_evidence(a, b, c)
    before_ranking, before_parsed = score_posts(before)
    ranking, parsed = score_posts(after)
    old_valid = accepted_set(before_parsed)
    new_valid = accepted_set(parsed) - old_valid
    new_rows = [row for row in parsed if row.get("counted_for_ranking") and
                (row["author"], row["play"], row["mode"], tuple(row["picks"])) in new_valid]
    prior_completed = completed_keys(a, b)
    new_complete = completed_keys(c)
    distinct_complete = len(set(prior_completed) | set(new_complete))

    # Verify there are no local artifacts with the formal freeze/settlement names.
    assert c.get("pre_draw_verified") is True, "collector crossed the draw deadline"
    assert len(accepted_set(parsed)) == sum(bool(r.get("counted_for_ranking")) for r in parsed)
    assert len({x.get("evidence_id") for x in after}) == len(after)
    audit = c.get("audit") or {}
    selected_found = c.get("comment_candidate_threads") or 0
    summary = {
        "issue": ISSUE,
        "test_only": True, "frozen": False, "settled": False,
        "archive_updated": False, "ledger_updated": False,
        "collection_status": c.get("status"),
        "collection_fetched_at": c.get("fetched_at"),
        "prior_completed_unique_threads": len(prior_completed),
        "newly_completed_unique_threads": len(set(new_complete)-set(prior_completed)),
        "merged_completed_unique_threads": distinct_complete,
        "prior_raw_records": len(before),
        "continuation_raw_records": len(c.get("evidence", [])),
        "merged_unique_evidence_records": len(after),
        "prior_recalculated_valid_materials": sum(x.get("counted_for_ranking", False) for x in before_parsed),
        "continuation_recalculated_valid_materials": sum(x.get("counted_for_ranking", False) for x in score_posts(c["evidence"])[1]),
        "merged_valid_materials": sum(x.get("counted_for_ranking", False) for x in parsed),
        "new_distinct_valid_materials": len(new_valid),
        "new_valid_material_detail": [
            {"author": row["author"], "play": row["play"], "mode": row["mode"],
             "picks": row["picks"], "source_url": row.get("source_url")}
            for row in new_rows
        ],
        "merged_distinct_valid_authors": len({x["author"] for x in parsed if x.get("counted_for_ranking")}),
        "merged_candidate_materials": len(parsed),
        "merged_excluded_materials": sum(not x.get("counted_for_ranking") for x in parsed),
        "merged_rejection_reasons": dict(Counter(
            x.get("exclusion_reason") or "unknown" for x in parsed if not x.get("counted_for_ranking"))),
        "merged_top3": [x["zodiac"] for x in ranking[:3]],
        "merged_top6": [x["zodiac"] for x in ranking[:6]],
        "merged_ranking": ranking,
        "prior_top6_recalculated": [x["zodiac"] for x in before_ranking[:6]],
        "new_listing_count": audit.get("listing_count"),
        "new_issue_listing_count": audit.get("issue_listing_count"),
        "new_featured_found_threads": c.get("featured_found_threads"),
        "new_featured_checked_threads": c.get("featured_checked_threads"),
        "new_comment_candidates": selected_found,
        "new_comment_selected": c.get("comment_selected_threads"),
        "new_comment_attempted": c.get("comment_attempted_threads"),
        "new_comment_checked": c.get("comment_checked_threads"),
        "new_comment_completed": c.get("comment_completed_threads"),
        "new_run_truncated": audit.get("truncated"),
        "new_run_errors": audit.get("detail_errors", [])[:30],
        "new_run_rejected_comments": [
            x for x in c.get("comment_checks", []) if x.get("truncated")],
        "picture_policy": c.get("comment_image_policy"),
        "quality_note": "Unfrozen retrospective combination; not a formal prediction or validated coverage of all forum posts."
    }
    out_dir.mkdir(parents=True, exist_ok=True)
    (out_dir/"merged-285-summary.json").write_text(
        json.dumps(summary, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
    (out_dir/"merged-285-evidence.json").write_text(
        json.dumps({"issue": ISSUE, "frozen": False, "evidence": after,
                    "parsed_predictions": parsed}, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")
    print(json.dumps(summary, ensure_ascii=False, indent=2), flush=True)
    return summary


def main():
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    prep = sub.add_parser("prepare")
    prep.add_argument("--first", type=Path, required=True)
    prep.add_argument("--second", type=Path, required=True)
    prep.add_argument("--config", type=Path, required=True)
    prep.add_argument("--out", type=Path, required=True)
    merge = sub.add_parser("merge")
    merge.add_argument("--first", type=Path, required=True)
    merge.add_argument("--second", type=Path, required=True)
    merge.add_argument("--new", type=Path, required=True)
    merge.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    if args.command == "prepare":
        prepare(args.first, args.second, args.config, args.out)
    else:
        summarize(args.first, args.second, args.new, args.out)


if __name__ == "__main__":
    main()
