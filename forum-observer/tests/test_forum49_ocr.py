import io
import sqlite3
import tempfile
import time
import unittest
from collections import Counter
from contextlib import closing
from pathlib import Path

from PIL import Image

from forum_observer.forum49_ocr_engine import PaddleWorker, recognize
from forum_observer.forum49_ocr_sources import article_image_url, body_image_urls
from forum_observer.forum49_ocr_test import collect_and_recognize, retry

POST = "https://49217.com/#/corpusdetail?id=123&type=3"
CDN = "https://jmz.jlidesign.com:4949"
IMAGE = CDN + "/unite49files/article/2026/11/26/body.png"


def slow_child(connection):
    connection.send(("ok", None))
    connection.recv()
    time.sleep(20)


def png(color):
    result = io.BytesIO()
    Image.new("RGB", (60, 40), color).save(result, "PNG")
    return result.getvalue()


class Forum49OcrTests(unittest.TestCase):
    def test_image_source_scope(self):
        self.assertEqual(article_image_url(IMAGE, POST), IMAGE)
        self.assertEqual(article_image_url("/unite49files/article/a.png", POST),
                         "https://49217.com/unite49files/article/a.png")
        for url in (IMAGE.replace("/article/", "/comment/"), IMAGE.replace(":4949", ""),
                    IMAGE.replace("https:", "http:"), IMAGE.replace("jmz.jlidesign.com", "example.com"),
                    CDN + "/unite49files/article/%2e%2e/comment/a.png",
                    CDN + "/unite49files/article/%2Fa%5Cb.png"):
            with self.subTest(url=url):
                self.assertIsNone(article_image_url(url, POST))

    def test_public_detail_filters_comments_and_paid_previews(self):
        comment = IMAGE.replace("/article/", "/comment/")
        detail = {
            "articleId": 123, "type": 3, "lotteryType": 2, "pay": 0,
            "description": f'<img src="{comment}"><img src="{IMAGE}">',
            "pictureList": [{"pictureUrl": IMAGE}],
            "comments": [{"pictureList": [{"pictureUrl": IMAGE.replace("body", "reply")}]}],
        }
        counts = Counter()
        self.assertEqual(list(body_image_urls(detail, "123", POST, counts)), [IMAGE, IMAGE])
        self.assertEqual(counts["rejected_image_url"], 1)
        for pay in (None, 1, True, "1"):
            self.assertEqual(list(body_image_urls(dict(detail, pay=pay), "123", POST, counts)), [])
        self.assertEqual(list(body_image_urls(detail, "124", POST, counts)), [])
        self.assertEqual(counts["detail_not_confirmed_public"], 4)
        self.assertEqual(counts["detail_identity_mismatch"], 1)

    def test_hard_timeout_kills_worker(self):
        with PaddleWorker(target=slow_child, startup_timeout=5, inference_timeout=0.2) as worker:
            with self.assertRaisesRegex(TimeoutError, "OCR inference"):
                worker(Path("image.png"), True)
            self.assertIsNone(worker.process)

    def test_recognize_with_existing_preprocessing(self):
        class FakePaddle:
            def predict(self, path):
                self.path = path
                return [{"rec_texts": ["原始文字", " ", "invalid"],
                         "rec_scores": [0.88, 0.92, float("nan")]}]

        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "image.png"
            path.write_bytes(png("white"))
            engine = FakePaddle()
            self.assertEqual(recognize(engine, str(path), True),
                             [{"text": "原始文字", "confidence": 0.88}])
            self.assertTrue(engine.path.endswith(".ocr.png"))

    def test_retry_records_failure_and_succeeds(self):
        def op(attempt):
            if attempt == 1:
                raise TimeoutError("first")
            return "done"

        result, errors = retry(op, backoff=lambda _: None)
        self.assertEqual(result, "done")
        self.assertEqual(errors[0]["reason"], "TimeoutError: first")

    def test_dedup_cache_and_failure_reporting(self):
        candidates = [{"source_url": POST, "image_url": str(i)} for i in (1, 1, 2, 3, 4)]
        calls = []

        def download(url, _, target):
            target.write_bytes({"1": png("white"), "2": png("white"),
                                "3": b"<html>", "4": png("black")}[url])

        def ocr(path, preprocessed):
            calls.append(preprocessed)
            if path.read_bytes() == png("black"):
                return []
            return [{"text": "公开正文", "confidence": 0.9}]

        with tempfile.TemporaryDirectory() as directory, closing(sqlite3.connect(":memory:")) as db:
            result, failures, duplicates = collect_and_recognize(
                iter(candidates), Path(directory), db, ocr, download=download
            )
            self.assertEqual(duplicates, {"duplicate_urls": 1, "duplicate_sha256": 1})
            self.assertEqual(result[0]["raw_text"], "公开正文")
            self.assertEqual(result[0]["mean_confidence"], 0.9)
            self.assertEqual(result[1]["failure_reason"], "ValueError: no_text_detected")
            self.assertEqual(len(failures[0]["download_errors"]), 2)
            self.assertEqual(calls, [True, True, False])
            again, _, _ = collect_and_recognize(
                iter(candidates[:1]), Path(directory), db, ocr, download=download
            )
            self.assertTrue(again[0]["cache_hit"])
            self.assertEqual(calls, [True, True, False])
