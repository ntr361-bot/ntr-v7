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


DETAIL_JS = r"""(id) => {
    const seen = new Set();
    const find = (o, d) => {
        if (!o || typeof o !== 'object' || d > 4 || seen.has(o)) return null;
        seen.add(o);
        if ('highQuality' in o && 'clickCount' in o && (!o.articleId || Number(o.articleId) === id)) return o;
        for (const v of Object.values(o)) { const r = find(v, d + 1); if (r) return r; }
        return null;
    };
    for (const el of document.querySelectorAll('*')) {
        const vm = el.__vue__;
        if (!vm) continue;
        const r = find(vm.$data, 0);
        if (r) return {click_count: Number(r.clickCount), like_count: Number(r.thumbUpCount),
                       is_featured: Number(r.highQuality) === 1, issue: String(r.periodStr || r.period || ''),
                       author: r.username || null};
    }
    return null;
}"""


def _find_article(obj, aid, depth=0):
    if depth > 6:
        return None
    if isinstance(obj, dict):
        if 'clickCount' in obj and 'highQuality' in obj and str(obj.get('articleId', aid)) == str(aid):
            return obj
        for v in obj.values():
            r = _find_article(v, aid, depth + 1)
            if r:
                return r
    elif isinstance(obj, list):
        for v in obj[:200]:
            r = _find_article(v, aid, depth + 1)
            if r:
                return r
    return None


def harvest_details(ids, minutes):
    """Open each detail page (read-only) and read its article counts from the
    page's own JSON responses (fallback: Vue data)."""
    import time
    from playwright.sync_api import sync_playwright
    cards, errors, diag = {}, [], []
    responses = []
    deadline = time.monotonic() + minutes * 60
    with sync_playwright() as pw:
        browser = pw.chromium.launch(headless=True, args=['--no-sandbox'])
        try:
            page = browser.new_page(viewport={'width': 1280, 'height': 1800})
            def on_response(r):
                try:
                    if 'json' in (r.headers.get('content-type') or ''):
                        responses.append((r.url, r.json()))
                except Exception:
                    pass
            page.on('response', on_response)
            for n, i in enumerate(sorted(ids)):
                if time.monotonic() > deadline:
                    errors.append('time budget'); break
                responses.clear()
                t0 = time.monotonic()
                try:
                    page.goto(f'https://49217.com/#/corpusdetail?id={i}&type=3', wait_until='domcontentloaded', timeout=20000)
                    got, how = None, None
                    for _ in range(24):
                        page.wait_for_timeout(250)
                        for url, body in list(responses):
                            a = _find_article(body, i)
                            if a:
                                got, how = a, url.split('?')[0][-60:]; break
                        if got: break
                        v = page.evaluate(DETAIL_JS, i)
                        if v:
                            cards[i] = v; how = 'vue'; break
                    if got:
                        cards[i] = {'click_count': int(got.get('clickCount') or 0),
                                    'like_count': int(got.get('thumbUpCount') or 0),
                                    'is_featured': int(got.get('highQuality') or 0) == 1,
                                    'issue': str(got.get('periodStr') or got.get('period') or ''),
                                    'author': got.get('username')}
                    if i not in cards:
                        errors.append(f'{i}: no article data')
                    if n < 3:
                        diag.append({'id': i, 'how': how, 'seconds': round(time.monotonic() - t0, 1),
                                     'json_urls': [u.split('?')[0][-60:] for u, _ in responses][:8],
                                     'card': cards.get(i)})
                except Exception as exc:
                    errors.append(f'{i}: {type(exc).__name__}')
        finally:
            browser.close()
    return cards, {'opened': len(ids), 'read': len(cards), 'errors': errors[:10],
                   'error_count': len(errors), 'diag': diag}

if __name__ == '__main__':
    import os
    from pathlib import Path
    from forum_observer.forum49 import COLLECTION_SCOPE
    def esc(x): return x.replace('%', '%25').replace('\r', '%0D').replace('\n', '%0A')
    run = json.loads(Path(sys.argv[1]).read_text())
    ids = {post_id(m['source_url']) for m in run['materials']} - {None}
    cards, audit = harvest_details(ids, int(sys.argv[3]))
    found = {i: cards[i] for i in ids if i in cards}
    full_rank, full_used = score(eligible_rows(run))
    rows, scope = scoped(run, cards, COLLECTION_SCOPE)
    new_rank, new_used = score(rows)
    old = {r['author'] for r in full_used}; new = {r['author'] for r in new_used}
    out = {'scan': audit,
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
