import tempfile
import unittest
from datetime import datetime
from unittest.mock import patch
from pathlib import Path
from forum_observer.audit import run
from forum_observer.page_collector import current_feed_boundary, LOCAL_TZ


class HistoricalAuditTests(unittest.TestCase):
    def test_explicit_period_does_not_stop_at_next_period_boundary(self):
        stamp=datetime.now(LOCAL_TZ).strftime('%Y-%m-%d %H:%M')
        rows=[dict(author=str(i),issue='2026285',published_at=stamp,title='资料') for i in range(2)]
        rows += [dict(author=str(i),issue='2026284',published_at=stamp,title='资料') for i in range(5)]
        self.assertEqual(current_feed_boundary(rows),'current_issue_boundary_reached')
        self.assertIsNone(current_feed_boundary(rows,'2026284'))
        rows += [dict(author=str(i),issue='2026283',published_at=stamp,title='资料') for i in range(5)]
        self.assertEqual(current_feed_boundary(rows,'2026284'),'current_issue_boundary_reached')

    def test_test_output_cannot_be_ready_or_frozen(self):
        result=dict(issue='2026284',status='ready_observation',selected_count=30,
                    ranking=[],raw_count=30,source_url='https://example.org')
        with tempfile.TemporaryDirectory() as folder, patch('forum_observer.audit.collect_page',return_value=result) as collector:
            saved=run(dict(forum_url='https://example.org',freeze_enabled=True),'2026284',folder)
            self.assertEqual(saved['status'],'historical_test_only')
            self.assertFalse(saved['prediction_eligible'])
            self.assertTrue(saved['test_only'])
            self.assertEqual(saved['selected_count'],0)
            self.assertFalse(collector.call_args.args[-1]['freeze_enabled'])
            self.assertEqual({p.name for p in Path(folder).iterdir()},{'status.json','2026284'})
