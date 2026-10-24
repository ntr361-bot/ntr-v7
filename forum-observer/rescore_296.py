"""TEST ONLY: re-score an archived 49 run under the collection scope (no writes)."""
import json, re, sys
from forum_observer.core import vote_zodiacs, ZODIACS
from forum_observer.evidence_audit import admit_one_material_per_author
from forum_observer.page_collector import apply_collection_scope

DEDUPE = ('duplicate author', 'superseded_by_same_author')


def eligible_rows(run):
    rows = []
    for m in run['materials']:
        if not (m['counted'] or any(str(m.get('reason') or '').startswith(d) for d in DEDUPE)):
            continue
        r = dict(m, issue=run['issue'], counted_for_ranking=True)
        r['vote_zodiacs'] = vote_zodiacs(m['picks'], m['mode'], m.get('play') or '')
        r['weight_per_zodiac'] = 1.0 / len(r['vote_zodiacs'])
        rows.append(r)
    return rows


def score(rows):
    rows = [dict(r) for r in rows]
    admit_one_material_per_author(rows)
    votes = {z: 0.0 for z in ZODIACS}
    used = [r for r in rows if r['counted_for_ranking']]
    for r in used:
        for z in r['vote_zodiacs']:
            votes[z] += r['weight_per_zodiac']
    order = sorted(ZODIACS, key=lambda z: (-votes[z], ZODIACS.index(z)))
    return [(z, round(votes[z], 6)) for z in order], used


def post_id(url):
    m = re.search(r'id=(\d+)', url or '')
    return int(m.group(1)) if m else None


def scoped(run, cards, policy):
    """cards: {article_id: {'is_featured','click_count','like_count'}}"""
    items = []
    for url in sorted({m['source_url'] for m in run['materials']}):
        c = cards.get(post_id(url)) or {}
        items.append({'key': url, 'author': '', 'is_featured': c.get('is_featured') is True,
                      'click_count': c.get('click_count'), 'like_count': c.get('like_count')})
    kept, audit = apply_collection_scope(items, policy)
    keep = {x['key'] for x in kept}
    return [r for r in eligible_rows(run) if r['source_url'] in keep], audit


def harvest_cards(url, stop_issue, minutes):
    """Scroll the live feed (read-only) and keep card counts by article id."""
    import time
    from playwright.sync_api import sync_playwright
    from forum_observer import page_collector, forum49
    from forum_observer.author_history import incremental_scan
    if forum49.FEATURED_ICON_SELECTOR not in page_collector.FEATURED_ICON_SELECTORS:
        page_collector.FEATURED_ICON_SELECTORS.append(forum49.FEATURED_ICON_SELECTOR)
    with sync_playwright() as pw:
        browser = pw.chromium.launch(headless=True, args=['--no-sandbox'])
        try:
            page = browser.new_page(viewport={'width': 1280, 'height': 1800})
            page.set_default_timeout(5000)
            page.goto(url, wait_until='domcontentloaded', timeout=60000)
            page.get_by_role('tab', name='澳彩', exact=True).wait_for(state='visible', timeout=15000)
            page.get_by_role('tab', name='澳彩', exact=True).click()
            page.get_by_text('最新', exact=True).click()
            page.wait_for_timeout(1100)
            items, audit = incremental_scan(
                page, lambda: page_collector._listing(page, None), max_steps=3000, delay_ms=650,
                deadline=time.monotonic() + minutes * 60,
                stop_predicate=lambda xs: any(x.get('issue') == stop_issue for x in xs))
        finally:
            browser.close()
    cards = {}
    for x in items:
        if x.get('article_id'):
            cards[int(x['article_id'])] = {k: x.get(k) for k in ('issue', 'is_featured', 'click_count', 'like_count', 'author')}
    return cards, audit


if __name__ == '__main__':
    import os
    from pathlib import Path
    from forum_observer.forum49 import COLLECTION_SCOPE
    def esc(x): return x.replace('%', '%25').replace('\r', '%0D').replace('\n', '%0A')
    run = json.loads(Path(sys.argv[1]).read_text())
    cards, audit = harvest_cards('https://49217.com/#/forum', sys.argv[2], int(sys.argv[3]))
    ids = {post_id(m['source_url']) for m in run['materials']}
    found = {i: cards[i] for i in ids if i in cards}
    full_rank, full_used = score(eligible_rows(run))
    rows, scope = scoped(run, cards, COLLECTION_SCOPE)
    new_rank, new_used = score(rows)
    old = {r['author'] for r in full_used}; new = {r['author'] for r in new_used}
    out = {'scan': {k: audit.get(k) for k in ('steps', 'items_seen', 'stop_reason', 'truncated')},
           'cards_harvested': len(cards), 'material_posts': len(ids), 'material_posts_with_counts': len(found),
           'featured_material_posts': sum(1 for c in found.values() if c.get('is_featured')),
           'scope_on_material_posts': scope,
           'full': {'valid': len(full_used), 'top6': full_rank[:6], 'top12': full_rank},
           'scoped': {'valid': len(new_used),
                      'recommend': sum(r['mode'] == 'include' for r in new_used),
                      'kill': sum(r['mode'] == 'exclude' for r in new_used),
                      'top6': new_rank[:6], 'top12': new_rank},
           'authors_dropped': len(old - new), 'authors_kept': len(old & new)}
    print('::notice title=RESCORE 296::' + esc(json.dumps(out, ensure_ascii=False)[:3800]))
    print('::notice title=RESCORE 296 kept authors::' + esc('、'.join(sorted(new))[:3800]))
    Path('rescore-296.json').write_text(json.dumps({'out': out, 'cards': {str(k): v for k, v in found.items()}}, ensure_ascii=False, indent=1))
