"""Collect evidence from the forum's rendered DOM when no public API exists."""
from __future__ import annotations

import json, os, re, shutil, subprocess, time
from collections import Counter
from urllib.parse import urljoin, urlparse
from zoneinfo import ZoneInfo
from datetime import timedelta
from datetime import datetime, timezone
from html.parser import HTMLParser
from pathlib import Path
from typing import Any
from .core import ZODIACS
from .author_history import incremental_scan, open_author_history, open_profile_post


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


def _ocr_image(src: str) -> str:
    """Best-effort OCR for public forum images; never turns OCR failure into data."""
    tesseract = shutil.which('tesseract')
    if not tesseract or not src or src.startswith('data:'):
        return ''
    try:
        import urllib.request
        image = urllib.request.urlopen(src, timeout=15).read()
        completed = subprocess.run([tesseract, 'stdin', 'stdout', '-l', 'eng+chi_sim'],
                                   input=image, capture_output=True, timeout=30, check=False)
        return completed.stdout.decode('utf-8','replace').strip() if completed.returncode == 0 else ''
    except Exception:
        return ''



LOCAL_TZ=ZoneInfo("Asia/Shanghai")
ZODIAC_CLASS=''.join(ZODIACS)
PICK_PATTERN=re.compile(r'(?P<play>(?:精选|推荐|主推|心水|看好|必出|重点|排除|杀)?(?:[一二三四五六七八九十\d]{1,2}肖|生肖|特肖|杀肖|红肖|蓝肖))\s*[:：=\-、【】]*\s*(?P<picks>(?:['+ZODIAC_CLASS+r'][\s、，,/|+.\-]*){1,12})')
HOSTS={'jmz.chunshengsh.com','x5k1pok.11852.com'}


def _issue(label):
    m=re.search(r'(?<!\d)20\d{5}(?!\d)',str(label))
    if m:return m.group(0)
    m=re.search(r'(?<!\d)(\d{3})(?!\d)',str(label))
    return str(datetime.now(LOCAL_TZ).year)+m.group(1) if m else ''


def _timestamp(value,now):
    v=str(value).strip()
    m=re.search(r'(20\d\d)[-/](\d{1,2})[-/](\d{1,2})\s+(\d{1,2}):(\d\d)',v)
    if m:
        try:return datetime(*map(int,m.groups()),tzinfo=LOCAL_TZ)
        except ValueError:return None
    if '刚刚' in v:return now
    m=re.search(r'(\d+)分钟前',v)
    if m:return now-timedelta(minutes=int(m.group(1)))
    m=re.search(r'(\d+)小时前',v)
    if m:return now-timedelta(hours=int(m.group(1)))
    return None


def current_issue(listing,now=None):
    now=now or datetime.now(LOCAL_TZ)
    recent={}
    for item in listing:
        if item.get('author') in ('论坛管理','管理员') or '公告' in item.get('title',''):
            continue
        period=_issue(item.get('issue',''))
        stamp=_timestamp(item.get('published_at'),now)
        if not period or not stamp or not period.startswith(str(now.year)):
            continue
        if not timedelta(minutes=-10)<=now-stamp<=timedelta(hours=72):
            continue
        recent.setdefault(period,[]).append((stamp,item.get('author','')))
    if not recent:raise ValueError('no recent issue posts; cannot use old announcement')
    # Require two distinct authors before treating a new period as confirmed.
    candidates={p:v for p,v in recent.items() if len({a for _,a in v})>=2}
    if not candidates:raise ValueError('no period with two independent recent authors')
    return max(candidates,key=lambda p:(max(x[0] for x in candidates[p]),int(p)))


def _listing(page,category):
    # Take one synchronous DOM snapshot. Per-card locators can detach while
    # this virtualized feed rerenders, each causing a 30-second auto-wait.
    rows=page.locator('.forum-list > li').evaluate_all(r"""cards => cards.map(li => {
        const value = selector => li.querySelector(selector)?.innerText?.trim() || '';
        const kinds = li.querySelectorAll('.ntool .num');
        return {author:value('.name'), issue:value('.slabel'),
            published_at:value('.time'), title:value('.formtitle'), body:value('.text'),
            category:kinds.length ? kinds[kinds.length-1].innerText.trim() : '',
            href:[...li.querySelectorAll('a[href]')].map(a=>a.href)
                .find(h=>h.includes('corpusdetail')) || ''};
    })""")
    result=[]
    for obj in rows:
        if not obj['author'] or not obj['issue']:continue
        if category and obj['category']!=category:continue
        obj['issue']=_issue(obj['issue'])
        obj['key']=(obj['author'],obj['issue'],obj['published_at'],obj['title'])
        result.append(obj)
    return result


def _pages(page,category,max_pages=25,deadline=None,on_progress=None):
    return incremental_scan(page,lambda: _listing(page,category),
                            max_steps=max(15,max_pages*4),delay_ms=650,
                            deadline=deadline,on_progress=on_progress)



def _detail(page,url,item,category,max_pages,deadline=None):
    # A profile's history card may have no href in this SPA.
    if item.get('profile_url') and not item.get('href'):
        if not open_profile_post(page,item,_issue,max_steps=max_pages):
            return False
        return _wait_detail(page,item,timeout_seconds=8)
    # Never reuse an index across pages or after reloading the list.
    if item.get('href') and 'corpusdetail' in item['href']:
        page.goto(item['href'],wait_until='domcontentloaded',timeout=45000)
        return _wait_detail(page,item,timeout_seconds=8)
    page.goto(url,wait_until='domcontentloaded',timeout=45000)
    page.wait_for_timeout(800)
    previous=set()
    for _ in range(max_pages):
        if deadline is not None and time.monotonic()>=deadline:return False
        visible=_listing(page,category)
        if any(x['key']==item['key'] for x in visible):
            cards=page.locator('.forum-list > li').filter(
                has=page.locator('.name').filter(has_text=re.compile('^'+re.escape(item['author'])+'$')))
            if item['title']:
                cards=cards.filter(has=page.locator('.formtitle').filter(
                    has_text=re.compile('^'+re.escape(item['title'])+'$')))
            # Click the actual title link; clicking the entire card may select
            # the avatar/profile route instead of the article.
            cards.first.locator('.formtitle').click(timeout=4000)
            return _wait_detail(page,item,timeout_seconds=8)
        current={x['key'] for x in _listing(page,category)}
        page.evaluate('window.scrollTo(0,document.body.scrollHeight)')
        page.wait_for_timeout(700)
        if any(x['key'] not in current for x in _listing(page,category)):continue
        nexts=page.locator('button, a').filter(has_text='下一页')
        if not nexts.count():break
        try:
            nexts.last.click(timeout=1600);page.wait_for_timeout(700)
        except Exception:break
        updated={x['key'] for x in _listing(page,category)}
        if not updated.difference(previous|current):break
        previous|=current
    return False


def _images(page,max_images,article_title=''):
    # The site includes dozens of repeated navbar and advertisement images.
    # Only original article/comment uploads are relevant; dedupe URLs.
    if not article_title:return []
    # An image must appear *after the article heading*, not in the previous
    # draw banner at the top of the page. Missing anchors mean no OCR evidence.
    try:
        heading=page.get_by_text(article_title,exact=True).last.bounding_box(timeout=2500)
    except Exception:
        return []
    if not heading:return []
    minimum=heading['y']+heading['height']
    comment_heading=page.get_by_text('最新评论',exact=True)
    max_y=float('inf')
    if comment_heading.count():
        try:
            bound=comment_heading.first.bounding_box(timeout=2500)
            if bound:max_y=bound['y']
        except Exception:pass
    raw=page.locator('img').evaluate_all(
        """els=>els.map(x=>{let r=x.getBoundingClientRect();
          return {src:x.getAttribute('data-src')||x.currentSrc||x.getAttribute('src')||'',
            y:r.top}}).filter(x=>x.src)""")
    images=[];seen=set()
    for image in raw:
        if not minimum<=image['y']<max_y:continue
        link=urljoin(page.url,image['src'])
        u=urlparse(link)
        if u.scheme!='https' or u.hostname not in HOSTS:continue
        if not re.search(r'/tk118files/(?:article|comment)/',u.path):continue
        if link not in seen:
            images.append(link);seen.add(link)
        if len(images)>=max_images:break
    return images


def _comments(text):
    if '最新评论' not in text:return ''
    c=text.split('最新评论',1)[1].split('想说点什么',1)[0].strip()
    return c[:12000] if re.search(r'\d+楼',c) else ''


def extract_picks(text):
    output=[]
    for m in PICK_PATTERN.finditer(str(text)):
        play=m.group('play')
        picks=list(dict.fromkeys(c for c in m.group('picks') if c in ZODIACS))
        if not picks:continue
        mode=('color_unverified' if '红肖' in play or '蓝肖' in play else
              'exclude' if '杀' in play or '排除' in play else 'include')
        output.append({'play':play,'picks':picks,'mode':mode,'excerpt':m.group(0)[:90]})
    return output


def _author_comments(text, author):
    """Only clearly credited OP replies can count as the OP's evidence."""
    if not text or not author:
        return ''
    parts=re.split(r'(?m)(?=^\d+\s*楼)',text)
    accepted=[]
    for part in parts:
        if re.match(r'^\d+\s*楼',part.strip()) and author in part[:100]:
            accepted.append(part[:3000])
    return '\n'.join(accepted)



# A post's issue is its publication period, not the period of every line inside
# its article. Previous-period "opened ... hit" rows in later posts are retrospective.
HISTORY_MARKER=re.compile(r'(?<!\d)(?:(?:20\d{2})(?P<full>\d{3})|(?P<short>\d{3}))\s*期\s*[:：]?')


def _period_marker(line,year):
    match=HISTORY_MARKER.search(str(line))
    if not match:return None
    return str(year)+(match.group('full') or match.group('short'))


def _title_span(text,title):
    """Match title exactly except for whitespace inserted by the forum UI."""
    text=str(text)
    title=str(title).strip()
    if not title:return None
    start=text.rfind(title)
    if start>=0:return (start,start+len(title))
    characters=[re.escape(ch) for ch in title if not ch.isspace()]
    if not characters:return None
    pattern=r'\s*'.join(characters)
    hits=list(re.finditer(pattern,text))
    return hits[-1].span() if hits else None


def _wait_detail(page,item,timeout_seconds=7):
    """Wait for a SPA post to render before rejecting an otherwise valid URL."""
    deadline=time.monotonic()+timeout_seconds
    while time.monotonic()<deadline:
        if 'corpusdetail' in page.url:
            try:
                text=page.locator('body').inner_text(timeout=2800)
                if _title_span(text,item.get('title','')) and item.get('author','') in text:
                    return True
            except Exception:
                pass
        page.wait_for_timeout(650)
    return False


def _article_body(page,item):
    """Bound article text by its *own title* and the comment boundary.

    The previous draw result in the page's header is not forum author evidence.
    """
    all_text=page.locator('body').inner_text()
    before_comments=all_text.split('最新评论',1)[0]
    span=_title_span(before_comments,item.get('title',''))
    if not span:return ''
    return before_comments[span[1]:][:50000].strip()


def current_period_text(text,issue):
    """Exclude earlier/later period history rows from this issue's voting."""
    year=str(issue)[:4]
    current=True
    seen_marker=False
    output=[]
    for line in str(text).splitlines():
        marker=_period_marker(line,year)
        if marker:
            current=marker==issue
            seen_marker=True
        if current:
            output.append(line)
    return '\n'.join(output)


def previous_period_records(text,requested_issue,post):
    """Return source-attributed retrospective snippets, never eligible votes.

    A 282 article may contain '281期: ... ←开:鸡10 准'. That statement was
    published after the 281 result, and must not be graded as a 281 forecast.
    """
    if not text or post.get('issue')==requested_issue:return []
    if not post.get('detail_url') or not post.get('author'):return []
    lines=str(text).splitlines()
    year=str(requested_issue)[:4]
    seen=set(); records=[]
    for index,line in enumerate(lines):
        if _period_marker(line,year)!=requested_issue:continue
        excerpt=line.strip()
        # A wrapped verdict such as "准/错" belongs to its preceding row.
        for extra in lines[index+1:index+3]:
            if _period_marker(extra,year):break
            if re.fullmatch(r'\s*(?:准|错|中|不中|命中|未中)\s*',extra):
                excerpt+=' '+extra.strip()
            else:
                break
        if not excerpt or excerpt in seen:continue
        seen.add(excerpt)
        records.append({
            'issue':requested_issue,
            'source_post_issue':post['issue'],
            'author':post['author'],
            'source_url':post['detail_url'],
            'post_published_at':post.get('published_at',''),
            'content':excerpt[:450],
            'source_section':'post_article',
            'classification':'retrospective_after_draw_not_prediction',
            'counted_for_ranking':False
        })
    return records


def score_posts(posts):
    votes={z:0.0 for z in ZODIACS}
    picks=[];seen=set();voted=set()
    for post in posts:
        # Reply text is saved as evidence, but must not be attributed to OP.
        body=current_period_text(post['body'],post['issue'])
        replies=current_period_text(_author_comments(post.get('comments',''),post['author']),post['issue'])
        text=post['title']+' '+body+' '+replies
        for pic in post.get('images',[]):
            if '/article/' in pic['src']:
                text+=' '+current_period_text(pic['ocr'],post['issue'])
        for p in extract_picks(text):
            key=(post['author'],p['play'],tuple(p['picks']),p['mode'])
            if key in seen:continue
            seen.add(key)
            ak=(post['author'],p['play'],p['mode'])
            eligible=[z for z in ZODIACS if z not in p['picks']] if p['mode']=='exclude' else p['picks']
            counted=ak not in voted and p['mode']!='color_unverified' and bool(eligible)
            picks.append({**p,'author':post['author'],'source_url':post['detail_url'],
                          'counted_for_ranking':counted})
            if not counted:continue
            voted.add(ak)
            weight=1.0/len(eligible)
            for z in eligible:votes[z]+=weight
    if not any(p['counted_for_ranking'] for p in picks):return [],picks
    order=sorted(ZODIACS,key=lambda z:(-votes[z],ZODIACS.index(z)))
    return [{'rank':i+1,'zodiac':z,'score':round(votes[z],6)} for i,z in enumerate(order)],picks


def collect_page(url,out,target_issue=None,target_category=None,policy=None):
    from playwright.sync_api import sync_playwright
    policy=policy or {}
    if target_issue and not re.fullmatch(r'20\d{5}',str(target_issue)):
        raise ValueError('explicit target issue must use YYYYNNN')
    max_pages=max(1,min(int(policy.get('max_pages',30)),50))
    max_posts=max(1,min(int(policy.get('max_posts',70)),150))
    posts=[];errors=[];historical_references=[]
    started=time.monotonic()
    deadline=started+max(60,int(policy.get('max_collection_seconds',1200)))
    out.mkdir(parents=True,exist_ok=True)
    issue=target_issue
    listing_candidates=[]
    def checkpoint(stage):
        ranking,predictions=score_posts(posts)
        progress={'issue':issue,'status':'incomplete_observation',
                  'stage':stage,'fetched_at':datetime.now(timezone.utc).isoformat(),
                  'raw_count':len(posts),'valid_materials':sum(x['counted_for_ranking'] for x in predictions),
                  'selected_count':0,'ranking':ranking,'evidence':posts,
                  'parsed_predictions':predictions,'rejected':errors,
                  'elapsed_seconds':round(time.monotonic()-started,1),
                  'listing_count':len(listing_candidates)}
        temp=out/'status.tmp'
        temp.write_text(json.dumps(progress,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
        temp.replace(out/'status.json')
        print(json.dumps({k:progress[k] for k in ('stage','raw_count','valid_materials','elapsed_seconds')},ensure_ascii=False),flush=True)
    checkpoint('starting')
    with sync_playwright() as pw:
        checkpoint('browser_launch')
        browser=pw.chromium.launch(headless=True,args=['--no-sandbox'],timeout=30000)
        try:
            page=browser.new_page(viewport={'width':1280,'height':1800})
            page.set_default_timeout(5000)
            page.on('requestfailed',lambda request: print(json.dumps({
                'event':'request_failed','host':urlparse(request.url).hostname,
                'path':urlparse(request.url).path,'reason':request.failure},
                ensure_ascii=False),flush=True))
            checkpoint('forum_navigation')
            page.goto(url,wait_until='domcontentloaded',timeout=60000)
            checkpoint('forum_render_wait')
            page.wait_for_timeout(1100)
            (out/'browser-diagnostics.json').write_text(json.dumps({
                'url':page.url,'title':page.title(),
                'body_preview':page.locator('body').inner_text(timeout=5000)[:2500]
            },ensure_ascii=False,indent=2),encoding='utf-8')
            def scan_progress(items,audit):
                nonlocal listing_candidates,issue
                listing_candidates=items
                if not target_issue:
                    try:issue=current_issue(items)
                    except ValueError:pass
                temp=out/'listing.tmp'
                temp.write_text(json.dumps({'items':items,'audit':audit},ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
                temp.replace(out/'listing.json')
                checkpoint('forum_scan: '+str(audit['steps']))
            checkpoint('forum_scan')
            listing,feed_audit=_pages(page,target_category,max_pages,
                deadline=min(deadline,time.monotonic()+180),on_progress=scan_progress)
            if not listing:raise ValueError('no forum posts or authors found')
            issue=target_issue or current_issue(listing)
            checkpoint('listing_complete')
            # Author profile history is the primary source for past issues.
            # Discover only profiles we can really open from on-screen author
            # cards/avatars. Never invent an author-ID URL.
            author_samples={}
            for item in listing:
                name=item.get('author','').strip()
                if name and name not in ('论坛管理','管理员'):
                    author_samples.setdefault(name,item)
            favored=set(policy.get('top_authors',[]))
            author_order=sorted(author_samples.values(),key=lambda x:(
                0 if x['issue']==issue else 1,
                0 if x['author'] in favored else 1,
                x['author']))
            author_limit=max(1,min(60,int(policy.get('max_profile_authors',30))))
            profile_steps=max(1,min(80,int(policy.get('profile_scroll_steps',32))))
            profile_posts=[];profile_checks=[];profile_errors=[]
            # Daily operation only needs CURRENT posts from the live forum.
            # An explicit historical audit (e.g. 281) opens author histories,
            # and only as far back as that issue. Never sweep all past issues.
            profile_samples=author_order[:author_limit] if target_issue else []
            for sample in profile_samples:
                if time.monotonic()>=deadline:
                    profile_errors.append({'reason':'collection time budget exhausted'})
                    break
                checkpoint('profile: '+sample['author'])
                try:
                    found,audit=open_author_history(
                        page,url,sample,_issue,max_steps=profile_steps,delay_ms=650,
                        target_issue=issue)
                    profile_checks.append(audit)
                    if audit.get('error'):
                        profile_errors.append(audit)
                        continue
                    for item in found:
                        # The author name was verified on their own profile.
                        if item['author']!=sample['author']:
                            raise ValueError('profile author mismatch')
                    profile_posts.extend(found)
                except Exception as exc:
                    profile_errors.append({'author':sample['author'],
                                           'reason':str(exc)[:240]})
            found_keys={x['key'] for x in listing}
            for item in profile_posts:
                if item['key'] not in found_keys:
                    listing.append(item)
                    found_keys.add(item['key'])
            scoped=[item for item in listing if item['issue']==issue and item['author'] not in ('论坛管理','管理员')]
            # A completed historical issue may no longer have any original
            # listing cards. Search next-period posts for explicitly labeled
            # prior-period rows, but keep those rows separate from predictions.
            periods=sorted({x['issue'] for x in listing if x['issue']})
            next_issue=str(int(issue)+1)
            retrospective=(target_issue is not None and next_issue in periods)
            next_period=[item for item in listing if retrospective
                         and item['issue']==next_issue
                         and item['author'] not in ('论坛管理','管理员')]
            targets=scoped+[x for x in next_period if x['key'] not in {p['key'] for p in scoped}]
            if not targets:raise ValueError('no current or next-period posts for '+issue)
            truncated=(len(targets)>max_posts or feed_audit.get('truncated',False)
                       or (bool(target_issue) and len(author_samples)>author_limit)
                       or any(x.get('truncated') for x in profile_checks))
            # Report missing author profiles as explicit incomplete coverage;
            # do not pretend browsing the visible forum card is exhaustive.
            profile_incomplete=(bool(target_issue) and
                                (bool(profile_errors) or len(author_samples)>author_limit))
            cache={}
            for item in targets[:max_posts]:
                if time.monotonic()>=deadline:
                    truncated=True
                    errors.append({'reason':'collection time budget exhausted; remaining posts not checked'})
                    break
                checkpoint('detail: '+item['author']+' '+item['title'])
                try:
                    if not _detail(page,url,item,target_category,max_pages,deadline=deadline):
                        errors.append({'author':item['author'],'title':item['title'],'reason':'detail not verified'})
                        continue
                    body=page.locator('body').inner_text()
                    article=_article_body(page,item)
                    imgs=[]
                    for src in _images(page,min(10,int(policy.get('max_images_per_post',5))),item['title']):
                        if time.monotonic()>=deadline:
                            truncated=True
                            errors.append({'reason':'collection time budget exhausted during OCR','author':item['author']})
                            break
                        if src not in cache:cache[src]=_ocr_image(src)
                        imgs.append({'src':src,'ocr':cache[src],
                                     'ocr_status':'recognized' if cache[src] else 'unreadable_or_nontext'})
                    item_post={**{k:v for k,v in item.items() if k not in ('key','href')},
                               'body':article,'comments':_comments(body),
                               'images':imgs,'detail_url':page.url}
                    if item['issue']==issue:
                        posts.append(item_post)
                    else:
                        # Only the article's own words/images; NEVER body header,
                        # public result tiles, or other authors' comments.
                        historical_references.extend(previous_period_records(
                            article,issue,item_post))
                        for pic in imgs:
                            if '/article/' in pic['src']:
                                historical_references.extend(previous_period_records(
                                    pic['ocr'],issue,item_post))
                except Exception as exc:
                    errors.append({'author':item['author'],'title':item['title'],'reason':str(exc)[:250]})
                    checkpoint('detail_complete')
        finally:
            checkpoint('browser_closing')
            browser.close()
    # Deduplicate repeated author + original article quote (including OCR).
    deduped=[];history_seen=set()
    for record in historical_references:
        key=(record['author'],record['source_post_issue'],record['content'])
        if key not in history_seen:
            history_seen.add(key);deduped.append(record)
    historical_references=deduped
    ranking,predictions=score_posts(posts)
    candidates=set(policy.get('top_authors',[]))
    top_found=sorted({p['author'] for p in posts if p['author'] in candidates})
    other_found=sorted({p['author'] for p in posts if p['author'] not in candidates})
    valid_votes=[p for p in predictions if p['counted_for_ranking']]
    valid_authors={p['author'] for p in valid_votes}
    valid_materials=len(valid_votes)
    rejected_materials=len(predictions)-valid_materials
    unscored_posts=sum(1 for p in posts if p['detail_url'] not in {v['source_url'] for v in valid_votes})
    enough=valid_materials>=int(policy.get('min_valid_materials',25))
    ready=enough and bool(ranking) and not errors and not truncated and not profile_incomplete
    result={'issue':issue,'status':'ready_observation' if ready else 'incomplete_observation',
            'source_url':url,'collector':'rendered-dom','fetched_at':datetime.now(timezone.utc).isoformat(),
            'raw_count':len(posts),'valid_leaderboard':0,'valid_outside':len(posts),
            'author_profiles_checked':len(profile_checks),
            'history_scope':'target_issue_and_next_issue' if target_issue else 'not_requested',
            'all_author_history_scraped':False,
            'author_profiles_found':sum(1 for x in profile_checks if not x.get('error')),
            'author_profile_history_posts':len(profile_posts),
            'author_profile_errors':profile_errors,
            'forum_scroll_audit':feed_audit,
            'historical_references':historical_references,
            'historical_reference_count':len(historical_references),
            'historical_reference_authors':len({r['author'] for r in historical_references}),
            'next_period_posts_checked':len(next_period),
            'selected_count':valid_materials if ready else 0,
            'valid_materials':valid_materials,
            'candidate_materials':len(predictions),
            'excluded_materials':rejected_materials,
            'unscored_posts':unscored_posts,
            'distinct_valid_authors':len(valid_authors),
            'selected_leaderboard':0,'selected_outside':0,
            'ranking':ranking,'top1':[r['zodiac'] for r in ranking[:1]],
            'top3':[r['zodiac'] for r in ranking[:3]],
            'top6':[r['zodiac'] for r in ranking[:6]],
            'evidence':posts,'parsed_predictions':predictions,'rejected':errors,
            'audit':{'listing_count':len(listing),'issue_listing_count':len(scoped),
                     'forum_scroll':feed_audit,
                     'profile_checks':profile_checks,
                     'profile_errors':profile_errors,
                     'author_profiles_discovered':len(author_samples),
                     'author_profiles_checked':len(profile_checks),
                     'author_history_posts_found':len(profile_posts),
                     'next_period_posts_checked':len(next_period),
                     'historical_reference_count':len(historical_references),
                     'excluded_draw_header':True,
                     'post_article_only':True,
                     'truncated':truncated,'detail_errors':errors,'issue_filter_strict':True},
            'progress':{'leaderboard_source':'historic_candidates_unverified',
                        'leaderboard_checked':0,'leaderboard_total':0,
                        'outside_checked':len(posts),'blocked':errors},
            'author_selection':{'top20_candidates':len(candidates),
                'top20_found':top_found,'other_found':other_found,
                'valid_prediction_authors':len(valid_authors),
                'valid_materials':valid_materials,
                'min_valid_materials':int(policy.get('min_valid_materials',25)),
                'comments_collected':sum(bool(p['comments']) for p in posts),
                'selection_status':'threshold_met' if ready else 'insufficient_or_unverified'},
            'note':('Forum feed and author pages are progressively scrolled to load cards. '
                    'Missing author profiles or incomplete scrolling are reported. '
                    'Retrospective references in next-period posts are not pre-draw forecasts; ' 
                    'page top draw results are excluded from scoring. '
                    'Experimental evidence only; no verified live leaderboard.')}
    out.mkdir(parents=True,exist_ok=True)
    (out/'status.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    return result

