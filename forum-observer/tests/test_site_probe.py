import tempfile
import unittest
from pathlib import Path
from unittest.mock import Mock

from forum_observer.site_probe import collect, navigation_targets, parse_snapshot, public_url


class SiteProbeTests(unittest.TestCase):
    def test_unicode_and_fragment_preserved(self):
        url = public_url('https://yydsddd.이미지갤러리갤.닷컴:8443/#0411/')
        self.assertIn('xn--', url)
        self.assertTrue(url.endswith(':8443/#0411/'))

    def test_private_and_non_web_targets_rejected(self):
        for url in ('http://127.0.0.1/', 'http://10.0.0.1/', 'http://localhost/',
                    'file:///etc/passwd', 'javascript:alert(1)', 'https://u:p@example.com/'):
            with self.subTest(url=url), self.assertRaises(ValueError):
                public_url(url)

    def test_binary_gate_and_relative_next(self):
        target = 'https://example.com:8443/two/'
        bits = ''.join(f'{ord(c):08b}' for c in target)
        html = f'<script>var urlArray=["{bits}"]; String.fromCharCode(n)</script>'
        self.assertEqual(navigation_targets(html, 'https://entrance.example/'), [target])
        self.assertEqual(navigation_targets('<script>const next = "./finish.html";</script>',
                                           target), [target + 'finish.html'])

    def test_xor_web_entrance(self):
        key = 'abc'
        def encode(s):
            return ''.join(f'{ord(c)^ord(key[i%3]):02x}' for i,c in enumerate(s))
        html = (f'<script>var enkey="{key}"; const uHead = d(\'{encode("https://")}\');'
                f'const uFoot = d(\'{encode(".example.com/#/home")}\');'
                'l_xl_3; randomString(7);</script>')
        self.assertEqual(navigation_targets(html, 'https://entrance.example'),
                         ['https://probe01.example.com/#/home'])

    def test_commented_redirect_does_not_hide_web_entrance(self):
        html = '<script>\n// window.location.href="https://"+websitelist[index]\n</script>'
        self.assertEqual(navigation_targets(html, 'https://example.com/'), [])

    def test_navigation_images_do_not_become_material(self):
        with tempfile.TemporaryDirectory() as directory:
            result = parse_snapshot('<title>入口</title><a>继续访问网页版</a><img src="ad.png">',
                                    'https://example.com/', Path(directory))
            self.assertEqual(result['status'], 'navigation_only')
            self.assertEqual(result['verified_material_count'], 0)
            self.assertEqual(result['image_candidates'][0]['url'], 'https://example.com/ad.png')
            self.assertEqual(result['forum_diagnostic']['status'], 'forum_container_not_found')

    def test_spa_shell_needs_rendering(self):
        with tempfile.TemporaryDirectory() as directory:
            result = parse_snapshot('<div id="app"></div><script src="app.js"></script>',
                                    'https://example.com/', Path(directory))
            self.assertEqual(result['status'], 'dynamic_render_required')

    def test_network_failure_persists_checkpoint_and_no_overwrite(self):
        with tempfile.TemporaryDirectory() as directory:
            out = Path(directory)
            result = collect('https://example.com', out,
                             fetcher=Mock(side_effect=TimeoutError('test timeout')))
            self.assertEqual(result['status'], 'error')
            self.assertEqual(result['error_type'], 'TimeoutError')
            self.assertEqual(len(result['hops']), 1)
            self.assertTrue((out / 'report.json').exists())
            with self.assertRaises(ValueError):
                collect('https://example.com', out)


if __name__ == '__main__':
    unittest.main()
