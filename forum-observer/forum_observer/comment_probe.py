"""Automated read-only DOM inspection for author comment uploads."""
import json,time
from pathlib import Path
from playwright.sync_api import sync_playwright
from .page_collector import _pages,_detail,_listing,current_feed_boundary


def main():
    cfg=json.loads(Path('collector-config.json').read_text())
    out=Path('comment-test-out');out.mkdir(exist_ok=True)
    issue='2026284';deadline=time.monotonic()+600
    with sync_playwright() as p:
        browser=p.chromium.launch(headless=True,args=['--no-sandbox'])
        page=browser.new_page(viewport={'width':1280,'height':1800})
        page.set_default_timeout(5000)
        page.goto(cfg['forum_url'],wait_until='domcontentloaded',timeout=60000)
        page.get_by_role('tab',name='澳彩',exact=True).wait_for(state='visible',timeout=15000)
        page.get_by_role('tab',name='澳彩',exact=True).click()
        page.get_by_text('最新',exact=True).click()
        page.wait_for_timeout(1000)
        listing,audit=_pages(page,cfg['category'],50,deadline=deadline,
                            stop_predicate=lambda items:current_feed_boundary(items,issue))
        preferred=['神算女','卓楚赢',*cfg['top_authors']]
        posts=[x for x in listing if x['issue']==issue]
        posts.sort(key=lambda x:(preferred.index(x['author']) if x['author'] in preferred else 999,
                                 len(x.get('body',''))))
        seen=set();results=[]
        for item in posts:
            if item['author'] in seen:continue
            seen.add(item['author'])
            if len(results)>=5:break
            verified=_detail(page,cfg['forum_url'],item,cfg['category'],50,deadline=deadline)
            result=dict(author=item['author'],title=item['title'],verified=verified,url=page.url)
            if verified:
                page.wait_for_timeout(500)
                for _ in range(4):
                    from .author_history import SCROLL_JS
                    page.evaluate(SCROLL_JS);page.wait_for_timeout(650)
                result['body']=page.locator('body').inner_text()
                result['images']=page.locator('img').evaluate_all('els=>els.filter(x=>(x.src||"").includes("/comment/")).map(x=>({src:x.src,ancestors:[x.parentElement,x.parentElement?.parentElement,x.parentElement?.parentElement?.parentElement].filter(Boolean).map(a=>a.outerHTML.slice(0,18000))}))')
                (out/(str(len(results))+'.html')).write_text(page.content(),encoding='utf-8')
            results.append(result)
            (out/'probe.json').write_text(json.dumps({'issue':issue,'test_only':True,'scan':audit,'posts':results},ensure_ascii=False,indent=2),encoding='utf-8')
            print(json.dumps({k:v for k,v in result.items() if k not in ('body','images')},ensure_ascii=False),flush=True)
        browser.close()


if __name__=='__main__':main()
