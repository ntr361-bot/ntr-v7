import unittest
from forum_observer.page_collector import _PostParser

class PageCollectorTests(unittest.TestCase):
    def test_parses_rendered_post_fields(self):
        parser = _PostParser()
        parser.feed('<ul><li><div class="name">作者甲</div><div class="time">2026-10-08 19:42</div><div class="slabel">281</div><div class="formtitle">精选六肖</div><div class="text"><div>鼠 虎</div></div><img src="https://example.org/a.jpg"></li></ul>')
        self.assertEqual(len(parser.posts),1)
        self.assertEqual(parser.posts[0]['author'].strip(),'作者甲')
        self.assertEqual(parser.posts[0]['issue'].strip(),'281')
        self.assertIn('鼠',parser.posts[0]['body'])

if __name__ == '__main__': unittest.main()
