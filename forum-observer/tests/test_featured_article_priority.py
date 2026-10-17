import unittest
from forum_observer.progress import ArticleProgress
from forum_observer.page_collector import prioritize_posts
from forum_observer.report import article_lines


class FeaturedArticlePriorityTests(unittest.TestCase):
    def test_featured_precedes_favored_ordinary_even_before_sampling_limit(self):
        items=[{'author':'榜单普通','is_featured':False},
               {'author':'加精一','is_featured':True},
               {'author':'普通','is_featured':False},
               {'author':'加精二','is_featured':True}]
        ordered=prioritize_posts(items,{'榜单普通'})
        self.assertEqual([x['author'] for x in ordered[:2]],['加精一','加精二'])

    def test_first_ordinary_preserves_proof_that_featured_checks_finished(self):
        p=ArticleProgress()
        featured={'author':'甲','is_featured':True}; ordinary={'author':'乙'}
        p.queue([ordinary,featured])
        p.mark(featured,'attempted');p.mark(featured,'completed')
        before=p.snapshot()
        p.mark(ordinary,'attempted')
        audit=p.snapshot()['priority_audit']
        self.assertEqual(audit['state'],'verified')
        self.assertEqual(audit['first_ordinary_attempt']['featured_completed'],1)
        self.assertEqual(audit['first_ordinary_attempt']['featured_queued'],1)
        self.assertIsNone(before['priority_audit']['first_ordinary_attempt'])

    def test_ordinary_before_pending_featured_is_explicit_violation(self):
        p=ArticleProgress()
        featured={'author':'甲','is_featured':True}; ordinary={'author':'乙'}
        p.queue([ordinary,featured]);p.mark(ordinary,'attempted')
        self.assertEqual(p.snapshot()['priority_audit']['state'],'violated')
        self.assertEqual(p.snapshot()['priority_audit']['violation_count'],1)

    def test_report_uses_article_counters_and_never_legacy_comment_values(self):
        result={'article_details':{
            'featured':{'queued':7,'attempted':6,'opened':5,'extracted':4,'completed':6},
            'ordinary':{'queued':9,'attempted':2,'opened':1,'extracted':1,'completed':2}},
            'featured_selected_threads':0,'featured_attempted_threads':0,
            'comment_checks':[{'images_seen':999}],
            'priority_audit':{'state':'verified'}}
        text='\n'.join(article_lines(result))
        self.assertIn('加精正文：入队：7篇；尝试：6篇；成功打开：5篇；提取正文：4篇',text)
        self.assertIn('普通正文：入队：9篇；尝试：2篇',text)
        self.assertNotIn('999',text)
        self.assertNotIn('评论',text)

    def test_legacy_report_does_not_invent_zero_body_counts(self):
        text='\n'.join(article_lines({'featured_attempted_threads':0}))
        self.assertIn('尝试：未统计',text)
        self.assertIn('未记录，不能确认',text)
