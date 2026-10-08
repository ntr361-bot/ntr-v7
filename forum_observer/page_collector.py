"""Collect evidence from the forum's rendered DOM when no public API exists."""
from __future__ import annotations

import json, os, re, shutil, subprocess
from datetime import datetime, timezone
from html.parser import HTMLParser
from pathlib import Path
from typing import Any
from .core import ZODIACS


class _PostParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.posts: list[dict[str, Any]] = []
        self.current: dict[str, Any] | None = None
        self.active: list[str | None] = []
        self.field: str | None = None

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        amap = dict(attrs)
        classes = set((amap.get('class') or '').split())
        self.active.append(self.field)
        if tag == 'li' and self.current is None:
            self.current = {'author':'','published_at':'','issue':'','category':'','title':'','body':'','images':[]}
        if self.current is None:
            return
        if 'name' in classes: self.field = 'author'
        elif 'time' in classes: self.field = 'published_at'
        elif 'slabel' in classes: self.field = 'issue'
        elif 'ntool' in classes: self.field = 'category'
        elif 'formtitle' in classes: self.field = 'title'
        elif 'text' in classes: self.field = 'body'
        if tag == 'img':
            src = amap.get('data-src') or amap.get('src')
            if src and not src.startswith('data:'): self.current['images'].append(src)

    def handle_endtag(self, tag: str) -> None:
        if tag == 'li' and self.current is not None:
            if self.current['author'] and self.current['published_at'] and self.current['issue']:
                self.posts.append(self.current)
            self.current = None; self.field = None; self.active.clear(); return
        if self.active: self.field = self.active.pop()

    def handle_data(self, data: str) -> None:
        if self.current is not None and data.strip() in {'澳门','香港','澳彩','港彩','体彩'}:
            self.current['category'] = data.strip()
        if self.current is not None and self.field in {'author','published_at','issue','title','body'}:
            self.current[self.field] += data


def _chrome() -> str:
    configured = os.environ.get('FORUM_CHROME_BIN')
    if configured: return configured
    for name in ('google-chrome','chromium','chromium-browser'):
        found = shutil.which(name)
        if found: return found
    raise RuntimeError('no Chromium-compatible browser found; set FORUM_CHROME_BIN')


def rendered_html(url: str, timeout: int = 60) -> str:
    command = [_chrome(),'--headless=new','--no-sandbox','--disable-gpu',
               '--disable-dev-shm-usage','--dump-dom','--virtual-time-budget=12000',url]
    completed = subprocess.run(command,capture_output=True,timeout=timeout,check=False)
    if completed.returncode != 0:
        raise RuntimeError((completed.stderr or completed.stdout).decode('utf-8','replace')[-2000:])
    return completed.stdout.decode('utf-8','replace')


def _issue(value: str) -> str:
    text = value.strip()
    # The forum renders the issue label as either "281", "第281期", or
    # occasionally the full "2026281" form.
    match = re.search(r'(?<!\d)(\d{3})(?!\d)', text)
    if match:
        return '2026' + match.group(1)
    full = re.search(r'20\d{5}', text)
    return full.group(0) if full else text


def collect_page(url: str, out: Path, target_issue: str | None = None,
                 target_category: str | None = None,
                 policy: dict[str, Any] | None = None) -> dict[str, Any]:
    parser = _PostParser(); parser.feed(rendered_html(url)); posts = []
    parsed_posts = parser.posts[:]
    for index, raw in enumerate(parser.posts):
        issue = _issue(raw['issue'])
        if target_issue and issue != target_issue: continue
        if target_category and raw.get('category','').strip() != target_category: continue
        title = ' '.join(raw['title'].split()); body = ' '.join(raw['body'].split())
        text = f'{title} {body}'
        counts = {z: len(re.findall(re.escape(z), text)) for z in ZODIACS}
        posts.append({**raw,'issue':issue,'title':title,'body':body,
                      'source_url':f'{url}#post-{index}','zodiac_mentions':counts})
    filter_fallback = False
    if target_issue and not posts and parsed_posts:
        # A stale/malformed issue label should not discard an otherwise useful
        # rendered-page observation. Keep the evidence and explain the scope.
        filter_fallback = True
        for index, raw in enumerate(parsed_posts):
            issue = _issue(raw['issue'])
            title = ' '.join(raw['title'].split()); body = ' '.join(raw['body'].split())
            text = f'{title} {body}'
            counts = {z: len(re.findall(re.escape(z), text)) for z in ZODIACS}
            posts.append({**raw,'issue':issue,'title':title,'body':body,
                          'source_url':f'{url}#post-{index}','zodiac_mentions':counts})
    totals = {z: sum(p['zodiac_mentions'][z] for p in posts) for z in ZODIACS}
    ranking = sorted(ZODIACS,key=lambda z:(-totals[z],ZODIACS.index(z))) if posts else []
    policy = policy or {}
    top_authors = set(policy.get('top_authors', []))
    author_posts: dict[str, list[dict[str, Any]]] = {}
    for post in posts:
        author_posts.setdefault(post['author'].strip(), []).append(post)
    top_found = sorted(a for a in author_posts if a in top_authors)
    other_found = sorted(a for a in author_posts if a not in top_authors)
    author_scores = []
    for author, author_items in author_posts.items():
        score = {z: sum(p['zodiac_mentions'][z] for p in author_items) for z in ZODIACS}
        author_scores.append({'author':author,'post_count':len(author_items),
                              'zodiac_mentions':score,
                              'materials':[p['source_url'] for p in author_items]})
    author_scores.sort(key=lambda x:(-x['post_count'], x['author']))
    result = {'issue':target_issue or (posts[0]['issue'] if posts else 'unknown'),
              'status':'html_observation' if posts else 'no_posts_found','source_url':url,
              'collector':'rendered-dom','fetched_at':datetime.now(timezone.utc).isoformat(),
              'raw_count':len(posts),'valid_leaderboard':0,'valid_outside':len(posts),'rejected':[],
              'selected_count':0,'selected_leaderboard':0,'selected_outside':0,
              'ranking':[{'rank':i+1,'zodiac':z,'score':totals[z]} for i,z in enumerate(ranking)],
              'top1':ranking[:1],'top3':ranking[:3],'top6':ranking[:6],'evidence':posts,
              'progress':{'leaderboard_source':'not_available','leaderboard_checked':0,
                          'leaderboard_total':0,'outside_checked':len(posts),'blocked':[]},
              'note':('Rendered-page observation only; requested issue/category had no exact label, '
                      'so all parsed posts were retained. No verified leaderboard or freeze was created.'
                      if filter_fallback else
                      'Rendered-page observation only; no verified leaderboard or freeze was created.'),
              'parsed_count':len(parsed_posts),'issue_filter_fallback':filter_fallback}
    result['author_selection'] = {
        'top20_candidates':len(top_authors), 'top20_found':top_found,
        'other_found':other_found,
        'min_top_authors':policy.get('min_top_authors',10),
        'min_other_authors':policy.get('min_other_authors',5),
        'comments_requested':bool(policy.get('collect_comments',False)),
        'comments_collected':False,
        'author_scores':author_scores,
        'selection_status':'insufficient_comments_pending' if policy.get('collect_comments') else 'list_observation'
    }
    out.mkdir(parents=True,exist_ok=True)
    (out/'status.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    return result

