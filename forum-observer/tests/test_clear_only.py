import unittest
from forum_observer.page_collector import score_posts, prioritize_posts, unparsed_evidence
from forum_observer.core import calculate

class ClearOnlyTests(unittest.TestCase):
    def post(self, body, author='甲', title='284期'):
        return dict(author=author, issue='2026284', title=title, body=body,
                    images=[], detail_url='https://example.org/'+author)

    def test_ambiguity_negation_and_missing_text_rejected(self):
        for prefix in ('不确定是否','不是','取消','可能','待核实'):
            ranks, records=score_posts([self.post(prefix+'杀肖:鼠牛虎兔龙蛇')])
            self.assertEqual(ranks, [])
            self.assertFalse(records[0]['counted_for_ranking'])
        self.assertEqual(score_posts([self.post('六肖:鼠牛虎兔龙蛇？')])[0], [])

    def test_ambiguous_title_applies_to_body(self):
        self.assertEqual(score_posts([self.post('六肖:鼠牛虎兔龙蛇',title='待确认资料')])[0], [])

    def test_unsupported_composites_and_kill_counts_rejected(self):
        for text in ('284期:前后二肖【后肖+虎牛】\n后肖(马羊猴鸡狗猪)',
                     '固定杀肖\n1次:羊鸡兔龙\n2次:猴\n尽中尽\n0次:牛兔龙鸡'):
            p=self.post(text)
            self.assertEqual(score_posts([p])[0], [])
            self.assertTrue(unparsed_evidence([p]))

    def test_partial_post_only_counts_explicit_current_kill(self):
        p=self.post('彬哥:鼠\n小婉:马\n0:牛羊猴兔鸡\n1:马狗蛇龙\n2:鼠虎猪\n另一组尽位\n282:杀兔\n283:杀鸡\n284:杀虎')
        ranks,records=score_posts([p])
        self.assertEqual(len(records),1)
        self.assertEqual(records[0]['picks'],['虎'])
        self.assertTrue(records[0]['counted_for_ranking'])
        self.assertEqual(next(r['score'] for r in ranks if r['zodiac']=='虎'),0)
        self.assertTrue(unparsed_evidence([p]))

    def test_mismatched_quantity_rejected(self):
        self.assertEqual(score_posts([self.post('二肖:鼠牛虎兔龙蛇')])[0], [])

    def test_priority_stable_and_outside_retained(self):
        items=[self.post('',a) for a in ('外1','榜2','外2','榜1')]
        self.assertEqual([p['author'] for p in prioritize_posts(items,{'榜1','榜2'})],
                         ['榜2','榜1','外1','外2'])

    def test_no_ratio_or_stop_at_25(self):
        works=[dict(author=str(i),source_group='outside',issue='2026284',play='六肖',
                    source_url='https://example.org/'+str(i),published_at='2026-10-08T19:00:00+08:00',
                    picks=list('鼠牛虎兔龙蛇'),evidence='明确六肖:鼠牛虎兔龙蛇') for i in range(31)]
        result=calculate('2026284','2026-10-11T21:00:00+08:00',works,set())
        self.assertEqual(result['selected_count'],31)
        self.assertEqual(result['selected_leaderboard'],0)
        self.assertEqual(result['selected_outside'],31)
        self.assertAlmostEqual(sum(r['score'] for r in result['ranking']),31,places=5)
