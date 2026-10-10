import json
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest.mock import Mock

from forum_observer.author_history import incremental_scan
from forum_observer.page_collector import _listing
from forum_observer.cycle import sync


class ScanFailureTests(unittest.TestCase):
    def test_listing_is_one_dom_snapshot_without_per_card_waits(self):
        page = Mock()
        page.locator.return_value.evaluate_all.return_value = [
            dict(author='甲', issue='283', published_at='刚刚', title='三肖',
                 body='鼠牛虎', category='澳门', href=''),
            dict(author='乙', issue='283', published_at='刚刚', title='三肖',
                 body='鼠牛虎', category='香港', href='')]
        posts = _listing(page, '澳门')
        self.assertEqual(len(posts), 1)
        self.assertEqual(posts[0]['issue'], '2026283')
        page.locator.assert_called_once_with('.forum-list > li')
        page.locator.return_value.evaluate_all.assert_called_once()
        page.locator.return_value.all.assert_not_called()

    def test_expired_scan_returns_incomplete_without_browser_work(self):
        page = Mock()
        rows, audit = incremental_scan(page, Mock(), deadline=0)
        self.assertEqual(rows, [])
        self.assertTrue(audit['truncated'])
        self.assertFalse(audit['end_confirmed'])
        self.assertEqual(audit['stop_reason'], 'scan_time_budget_exhausted')
        page.evaluate.assert_not_called()

    def test_missing_issue_preserves_previous_archive(self):
        with TemporaryDirectory() as tmp:
            root = Path(tmp); out = root/'out'; archive = root/'archive'
            out.mkdir(); archive.mkdir()
            previous = {'issue':'2026282','status':'incomplete_observation'}
            (archive/'latest.json').write_text(json.dumps(previous))
            (out/'status.json').write_text(json.dumps({
                'issue':None,'status':'incomplete_observation','stage':'forum_navigation',
                'raw_count':0,'fetched_at':'2026-10-10T04:00:00+00:00'}))
            history = root/'history.json'
            history.write_text(json.dumps({'records':[]}))
            sync(out, archive, history)
            self.assertEqual(json.loads((archive/'latest.json').read_text()), previous)
            self.assertIsNone(json.loads((archive/'collection-failure.json').read_text())['issue'])
            self.assertFalse((archive/'issues'/'None').exists())
