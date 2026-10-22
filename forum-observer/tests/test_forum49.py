import json
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest import mock

from forum_observer import forum49


def mat(author, mode='include', counted=True, reason=None, picks=('鼠', '牛', '虎', '兔', '龙', '蛇')):
    return {'author': author, 'mode': mode, 'play': '六肖', 'picks': list(picks),
            'counted_for_ranking': counted, 'exclusion_reason': reason, 'source_url': 'u'}


RESULT = {
    'issue': '2026295',
    'evidence': [{'body': 'x'}, {'body': ' '}],
    'parsed_predictions': [mat('甲'), mat('乙', mode='exclude', picks=('鼠',)),
                           mat('丙', counted=False, reason='recommendation requires at least 6')],
    'ranking': [{'zodiac': '牛', 'score': 0.5}],
    'rejected': [],
}


class Forum49Test(unittest.TestCase):
    def test_summary_counts_only_counted_materials(self):
        s = forum49.summarize(RESULT, '2026295')
        self.assertEqual(s['model'], '49论坛模型')
        self.assertTrue(s['issue_matches_request'])
        self.assertEqual(s['valid_materials'], 2)
        self.assertEqual(s['distinct_valid_authors'], 2)
        self.assertEqual(s['material_counts'], {'recommend': 1, 'kill_zodiac': 1})
        self.assertEqual(s['nonempty_article_posts'], 1)
        self.assertEqual(len(s['materials']), 3)
        self.assertFalse(s['frozen'] or s['settled'] or s['published_to_ledger'])

    def test_issue_mismatch_is_flagged(self):
        self.assertFalse(forum49.summarize(RESULT, '2026296')['issue_matches_request'])

    def test_run_writes_one_file_per_run_and_never_overwrites(self):
        with TemporaryDirectory() as d:
            d = Path(d)
            (d / 'collector-config.json').write_text('{}')
            with mock.patch('forum_observer.page_collector.collect_page', return_value=RESULT), \
                 mock.patch.dict('os.environ', {'GITHUB_RUN_ID': '42'}):
                rec = forum49.run(forum49.DEFAULT_URL, '2026295', '澳门', d / 'out', d / 'arch', d / 'collector-config.json')
                target = d / 'arch/issues/2026295/run-42.json'
                self.assertTrue(rec['ok'])
                self.assertEqual(json.loads(target.read_text())['valid_materials'], 2)
                ev = json.loads((d / 'out/forum49-evidence.json').read_text())
                self.assertEqual((ev['issue'], len(ev['posts'])), ('2026295', 2))
                with self.assertRaises(SystemExit):
                    forum49.run(forum49.DEFAULT_URL, '2026295', '澳门', d / 'out', d / 'arch', d / 'collector-config.json')

    def test_collector_failure_is_recorded_not_hidden(self):
        with TemporaryDirectory() as d:
            d = Path(d)
            (d / 'collector-config.json').write_text('{}')
            with mock.patch('forum_observer.page_collector.collect_page', side_effect=ValueError('no forum posts')), \
                 mock.patch.dict('os.environ', {'GITHUB_RUN_ID': '7'}):
                rec = forum49.run(forum49.DEFAULT_URL, '2026295', '澳门', d / 'out', d / 'arch', d / 'collector-config.json')
                self.assertFalse(rec['ok'])
                self.assertIn('no forum posts', rec['error'])
                self.assertTrue((d / 'arch/issues/2026295/run-7.json').exists())


if __name__ == '__main__':
    unittest.main()
