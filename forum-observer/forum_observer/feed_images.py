"""Bounded OCR for original article images in current forum feed cards.

Keep downloaded/recognized images attached to the originating author card.
Do not read draw banners, avatars, or advertisements as predictions.
"""
from __future__ import annotations

import time
from collections import Counter
from urllib.parse import urlparse

from .image_queue import ImageQueue, attach_result
from .page_collector import HOSTS


def process_feed_images(posts, deadline, policy):
    limit=max(0,min(120,int(policy.get("max_feed_images",40))))
    queue=ImageQueue(policy.get("image_queue_state","archive/image-queue/state.json"),
                     policy.get("image_cache_dir",".image-cache"))
    pictures=[]
    seen=set()
    for post in posts:
        if post.get("evidence_scope")!="current_feed_card":
            continue
        for pic in post.get("images",[]):
            src=pic.get("src","")
            u=urlparse(src)
            if u.scheme!="https" or u.hostname not in HOSTS or "/tk118files/article/" not in u.path:
                pic.update(ocr="",ocr_status="unapproved_source",period_verified=False)
                continue
            if src not in seen and len(seen)>=limit:
                pic.update(ocr="",ocr_status="deferred_image_limit",period_verified=False)
                continue
            seen.add(src)
            pictures.append((post,pic,queue.enqueue(src)))
    if pictures and time.monotonic()<deadline:
        jobs=queue.process([key for _,_,key in pictures],deadline,
                           workers=int(policy.get("image_workers",3)))
        for post,pic,key in pictures:
            pic.update(attach_result(jobs[key],post))
    else:
        for _,pic,_ in pictures:
            pic.update(ocr="",ocr_status="deferred_time_budget",period_verified=False)
    counts=Counter(pic.get("ocr_status","unknown") for _,pic,_ in pictures)
    return {"images_seen":len(pictures),"unique_images":len(seen),
            "recognized_consistent":counts.get("recognized_consistent",0),
            "image_status_counts":dict(counts),
            "image_limit_reached":len(seen)>=limit if limit else bool(pictures)}
