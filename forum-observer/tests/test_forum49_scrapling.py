import json
import sys
import types
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest import mock

from forum_observer import forum49_scrapling as fs


def post(author, body, title='295期六肖'):
    return {'author': author, 'issue': '2026295', 'title': title, 'body': body,
            'comments': '', 'images': [], 'detail_url': 'https://49217.com/#/corpusdetail?id=' + author,
            'published_at': '2026-10-22T10:00:00+08:00'}


class CompareTest(unittest.TestCase):
    def test_identical_bodies_agree(self):
        ps = [post('甲', '295期六肖：鼠牛虎兔龙蛇'), post('乙', '295期杀二肖：虎狗')]
        r = fs.compare(ps, [dict(p) for p in ps])
        self.assertTrue(r['agree'])
        self.assertEqual(r['tally']['same'], 2)
        self.assertEqual(r['primary_valid'], 2)

    def test_detects_each_kind_of_disagreement(self):
        primary = [post('甲', '295期六肖：鼠牛虎兔龙蛇'), post('乙', '295期杀二肖：虎狗'), post('丙', '无资料')]
        other = [post('甲', '295期六肖：鼠牛虎兔龙马'), post('乙', '广告'), post('丙', '295期杀二肖：龙猴')]
        r = fs.compare(primary, other)
        status = {row['author']: row['status'] for row in r['authors']}
        self.assertEqual(status, {'甲': 'different', '乙': 'primary_only', '丙': 'scrapling_only'})
        self.assertFalse(r['agree'])

    def test_text_agreement_ignores_whitespace(self):
        self.assertTrue(fs.text_agreement('六肖：鼠 牛\n虎', '六肖：鼠牛虎')['identical'])
        t = fs.text_agreement('六肖：鼠牛虎', '六肖：鼠牛马')
        self.assertFalse(t['identical'])
        self.assertLess(t['ratio'], 1.0)

    def test_article_bounding_reuses_primary_rules(self):
        page = '顶部开奖记录 鼠\n295期六肖\n正文 六肖：鼠牛虎兔龙蛇\n相关推荐\n别的帖子\n最新评论\n评论内容'
        body = fs.article_from_text(page, post('甲', ''))
        self.assertIn('鼠牛虎兔龙蛇', body)
        self.assertNotIn('顶部开奖记录', body)
        self.assertNotIn('别的帖子', body)
        self.assertNotIn('评论内容', body)


class RunTest(unittest.TestCase):
    def _evidence(self, d, posts):
        f = d / 'ev.json'
        f.write_text(json.dumps({'issue': '2026295', 'posts': posts}, ensure_ascii=False))
        return f

    def test_run_records_comparison_and_never_overwrites(self):
        ps = [post('甲', '295期六肖：鼠牛虎兔龙蛇'), post('乙', 'x')]
        fetched = [{'status': 'ok', 'body': '295期六肖：鼠牛虎兔龙蛇', 'seconds': 1.0},
                   {'status': 'no_detail_shell', 'seconds': 2.0}]
        with TemporaryDirectory() as d, \
                mock.patch.dict(sys.modules, {'scrapling': types.ModuleType('scrapling')}), \
                mock.patch.object(fs, 'fetch_bodies', return_value=fetched), \
                mock.patch.dict('os.environ', {'GITHUB_RUN_ID': '9'}):
            d = Path(d)
            rec = fs.run(self._evidence(d, ps), d / 'out.json', d / 'arch')
            self.assertTrue(rec['ok'])
            self.assertFalse(rec['feeds_scoring'])
            self.assertEqual(rec['checked_posts'], 1)
            self.assertEqual(rec['fetch_status_counts'], {'ok': 1, 'no_detail_shell': 1})
            self.assertTrue(rec['materials']['agree'])
            self.assertTrue((d / 'arch/issues/2026295/scrapling-9.json').exists())
            with self.assertRaises(SystemExit):
                fs.run(self._evidence(d, ps), d / 'out.json', d / 'arch')

    def test_missing_scrapling_is_recorded_not_hidden(self):
        with TemporaryDirectory() as d, \
                mock.patch.dict(sys.modules, {'scrapling': None}), \
                mock.patch.dict('os.environ', {'GITHUB_RUN_ID': '10'}):
            d = Path(d)
            rec = fs.run(self._evidence(d, [post('甲', 'x')]), d / 'out.json', d / 'arch')
            self.assertFalse(rec['ok'])
            self.assertIn('Error', rec['error'])


if __name__ == '__main__':
    unittest.main()
