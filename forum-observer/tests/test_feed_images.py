import tempfile
import time
import unittest
from unittest.mock import patch
from pathlib import Path

from forum_observer.feed_images import process_feed_images
from forum_observer.image_queue import attach_result
from forum_observer.page_collector import score_posts


class FeedImageTests(unittest.TestCase):
    def post(self, published="2026-10-12T19:55:00+08:00"):
        return dict(author="真实作者", issue="2026285", published_at=published,
                    title="285期图片发布", body="", comments="",
                    detail_url="https://x5k1pok.11852.com:8443/corpusdetail/285",
                    evidence_scope="current_feed_card",
                    images=[dict(src="https://jmz.chunshengsh.com/tk118files/article/285.png",
                                 ocr="",ocr_status="not_read_from_card")])

    def test_article_image_ocr_is_counted_only_with_independent_agreement(self):
        post=self.post()
        with tempfile.TemporaryDirectory() as root:
            cfg=dict(image_queue_state=str(Path(root)/"jobs.json"),
                     image_cache_dir=str(Path(root)/"cache"))
            with patch("forum_observer.feed_images.ImageQueue") as queue:
                queue.return_value.enqueue.return_value="imagekey"
                queue.return_value.process.return_value={
                    "imagekey":dict(status="ocr_complete",
                                    url=post["images"][0]["src"],
                                    passes={"6":"六肖：鼠牛虎兔龙蛇",
                                            "11":"六肖【鼠牛虎兔龙蛇】"})}
                audit=process_feed_images([post],time.monotonic()+10,cfg)
        self.assertEqual(audit["recognized_consistent"],1)
        ranking,parsed=score_posts([post])
        self.assertEqual(len(ranking),12)
        self.assertEqual(sum(p["counted_for_ranking"] for p in parsed),1)

    def test_after_draw_article_rejected_even_with_period_text(self):
        post=self.post("2026-10-12T22:15:00+08:00")
        image=attach_result(dict(status="ocr_complete",
            url=post["images"][0]["src"],
            passes={"6":"285期六肖：鼠牛虎兔龙蛇",
                    "11":"285期六肖：鼠牛虎兔龙蛇"}),post)
        post["images"][0].update(image)
        self.assertEqual(image["ocr_status"],"article_source_time_unverified")
        self.assertEqual(score_posts([post])[0],[])

    def test_conflicting_ocr_cannot_vote(self):
        post=self.post()
        post["images"][0].update(attach_result(dict(status="ocr_complete",
            url=post["images"][0]["src"],
            passes={"6":"285期六肖：鼠牛虎兔龙蛇",
                    "11":"285期六肖：鼠牛虎兔龙马"}),post))
        self.assertEqual(score_posts([post])[0],[])

    def test_advertisement_and_unapproved_host_rejected(self):
        post=self.post()
        post["images"]=[
            {"src":"https://external.example/tk118files/article/x.png","ocr":"六肖：鼠牛虎兔龙蛇"},
            {"src":"https://jmz.chunshengsh.com/banners/draw.png","ocr":"六肖：鼠牛虎兔龙蛇"}]
        audit=process_feed_images([post],0,{"max_feed_images":10})
        self.assertEqual(audit["images_seen"],0)
        self.assertEqual(score_posts([post])[0],[])


if __name__=="__main__":
    unittest.main()
