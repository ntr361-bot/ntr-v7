"""Run all 103 previously captured URLs; no forum browsing or prediction writes."""
import json,time
from collections import Counter
from pathlib import Path
from .image_queue import ImageQueue,attach_result
from .page_collector import score_posts


def main():
    data=json.loads(Path('tests/fixtures/comment-images-284.json').read_text())
    out=Path('comment-test-out');out.mkdir(exist_ok=True)
    q=ImageQueue(out/'image-queue.json','.image-cache')
    keys=[q.enqueue(i['src']) for p in data['posts'] for i in p['images']]
    keys=list(dict.fromkeys(keys))
    if len(keys)!=103:raise ValueError('fixture must contain 103 unique original images')
    results=q.process(keys,time.monotonic()+1800,workers=3)
    for p in data['posts']:
        for i in p['images']:i.update(attach_result(results[q.enqueue(i['src'])],p))
    ranking,parsed=score_posts(data['posts'])
    summary=dict(issue=data['issue'],test_only=True,unique_images=len(keys),
        processing_statuses=dict(Counter(x['status'] for x in results.values())),
        parsing_statuses=dict(Counter(i['ocr_status'] for p in data['posts'] for i in p['images'])),
        valid_materials=sum(x['counted_for_ranking'] for x in parsed),ranking=ranking)
    (out/'report.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2))
    (out/'evidence.json').write_text(json.dumps(dict(posts=data['posts'],parsed=parsed),ensure_ascii=False,indent=2))
    print(json.dumps(summary,ensure_ascii=False),flush=True)
    if any(x['status'] in ('pending','processing') for x in results.values()):raise SystemExit(2)


if __name__=='__main__':main()
