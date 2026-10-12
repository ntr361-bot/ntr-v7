"""Collect evidence from the forum's rendered DOM when no public API exists."""
from __future__ import annotations

import hashlib, json, os, re, shutil, subprocess, time
from collections import Counter
from urllib.parse import urljoin, urlparse
from zoneinfo import ZoneInfo
from datetime import timedelta
from datetime import datetime, timezone
from html.parser import HTMLParser
from pathlib import Path
from typing import Any
from .core import ZODIACS, vote_zodiacs, material_counts, SCORING_RULE_VERSION, SCORING_RULES
from .author_history import SCROLL_JS, incremental_scan, open_author_history, open_profile_post
from .comment_images import eligible_comment_image
from .collection_policy import choose_profile_samples, choose_comment_threads


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


def _ocr_image(src: str, psm=None) -> str:
    """Best-effort OCR for public forum images; never turns OCR failure into data."""
    tesseract = shutil.which('tesseract')
    if not tesseract or not src or src.startswith('data:'):
        return ''
    try:
        import urllib.request
        image = urllib.request.urlopen(src, timeout=15).read()
        command=[tesseract, 'stdin', 'stdout', '-l', 'eng+chi_sim']
        if psm is not None:command+=['--psm',str(psm)]
        completed = subprocess.run(command,
                                   input=image, capture_output=True, timeout=30, check=False)
        return completed.stdout.decode('utf-8','replace').strip() if completed.returncode == 0 else ''
    except Exception:
        return ''



LOCAL_TZ=ZoneInfo("Asia/Shanghai")
ZODIAC_CLASS=''.join(ZODIACS)
PICK_PATTERN=re.compile(r'(?P<play>(?:绝)?杀[一二三四五六七八九十\d]{1,2}(?:码|号码)|(?:精选|推荐|主推|心水|看好|必出|重点|排除|杀)?(?:[一二三四五六七八九十\d]{1,2}肖|生肖|特肖|杀肖|红肖|蓝肖))\s*[:：=\-、【】]*\s*(?P<picks>(?:['+ZODIAC_CLASS+r'][\s、，,/|+.\-]*){1,12})')
UNCERTAIN_PATTERN=re.compile(r'不确定|未确定|不明确|待确认|待核实|可能|或许|疑似|是否|不知|看不清|仅供猜测|不是|并非|不杀|不推荐|取消|作废|尽中尽|固定杀肖|混合杀肖|杀肖次数')
BARE_KILL_PATTERN=re.compile(r'(?<![\u4e00-\u9fff])(?P<play>杀)\s*[:：]?\s*(?P<picks>['+ZODIAC_CLASS+r']+)')
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
        // Only the separate orange "精" badge beside the issue; never post text.
        const periodBadge = li.querySelector('.slabel');
        const badgeRow = periodBadge?.parentElement;
        const isFeatured = !!badgeRow && [...badgeRow.querySelectorAll('*')].some(el =>
            el !== periodBadge && !periodBadge.contains(el) &&
            el.children.length === 0 && (el.textContent || '').trim() === '精' &&
            !el.closest('.formtitle, .text, .name, .comment-item, .user-info'));
        return {author:value('.name'), issue:value('.slabel'), is_featured:isFeatured,
            published_at:value('.time'), title:value('.formtitle'), body:value('.text'),
            category:kinds.length ? kinds[kinds.length-1].innerText.trim() : '',
            image_urls:[...li.querySelectorAll('.text img')].map(img=>img.getAttribute('data-src')||img.src).filter(Boolean),
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


def _pages(page,category,max_pages=25,deadline=None,on_progress=None,stop_predicate=None):
    return incremental_scan(page,lambda: _listing(page,category),
                            max_steps=max(15,max_pages*4),delay_ms=650,
                            deadline=deadline,on_progress=on_progress,stop_predicate=stop_predicate)



def current_feed_boundary(items,target_issue=None):
    recent=[x for x in items if x.get('author') not in ('论坛管理','管理员') and '公告' not in x.get('title','')]
    if not recent:return None
    try:issue=target_issue or current_issue(recent)
    except ValueError:return None
    tail=recent[-5:]
    if (len(tail)==5 and len({x['author'] for x in tail})>=2
        and all(str(x.get('issue','')).isdigit() and int(x['issue'])<int(issue)
                and _timestamp(x.get('published_at'),datetime.now(LOCAL_TZ)) for x in tail)):
        return 'current_issue_boundary_reached'
    return None


def feed_card_post(item, forum_url, now=None):
    """Use only card-owned DOM text; never the header or neighboring cards."""
    now=now or datetime.now(LOCAL_TZ)
    stamp=_timestamp(item.get('published_at'),now)
    if not stamp or stamp>now+timedelta(minutes=1):
        raise ValueError('card publication time is not verifiable')
    identity=hashlib.sha256(json.dumps([item.get(k,'') for k in
        ('author','issue','published_at','title')],ensure_ascii=False).encode()).hexdigest()[:20]
    return {**{k:v for k,v in item.items() if k not in ('key','href')},
        'published_at':stamp.isoformat(),'body':item.get('body',''),
        'images':[{'src':src,'ocr':'','ocr_status':'not_read_from_card'}
                  for src in item.get('image_urls',[])],
        'comments':'','detail_url':item.get('href') or forum_url,
        'evidence_id':identity,'evidence_scope':'current_feed_card',
        'comments_coverage':'not_expanded','detail_coverage':'not_opened'}


def _detail(page,url,item,category,max_pages,deadline=None):
    if deadline is not None and time.monotonic() >= deadline:return False
    def remaining_ms(default):
        return default if deadline is None else max(1,min(default,int((deadline-time.monotonic())*1000)))
    def wait_detail():
        seconds=8 if deadline is None else min(8,max(0,deadline-time.monotonic()))
        return _wait_detail(page,item,timeout_seconds=seconds)
    # A profile's history card may have no href in this SPA.
    if item.get('profile_url') and not item.get('href'):
        if not open_profile_post(page,item,_issue,max_steps=max_pages):
            return False
        return wait_detail()
    # Never reuse an index across pages or after reloading the list.
    if item.get('href') and 'corpusdetail' in item['href']:
        page.goto(item['href'],wait_until='domcontentloaded',timeout=remaining_ms(45000))
        return wait_detail()
    page.goto(url,wait_until='domcontentloaded',timeout=remaining_ms(45000))
    page.get_by_role('tab',name='澳彩',exact=True).wait_for(state='visible',timeout=remaining_ms(15000))
    page.get_by_role('tab',name='澳彩',exact=True).click()
    page.get_by_text('最新',exact=True).click()
    page.wait_for_timeout(800)
    previous=set()
    for _ in range(max_pages*4):
        if deadline is not None and time.monotonic()>=deadline:return False
        visible=_listing(page,category)
        if any(x['key']==item['key'] for x in visible):
            # The feed is virtualized: a locator may detach between a DOM
            # snapshot and Playwright's auto-wait click. Find and click the
            # exact visible article title in one synchronous DOM operation.
            opened=page.locator('.forum-list > li').evaluate_all(r"""(cards, target) => {
                for (const li of cards) {
                    const get = css => li.querySelector(css)?.innerText?.trim() || '';
                    const label = get('.slabel');
                    if (get('.name') !== target.author ||
                        get('.formtitle') !== target.title ||
                        !label.includes(target.issue.slice(-3))) continue;
                    const title=li.querySelector('.formtitle');
                    if (title) {title.click(); return true;}
                }
                return false;
            }""", {'author':item['author'],'title':item['title'],'issue':item['issue']})
            if opened and wait_detail():
                return True
        current={x['key'] for x in visible}
        # This SPA uses a nested scroll container; window.scrollTo leaves
        # the virtualized feed in place and makes every detail lookup fail.
        move=page.evaluate(SCROLL_JS)
        page.wait_for_timeout(650)
        if any(x['key'] not in current for x in _listing(page,category)):continue
        if not move.get('at_end'):continue
        nexts=page.locator('button, a').filter(has_text=re.compile(r'下一页|下页|加载更多'))
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


FRONT_BACK_ZODIACS={'前肖':tuple('鼠牛虎兔龙蛇'),'后肖':tuple('马羊猴鸡狗猪')}
GROUP_DEFINITION=re.compile(r'(?P<group>前肖|后肖)\s*[:：=]?\s*[（(【\[](?P<picks>[^）)】\]\n]+)[）)】\]]')
EIGHT_EXPRESSION=re.compile(
    r'(?P<group>前肖|后肖)\s*[+＋]\s*(?P<extra>(?:['+ZODIAC_CLASS+r'][\s、，,]*){2})(?=\s*[】\]）)])')


def extract_front_back_eight(text):
    """Front/back six plus two distinct outside zodiacs is one eight-zodiac vote.

    Require explicit consistent definitions in this evidence. Do not infer a
    missing/garbled legend, reinterpret kills, or count draw-result placeholders.
    """
    text=re.sub(r'(前|后)六肖',r'\1肖',str(text))
    text=re.sub(r'(前肖|后肖)[ \t]*[:：=][ \t]*((?:['+ZODIAC_CLASS+r'][ \t、，,]*){6})(?!['+ZODIAC_CLASS+r'])',r'\1(\2)',text)
    definitions={}; invalid=False; definition_evidence=[]
    for m in GROUP_DEFINITION.finditer(text):
        group=m.group('group')
        content=m.group('picks')
        if re.search(r'前肖|后肖|[+＋]',content):continue
        chars=[c for c in content if c in ZODIACS]
        if (re.sub('['+ZODIAC_CLASS+r'\s、，,]','',content)
                or len(chars)!=6 or set(chars)!=set(FRONT_BACK_ZODIACS[group])):
            invalid=True
        definitions[group]=chars
        definition_evidence.append(m.group(0))
    output=[]
    for line in text.splitlines():
        if not re.search(r'前后二肖|前后六肖|前后肖|前后生肖|八肖',line):continue
        for match in EIGHT_EXPRESSION.finditer(line):
            group=match.group('group')
            extra=[c for c in match.group('extra') if c in ZODIACS]
            before=line[:match.start()]
            error=None
            certainty_line=re.sub(r'开\s*[:：]?\s*[《【(（]?\s*(?:[?？]+\s*(?:00)?|00)\s*[》】)）]?\s*(?:准|中|对)?\s*$', '',line)
            if UNCERTAIN_PATTERN.search(certainty_line) or re.search(r'杀|排除|[?？]',before):
                error='ambiguous or unsupported front/back direction'
            elif invalid or set(definitions)!=set(FRONT_BACK_ZODIACS):
                error='missing or inconsistent front/back definitions'
            elif len(set(extra))!=2 or set(extra)&set(FRONT_BACK_ZODIACS[group]):
                error='front/back plus two must yield 8 distinct zodiacs'
            picks=list(dict.fromkeys([*definitions.get(group,[]),*extra]))
            output.append({'play':'八肖（前后二肖）','picks':picks,
                'mode':'unverified' if error else 'include',
                'excerpt':line[:450],'raw_expression':match.group(0),
                'definition_evidence':definition_evidence,'parse_error':error})
    return output


def extract_picks(text):
    """Only explicit supported plays; ambiguity never becomes a vote."""
    output=extract_front_back_eight(str(text))
    for line in str(text).splitlines():
        certainty_line=re.sub(r'开\s*[:：]?\s*[《【(（]?\s*(?:[?？]+\s*(?:00)?|00)\s*[》】)）]?\s*$', '',line)
        matches=list(PICK_PATTERN.finditer(line))+list(BARE_KILL_PATTERN.finditer(line))
        for m in sorted(matches,key=lambda m:m.start()):
            # Front/back group definitions are legends, not six-zodiac tips.
            if m.start() and line[m.start()-1] in '前后':continue
            play=m.group('play')
            picks=list(dict.fromkeys(c for c in m.group('picks') if c in ZODIACS))
            if not picks:continue
            mode=('unverified' if UNCERTAIN_PATTERN.search(certainty_line) or '?' in certainty_line or '？' in certainty_line else
                  'color_unverified' if '红肖' in play or '蓝肖' in play else
                  'exclude' if '杀' in play or '排除' in play else 'include')
            output.append({'play':play,'picks':picks,'mode':mode,'excerpt':line[:450]})
    return output


def consistent_comment_ocr(first,second,issue):
    """Both OCR layouts must independently yield the same valid current plays."""
    def signature(text):
        if not any(str(issue)[:4]+(m.group('full') or m.group('short'))==str(issue)
                   for m in HISTORY_MARKER.finditer(str(text))):return []
        rows=extract_picks(current_period_text(text,issue))
        accepted=[]
        for row in rows:
            if row.get('parse_error'):return []
            if '肖' not in row['play']:return []
            try:vote_zodiacs(row['picks'],row['mode'],row['play'],row['excerpt'])
            except ValueError:return []
            accepted.append((row['play'],row['mode'],tuple(sorted(row['picks']))))
        return sorted(set(accepted))
    a=signature(first);b=signature(second)
    return first if a and a==b else ''


def prioritize_posts(items, favored):
    """Featured first; every accepted evidence row still has the same vote weight."""
    return sorted(items,key=lambda item:(
        item.get('is_featured') is not True, item['author'] not in favored))


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
HISTORY_MARKER=re.compile(r'(?:(?<!\d)(?:20\d{2})(?P<full>\d{3})|(?P<short>\d{3})(?:(?=\s*期)|(?<!\d\d\d\d)))\s*(?:期\s*[:：]?|[:：]|[。．.]\s*(?=杀)|(?=杀))')


def period_lines(text):
    """DOM cards can collapse all historical rows into a single line."""
    value=str(text)
    starts={m.start() for m in HISTORY_MARKER.finditer(value)}
    return ''.join(('\n' if i in starts and i else '')+ch
                   for i,ch in enumerate(value)).splitlines()


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
    for line in period_lines(text):
        if re.search(r'上\s*(?:一\s*)?期|上一回|上回|往期',line):continue
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
    lines=period_lines(text)
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
        text=current_period_text(post['title'],post['issue'])+'\n'+body+'\n'+replies
        for pic in post.get('images',[]):
            if (('/article/' in pic['src'] and pic.get('ocr_status')=='recognized_consistent' and pic.get('period_verified') is True)
                    or eligible_comment_image(pic,post)):
                text+='\n'+current_period_text(pic['ocr'],post['issue'])
        parsed=extract_picks(text)
        for p in parsed:
            key=(post['author'],p['play'],tuple(p['picks']),p['mode'])
            if key in seen:continue
            seen.add(key)
            ak=(post['author'],p['play'],p['mode'])
            reason=p.get('parse_error')
            if UNCERTAIN_PATTERN.search(post['title']):
                p['mode']='unverified'
            try:
                if reason:raise ValueError(reason)
                eligible=vote_zodiacs(p['picks'],p['mode'],p['play'],p['excerpt'])
            except ValueError as error:
                eligible=[]
                reason=str(error)
            if not reason and ak in voted:
                reason='duplicate author/play'
            counted=reason is None
            picks.append({**p,'author':post['author'],'source_url':post['detail_url'],'evidence_id':post.get('evidence_id',post['detail_url']),
                          'counted_for_ranking':counted,'exclusion_reason':reason,
                          'vote_zodiacs':eligible if counted else [],
                          'weight_per_zodiac':1.0/len(eligible) if counted else 0.0})
            if not counted:continue
            voted.add(ak)
            weight=1.0/len(eligible)
            for z in eligible:votes[z]+=weight
    if not any(p['counted_for_ranking'] for p in picks):return [],picks
    order=sorted(ZODIACS,key=lambda z:(-votes[z],ZODIACS.index(z)))
    return [{'rank':i+1,'zodiac':z,'score':round(votes[z],6)} for i,z in enumerate(order)],picks


def unparsed_evidence(posts):
    rejected=[]
    for post in posts:
        text=current_period_text(post['body'],post['issue'])
        text+='\n'+current_period_text(_author_comments(post.get('comments',''),post['author']),post['issue'])
        for pic in post.get('images',[]):
            if (('/article/' in pic['src'] and pic.get('ocr_status')=='recognized_consistent' and pic.get('period_verified') is True)
                    or eligible_comment_image(pic,post)):
                text+='\n'+current_period_text(pic.get('ocr',''),post['issue'])
        parsed_lines={p['excerpt'] for p in extract_picks(post['title']+'\n'+text)}
        found=False
        for line in text.splitlines():
            if any(z in line for z in ZODIACS) and line[:450] not in parsed_lines:
                rejected.append({'author':post['author'],'source_url':post['detail_url'],
                                 'evidence_id':post.get('evidence_id',post['detail_url']),'excerpt':line[:450],'reason':'unsupported or ambiguous play',
                                 'counted_for_ranking':False})
                found=True
        if not found and not extract_picks(post['title']+'\n'+text):
            rejected.append({'author':post['author'],'source_url':post['detail_url'],
                             'evidence_id':post.get('evidence_id',post['detail_url']),'excerpt':post['title'][:450],'reason':'no supported current-issue play',
                             'counted_for_ranking':False})
    return rejected


def collect_page(url,out,target_issue=None,target_category=None,policy=None):
    from playwright.sync_api import sync_playwright
    policy=policy or {}
    if target_issue and not re.fullmatch(r'20\d{5}',str(target_issue)):
        raise ValueError('explicit target issue must use YYYYNNN')
    max_pages=max(1,min(int(policy.get('max_pages',30)),50))
    max_posts=max(1,min(int(policy.get('max_posts',150)),2000))
    feed_mode=policy.get('collection_mode')=='current_feed'
    posts=[];errors=[];historical_references=[];comment_checks=[]
    comment_targets=[];comment_target_total=0;comment_attempted=0;profile_budget_exhausted=False
    article_image_audit={}
    started=time.monotonic()
    deadline=started+max(60,int(policy.get('max_collection_seconds',1200)))
    out.mkdir(parents=True,exist_ok=True)
    issue=target_issue
    listing_candidates=[]
    def checkpoint(stage):
        ranking,predictions=score_posts(posts)
        progress={'scoring_rule_version':SCORING_RULE_VERSION,'scoring_rules':SCORING_RULES,
                  'material_counts':material_counts(predictions),'issue':issue,'status':'incomplete_observation',
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
            page.get_by_role('tab',name='澳彩',exact=True).wait_for(state='visible',timeout=15000)
            page.get_by_role('tab',name='澳彩',exact=True).click()
            page.get_by_text('最新',exact=True).click()
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
                deadline=min(deadline,time.monotonic()+300),on_progress=scan_progress,
                stop_predicate=(lambda items:current_feed_boundary(items,target_issue)) if feed_mode else None)
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
                0 if (feed_mode and 'corpusdetail' in (x.get('href') or '')) else 1 if feed_mode else 0,
                0 if x['author'] in favored else 1,
                '' if feed_mode else x['author']))
            author_limit=max(1,min(60,int(policy.get('max_profile_authors',30))))
            profile_steps=max(1,min(80,int(policy.get('profile_scroll_steps',32))))
            profile_posts=[];profile_checks=[];profile_errors=[]
            # Daily operation only needs CURRENT posts from the live forum.
            # An explicit historical audit (e.g. 281) opens author histories,
            # and only as far back as that issue. Never sweep all past issues.
            profile_samples=choose_profile_samples(author_order,target_issue,feed_mode,policy)
            for sample in profile_samples:
                # Author discovery is bounded: leave room for verified reply
                # authors and their images before the collection deadline.
                if feed_mode and time.monotonic()>=deadline-max(180,int(policy.get('min_comment_reserve_seconds',180))):
                    profile_budget_exhausted=True
                    break
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
            targets=prioritize_posts(scoped,favored)+[x for x in prioritize_posts(next_period,favored) if x['key'] not in {p['key'] for p in scoped}]
            if not targets:raise ValueError('no current or next-period posts for '+issue)
            truncated=(len(targets)>max_posts or feed_audit.get('truncated',False)
                       or (bool(target_issue) and not feed_mode and len(author_samples)>author_limit)
                       or any(x.get('truncated') for x in profile_checks))
            # Report missing author profiles as explicit incomplete coverage;
            # do not pretend browsing the visible forum card is exhaustive.
            profile_incomplete=(bool(target_issue) and not feed_mode and
                                (bool(profile_errors) or len(author_samples)>author_limit))
            cache={}
            for item in targets[:max_posts]:
                if time.monotonic()>=deadline:
                    truncated=True
                    errors.append({'reason':'collection time budget exhausted; remaining posts not checked'})
                    break
                if feed_mode:
                    try:
                        captured=feed_card_post(item,url)
                        if item['issue']==issue:
                            posts.append(captured)
                        else:
                            historical_references.extend(previous_period_records(
                                captured['body'],issue,captured))
                    except ValueError as exc:
                        errors.append({'author':item['author'],'title':item['title'],'reason':str(exc)})
                    continue
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
            if feed_mode and policy.get('collect_article_images',True):
                from .feed_images import process_feed_images
                # Leave a separate budget for replies and their pictures.
                article_image_deadline=min(deadline-145,time.monotonic()+105)
                article_image_audit=process_feed_images(posts,article_image_deadline,policy)
                checkpoint('article_image_ocr_complete')
            if feed_mode and policy.get('collect_comments',False):
                from .comment_collector import collect_comments
                # Do not depend on the thread title saying "see comments".
                # Reply authors can publish image predictions on any thread.
                comment_targets,comment_target_total=choose_comment_threads(posts,scoped,policy)
                for parent,item in comment_targets:
                    if time.monotonic()>=deadline-45:
                        truncated=True
                        errors.append({'reason':'comment collection time budget exhausted; threads remain'})
                        break
                    comment_attempted+=1
                    post_deadline=min(deadline-45,time.monotonic()+
                        max(10,min(120,int(policy.get('max_post_seconds',75)))))
                    try:
                        if not _detail(page,url,item,target_category,max_pages,post_deadline):
                            errors.append({'author':parent['author'],'title':parent['title'],
                                           'reason':'comment detail not verified'})
                            continue
                        # Read full same-author article text, not just the feed-card excerpt.
                        # It shares the per-post budget with the comment scan.
                        article_text=_article_body(page,item)
                        if article_text:
                            parent['body']=article_text
                            parent['detail_coverage']='verified_article_text'
                        seq=int(issue[-3:]);h,m=map(int,policy.get('verified_draw_time','21:32').split(':'))
                        cutoff=(datetime(int(issue[:4]),1,1,tzinfo=LOCAL_TZ)+timedelta(days=seq-1)).replace(hour=h,minute=m)
                        thread_deadline=min(post_deadline,time.monotonic()+max(10,min(60,int(policy.get('comment_scan_seconds',25)))))
                        children,check=collect_comments(page,dict(parent,draw_at=cutoff.isoformat()),thread_deadline,policy)
                        check.update(author=parent['author'],title=parent['title'],source_url=page.url,
                                     is_featured=parent.get('is_featured') is True)
                        comment_checks.append(check)
                        posts.extend(children)
                        if check.get('truncated'):truncated=True
                        parent['comments_coverage']='verified_comment_rows'
                        parent['detail_url']=page.url
                    except Exception as exc:
                        errors.append({'author':parent['author'],'title':parent['title'],
                                       'reason':'comment collection: '+str(exc)[:210]})
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
    unscored_posts=sum(1 for p in posts if p.get('evidence_id',p['detail_url']) not in {v.get('evidence_id',v['source_url']) for v in valid_votes})
    enough=(valid_materials>=int(policy.get('min_valid_materials',25))
            and len(valid_authors)>=int(policy.get('min_valid_authors',5)))
    seq=int(issue[-3:])
    if not 1<=seq<=366:raise ValueError('invalid daily issue')
    draw_day=datetime(int(issue[:4]),1,1,tzinfo=LOCAL_TZ)+timedelta(days=seq-1)
    hour,minute=map(int,policy.get('verified_draw_time','21:32').split(':'))
    draw_at=draw_day.replace(hour=hour,minute=minute)
    pre_draw=datetime.now(LOCAL_TZ)<draw_at
    ready=enough and bool(ranking) and not errors and not truncated and not profile_incomplete and pre_draw
    result={'issue':issue,'status':'ready_observation' if ready else 'incomplete_observation',
            'collection_priority':'featured_then_reply_hint_then_direct_link_then_favored_author',
            'source_url':url,'collector':'rendered-dom',
            'draw_at':draw_at.isoformat(),'pre_draw_verified':pre_draw,
            'evidence_scope':'current_feed_card' if feed_mode else 'post_article',
            'comment_image_policy':('text_first_deferred' if not policy.get('collect_comment_images',True)
                                    else 'prioritized_current_issue_threads') if policy.get('collect_comments') else 'not_expanded',
            'comment_checks':comment_checks,
            'article_image_audit':article_image_audit,
            'comment_records':sum(p.get('evidence_scope')=='verified_comment' for p in posts),
            'coverage_label':('当期列表、优先正文和评论文字：抽样检查，非全覆盖' if feed_mode else '历史期目标采集') if not truncated else '部分覆盖：超时或未完成','fetched_at':datetime.now(timezone.utc).isoformat(),
            'raw_count':len(posts),'valid_leaderboard':0,'valid_outside':len(posts),
            'author_profiles_checked':len(profile_checks),
            'author_profile_budget_exhausted':profile_budget_exhausted,
            'comment_candidate_threads':comment_target_total,
            'comment_selected_threads':len(comment_targets),
            'comment_attempted_threads':comment_attempted,
            'comment_checked_threads':len(comment_checks),
            'comment_completed_threads':sum(not c.get('truncated',False) for c in comment_checks),
            'featured_found_threads':sum(p.get('is_featured') is True for p in scoped),
            'featured_selected_threads':sum(p.get('is_featured') is True for p,_ in comment_targets),
            'featured_checked_threads':sum(c.get('is_featured') is True for c in comment_checks),
            'comment_unchecked_threads':max(0,comment_target_total-len(comment_checks)),
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
            'scoring_rule_version':SCORING_RULE_VERSION,'scoring_rules':SCORING_RULES,
            'material_counts':material_counts(predictions),
            'candidate_materials':len(predictions),
            'excluded_materials':rejected_materials,
            'unscored_posts':unscored_posts,
            'distinct_valid_authors':len(valid_authors),
            'selected_leaderboard':0,'selected_outside':0,
            'ranking':ranking,'top1':[r['zodiac'] for r in ranking[:1]],
            'top3':[r['zodiac'] for r in ranking[:3]],
            'top6':[r['zodiac'] for r in ranking[:6]],
            'evidence':posts,'parsed_predictions':predictions,'rejected':errors,
            'unparsed_evidence':unparsed_evidence(posts),
            'audit':{'listing_count':len(listing),'issue_listing_count':len(scoped),
                     'forum_scroll':feed_audit,
                     'profile_checks':profile_checks,
                     'comment_checks':comment_checks,
                     'article_image_audit':article_image_audit,
                     'comment_candidate_threads':comment_target_total,
                     'comment_attempted_threads':comment_attempted,
                     'comment_checked_threads':len(comment_checks),
                     'comment_completed_threads':sum(not c.get('truncated',False) for c in comment_checks),
                     'featured_found_threads':sum(p.get('is_featured') is True for p in scoped),
                     'featured_selected_threads':sum(p.get('is_featured') is True for p,_ in comment_targets),
                     'featured_checked_threads':sum(c.get('is_featured') is True for c in comment_checks),
                     'profile_budget_exhausted':profile_budget_exhausted,
                     'profile_errors':profile_errors,
                     'author_profiles_discovered':len(author_samples),
                     'author_profiles_checked':len(profile_checks),
                     'author_history_posts_found':len(profile_posts),
                     'next_period_posts_checked':len(next_period),
                     'historical_reference_count':len(historical_references),
                     'excluded_draw_header':True,
                     'post_article_only':not feed_mode,'card_owned_text_only':feed_mode,
                     'truncated':truncated,'detail_errors':errors,'issue_filter_strict':True},
            'progress':{'leaderboard_source':'historic_candidates_unverified',
                        'leaderboard_checked':0,'leaderboard_total':0,
                        'outside_checked':len(posts),'blocked':errors},
            'author_selection':{'top20_candidates':len(candidates),
                'top20_found':top_found,'other_found':other_found,
                'valid_prediction_authors':len(valid_authors),
                'valid_materials':valid_materials,
                'min_valid_materials':int(policy.get('min_valid_materials',25)),
                'min_valid_authors':int(policy.get('min_valid_authors',5)),
                'comments_collected':sum(bool(p['comments']) or p.get('evidence_scope')=='verified_comment' for p in posts),
                'selection_status':'threshold_met' if ready else 'insufficient_or_unverified'},
            'note':('Forum feed and author pages are progressively scrolled to load cards. '
                    'Missing author profiles or incomplete scrolling are reported. '
                    'Retrospective references in next-period posts are not pre-draw forecasts; ' 
                    'page top draw results are excluded from scoring. '
                    'Experimental evidence only; no verified live leaderboard.')}
    from .questions import pending_questions
    result['pending_rule_questions']=pending_questions(result)
    result['pending_rule_question_count']=len(result['pending_rule_questions'])
    out.mkdir(parents=True,exist_ok=True)
    (out/'status.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    return result
