"""Read-only probe for a candidate forum source.

Checks whether a new site runs the same forum platform as the current source
(``.forum-list`` Vue feed with 澳彩 tab), so the existing collector could be
reused by configuration only.

Safety: this module never scores, freezes, archives or publishes. Everything it
writes goes under the ``out`` directory given on the command line, which the
workflow uploads as an artifact and then discards.
"""
from __future__ import annotations

import argparse
import json
import time
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import urlparse

PLATFORM_TAB = '澳彩'
CARD_SELECTOR = '.forum-list > li'


def classify(summary: dict) -> dict:
    """Decide from probe evidence whether the site matches the current platform.

    Pure function so the decision rule is unit-tested without a browser.
    """
    cards = summary.get('cards') or []
    usable = [c for c in cards if c.get('author') and c.get('issue')]
    reasons = []
    if not summary.get('page_loaded'):
        reasons.append('page_not_loaded')
    if summary.get('card_count', 0) <= 0:
        reasons.append('no_forum_list_cards')
    if PLATFORM_TAB not in (summary.get('tabs') or []):
        reasons.append('missing_platform_tab')
    if not usable:
        reasons.append('no_card_with_author_and_issue')
    return {
        'platform_match': not reasons,
        'usable_cards': len(usable),
        'distinct_authors': len({c['author'] for c in usable}),
        'issues_seen': sorted({str(c['issue']) for c in usable}),
        'reasons': reasons,
    }


def _short(card: dict) -> dict:
    keep = ('author', 'issue', 'title', 'published_at', 'category',
            'is_featured', 'article_id', 'href')
    row = {k: card.get(k) for k in keep}
    row['body_excerpt'] = str(card.get('body', ''))[:120]
    return row


def probe(url: str, out: Path, wait_seconds: int = 20) -> dict:
    from playwright.sync_api import sync_playwright
    from .page_collector import _listing

    out.mkdir(parents=True, exist_ok=True)
    summary = {
        'url': url, 'host': urlparse(url).hostname,
        'started_at': datetime.now(timezone.utc).isoformat(),
        'page_loaded': False, 'tabs': [], 'card_count': 0, 'cards': [],
        'request_failures': [], 'console_errors': [], 'steps': [],
        'scored': False, 'frozen': False, 'published': False,
    }

    def step(name, **info):
        entry = {'step': name, 'at': datetime.now(timezone.utc).isoformat(), **info}
        summary['steps'].append(entry)
        print('PROBE_STEP ' + json.dumps(entry, ensure_ascii=False), flush=True)

    with sync_playwright() as pw:
        browser = pw.chromium.launch(headless=True, args=['--no-sandbox'], timeout=30000)
        try:
            page = browser.new_page(viewport={'width': 1280, 'height': 1800})
            page.on('requestfailed', lambda r: summary['request_failures'].append(
                {'host': urlparse(r.url).hostname, 'path': urlparse(r.url).path[:120],
                 'reason': r.failure}))
            page.on('console', lambda m: m.type == 'error' and summary['console_errors'].append(m.text[:300]))
            step('navigate')
            try:
                response = page.goto(url, wait_until='domcontentloaded', timeout=60000)
                summary['http_status'] = response.status if response else None
                summary['page_loaded'] = True
            except Exception as exc:  # report, never hide
                summary['navigation_error'] = f'{type(exc).__name__}: {exc}'[:500]
                step('navigate_failed', error=summary['navigation_error'])
            if summary['page_loaded']:
                deadline = time.monotonic() + wait_seconds
                while time.monotonic() < deadline:
                    if page.locator(f'{CARD_SELECTOR}, [role=tab]').count():
                        break
                    page.wait_for_timeout(500)
                summary['final_url'] = page.url
                summary['title'] = page.title()
                summary['tabs'] = [t.strip() for t in page.locator('[role=tab]').all_inner_texts() if t.strip()][:40]
                step('rendered', tabs=summary['tabs'][:12])
                if PLATFORM_TAB in summary['tabs']:
                    try:
                        page.get_by_role('tab', name=PLATFORM_TAB, exact=True).click(timeout=10000)
                        latest = page.get_by_text('最新', exact=True)
                        if latest.count():
                            latest.first.click(timeout=10000)
                        page.wait_for_timeout(1500)
                        step('platform_tab_clicked')
                    except Exception as exc:
                        step('platform_tab_click_failed', error=str(exc)[:300])
                summary['card_count'] = page.locator(CARD_SELECTOR).count()
                if summary['card_count']:
                    try:
                        summary['cards'] = [_short(c) for c in _listing(page, None)[:12]]
                    except Exception as exc:
                        summary['listing_error'] = f'{type(exc).__name__}: {exc}'[:500]
                summary['body_preview'] = page.locator('body').inner_text(timeout=5000)[:2000]
                step('listing_read', card_count=summary['card_count'], parsed=len(summary['cards']))
                page.screenshot(path=str(out / 'probe.png'), full_page=False)
        finally:
            browser.close()
    summary['request_failures'] = summary['request_failures'][:30]
    summary['console_errors'] = summary['console_errors'][:20]
    summary['verdict'] = classify(summary)
    summary['finished_at'] = datetime.now(timezone.utc).isoformat()
    (out / 'probe-summary.json').write_text(
        json.dumps(summary, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print('PROBE_SUMMARY ' + json.dumps({k: summary.get(k) for k in (
        'url', 'final_url', 'http_status', 'title', 'tabs', 'card_count', 'verdict')},
        ensure_ascii=False), flush=True)
    return summary


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--url', required=True)
    ap.add_argument('--out', required=True, type=Path)
    args = ap.parse_args(argv)
    if urlparse(args.url).scheme != 'https':
        raise SystemExit('only https URLs are probed')
    probe(args.url, args.out)


if __name__ == '__main__':
    main()
