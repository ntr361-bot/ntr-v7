"""49论坛模型 — collection run for the 49图库 高手论坛 (49217.com).

Reuses the existing page collector and parser unchanged (same >=6 zodiac and
kill-zodiac rules, one material per author). This module only collects and
records an observation: it never freezes, settles, publishes to the ledger or
touches the formal forum model's archive, D1 or V7.

Output: forum-observer/forum49/issues/<issue>/run-<run_id>.json (one file per
run, never overwritten) plus a compact summary for workflow annotations.
"""
from __future__ import annotations

import argparse
import json
import os
import time
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import urlparse

from .core import material_counts

MODEL_NAME = '49论坛模型'
DEFAULT_URL = 'https://49217.com/#/forum'
MAX_POSTS = 2000  # collector's own ceiling; feed scan stops at the issue boundary
FREEZE_CUTOFF = (21, 0)
PUBLISH_RESERVE_SECONDS = 90
# 49图库 marks featured (加精) cards with a '精' icon here; its `span.up > img`
# (no .icon class) is the pinned/置顶 marker. Verified by source probe run
# 40058947187: 11 of 20 cards had this icon and all 11 had highQuality=1.
FEATURED_ICON_SELECTOR = '.title .van19 > span.van19 > img.icon'
# Shadow scope (user decision 2026-10-24): record only. Formal collection stays
# full (all posts, no cap). Each run also scores the subset this scope would
# keep - all featured posts plus ordinary posts with clicks >= 300 or likes >= 10,
# no cap - so the two can be compared before any switch. Never used for freeze.
SHADOW_SCOPE = {'ordinary_min_clicks': 300, 'ordinary_min_likes': 10}


def shadow_observation(result: dict, scope: dict = SHADOW_SCOPE) -> dict:
    """Score the posts a popularity scope would keep (observation only)."""
    from .page_collector import apply_collection_scope, score_posts
    posts = [dict(p) for p in (result.get('evidence') or [])]
    kept, audit = apply_collection_scope(posts, scope)
    ranking, mats = score_posts([dict(p) for p in kept])
    counted = [m for m in mats if m.get('counted_for_ranking')]
    return {
        'scope': dict(scope), 'used_for_freeze': False,
        'opened_posts': len(posts), 'kept_posts': len(kept), 'audit': audit,
        'valid_materials': len(counted),
        'distinct_valid_authors': len({m.get('author') for m in counted}),
        'material_counts': material_counts(mats),
        'top6': [r['zodiac'] for r in ranking[:6]],
        'ranking': [[r['zodiac'], r.get('score')] for r in ranking],
    }


def collection_budget(now_local, default_seconds: int) -> int:
    """Bound a same-evening scan so it completes before the 21:00 freeze.

    After 21:00 the scan may still run as a pure observation; the cycle step
    then refuses to use it as a pre-freeze draft.
    """
    cutoff = now_local.replace(hour=FREEZE_CUTOFF[0], minute=FREEZE_CUTOFF[1], second=0, microsecond=0)
    remaining = (cutoff - now_local).total_seconds() - PUBLISH_RESERVE_SECONDS
    if now_local >= cutoff:
        return default_seconds
    if remaining < 60:
        raise ValueError('too close to the 21:00 freeze to complete a collection')
    return int(min(default_seconds, remaining))


def summarize(result: dict, requested_issue: str | None) -> dict:
    """Build the recorded summary from a collect_page result (pure, unit-tested)."""
    posts = result.get('evidence') or []
    mats = result.get('parsed_predictions') or []
    counted = [m for m in mats if m.get('counted_for_ranking')]
    issue = str(result.get('issue') or '')
    return {
        'model': MODEL_NAME,
        'requested_issue': requested_issue,
        'issue': issue,
        'issue_matches_request': (not requested_issue) or issue == str(requested_issue),
        'detail_posts': len(posts),
        'nonempty_article_posts': sum(bool(str(p.get('body', '')).strip()) for p in posts),
        'valid_materials': len(counted),
        'distinct_valid_authors': len({m.get('author') for m in counted}),
        'material_counts': material_counts(mats),
        'ranking_observation': [
            {'zodiac': r['zodiac'], 'score': r.get('score')} for r in (result.get('ranking') or [])],
        'materials': [{
            'author': m.get('author'), 'play': m.get('play'), 'mode': m.get('mode'),
            'picks': m.get('picks'), 'counted': bool(m.get('counted_for_ranking')),
            'reason': m.get('exclusion_reason'), 'source_url': m.get('source_url'),
            'published_at': m.get('published_at')} for m in mats],
        'excluded_reasons': Counter(
            str(m.get('exclusion_reason'))[:60] for m in mats
            if not m.get('counted_for_ranking')).most_common(15),
        'collector_rejected': (result.get('rejected') or [])[:50],
        'frozen': False, 'settled': False, 'published_to_ledger': False,
    }


def run(url: str, issue: str | None, category: str | None, out: Path, archive: Path,
        config_path: Path = Path('collector-config.json')) -> dict:
    from . import page_collector
    page_collector.HOSTS.add(urlparse(url).hostname)  # this process only
    if FEATURED_ICON_SELECTOR not in page_collector.FEATURED_ICON_SELECTORS:
        page_collector.FEATURED_ICON_SELECTORS.append(FEATURED_ICON_SELECTOR)  # this process only
    out.mkdir(parents=True, exist_ok=True)
    cfg = json.loads(Path(config_path).read_text(encoding='utf-8'))
    cfg.update(max_posts=MAX_POSTS, max_pages=50, freeze_enabled=False,
               daily_profile_authors=0, collection_mode='current_feed')
    from zoneinfo import ZoneInfo
    now_local = datetime.now(ZoneInfo('Asia/Shanghai'))
    started = datetime.now(timezone.utc)
    t0 = time.monotonic()
    record = {'model': MODEL_NAME, 'url': url, 'category': category,
              'run_id': os.environ.get('GITHUB_RUN_ID'),
              'started_at': started.isoformat()}
    try:
        cfg['max_collection_seconds'] = collection_budget(now_local, int(cfg.get('max_collection_seconds', 1800)))
        record['max_collection_seconds'] = cfg['max_collection_seconds']
        result = page_collector.collect_page(url, out, issue, category, cfg)
        # Same human-readable report renderer as 118论坛模型 (archived by cycle),
        # compact: no long excluded/pending listings (user, 2026-10-22).
        from .report import render
        render(result, out / str(result['issue']) / 'report.md', compact=True)
        record.update(ok=True, **summarize(result, issue))
        try:  # observation only; a failure here never affects the formal record
            record['shadow_scope'] = shadow_observation(result)
        except Exception as exc:
            record['shadow_scope'] = {'error': f'{type(exc).__name__}: {exc}'[:300]}
        # Full post evidence for the Scrapling side-by-side comparison step.
        # Workflow-local only (artifact), not committed.
        (out / 'forum49-evidence.json').write_text(json.dumps(
            {'issue': result.get('issue'), 'posts': result.get('evidence') or []},
            ensure_ascii=False) + '\n', encoding='utf-8')
    except Exception as exc:  # recorded, never hidden
        record.update(ok=False, error=f'{type(exc).__name__}: {exc}'[:1000],
                      requested_issue=issue)
    record['elapsed_seconds'] = round(time.monotonic() - t0, 1)
    record['finished_at'] = datetime.now(timezone.utc).isoformat()
    folder = archive / 'issues' / str(record.get('issue') or issue or 'unknown')
    folder.mkdir(parents=True, exist_ok=True)
    target = folder / f"run-{record['run_id'] or started.strftime('%Y%m%dT%H%M%S')}.json"
    if target.exists():
        raise SystemExit(f'refusing to overwrite {target}')
    target.write_text(json.dumps(record, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    (out / 'forum49-summary.json').write_text(
        json.dumps(record, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print('FORUM49_RECORD ' + str(target), flush=True)
    return record


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--url', default=DEFAULT_URL)
    ap.add_argument('--issue', default=None, help='YYYYNNN; omit to use latest')
    ap.add_argument('--category', default='澳门')
    ap.add_argument('--out', type=Path, default=Path('forum49-out'))
    ap.add_argument('--archive', type=Path, default=Path('forum49'))
    a = ap.parse_args(argv)
    record = run(a.url, a.issue or None, a.category or None, a.out, a.archive)
    if not record.get('ok'):
        raise SystemExit(1)


if __name__ == '__main__':
    main()
