import unittest

from forum_observer.forum49_ocr_shadow import inspect_lines, summarize


def recognized(*text):
    return [{"text": line, "confidence": 0.9} for line in text]


class ShadowSummaryTests(unittest.TestCase):
    def test_other_issue_resets_current_period(self):
        lines = recognized("329期九肖:鼠牛虎兔龙蛇马羊猴", "330期九肖",
                           "【猴豬羊蛇鸡鼠龙兔虎】", "331期九肖:猪狗鸡猴羊马蛇龙兔")
        periods, evidence = inspect_lines(lines, "2026330")
        self.assertEqual(periods, ["2026329", "2026330", "2026331"])
        self.assertEqual([(row["period_line"], row["line"]) for row in evidence], [(2, 3)])
        self.assertEqual(evidence[0]["text"], "【猴豬羊蛇鸡鼠龙兔虎】")

    def test_ambiguous_and_wrong_year_are_excluded(self):
        lines = recognized("2025年第330期九肖:龙狗羊鸡猪猴牛马虎",
                           "329期和330期九肖", "【龙狗羊鸡猪猴牛马虎】")
        periods, evidence = inspect_lines(lines, "2026330")
        self.assertEqual(periods, ["2025330", "2026329", "2026330"])
        self.assertEqual(evidence, [])

    def test_sha256_dedup_keeps_source_links_and_distinguishes_missing_text(self):
        def image(digest, url, lines):
            return {"sha256": digest * 64, "source_url": f"https://example.com/post/{url}",
                    "image_url": f"https://example.com/img/{url}", "lines": lines}

        report = {
            "test_only": True, "used_for_freeze": False, "issue": "2026330",
            "issue_post_coverage": 10, "download_failures": [{}],
            "images": [image("a", "one", recognized("330期九肖:龙狗羊鸡猪猴牛马虎")),
                       image("a", "two", []), image("b", "three", recognized("329期")),
                       image("c", "four", recognized("样本")), image("d", "five", [])],
        }
        result = summarize(report, "2026330")
        self.assertFalse(result["used_for_freeze"])
        self.assertEqual(result["coverage"], {
            "issue_post_coverage": 10, "audited_image_posts": 5, "unique_images": 4,
            "images_with_ocr_text": 3, "images_showing_target_issue": 1,
            "images_showing_other_issues_only": 1, "images_without_period_marker": 1,
            "images_without_ocr_text": 1, "images_with_review_candidates": 1,
            "failed_downloads": 1,
        })
        self.assertEqual(len(result["images"][0]["source_urls"]), 2)

    def test_rejects_formal_report_and_mismatched_issue(self):
        with self.assertRaises(ValueError):
            summarize({"test_only": True, "used_for_freeze": True}, "2026330")
        with self.assertRaises(ValueError):
            summarize({"test_only": True, "used_for_freeze": False, "issue": "2026331"}, "2026330")


if __name__ == "__main__":
    unittest.main()
