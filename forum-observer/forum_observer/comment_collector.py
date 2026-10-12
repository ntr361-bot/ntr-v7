"""Read actual forum comment rows; never attribute a reply to the thread owner."""
import hashlib,json,re,time
from datetime import datetime
from .author_history import incremental_scan


def comment_rows(page):
    rows=page.locator('.new-list .comment-item').evaluate_all(r'''items=>items.map(item=>{
        const get=css=>item.querySelector(css)?.innerText?.trim()||'';
        const text=item.querySelector('.comment-item-bd .text');
        return {author:get('.user-info .name').replace(/\s*LV\.\d+[\s\S]*$/,'').trim(),
            floor:get('.floor'),published_at:get('.comment-item-bd .secs .time'),
            text:text?.querySelector('[aria-label]')?.getAttribute('aria-label')||text?.innerText||'',
            image_urls:[...item.querySelectorAll('.comment-item-bd .images-box img')]
                .map(img=>img.getAttribute('data-src')||img.src).filter(Boolean)};
    })''')
    for row in rows:
        row['image_urls']=list(dict.fromkeys(row['image_urls']))
        row['key']=(row['author'],row['floor'])
    return rows


def collect_comments(page,parent,deadline,policy):
    from .page_collector import LOCAL_TZ,_timestamp,HOSTS
    from urllib.parse import urlparse,urljoin
    snapshots={}
    def latest_rows():
        current=comment_rows(page)
        for row in current:
            if row['key'] in snapshots:snapshots[row['key']].update(row)
            else:snapshots[row['key']]=row
        return [snapshots[row['key']] for row in current]
    rows,audit=incremental_scan(page,latest_rows,
        max_steps=int(policy.get('max_comment_scroll_steps',160)),delay_ms=650,
        deadline=deadline)
    now=datetime.now(LOCAL_TZ)
    posts=[];rejected=[]
    for row in rows:
        author=row['author'];stamp=_timestamp(row['published_at'],now)
        if not author or author in ('论坛管理','管理员') or not stamp:
            rejected.append(dict(author=author,floor=row['floor'],reason='unverified_comment_author_or_time'))
            continue
        identity=hashlib.sha256(json.dumps([parent['evidence_id'],*row['key']],ensure_ascii=False).encode()).hexdigest()[:20]
        if stamp>=datetime.fromisoformat(parent['draw_at']):
            rejected.append(dict(author=author,floor=row['floor'],reason='comment_after_draw'))
            continue
        post=dict(author=author,issue=parent['issue'],title=parent['issue']+'期评论资料',
            body=row['text'],comments='',images=[],detail_url=page.url,
            published_at=stamp.isoformat(),evidence_id=identity,
            evidence_scope='verified_comment',parent_author=parent['author'],
            comment_floor=row['floor'],author_verified=True,parent_title=parent['title'])
        for raw in row['image_urls']:
            src=urljoin(page.url,raw);u=urlparse(src)
            if u.scheme!='https' or u.hostname not in HOSTS or '/tk118files/comment/' not in u.path:continue
            post['images'].append(dict(src=src,ocr='',ocr_status='pending',
                period_verified=False,source_section='author_comment',author=author,
                author_verified=True,issue=parent['issue'],published_at=stamp.isoformat()))
        posts.append(post)
    # In text-first mode preserve the original image/source for later review,
    # but do not download, run OCR, send images anywhere or give them votes.
    if policy.get('collect_comment_images',True):
        from .image_queue import ImageQueue,attach_result
        queue=ImageQueue(policy.get('image_queue_state','archive/image-queue/state.json'),
                         policy.get('image_cache_dir','.image-cache'))
        keys=[queue.enqueue(pic['src']) for post in posts for pic in post['images']]
        if keys:
            jobs=queue.process(keys,deadline,workers=int(policy.get('image_workers',3)))
            for post in posts:
                for pic in post['images']:
                    job=jobs[queue.enqueue(pic['src'])]
                    pic.update(attach_result(job,post))
    else:
        for post in posts:
            for pic in post['images']:
                pic.update(ocr='',ocr_status='deferred_text_first',period_verified=False)
    from collections import Counter
    audit.update(image_status_counts=dict(Counter(pic['ocr_status'] for post in posts for pic in post['images'])))
    audit.update(rows_seen=len(rows),verified_rows=len(posts),rejected_rows=rejected,
                 images_seen=sum(len(p['images']) for p in posts),
                 images_recognized=sum(bool(i['ocr']) for p in posts for i in p['images']))
    return posts,audit
