"""Regression tests for dynamic forum detail verification (no website access)."""
import unittest
from unittest.mock import Mock, patch

from forum_observer.page_collector import _detail, _wait_detail


class DetailVerificationTests(unittest.TestCase):
    def test_exact_article_author_and_detail_route_required(self):
        page=Mock()
        page.url='https://example.com/#/corpusdetail/17'
        page.locator.return_value.inner_text.return_value='作者甲\n预测标题\n推荐六肖 鼠牛虎兔龙蛇'
        diagnostic={}
        self.assertTrue(_wait_detail(page,dict(author='作者甲',title='预测标题'),
                                     timeout_seconds=0.5,diagnostic=diagnostic))
        self.assertEqual(diagnostic['verification'],'verified')

    def test_reject_feed_even_if_title_and_author_visible(self):
        page=Mock()
        page.url='https://example.com/#/forum'
        page.locator.return_value.inner_text.return_value='作者甲\n预测标题'
        diagnostic={}
        self.assertFalse(_wait_detail(page,dict(author='作者甲',title='预测标题'),
                                      timeout_seconds=0,diagnostic=diagnostic))
        self.assertEqual(diagnostic['verification'],'detail_url_missing')

    @patch('forum_observer.page_collector._listing')
    @patch('forum_observer.page_collector._wait_detail')
    def test_failed_deep_link_retries_original_feed_card(self,verify,listing):
        target=dict(author='作者甲',title='预测标题',issue='2026287',
                    href='https://example.com/#/corpusdetail/17',
                    key=('作者甲','2026287','刚刚','预测标题'))
        verify.side_effect=[False,True]
        listing.return_value=[target]
        page=Mock()
        page.url='https://example.com/#/forum'
        page.locator.return_value.evaluate_all.return_value=True
        diag={}
        result=_detail(page,'https://example.com/#/forum',target,'澳门',2,
                       diagnostic=diag)
        self.assertTrue(result)
        self.assertEqual(page.goto.call_count,2)
        self.assertEqual(diag['navigation'],'feed_card_fallback')
        self.assertTrue(diag['card_clicked'])

    @patch('forum_observer.page_collector._listing')
    @patch('forum_observer.page_collector._wait_detail')
    def test_never_accept_unverified_detail(self,verify,listing):
        target=dict(author='作者甲',title='预测标题',issue='2026287',
                    href='https://example.com/#/corpusdetail/17',
                    key=('作者甲','2026287','刚刚','预测标题'))
        verify.return_value=False
        listing.return_value=[]
        page=Mock()
        page.locator.return_value.evaluate_all.return_value=False
        page.evaluate.return_value={'at_end':True}
        page.locator.return_value.filter.return_value.count.return_value=0
        self.assertFalse(_detail(page,'https://example.com/#/forum',
                                 target,'澳门',1,diagnostic={}))


if __name__=='__main__':
    unittest.main()
