"""Full, isolated historic-issue forum TEXT audit. Never reads outcomes.

Collect all accessible cards and public comment threads for the requested issue,
write audit artifacts only; do not call cycle, settlement or freeze.
"""
import argparse
import hashlib
import json
from pathlib import Path

from .audit import run
from .core import ZODIACS


def perform(issue,config,out,snapshot):
    cfg=json.loads(Path(config).read_text(encoding="utf-8"))
    # Overrides only in this separate historical test process, never the
    # production collection configuration.
    cfg.update(
        retrospective_exhaustive=True,
        collection_mode="current_feed",
        freeze_enabled=False,
        collect_comments=True,
        collect_comment_images=False,
        collect_article_images=False,
        max_pages=500,
        max_posts=100000,
        max_comment_threads=10000,
        max_featured_post_seconds=110,
        max_post_seconds=70,
        comment_scan_seconds=50,
        max_comment_scroll_steps=300,
        max_collection_seconds=20400,
        daily_profile_authors=0,
    )
    out=Path(out)
    out.mkdir(parents=True,exist_ok=True)
    (out/"test-policy.json").write_text(json.dumps({
        "issue":issue,"mode":"retrospective_text_only",
        "ocr_enabled":False,"no_outcome_data":True,"no_freeze":True,
        "crawl_scope":"all_accessible_issue_cards_and_public_comment_threads",
        "total_runtime_constraint":"GitHub Actions platform job limit, not nightly cutoff",
    },ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
    result=run(cfg,issue,out)
    snap=json.loads(Path(snapshot).read_text(encoding="utf-8"))
    assert snap["issue"]==issue
    actual_ranks={r["zodiac"]:r["rank"] for r in result.get("ranking",[])}
    old_ranks={r["zodiac"]:r["rank"] for r in snap.get("ranking",[])}
    states=result.get("comment_thread_state_counts",{})
    pending=sum(int(states.get(k,0)) for k in ("queued","timeout","failed"))
    audit=result.get("audit",{})
    scroll=audit.get("forum_scroll",{})
    forum_boundary=scroll.get("stop_reason") in ("current_issue_boundary_reached",)
    listing_done=bool(scroll.get("end_confirmed") or forum_boundary)
    exhausted=bool(audit.get("truncated") or pending or not listing_done)
    valid=result.get("valid_materials",0)
    complete=valid>=0 and len(actual_ranks)==12 and not exhausted
    old_top6=snap.get("top6",[])
    new_top6=result.get("top6",[])
    metrics={
        "issue":issue,"status":"complete_retrospective_text_audit" if complete else "INCOMPLETE_NOT_FINAL",
        "historical_test_only":True,"outcome_data_used":False,
        "ocr_enabled":False,"frozen_snapshot_unchanged":True,
        "old_valid_materials":snap.get("valid_materials"),
        "new_valid_materials":valid,
        "material_difference":valid-int(snap.get("valid_materials") or 0),
        "raw_posts":result.get("raw_count"),
        "featured_valid_materials":result.get("featured_valid_materials"),
        "ordinary_valid_materials":result.get("ordinary_valid_materials"),
        "featured_found_threads":result.get("featured_found_threads"),
        "featured_attempted_threads":result.get("featured_attempted_threads"),
        "featured_completed_threads":result.get("featured_completed_threads"),
        "comment_candidate_threads":result.get("comment_candidate_threads"),
        "comment_attempted_threads":result.get("comment_attempted_threads"),
        "thread_states":states,
        "listing_end_confirmed":bool(scroll.get("end_confirmed")),
        "listing_stop_reason":scroll.get("stop_reason"),
        "thread_incomplete_count":pending,
        "old_top3":snap.get("top3",[]),"new_top3":result.get("top3",[]),
        "old_top6":old_top6,"new_top6":new_top6,
        "top6_added":[z for z in new_top6 if z not in old_top6],
        "top6_removed":[z for z in old_top6 if z not in new_top6],
        "rank_changes":[{"zodiac":z,"frozen":old_ranks[z],"retest":actual_ranks.get(z),
                         "delta":None if z not in actual_ranks else actual_ranks[z]-old_ranks[z]}
                        for z in ZODIACS if z in old_ranks],
        "limitations":["Public, accessible posts only; private/paywalled material cannot be verified.",
                       "Edits to already published posts after the draw cannot always be detected.",
                       "Historical crawl result is never a contemporaneous frozen prediction."],
    }
    if not complete:
        metrics["incomplete_reason"]="Forum scan did not finish, an item was not checked, or no 12-zodiac scoring exists"
    (out/"comparison.json").write_text(json.dumps(metrics,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
    if complete:
        rows=[
          f"# {issue}期完整论坛文字回测 vs 原冻结",
          "",
          "> 历史回测只按期号/原帖发布时间筛选资料；不读取开奖结果、不参与兑奖，图片识别关闭。",
          f"有效资料：原冻结{metrics['old_valid_materials']}份，回测{valid}份，变化{metrics['material_difference']:+d}份。",
          f"加精有效资料：{metrics['featured_valid_materials']}份；普通有效资料：{metrics['ordinary_valid_materials']}份。",
          f"原Top3：{'、'.join(metrics['old_top3'])}；回测Top3：{'、'.join(metrics['new_top3'])}",
          f"原Top6：{'、'.join(old_top6)}；回测Top6：{'、'.join(new_top6)}",
          f"Top6新增：{'、'.join(metrics['top6_added']) or '无'}；移出：{'、'.join(metrics['top6_removed']) or '无'}",
          "",
          "## 12生肖排名变化（正数表示回测名次更靠后）",
          *[f"- {r['zodiac']}：冻结第{r['frozen']}，回测第{r['retest']}，变化{r['delta']:+d}"
            for r in metrics["rank_changes"] if r["delta"] is not None],
          "",
          "原始证据、剔除项、来源、规则说明和评论覆盖见本次artifact中的 2026286/report.md 与 status.json。",
          "该回测无法排除作者在开奖后修改旧帖的可能，因此不应替代当晚冻结的预测。",
        ]
        (out/"comparison.md").write_text("\n".join(rows)+"\n",encoding="utf-8")
    print(json.dumps({k:metrics[k] for k in ("status","issue","old_valid_materials",
        "new_valid_materials","featured_valid_materials","comment_candidate_threads",
        "comment_attempted_threads","thread_incomplete_count")},ensure_ascii=False),flush=True)
    return 0 if complete else 2


def main():
    p=argparse.ArgumentParser()
    p.add_argument("--issue",default="2026286")
    p.add_argument("--config",default="collector-config.json")
    p.add_argument("--out",default="backtest-286")
    p.add_argument("--snapshot",default="archive/issues/2026286/snapshot.json")
    a=p.parse_args()
    return perform(a.issue,a.config,a.out,a.snapshot)


if __name__=="__main__":
    raise SystemExit(main())
