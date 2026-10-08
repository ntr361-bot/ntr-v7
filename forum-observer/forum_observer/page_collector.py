"""Collect evidence from the forum's rendered DOM when no public API exists."""
from __future__ import annotations

import json, os, re, shutil, subprocess
from collections import Counter
from urllib.parse import urljoin, urlparse
from zoneinfo import ZoneInfo
from datetime import timedelta
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
    result=[]
    for li in page.locator('li').all():
        try:
            if not li.locator('.name').count() or not li.locator('.slabel').count():continue
            kind=li.locator('.ntool .num').last.inner_text().strip() if li.locator('.ntool .num').count() else ''
            if category and kind!=category:continue
            def field(selector):
                q=li.locator(selector)
                return q.first.inner_text().strip() if q.count() else ''
            author=field('.name'); raw_issue=field('.slabel')
            if not author or not raw_issue:continue
            href=''
            for a in li.locator('a[href]').all():
                h=a.get_attribute('href') or ''
                if 'corpusdetail' in h:
                    href=urljoin(page.url,h);break
            obj={'author':author,'issue':_issue(raw_issue),'published_at':field('.time'),
                 'title':field('.formtitle'),'body':field('.text'),'category':kind,'href':href}
            obj['key']=(obj['author'],obj['issue'],obj['published_at'],obj['title'])
            result.append(obj)
        except Exception:
            continue
    return result


def _pages(page,category,max_pages=25):
    seen=set(); found=[]
    for i in range(max_pages):
        for x in _listing(page,category):
            if x['key'] not in seen:
                seen.add(x['key']);found.append(x)
        before=len(seen)
        page.evaluate('window.scrollTo(0,document.body.scrollHeight)')
        page.wait_for_timeout(700)
        for x in _listing(page,category):
            if x['key'] not in seen:
                seen.add(x['key']);found.append(x)
        if len(seen)>before:continue
        nexts=page.locator('button, a').filter(has_text='下一页')
        if not nexts.count():break
        try:
            nexts.last.click(timeout=1800);page.wait_for_timeout(700)
        except Exception:break
        fresh=_listing(page,category)
        if not any(x['key'] not in seen for x in fresh):break
    return found


def _detail(page,url,item,category,max_pages):
    # Never reuse an index across pages or after reloading the list.
    if item.get('href') and 'corpusdetail' in item['href']:
        page.goto(item['href'],wait_until='domcontentloaded',timeout=45000)
        page.wait_for_timeout(600)
        return 'corpusdetail' in page.url and (not item['title'] or item['title'] in page.locator('body').inner_text())
    page.goto(url,wait_until='domcontentloaded',timeout=45000)
    page.wait_for_timeout(800)
    previous=set()
    for _ in range(max_pages):
        for i,x in enumerate(_listing(page,category)):
            if x['key']==item['key']:
                # Locator nth index must be relative to all li's, not only eligible cards.
                # Find an exact matching card before clicking.
                cards=page.locator('li')
                for n in range(cards.count()):
                    li=cards.nth(n)
                    if (li.locator('.name').count() and li.locator('.slabel').count()
                            and li.locator('.name').first.inner_text().strip()==item['author']
                            and _issue(li.locator('.slabel').first.inner_text().strip())==item['issue']
                            and (not item['title'] or
                                 (li.locator('.formtitle').count() and li.locator('.formtitle').first.inner_text().strip()==item['title']))):
                        li.click(timeout=4000);page.wait_for_timeout(600)
                        return 'corpusdetail' in page.url and (not item['title'] or item['title'] in page.locator('body').inner_text())
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


def _images(page,max_images):
    # The site includes dozens of repeated navbar and advertisement images.
    # Only original article/comment uploads are relevant; dedupe URLs.
    raw=page.locator('img').evaluate_all(
        "els=>els.map(x=>x.getAttribute('data-src')||x.currentSrc||x.getAttribute('src')||'').filter(Boolean)")
    images=[];seen=set()
    for src in raw:
        link=urljoin(page.url,src)
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


def score_posts(posts):
    votes={z:0.0 for z in ZODIACS}
    picks=[];seen=set();voted=set()
    for post in posts:
        # Reply text is saved as evidence, but must not be attributed to OP.
        text=post['title']+' '+post['body']
        for pic in post.get('images',[]):
            if '/article/' in pic['src']:text+=' '+pic['ocr']
        for p in extract_picks(text):
            key=(post['author'],p['play'],tuple(p['picks']),p['mode'])
            if key in seen:continue
            seen.add(key)
            picks.append({**p,'author':post['author'],'source_url':post['detail_url']})
            ak=(post['author'],p['mode'])
            if ak in voted or p['mode']=='color_unverified':continue
            voted.add(ak)
            weight=(-1.0 if p['mode']=='exclude' else 1.0)/len(p['picks'])
            for z in p['picks']:votes[z]+=weight
    if not any(p['mode']!='color_unverified' for p in picks):return [],picks
    order=sorted(ZODIACS,key=lambda z:(-votes[z],ZODIACS.index(z)))
    return [{'rank':i+1,'zodiac':z,'score':round(votes[z],6)} for i,z in enumerate(order)],picks


def collect_page(url,out,target_issue=None,target_category=None,policy=None):
    from playwright.sync_api import sync_playwright
    policy=policy or {}
    if target_issue and not re.fullmatch(r'20\d{5}',str(target_issue)):
        raise ValueError('explicit target issue must use YYYYNNN')
    max_pages=max(1,min(int(policy.get('max_pages',30)),50))
    max_posts=max(1,min(int(policy.get('max_posts',70)),150))
    posts=[];errors=[]
    with sync_playwright() as pw:
        browser=pw.chromium.launch(headless=True,args=['--no-sandbox'])
        try:
            page=browser.new_page(viewport={'width':1280,'height':1800})
            page.goto(url,wait_until='domcontentloaded',timeout=60000)
            page.wait_for_timeout(1100)
            listing=_pages(page,target_category,max_pages)
            if not listing:raise ValueError('no forum posts or authors found')
            issue=target_issue or current_issue(listing)
            scoped=[item for item in listing if item['issue']==issue and item['author'] not in ('论坛管理','管理员')]
            if not scoped:raise ValueError('no matching posts for '+issue)
            truncated=len(scoped)>max_posts
            cache={}
            for item in scoped[:max_posts]:
                try:
                    if not _detail(page,url,item,target_category,max_pages):
                        errors.append({'author':item['author'],'title':item['title'],'reason':'detail not verified'})
                        continue
                    body=page.locator('body').inner_text()
                    imgs=[]
                    for src in _images(page,min(10,int(policy.get('max_images_per_post',5)))):
                        if src not in cache:cache[src]=_ocr_image(src)
                        imgs.append({'src':src,'ocr':cache[src],
                                     'ocr_status':'recognized' if cache[src] else 'unreadable_or_nontext'})
                    posts.append({**{k:v for k,v in item.items() if k not in ('key','href')},
                                  'comments':_comments(body),'images':imgs,'detail_url':page.url})
                except Exception as exc:
                    errors.append({'author':item['author'],'title':item['title'],'reason':str(exc)[:250]})
        finally:
            browser.close()
    ranking,predictions=score_posts(posts)
    candidates=set(policy.get('top_authors',[]))
    top_found=sorted({p['author'] for p in posts if p['author'] in candidates})
    other_found=sorted({p['author'] for p in posts if p['author'] not in candidates})
    valid_authors={p['author'] for p in predictions if p['mode']!='color_unverified'}
    enough=(len(top_found)>=int(policy.get('min_top_authors',10)) and
            len(other_found)>=int(policy.get('min_other_authors',5)) and
            len(valid_authors)>=int(policy.get('min_top_authors',10))+int(policy.get('min_other_authors',5)))
    ready=enough and bool(ranking) and not errors and not truncated
    result={'issue':issue,'status':'ready_observation' if ready else 'incomplete_observation',
            'source_url':url,'collector':'rendered-dom','fetched_at':datetime.now(timezone.utc).isoformat(),
            'raw_count':len(posts),'valid_leaderboard':0,'valid_outside':len(posts),
            'selected_count':len(valid_authors) if ready else 0,
            'selected_leaderboard':0,'selected_outside':0,
            'ranking':ranking,'top1':[r['zodiac'] for r in ranking[:1]],
            'top3':[r['zodiac'] for r in ranking[:3]],
            'top6':[r['zodiac'] for r in ranking[:6]],
            'evidence':posts,'parsed_predictions':predictions,'rejected':errors,
            'audit':{'listing_count':len(listing),'issue_listing_count':len(scoped),
                     'truncated':truncated,'detail_errors':errors,'issue_filter_strict':True},
            'progress':{'leaderboard_source':'historic_candidates_unverified',
                        'leaderboard_checked':0,'leaderboard_total':0,
                        'outside_checked':len(posts),'blocked':errors},
            'author_selection':{'top20_candidates':len(candidates),
                'top20_found':top_found,'other_found':other_found,
                'valid_prediction_authors':len(valid_authors),
                'comments_collected':sum(bool(p['comments']) for p in posts),
                'selection_status':'threshold_met' if ready else 'insufficient_or_unverified'},
            'note':'Experimental evidence only; candidate author list not verified live leaderboard.'}
    out.mkdir(parents=True,exist_ok=True)
    (out/'status.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    return result
