from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

try:
    from scrapling.parser import Selector as _ScraplingSelector
except ImportError:
    _ScraplingSelector = None

from forum_observer.scrapling_assist import compare_listing, inspect_listing


@unittest.skipIf(_ScraplingSelector is None, "Scrapling is installed only for collection runs")
class ScraplingAssistTests(unittest.TestCase):
    def setUp(self):
        self.tempdir = tempfile.TemporaryDirectory()
        self.storage = Path(self.tempdir.name) / "selectors.sqlite3"
        self.url = "https://x5k1pok.11852.com:8443/#/forum"

    def tearDown(self):
        self.tempdir.cleanup()

    def test_parses_only_verified_forum_cards_and_detail_links(self):
        html = """<div class="forum-list"><ul>
          <li><span class="name">作者甲</span><span class="time">2026-10-20 20:01</span>
          <span class="slabel">第293期</span><span class="ntool"><span class="num">澳门</span></span>
          <span class="formtitle">推荐六肖</span>
          <a href="#/corpusdetail?id=12345&type=3">正文</a></li>
          <li><span class="name">广告账号</span><span class="time">2026-10-20 20:02</span>
          <span class="slabel">293</span><span class="ntool"><span class="num">香港</span></span>
          <span class="formtitle">无关类别</span>
          <a href="#/corpusdetail?id=12346&type=3">正文</a></li>
        </ul></div>"""
        result = inspect_listing(html, self.url, "澳门", self.storage)
        self.assertEqual(result["status"], "ok")
        self.assertEqual(result["candidate_count"], 1)
        self.assertEqual(result["unique_authors"], ["作者甲"])
        self.assertEqual(result["verified_detail_link_count"], 1)

    def test_adaptive_container_recovery_is_diagnostic_only(self):
        original = """<div class="forum-list"><ul>
          <li><span class="name">作者乙</span><span class="time">2026-10-20 20:03</span>
          <span class="slabel">第293期</span><span class="ntool"><span class="num">澳门</span></span>
          <span class="formtitle">推荐六肖</span>
          <a href="#/corpusdetail?id=12347&type=3">正文</a></li></ul></div>"""
        changed = original.replace('class="forum-list"', 'class="feed-stream"')
        first = inspect_listing(original, self.url, "澳门", self.storage)
        self.assertEqual(first["status"], "ok")
        second = inspect_listing(changed, self.url, "澳门", self.storage)
        self.assertEqual(second["status"], "ok")
        self.assertEqual(second["selector_method"], "adaptive")
        self.assertEqual(second["unique_authors"], ["作者乙"])

    def test_comparison_reports_difference_without_mutating_playwright_rows(self):
        html = """<div class="forum-list"><ul>
          <li><span class="name">作者丙</span><span class="time">2026-10-20 20:04</span>
          <span class="slabel">2026293</span><span class="ntool"><span class="num">澳门</span></span>
          <span class="formtitle">推荐六肖</span>
          <a href="#/corpusdetail?id=12348&type=3">正文</a></li></ul></div>"""
        primary = []
        result = compare_listing(html, self.url, "澳门", primary, self.storage)
        self.assertEqual(result["scrapling_only_count"], 1)
        self.assertEqual(result["scrapling_only_authors"], ["作者丙"])
        self.assertEqual(primary, [])


if __name__ == "__main__":
    unittest.main()
