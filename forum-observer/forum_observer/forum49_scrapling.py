"""Scrapling side-by-side check for 49论坛模型 (observation only).

After the primary collector finishes, Scrapling independently opens every
collected post's detail URL in its own browser session, extracts the page text
with its own parser, and both texts go through the SAME article bounding
(page_collector._article_body) and SAME scoring parser (score_posts).
The result says, per post and per author, whether the two collectors agree.

Nothing here feeds scoring, freezing, settlement or the ledger; the primary
collector's record is never modified. 118论坛模型 is not involved.
"""
from __future__ import annotations

import argparse
import difflib
import json
import os
import re
import time
from datetime import datetime, timezone
from pathlib import Path

DETAIL_READY_JS = """() => {
  const t = document.body ? document.body.innerText : '';
  return t.includes('阅读数') && t.includes('最新评论');
}"""


class _TextPage:
    """Minimal stand-in so page_collector._article_body runs on plain text."""

    def __init__(self, text: str):
        self._text = text

    def locator(self, _selector):
        return self

    def inner_text(self, timeout=None):
        return self._text


def article_from_text(text: str, post: dict) -> str:
    from .page_collector import _article_body
    return _article_body(_TextPage(text), post)


def _norm(text: str) -> str:
    return re.sub(r'\s+', '', text or '')


def material_key(m: dict) -> tuple:
    return (m.get('play'), m.get('mode'), tuple(sorted(m.get('picks') or [])))


def counted_by_author(materials: list[dict]) -> dict:
    return {m['author']: m for m in materials if m.get('counted_for_ranking')}


def compare(primary_posts: list[dict], scrapling_posts: list[dict]) -> dict:
    """Pure comparison of two post lists (same posts, possibly different bodies)."""
    from .page_collector import score_posts
    from .forum49 import STRICT_ISSUE_LINES
    _, p_mats = score_posts([dict(p) for p in primary_posts], STRICT_ISSUE_LINES)
    _, s_mats = score_posts([dict(p) for p in scrapling_posts], STRICT_ISSUE_LINES)
    p_by, s_by = counted_by_author(p_mats), counted_by_author(s_mats)
    authors = sorted(set(p_by) | set(s_by))
    rows, tally = [], {'same': 0, 'different': 0, 'primary_only': 0, 'scrapling_only': 0}
    for a in authors:
        p, s = p_by.get(a), s_by.get(a)
        if p and s:
            status = 'same' if material_key(p) == material_key(s) else 'different'
        else:
            status = 'primary_only' if p else 'scrapling_only'
        tally[status] += 1
        fmt = lambda m: None if not m else f"{m.get('play')}|{m.get('mode')}|{''.join(m.get('picks') or [])}"
        rows.append({'author': a, 'status': status, 'primary': fmt(p), 'scrapling': fmt(s)})
    return {'authors': rows, 'tally': tally,
            'primary_valid': len(p_by), 'scrapling_valid': len(s_by),
            'agree': tally['different'] == tally['primary_only'] == tally['scrapling_only'] == 0}


def text_agreement(primary: str, other: str) -> dict:
    a, b = _norm(primary), _norm(other)
    if not a and not b:
        return {'identical': True, 'ratio': 1.0}
    ratio = difflib.SequenceMatcher(None, a, b, autojunk=False).ratio() if (a and b) else 0.0
    return {'identical': a == b, 'ratio': round(ratio, 4)}


# Session options: one browser for the whole check, and no images/fonts/media.
# Text is read from the DOM by Scrapling's parser, so skipping those resources
# does not change what is compared; it only makes each page load faster.
SESSION_OPTIONS = {'headless': True, 'disable_resources': True, 'network_idle': False}
DETAIL_WAIT_MS = 8000     # wait for the detail shell (阅读数 + 最新评论)
FETCH_TIMEOUT_MS = 20000  # per-page navigation timeout


def _supported_kwargs(func, wanted: dict) -> dict:
    """Keep only the options this Scrapling version's call accepts."""
    import inspect
    try:
        params = inspect.signature(func).parameters
    except (TypeError, ValueError):
        return dict(wanted)
    if any(p.kind is p.VAR_KEYWORD for p in params.values()):
        return dict(wanted)
    return {k: v for k, v in wanted.items() if k in params}


class _ReusedSession:
    """One browser session reused for every post; reopened once after a failure."""

    def __init__(self, cls):
        self._cls = cls
        self._session = None
        self.options = _supported_kwargs(cls.__init__, SESSION_OPTIONS)
        self._open()

    def _open(self):
        try:
            session = self._cls(**self.options)
        except TypeError:
            self.options = {'headless': True}
            session = self._cls(headless=True)
        session.__enter__()
        self._session = session

    def close(self):
        session, self._session = self._session, None
        if session is not None:
            try:
                session.__exit__(None, None, None)
            except Exception:
                pass

    def fetch(self, url, **kw):
        try:
            return self._session.fetch(url, **kw)
        except Exception:
            # A crashed browser would fail every later post; restart it so only
            # this post is recorded as fetch_error, then let the error surface.
            self.close()
            self._open()
            raise


def _open_fetcher():
    """Pick whichever dynamic (browser) fetcher this Scrapling version ships.

    Returns (name, fetch_callable, closer). Session classes are preferred so a
    single browser is reused for all posts instead of one launch per post.
    Newer releases expose DynamicSession / DynamicFetcher; older ones
    PlayWrightFetcher; 0.5 ships only StealthySession / StealthyFetcher.
    """
    import scrapling
    import scrapling.fetchers as f
    version = getattr(scrapling, '__version__', 'unknown')
    session_cls = getattr(f, 'DynamicSession', None)
    if session_cls is not None:
        session = _ReusedSession(session_cls)
        return f'DynamicSession@{version}', session.fetch, session.close
    # Plain browser fetchers first. The stealth browser is only a fallback for
    # releases that ship nothing else; it is used solely on sites that permit
    # automated access (49217.com does) and never to get around a refusal.
    for name in ('DynamicFetcher', 'PlayWrightFetcher'):
        cls = getattr(f, name, None)
        if cls is None:
            continue

        def call(url, _cls=cls, **kw):
            try:
                return _cls.fetch(url, **kw)
            except TypeError as exc:
                if 'self' not in str(exc) and 'positional' not in str(exc):
                    raise
                return _cls().fetch(url, **kw)
        return f'{name}@{version}', call, lambda: None
    stealth_session = getattr(f, 'StealthySession', None)
    if stealth_session is not None:
        session = _ReusedSession(stealth_session)
        return f'StealthySession@{version}', session.fetch, session.close
    for name in ('StealthyFetcher',):
        cls = getattr(f, name, None)
        if cls is None:
            continue

        def call(url, _cls=cls, **kw):
            try:
                return _cls.fetch(url, **kw)
            except TypeError as exc:
                if 'self' not in str(exc) and 'positional' not in str(exc):
                    raise
                return _cls().fetch(url, **kw)
        return f'{name}@{version}', call, lambda: None
    available = ','.join(n for n in dir(f) if not n.startswith('_'))
    raise ImportError(f'no browser fetcher in scrapling {version}; available: {available}')


def _page_text(page) -> str:
    for getter in (lambda: page.find('body'), lambda: page.css_first('body'), lambda: page.css('body')[0]):
        try:
            node = getter()
        except Exception:
            continue
        if node is not None:
            return node.get_all_text(separator='\n', strip=True)
    return ''


def priority_order(posts: list[dict]) -> list[int]:
    """Indices with posts that yield parsed material on the primary side first."""
    from .page_collector import extract_picks
    with_material, rest = [], []
    for i, post in enumerate(posts):
        text = f"{post.get('title', '')}\n{post.get('body', '')}"
        (with_material if extract_picks(text) else rest).append(i)
    return with_material + rest


def fetch_bodies(posts: list[dict], budget_seconds: int, max_posts: int) -> tuple[str, list[dict]]:
    def wait_detail(page):
        try:
            page.wait_for_function(DETAIL_READY_JS, timeout=DETAIL_WAIT_MS)
        except Exception:
            pass  # recorded below as a detail-shell miss
        return page

    fetcher_name, fetch, close = _open_fetcher()
    results: list[dict | None] = [None] * len(posts)
    order = priority_order(posts)
    if max_posts and max_posts > 0:  # 0 or less = no post limit (time budget still applies)
        order = order[:max_posts]
    deadline = time.monotonic() + budget_seconds
    try:
        for i in order:
            post = posts[i]
            entry = {'detail_url': post.get('detail_url'), 'author': post.get('author')}
            if time.monotonic() > deadline:
                entry.update(status='not_checked_time_budget')
                results[i] = entry
                continue
            t0 = time.monotonic()
            try:
                page = fetch(post['detail_url'], page_action=wait_detail, timeout=FETCH_TIMEOUT_MS)
                text = _page_text(page)
                shell = '阅读数' in text and '最新评论' in text
                body = article_from_text(text, post) if shell else ''
                entry.update(status='ok' if body else ('no_detail_shell' if not shell else 'title_not_found'),
                             http_status=getattr(page, 'status', None), body=body)
            except Exception as exc:
                entry.update(status='fetch_error', error=f'{type(exc).__name__}: {exc}'[:300])
            entry['seconds'] = round(time.monotonic() - t0, 2)
            results[i] = entry
    finally:
        close()
    for i, post in enumerate(posts):
        if results[i] is None:
            results[i] = {'detail_url': post.get('detail_url'), 'author': post.get('author'),
                          'status': 'not_checked_post_limit'}
    return fetcher_name, results


def run(evidence_file: Path, out_file: Path, archive: Path,
        budget_seconds: int = 1800, max_posts: int = 0) -> dict:
    started = datetime.now(timezone.utc)
    data = json.loads(evidence_file.read_text(encoding='utf-8'))
    posts, issue = data.get('posts') or [], str(data.get('issue') or 'unknown')
    record = {'model': '49论坛模型', 'check': 'scrapling_side_by_side', 'issue': issue,
              'run_id': os.environ.get('GITHUB_RUN_ID'), 'started_at': started.isoformat(),
              'primary_posts': len(posts), 'feeds_scoring': False}
    try:
        import scrapling  # noqa: F401
        fetcher_name, fetched = fetch_bodies(posts, budget_seconds, max_posts)
        record['fetcher'] = fetcher_name
        record['fetch_settings'] = {'detail_wait_ms': DETAIL_WAIT_MS, 'fetch_timeout_ms': FETCH_TIMEOUT_MS,
                                    'max_posts': max_posts if max_posts and max_posts > 0 else None,
                                    'budget_seconds': budget_seconds}
        statuses = {}
        for f in fetched:
            statuses[f['status']] = statuses.get(f['status'], 0) + 1
        checked_posts, scrapling_posts, post_rows = [], [], []
        for post, f in zip(posts, fetched):
            row = {'author': post.get('author'), 'url': post.get('detail_url'), 'status': f['status'],
                   'seconds': f.get('seconds'), 'error': f.get('error')}
            if f['status'] == 'ok':
                row.update(text_agreement(post.get('body', ''), f['body']))
                if not row['identical']:
                    row['primary_excerpt'] = (post.get('body') or '')[:120]
                    row['scrapling_excerpt'] = f['body'][:120]
                checked_posts.append(post)
                scrapling_posts.append({**post, 'body': f['body']})
            post_rows.append(row)
        record.update(ok=True, fetch_status_counts=statuses,
                      checked_posts=len(checked_posts),
                      identical_text_posts=sum(1 for r in post_rows if r.get('identical')),
                      materials=compare(checked_posts, scrapling_posts),
                      posts=post_rows)
    except Exception as exc:
        record.update(ok=False, error=f'{type(exc).__name__}: {exc}'[:800])
    record['finished_at'] = datetime.now(timezone.utc).isoformat()
    out_file.parent.mkdir(parents=True, exist_ok=True)
    out_file.write_text(json.dumps(record, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    folder = archive / 'issues' / issue
    folder.mkdir(parents=True, exist_ok=True)
    target = folder / f"scrapling-{record['run_id'] or started.strftime('%Y%m%dT%H%M%S')}.json"
    if target.exists():
        raise SystemExit(f'refusing to overwrite {target}')
    target.write_text(json.dumps(record, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print('SCRAPLING_COMPARE ' + json.dumps({k: record.get(k) for k in (
        'ok', 'error', 'fetcher', 'primary_posts', 'checked_posts', 'identical_text_posts', 'fetch_status_counts')},
        ensure_ascii=False), flush=True)
    return record


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--evidence', type=Path, default=Path('forum49-out/forum49-evidence.json'))
    ap.add_argument('--out', type=Path, default=Path('forum49-out/scrapling-compare.json'))
    ap.add_argument('--archive', type=Path, default=Path('forum49'))
    ap.add_argument('--budget-seconds', type=int, default=1800)
    ap.add_argument('--max-posts', type=int, default=0, help='0 = no post limit; the time budget still applies')
    a = ap.parse_args(argv)
    run(a.evidence, a.out, a.archive, a.budget_seconds, a.max_posts)


if __name__ == '__main__':
    main()
