import unittest

from forum_observer.forum49_ocr_comments import discover_comments, image_url
from forum_observer.forum49_ocr_shadow import summarize

POST = "https://49217.com/#/corpusdetail?id=42&type=3"
CDN = "https://jmz.jlidesign.com:4949/unite49files"
COMMENT_IMAGE = CDN + "/comment/2026/11/26/image.jpg"
BODY_IMAGE = CDN + "/article/2026/11/26/image.jpg"


def comment(row_id, related=42, kind=3, status=1, url=COMMENT_IMAGE, **extra):
    return dict(id=row_id, relatedId=related, type=kind, status=status,
                pictureList=[{"pictureUrl": url}], **extra)


def article(endpoint, *, articleId):
    return {"articleId": articleId, "pay": 0, "type": 3, "lotteryType": 2, "year": 2026, "period": 330}


class CommentImageTests(unittest.TestCase):
    def test_latest_ignores_public_comments_from_other_posts_and_collects_replies(self):
        requests = []

        def read(endpoint, **params):
            requests.append((endpoint, params))
            if endpoint == "listLatest" and params["pageNum"] == 1:
                return {"list": [comment(27, related=77), comment(28, status=0),
                                 comment(29, url=BODY_IMAGE), comment(30, secondLevelCount=2,
                                     subCommentList=[comment(31, kind=6)])]}
            if endpoint == "listSecondComment":
                return {"firstLevelComment": {"id": 30, "relatedId": 42}, "list": [
                    comment(31, kind=6), comment(32, kind=6),
                    comment(33, kind=6, related=77)], "moreSecondComments": False}
            self.fail(f"unexpected comment endpoint: {endpoint}")

        diagnostics = {}
        results = list(discover_comments("2026330", ["42"], diagnostics, pages=2,
                                        read_article=article, read_comment=read))
        self.assertEqual([r["comment_id"] for r in results], ["30", "31", "32"])
        self.assertEqual(diagnostics["counts"]["unverified_comment_rows"], 3)
        self.assertEqual(requests[1][1]["commentId"], "30")
        self.assertEqual(diagnostics["counts"]["rejected_image_urls"], 1)

    def test_reply_page_has_to_match_verified_parent(self):
        def read(endpoint, **params):
            if endpoint == "listLatest":
                return {"list": [comment(30, secondLevelCount=1)]}
            return {"firstLevelComment": {"id": 9, "relatedId": 42},
                    "list": [comment(31, kind=6)]}
        diagnostics = {}
        rows = list(discover_comments("2026330", ["42"], diagnostics, pages=1,
                                     read_article=article, read_comment=read))
        self.assertEqual([row["comment_id"] for row in rows], ["30"])
        self.assertEqual(diagnostics["counts"]["unverified_reply_pages"], 1)

    def test_rejects_paid_or_other_period_posts_before_reading_comments(self):
        def read(endpoint, *, articleId):
            return {**article(endpoint, articleId=articleId),
                    **({"pay": 1} if articleId == "42" else {"period": 329})}
        def forbidden(*args, **kwargs):
            self.fail("should not fetch comments")
        diagnostics = {}
        self.assertEqual(list(discover_comments("2026330", ["42", "43"], diagnostics,
                                                pages=2, read_article=read, read_comment=forbidden)), [])
        self.assertEqual(diagnostics["counts"]["unverified_posts"], 2)

    def test_combines_identical_body_and_comment_image_and_retains_provenance(self):
        sha = "a" * 64
        common = {"sha256": sha, "source_url": POST}
        body = dict(common, image_url=BODY_IMAGE, lines=[{"text": "330期六肖:鼠牛虎兔龙蛇"}])
        reply = dict(common, image_url=COMMENT_IMAGE, comment_id="31", lines=[])
        result = summarize({"test_only": True, "used_for_freeze": False, "images": [body, reply]}, "2026330")
        self.assertEqual(result["coverage"]["unique_images"], 1)
        self.assertEqual(result["coverage"]["images_with_review_candidates"], 1)
        self.assertEqual(result["images"][0]["image_urls"], [BODY_IMAGE, COMMENT_IMAGE])
        self.assertEqual(result["images"][0]["comment_sources"][0]["comment_id"], "31")

    def test_comment_image_path_and_host(self):
        self.assertEqual(image_url(COMMENT_IMAGE, POST), COMMENT_IMAGE)
        self.assertIsNone(image_url(BODY_IMAGE, POST))
        self.assertIsNone(image_url(COMMENT_IMAGE.replace("jmz.jlidesign.com", "example.org"), POST))
        self.assertIsNone(image_url(CDN + "/comment/%2e%2e/article/image.jpg", POST))


if __name__ == "__main__":
    unittest.main()
