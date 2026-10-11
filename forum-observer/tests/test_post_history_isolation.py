import unittest
from unittest.mock import Mock
from forum_observer.page_collector import (
    _article_body, previous_period_records, current_period_text,
    score_posts, _period_marker, _issue, _title_span
)


class ForumPeriodBoundariesTests(unittest.TestCase):
    def test_read_only_author_article_not_top_draw_banner(self):
        top=("澳门第282期\n第281期\n22 19 21 32 48 38 + 10\n"
             "鸡/火\n")
        actual=("港澳百家网【无错二波中特】\n"
                "280期：特码波路→【绿波】【红波】←开:龙15 错\n"
                "281期：特码波路→【蓝波】【绿波】←开:鸡10\n准\n"
                "282期：特码波路→【蓝波】【绿波】←开:？\n"
                "最新评论\n成为第一个评论的人\n")
        page=Mock()
        page.locator.return_value.inner_text.return_value=top+actual
        result=_article_body(page,{'title':'港澳百家网【无错二波中特】'})
        self.assertIn('281期：特码波路',result)
        self.assertNotIn('第281期\n22 19',result)
        self.assertNotIn('鸡/火',result)
        self.assertNotIn('成为第一个评论的人',result)

    def test_title_with_spaced_rendering_is_still_article_boundary(self):
        title="港澳百家网【无错二波中特】"
        content="第281期 开鸡10\\n港澳百家网【无错 二波中特】\\n281期：蓝波 绿波"
        span=_title_span(content,title)
        self.assertIsNotNone(span)
        self.assertNotIn("开鸡10",content[span[1]:])

    def test_historical_281_record_in_post_282_is_not_prediction(self):
        text=("280期：特码波路→【绿波】【红波】←开:龙15 错\n"
              "281期：特码波路→【蓝波】【绿波】←开:鸡10\n准\n"
              "282期：特码波路→【蓝波】【绿波】←开:？\n")
        post={'issue':'2026282','author':'节外生枝',
              'detail_url':'https://example.org/corpusdetail/282',
              'published_at':'2026-10-08 22:49'}
        refs=previous_period_records(text,'2026281',post)
        self.assertEqual(len(refs),1)
        self.assertIn('蓝波',refs[0]['content'])
        self.assertIn('开:鸡10',refs[0]['content'])
        self.assertEqual(refs[0]['source_post_issue'],'2026282')
        self.assertFalse(refs[0]['counted_for_ranking'])
        self.assertEqual(refs[0]['classification'],'retrospective_after_draw_not_prediction')

    def test_no_previous_period_retro_when_post_is_same_issue(self):
        post={'issue':'2026281','author':'作者','detail_url':'https://example.org/281'}
        self.assertEqual(previous_period_records('281期：推荐三肖：鼠牛虎','2026281',post),[])

    def test_282_vote_cannot_come_from_281_historic_rows(self):
        post={'author':'甲','issue':'2026282','title':'282期作品',
              'body':'281期：推荐三肖：鼠牛虎\n282期：推荐六肖：龙蛇马羊猴鸡',
              'images':[],'detail_url':'https://example.org/corpusdetail/282'}
        ranking,works=score_posts([post])
        self.assertEqual(len(works),1)
        self.assertEqual(works[0]['picks'],['龙','蛇','马','羊','猴','鸡'])
        self.assertEqual(sum(x['counted_for_ranking'] for x in works),1)
        self.assertEqual(ranking[0]['zodiac'],'龙')
        self.assertAlmostEqual(next(x['score'] for x in ranking if x['zodiac']=='鼠'),0.0)

    def test_previous_result_header_without_post_title_is_not_article(self):
        page=Mock()
        page.locator.return_value.inner_text.return_value='第281期 鸡10 澳门第282期'
        self.assertEqual(_article_body(page,{'title':'找不到的282帖'}),'')

    def test_history_markers_distinguish_full_and_short_issue(self):
        self.assertEqual(_period_marker('第281期：开鸡10','2026'),'2026281')
        self.assertEqual(_period_marker('2026281期：开鸡10','2026'),'2026281')
        self.assertIsNone(_period_marker('2026/10/08 281帖','2026'))
        self.assertEqual(_issue('2026282'),'2026282')


if __name__=='__main__':
    unittest.main()
