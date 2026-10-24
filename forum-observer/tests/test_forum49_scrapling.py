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


class AdapterTest(unittest.TestCase):
    def fake_module(self, **attrs):
        pkg = types.ModuleType('scrapling'); pkg.__version__ = '9.9'
        sub = types.ModuleType('scrapling.fetchers')
        for k, v in attrs.items():
            setattr(sub, k, v)
        pkg.fetchers = sub
        return {'scrapling': pkg, 'scrapling.fetchers': sub}

    def test_prefers_session_then_falls_back_to_older_fetchers(self):
        class Old:
            def fetch(self, url, **kw):
                return ('old', url, kw)
        with mock.patch.dict(sys.modules, self.fake_module(PlayWrightFetcher=Old)):
            name, fetch, close = fs._open_fetcher()
            self.assertEqual(name, 'PlayWrightFetcher@9.9')
            self.assertEqual(fetch('u', timeout=1)[0:2], ('old', 'u'))

        class Dyn:
            @classmethod
            def fetch(cls, url, **kw):
                return ('dyn', url)
        with mock.patch.dict(sys.modules, self.fake_module(DynamicFetcher=Dyn, PlayWrightFetcher=Old)):
            name, fetch, _ = fs._open_fetcher()
            self.assertEqual((name, fetch('u')), ('DynamicFetcher@9.9', ('dyn', 'u')))

    def test_stealth_browser_is_last_resort(self):
        class Stealth:
            @classmethod
            def fetch(cls, url, **kw):
                return 'stealth'
        class Dyn:
            @classmethod
            def fetch(cls, url, **kw):
                return 'dyn'
        with mock.patch.dict(sys.modules, self.fake_module(StealthyFetcher=Stealth, Fetcher=object)):
            self.assertEqual(fs._open_fetcher()[0], 'StealthyFetcher@9.9')
        with mock.patch.dict(sys.modules, self.fake_module(StealthyFetcher=Stealth, DynamicFetcher=Dyn)):
            self.assertEqual(fs._open_fetcher()[0], 'DynamicFetcher@9.9')

    def test_stealth_session_reuses_one_browser_before_per_call_fetcher(self):
        events = []

        class StealthSession:
            def __init__(self, headless=True, disable_resources=False):
                events.append(('init', headless, disable_resources))
            def __enter__(self):
                events.append('enter'); return self
            def __exit__(self, *a):
                events.append('exit')
            def fetch(self, url, **kw):
                return ('session', url)
        class Stealth:
            @classmethod
            def fetch(cls, url, **kw):
                return 'stealth'
        mods = self.fake_module(StealthySession=StealthSession, StealthyFetcher=Stealth, Fetcher=object)
        with mock.patch.dict(sys.modules, mods):
            name, fetch, close = fs._open_fetcher()
            self.assertEqual(name, 'StealthySession@9.9')
            self.assertEqual(fetch('a'), ('session', 'a'))
            self.assertEqual(fetch('b'), ('session', 'b'))
            close()
        # one launch for both posts; unsupported options (network_idle) dropped
        self.assertEqual(events, [('init', True, True), 'enter', 'exit'])

    def test_plain_fetcher_still_preferred_over_stealth_session(self):
        class StealthSession:
            def __init__(self, **kw): pass
        class Dyn:
            @classmethod
            def fetch(cls, url, **kw):
                return 'dyn'
        with mock.patch.dict(sys.modules, self.fake_module(StealthySession=StealthSession, DynamicFetcher=Dyn)):
            self.assertEqual(fs._open_fetcher()[0], 'DynamicFetcher@9.9')

    def test_session_restarts_after_failure_and_records_only_that_post(self):
        opened = []

        class Flaky:
            def __init__(self, **kw):
                opened.append(kw)
                self.n = len(opened)
            def __enter__(self): return self
            def __exit__(self, *a): pass
            def fetch(self, url, **kw):
                if self.n == 1 and url == 'bad':
                    raise RuntimeError('browser crashed')
                return url
        with mock.patch.dict(sys.modules, self.fake_module(StealthySession=Flaky)):
            _, fetch, close = fs._open_fetcher()
            with self.assertRaises(RuntimeError):
                fetch('bad')
            self.assertEqual(fetch('good'), 'good')
            close()
        self.assertEqual(len(opened), 2)

    def test_no_post_limit_by_default(self):
        posts = [post(str(i), '295期杀二肖：虎狗') for i in range(5)]
        seen = []

        class Page:
            status = 200
            def find(self, _):
                return self
            def get_all_text(self, **kw):
                return '295期六肖\n正文\n阅读数\n最新评论'
        def fetch(url, **kw):
            seen.append(url); return Page()
        with mock.patch.object(fs, '_open_fetcher', return_value=('T', fetch, lambda: None)):
            _, results = fs.fetch_bodies(posts, 60, 0)
            self.assertEqual(len(seen), 5)
            self.assertNotIn('not_checked_post_limit', {r['status'] for r in results})
            seen.clear()
            _, results = fs.fetch_bodies(posts, 60, 2)
            self.assertEqual(len(seen), 2)
            self.assertEqual(sum(r['status'] == 'not_checked_post_limit' for r in results), 3)

    def test_reports_available_names_when_no_browser_fetcher(self):
        with mock.patch.dict(sys.modules, self.fake_module(Fetcher=object)):
            with self.assertRaisesRegex(ImportError, 'available: Fetcher'):
                fs._open_fetcher()

    def test_posts_with_material_are_checked_first(self):
        posts = [post('甲', '广告'), post('乙', '295期杀二肖：虎狗', title='聊天'), post('丙', '无')]
        self.assertEqual(fs.priority_order(posts)[0], 1)


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
                mock.patch.object(fs, 'fetch_bodies', return_value=('TestFetcher@0', fetched)), \
                mock.patch.dict('os.environ', {'GITHUB_RUN_ID': '9'}):
            d = Path(d)
            rec = fs.run(self._evidence(d, ps), d / 'out.json', d / 'arch')
            self.assertTrue(rec['ok'])
            self.assertFalse(rec['feeds_scoring'])
            self.assertEqual(rec['checked_posts'], 1)
            self.assertEqual(rec['fetch_status_counts'], {'ok': 1, 'no_detail_shell': 1})
            self.assertTrue(rec['materials']['agree'])
            self.assertEqual(rec['fetcher'], 'TestFetcher@0')
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
