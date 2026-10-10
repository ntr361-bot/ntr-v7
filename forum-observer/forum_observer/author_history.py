"""Navigate dynamic forum feeds and verified author history pages.

No global draw banner text is returned as an author article or prediction.
This module deliberately does not infer author URLs from usernames.
"""
from __future__ import annotations

import re,time
from urllib.parse import urljoin


SCROLL_JS = r"""() => {
  const card = document.querySelector('li .slabel')?.closest('li')
    || document.querySelector('li .formtitle')?.closest('li')
    || document.querySelector('[class*="post-item"]')
    || document.querySelector('[class*="article-item"]');
  let target = null;
  for(let p=card; p; p=p.parentElement) {
    const css=getComputedStyle(p);
    if(p.scrollHeight > p.clientHeight+24
       && /(auto|scroll)/.test(css.overflowY)) {target=p;break;}
  }
  if(!target) {
    const candidates=[...document.querySelectorAll('div,main,section,scroll-view')]
      .filter(e=>e.scrollHeight>e.clientHeight+40)
      .filter(e=>/(auto|scroll)/.test(getComputedStyle(e).overflowY));
    candidates.sort((a,b)=>(b.clientHeight>=a.clientHeight?1:-1));
    target=candidates[0]||document.scrollingElement||document.documentElement;
  }
  const before=target.scrollTop;
  const amount=Math.max(350,Math.floor(target.clientHeight*0.75));
  target.scrollTop=Math.min(target.scrollHeight, before+amount);
  const after=target.scrollTop;
  return {before,after,at_end:after>=target.scrollHeight-target.clientHeight-4,
    scroll_height:target.scrollHeight};
}"""


def incremental_scan(page, get_items, max_steps=90, delay_ms=650,
                     max_empty_bottom_rounds=4, stop_predicate=None,
                     deadline=None, on_progress=None):
    """Progressively scroll the actual feed container, not only window.

    Capture after every scroll (virtualized posts may disappear on later
    scrolls). Keep waiting after reaching bottom while AJAX loads more.
    """
    unique={}
    stable=0
    moves=0
    reached_bottom=False
    for step in range(max(1,max_steps)):
        if deadline is not None and time.monotonic()>=deadline:
            return list(unique.values()),{'steps':step,'moves':moves,
                'end_confirmed':False,'items_seen':len(unique),'truncated':True,
                'stop_reason':'scan_time_budget_exhausted'}
        old_size=len(unique)
        for item in get_items():
            key=item.get('key')
            if key is None:continue
            unique.setdefault(key,item)
        if on_progress is not None:
            on_progress(list(unique.values()),{'steps':step,'items_seen':len(unique)})
        move=page.evaluate(SCROLL_JS)
        page.wait_for_timeout(delay_ms)
        for item in get_items():
            key=item.get('key')
            if key is not None:unique.setdefault(key,item)
        if on_progress is not None:
            on_progress(list(unique.values()),{'steps':step+1,'items_seen':len(unique)})
        # A period-specific author audit does not need to scroll through every
        # publication the author has ever made. Stop only on an explicit,
        # auditable target-window decision, not just the first visible card.
        if stop_predicate is not None:
            reason=stop_predicate(list(unique.values()))
            if reason:
                return list(unique.values()),{
                    'steps':step+1,'moves':moves,'end_confirmed':False,
                    'items_seen':len(unique),'truncated':False,
                    'stop_reason':str(reason),'scope':'target_issue_window'}
        has_new=len(unique)>old_size
        progressed=move['after']>move['before']+2
        if progressed:moves+=1
        at_end=bool(move.get('at_end'))
        reached_bottom=reached_bottom or at_end
        if has_new or progressed:
            stable=0
        elif at_end:
            stable+=1
        else:
            stable=0
        if stable>=max_empty_bottom_rounds:
            # The site sometimes has explicit pagination in addition to scroll.
            buttons=page.locator('button, a').filter(has_text=re.compile(r'下一页|下页|加载更多'))
            if buttons.count():
                try:
                    buttons.last.click(timeout=2500)
                    page.wait_for_timeout(max(700,delay_ms))
                    stable=0
                    continue
                except Exception:
                    pass
            return list(unique.values()),{
                'steps':step+1,'moves':moves,'end_confirmed':True,
                'items_seen':len(unique),'truncated':False}
    return list(unique.values()),{
        'steps':max_steps,'moves':moves,'end_confirmed':False,
        'items_seen':len(unique),'truncated':True,
        'reached_bottom':reached_bottom}


def target_issue_window(target_issue, older_confirmation_rounds=3):
    """Close an author's recent history after scrolling beyond target issue.

    This is a *targeted* scan, not proof that the author's entire history was
    loaded. The cutoff is reached only after older dated issue cards have been
    visible in several scan rounds. If dates are missing, the bounded step
    limit applies instead.
    """
    past_rounds=0
    def stop(items):
        nonlocal past_rounds
        older=any(str(x.get('issue','')).isdigit()
                  and len(str(x.get('issue','')))==7
                  and str(x['issue'])[:4]==str(target_issue)[:4]
                  and int(x['issue'])<int(target_issue)
                  for x in items)
        if older:
            past_rounds+=1
            if past_rounds>=older_confirmation_rounds:
                return "older_period_reached"
        else:
            past_rounds=0
        return None
    return stop


def profile_items(page, author, issue_parser):
    """Extract cards from *verified* author's history tab.

    Profile history cards do not always repeat the username on each card.
    Never treat a profile header's official draw result as a posted article.
    """
    found=[]
    cards=page.locator('li, [class*="post-item"], [class*="article-item"]')
    for i in range(cards.count()):
        li=cards.nth(i)
        try:
            def value(selector):
                q=li.locator(selector)
                return q.first.inner_text(timeout=1500).strip() if q.count() else ''
            title=value('.formtitle')
            if not title:title=value('[class*="title"]')
            if not title:continue
            period=value('.slabel')
            if not period:
                # A title must explicitly label a period, not merely contain
                # a 3-digit number (such as a view count or a betting number).
                m=re.search(r'(?<!\d)(?:20\d{2})?\d{3}\s*期(?!\d)',title)
                period=m.group(0) if m else ''
            issue=issue_parser(period)
            if not issue:continue
            published=value('.time') or value('[class*="time"]')
            body=value('.text')
            href=''
            for a in li.locator('a[href]').all()[:10]:
                link=a.get_attribute('href') or ''
                if 'corpusdetail' in link:
                    href=urljoin(page.url,link)
                    break
            key=(author,issue,published,title)
            found.append({'author':author,'issue':issue,'published_at':published,
                          'title':title,'body':body,'category':'',
                          'href':href,'key':key,'profile_url':page.url})
        except Exception:
            continue
    return found


def _has_history_tab(page):
    return page.get_by_text(re.compile(r'历史帖子|历史发表|发表的帖子|他的帖子')).count()>0


def open_author_history(page, forum_url, sample, issue_parser,
                        max_steps=12,delay_ms=650,target_issue=None):
    """Enter the *real* author profile by clicking the visible author/avatar.

    Never synthesize a URL or assume the route structure; verify both author
    name and profile's history button before collecting.
    """
    author=sample['author']
    direct=sample.get('href')
    page.goto(direct if direct and 'corpusdetail' in direct else forum_url,
              wait_until='domcontentloaded',timeout=45000)
    page.wait_for_timeout(950)
    if author not in page.locator('body').inner_text(timeout=5000):
        return [],{'author':author,'error':'author name absent from source page'}
    # Clicking the username or adjacent avatar is the site's navigation flow.
    triggers=[]
    name=page.get_by_text(author,exact=True)
    if name.count():
        triggers.append(name.first)
        # Avatar near the same author label, not an arbitrary document image.
        try:
            wrapper=name.first.locator('xpath=..')
            avatar=wrapper.locator('img')
            if avatar.count():triggers.insert(0,avatar.first)
        except Exception:pass
    for trig in triggers:
        try:
            if _has_history_tab(page):break
            trig.click(timeout=3000)
            page.wait_for_timeout(900)
            if _has_history_tab(page):break
            # If it opened only a dialog or a wrong route, navigate back to
            # source and try the next controlled author trigger.
            page.goto(direct if direct and 'corpusdetail' in direct else forum_url,
                      wait_until='domcontentloaded',timeout=45000)
            page.wait_for_timeout(800)
        except Exception:
            continue
    if not _has_history_tab(page):
        return [],{'author':author,'error':'author profile / history button not verified'}
    if author not in page.locator('body').inner_text(timeout=5000):
        return [],{'author':author,'error':'profile author mismatch'}
    button=page.get_by_text(re.compile(r'历史帖子|历史发表|发表的帖子|他的帖子'))
    button.last.click(timeout=4000)
    page.wait_for_timeout(850)
    profile=page.url
    if author not in page.locator('body').inner_text(timeout=5000):
        return [],{'author':author,'error':'history tab author mismatch'}
    posts,audit=incremental_scan(
        page,lambda:profile_items(page,author,issue_parser),
        max_steps=max_steps,delay_ms=delay_ms,
        stop_predicate=target_issue_window(target_issue) if target_issue else None)
    if target_issue:
        relevant={str(target_issue),str(int(target_issue)+1)}
        posts=[item for item in posts if item.get('issue') in relevant]
    for item in posts:item['profile_url']=profile
    return posts,{'author':author,'profile_url':profile,**audit,
                  'target_issue':target_issue,
                  'relevant_posts':len(posts),
                  'all_history_requested':False}


def open_profile_post(page,item,issue_parser,max_steps=35):
    """Find exact history card again if its SPA route has no copyable href."""
    profile=item.get('profile_url')
    if not profile:return False
    page.goto(profile,wait_until='domcontentloaded',timeout=45000)
    page.wait_for_timeout(850)
    if item['author'] not in page.locator('body').inner_text(timeout=4500):
        return False
    if _has_history_tab(page):
        page.get_by_text(re.compile(r'历史帖子|历史发表|发表的帖子|他的帖子')).last.click(timeout=3000)
        page.wait_for_timeout(550)
    for _ in range(max_steps):
        cards=page.locator('li, [class*="post-item"], [class*="article-item"]')
        for card_idx in range(cards.count()):
            card=cards.nth(card_idx)
            t=card.locator('.formtitle')
            if not t.count():continue
            try:
                title=t.first.inner_text(timeout=1300).strip()
                if title!=item.get('title'):continue
                p=card.locator('.slabel')
                if p.count() and issue_parser(p.first.inner_text())!=item['issue']:
                    continue
                card.click(timeout=3500)
                page.wait_for_timeout(750)
                return 'corpusdetail' in page.url
            except Exception:continue
        step=page.evaluate(SCROLL_JS)
        page.wait_for_timeout(650)
        if step['at_end'] and step['after']==step['before']:
            break
    return False

