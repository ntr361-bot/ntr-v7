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

    def test_different_plays_by_same_author_each_have_one_unit(self):
        rows, works = score_posts([self.post('推荐三肖：鼠牛虎\n精选六肖：鼠牛虎兔龙蛇')])
        self.assertEqual(sum(w['counted_for_ranking'] for w in works), 2)
        self.assertAlmostEqual(sum(row['score'] for row in rows), 2, places=5)

    def test_excluding_all_zodiacs_is_not_a_valid_vote(self):
        rows, works = score_posts([self.post('杀生肖：鼠牛虎兔龙蛇马羊猴鸡狗猪')])
        self.assertEqual(rows, [])
        self.assertFalse(works[0]['counted_for_ranking'])
