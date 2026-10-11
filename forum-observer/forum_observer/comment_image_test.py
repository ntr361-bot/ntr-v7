"""Reproducible read-only image smoke test using a previously verified post URL."""
import json,time
from pathlib import Path
from playwright.sync_api import sync_playwright
from .page_collector import _wait_detail,score_posts,unparsed_evidence,LOCAL_TZ
from .comment_collector import collect_comments
from .questions import pending_questions


def main():
    out=Path('comment-test-out');out.mkdir(exist_ok=True)
    parent=dict(author='神算女',issue='2026284',title='284期， 更新在⬇️评论区⬇️',
                evidence_id='verified-post-11069578',draw_at='2026-10-11T21:32:00+08:00')
    url='https://x5k1pok.11852.com:8443/#/corpusdetail?id=11069578&type=3'
    with sync_playwright() as p:
        browser=p.chromium.launch(headless=True,args=['--no-sandbox'])
        page=browser.new_page(viewport={'width':1280,'height':1800})
        page.set_default_timeout(5000)
        page.goto(url,wait_until='domcontentloaded',timeout=60000)
        if not _wait_detail(page,parent,timeout_seconds=20):raise ValueError('target post not verified')
        posts,audit=collect_comments(page,parent,time.monotonic()+90,{})
        ranking,parsed=score_posts(posts)
        result=dict(issue=parent['issue'],status='historical_test_only',test_only=True,
                    source_url=url,comment_checks=[audit],evidence=posts,parsed_predictions=parsed,
                    unparsed_evidence=unparsed_evidence(posts),ranking=ranking,
                    valid_materials=sum(x['counted_for_ranking'] for x in parsed))
        result['pending_rule_questions']=pending_questions(result)
        (out/'comment-images.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
        print(json.dumps({k:v for k,v in result.items() if k not in ('evidence','parsed_predictions','unparsed_evidence','ranking','pending_rule_questions')},ensure_ascii=False),flush=True)
        browser.close()


if __name__=='__main__':main()
