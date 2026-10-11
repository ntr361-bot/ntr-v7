import unittest
from forum_observer.page_collector import score_posts


class EqualWeightTests(unittest.TestCase):
    def post(self, title):
        return {'author': '甲', 'issue': '2026282', 'title': title,
                'body': '', 'images': [], 'detail_url': 'https://example.org/post'}

    def test_exclusion_distributes_one_unit_to_remaining_zodiacs(self):
        rows, works = score_posts([self.post('杀二肖：鼠牛')])
        scores = {row['zodiac']: row['score'] for row in rows}
        self.assertEqual(scores['鼠'], 0)
        self.assertEqual(scores['牛'], 0)
        self.assertAlmostEqual(sum(scores.values()), 1)
        self.assertTrue(works[0]['counted_for_ranking'])

    def test_short_recommendation_is_excluded(self):
        rows, works = score_posts([self.post('推荐三肖：鼠牛虎\n精选六肖：鼠牛虎兔龙蛇')])
        self.assertEqual(sum(w['counted_for_ranking'] for w in works), 1)
        self.assertAlmostEqual(sum(row['score'] for row in rows), 1, places=5)

    def test_excluding_all_zodiacs_is_not_a_valid_vote(self):
        rows, works = score_posts([self.post('杀生肖：鼠牛虎兔龙蛇马羊猴鸡狗猪')])
        self.assertEqual(rows, [])
        self.assertFalse(works[0]['counted_for_ranking'])

    def test_six_distinct_zodiacs_boundary(self):
        for title in ('推荐五肖：鼠牛虎兔龙', '推荐六肖：鼠鼠牛虎兔龙'):
            rows, works = score_posts([self.post(title)])
            self.assertEqual(rows, [])
            self.assertIn('6 distinct', works[0]['exclusion_reason'])
        rows, works = score_posts([self.post('推荐六肖：鼠牛虎兔龙蛇')])
        self.assertTrue(works[0]['counted_for_ranking'])
        self.assertAlmostEqual(sum(r['score'] for r in rows), 1, places=5)

    def test_kill_numbers_never_become_kill_zodiacs(self):
        rows, works = score_posts([self.post('绝杀10码：鼠牛虎')])
        self.assertEqual(rows, [])
        self.assertEqual(works[0]['exclusion_reason'], 'kill numbers are not kill zodiacs')

    def test_recommendations_and_kills_combine_equal_weights(self):
        rows, works = score_posts([self.post('推荐六肖：鼠牛虎兔龙蛇\n杀二肖：鼠牛')])
        self.assertEqual(sum(w['counted_for_ranking'] for w in works), 2)
        self.assertAlmostEqual(sum(r['score'] for r in rows), 2, places=5)
        self.assertAlmostEqual(next(r['score'] for r in rows if r['zodiac']=='鼠'), 1/6, places=5)

    def test_structured_input_obeys_same_rules(self):
        from forum_observer.core import vote_zodiacs
        with self.assertRaises(ValueError):
            vote_zodiacs(['鼠','牛','虎'], 'include', '推荐三肖')
        with self.assertRaises(ValueError):
            vote_zodiacs(['鼠','牛'], 'exclude', '绝杀10码')
        self.assertEqual(len(vote_zodiacs(['鼠','牛'], 'exclude', '杀二肖')), 10)
