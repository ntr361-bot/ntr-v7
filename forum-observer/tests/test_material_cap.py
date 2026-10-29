"""49论坛模型 material cap (user decision 2026-10-29, from issue 303):
every featured material counts; ordinary materials fill up to the cap by card
click count; featured above the cap keeps all featured and no ordinary."""
import unittest

from forum_observer import forum49, pre_freeze
from forum_observer.page_collector import MATERIAL_CAP_REASON, score_posts

SIX = ['鼠牛虎兔龙蛇', '马羊猴鸡狗猪', '鼠虎龙马猴狗', '牛兔蛇羊鸡猪', '鼠牛龙马鸡狗', '虎兔蛇羊猴猪']


def post(n, featured=False, clicks=0, author=None, body=None, minute=0):
    return {'author': author or f'作者{n}', 'issue': '2026303', 'title': '六肖',
            'body': body or f'303期:六肖【{SIX[n % len(SIX)]}】',
            'detail_url': f'https://49217.com/#/corpusdetail?id={n}&type=3',
            'published_at': f'2026-10-30 10:{minute:02d}', 'images': [],
            'is_featured': featured, 'click_count': clicks, 'like_count': 0}


def counted(mats):
    return sorted(m['author'] for m in mats if m['counted_for_ranking'])


class MaterialCap(unittest.TestCase):
    def test_forum49_formal_cap_is_100(self):
        self.assertEqual(forum49.MATERIAL_CAP, 100)

    def test_featured_all_then_ordinary_by_clicks(self):
        posts = [post(1, featured=True), post(2, featured=True),
                 post(3, clicks=50), post(4, clicks=900), post(5, clicks=300)]
        _, mats = score_posts(posts, False, 4)
        self.assertEqual(counted(mats), ['作者1', '作者2', '作者4', '作者5'])
        cut = [m for m in mats if m['author'] == '作者3'][0]
        self.assertEqual(cut['exclusion_reason'], MATERIAL_CAP_REASON)
        self.assertEqual(cut['vote_zodiacs'], [])

    def test_featured_above_cap_keeps_all_featured_no_ordinary(self):
        posts = [post(i, featured=True) for i in range(1, 5)] + [post(9, clicks=99999)]
        _, mats = score_posts(posts, False, 3)
        self.assertEqual(counted(mats), ['作者1', '作者2', '作者3', '作者4'])

    def test_author_with_featured_material_is_secured_with_best_material(self):
        kill = post(1, featured=True, author='甲', body='303期杀【牛】')
        six = post(2, clicks=1, author='甲', minute=5)
        others = [post(3, clicks=900), post(4, clicks=800)]
        _, mats = score_posts([kill, six, *others], False, 2)
        chosen = [m for m in mats if m['author'] == '甲' and m['counted_for_ranking']]
        self.assertEqual(len(chosen), 1)
        self.assertEqual(chosen[0]['mode'], 'include')  # rule 2: six beats kill
        self.assertEqual(counted(mats), ['作者3', '甲'])  # cap 2: 甲 secured + top click

    def test_ranking_matches_counted_votes(self):
        posts = [post(1, featured=True)] + [post(i, clicks=i) for i in range(2, 8)]
        ranking, mats = score_posts(posts, False, 3)
        total = sum(m['weight_per_zodiac'] * len(m['vote_zodiacs']) for m in mats if m['counted_for_ranking'])
        self.assertAlmostEqual(sum(r['score'] for r in ranking), total)
        self.assertEqual(len(counted(mats)), 3)

    def test_no_cap_is_unchanged(self):  # 118论坛模型 passes no cap
        posts = [post(i, clicks=i) for i in range(1, 6)]
        _, mats = score_posts(posts, False)
        self.assertEqual(len(counted(mats)), 5)

    def test_pre_freeze_exclusion_backfills_by_clicks(self):
        posts = [post(1, featured=True), post(2, clicks=900), post(3, clicks=500)]
        ranking, mats = score_posts(posts, False, 2)
        self.assertEqual(counted(mats), ['作者1', '作者2'])
        status = {'issue': '2026303', 'material_cap': 2, 'evidence': posts,
                  'parsed_predictions': mats, 'draw_at': '2099-01-01T21:32:00+08:00',
                  'ranking': ranking, 'top6': []}
        target = [m for m in mats if m['author'] == '作者2'][0]
        spec = {'issue': '2026303', 'exclusions': [{
            'evidence_id': target['evidence_id'], 'mode': target['mode'],
            'picks': target['picks'], 'reason': 'test',
            'decided_at': '2026-10-30T20:00:00+08:00'}]}
        new, _ = pre_freeze.apply(status, spec)
        self.assertEqual(counted(new['parsed_predictions']), ['作者1', '作者3'])
        self.assertEqual(new['valid_materials'], 2)


if __name__ == '__main__':
    unittest.main()
