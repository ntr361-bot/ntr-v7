"""Explicit named group + two picks: always one verifiable eight-zodiac vote."""
import unittest
from forum_observer.page_collector import extract_picks, score_posts, unparsed_evidence
from forum_observer.evidence_audit import audit_material


DOMESTIC = '家禽：牛、马、羊、鸡、狗、猪'
WILD = '野兽：鼠、虎、兔、龙、蛇、猴'


def post(body, author='叶落归根', issue='2026287', title='287期家野两肖'):
    return dict(author=author, issue=issue, title=title, body=body,
                images=[], detail_url='https://example.org/article')


class NamedGroupComboTests(unittest.TestCase):
    def test_real_287_case_domestic_plus_monkey_rat(self):
        body = '287期家野两肖【家禽+猴鼠】开:？00准\n' + DOMESTIC + '\n' + WILD
        ranking, rows = score_posts([post(body)])
        self.assertEqual(len(rows), 1, rows)
        row = rows[0]
        self.assertTrue(row['counted_for_ranking'], rows)
        self.assertEqual(row['picks'], list('牛马羊鸡狗猪猴鼠'))
        self.assertEqual(row['vote_zodiacs'], list('牛马羊鸡狗猪猴鼠'))
        self.assertAlmostEqual(row['weight_per_zodiac'], 0.125)
        self.assertEqual(len(ranking), 12)
        self.assertAlmostEqual(sum(r['score'] for r in ranking), 1, places=5)
        self.assertEqual(row['exclusion_reason'], None)

    def test_wild_plus_ox_dog_composes_eight(self):
        body = '287期家野两肖【野兽＋牛狗】开？00准\n'+DOMESTIC+'\n'+WILD
        ranking, rows = score_posts([post(body)])
        self.assertTrue(rows[0]['counted_for_ranking'], rows)
        self.assertEqual(rows[0]['picks'], list('鼠虎兔龙蛇猴牛狗'))
        self.assertEqual(len([r for r in ranking if r['score'] > 0]), 8)

    def test_livestock_name_supported_only_with_matching_legend(self):
        body = '287期家野两肖【家畜+猴龙】开00准\n家畜：牛马羊鸡狗猪\n野兽：鼠虎兔龙蛇猴'
        ranking,rows = score_posts([post(body)])
        self.assertEqual(rows[0]['picks'], list('牛马羊鸡狗猪猴龙'))
        self.assertTrue(rows[0]['counted_for_ranking'])
        self.assertAlmostEqual(sum(r['score'] for r in ranking),1,places=5)

    def test_no_legend_never_uses_customary_group_meaning(self):
        for legend in ('', '家禽：牛马羊鸡狗猪',
                       '家禽：牛马羊鸡狗猪\n野兽：鼠虎兔龙蛇狗',
                       '家禽：牛马羊鸡狗猪\n野兽：鼠虎兔龙蛇猴\n野兽：鼠虎兔龙蛇猴',
                       '家禽：牛马羊鸡狗猪\n野兽：鼠虎兔龙蛇猴\n家畜：牛马羊鸡狗猪'):
            ranking, rows = score_posts([post('287期家野两肖【家禽+猴鼠】\n'+legend)])
            self.assertEqual(ranking, [], (legend, rows))
            self.assertEqual(len(rows), 1)
            self.assertFalse(rows[0]['counted_for_ranking'], rows)

    def test_repeated_or_overlapping_extras_never_count(self):
        for extra in ('猴猴','猴鸡','猴鼠兔'):
            ranking, rows=score_posts([post('287期家野两肖【家禽+'+extra+'】\n'+DOMESTIC+'\n'+WILD)])
            self.assertEqual(ranking, [], (extra, rows))

    def test_uncertain_or_kill_direction_rejected(self):
        for expression in ('可能家禽+猴鼠', '杀家禽+猴鼠', '排除家禽+猴鼠'):
            ranking,rows = score_posts([post('287期家野两肖【'+expression+'】\n'+DOMESTIC+'\n'+WILD)])
            self.assertEqual(ranking, [], (expression, rows))

    def test_group_definitions_alone_do_not_vote(self):
        self.assertEqual(extract_picks(DOMESTIC+'\n'+WILD), [])

    def test_audit_rejects_corrupted_expansion(self):
        rows = extract_picks('287期家野两肖【家禽+猴鼠】\n'+DOMESTIC+'\n'+WILD)
        self.assertEqual(len(rows),1)
        row=rows[0]
        self.assertIsNone(audit_material(row, '287期家野两肖【家禽+猴鼠】\n'+DOMESTIC+'\n'+WILD))
        self.assertIn('audit:', audit_material(dict(row,picks=list('牛马羊鸡狗猪猴狗')),
                                                '287期家野两肖【家禽+猴鼠】\n'+DOMESTIC+'\n'+WILD))
        self.assertIn('audit:',audit_material(dict(row,excerpt=row['excerpt'].replace('猪','鼠')),
                                             '287期家野两肖【家禽+猴鼠】\n'+DOMESTIC+'\n'+WILD))

    def test_one_author_still_at_most_one_vote(self):
        body = ('287期家野两肖【家禽+猴鼠】\n'+DOMESTIC+'\n'+WILD
                +'\n推荐六肖：鼠牛虎兔龙蛇')
        ranking, rows = score_posts([post(body)])
        adopted=[r for r in rows if r['counted_for_ranking']]
        self.assertEqual(len(adopted),1,rows)
        self.assertEqual(adopted[0]['play'],'八肖（家野组合）')
        self.assertAlmostEqual(sum(r['score'] for r in ranking),1,places=5)

    def test_front_back_legacy_eight_still_valid(self):
        body = ('287期前后六肖【后六肖+虎牛】开:？00准\n'
                '前六肖：鼠牛虎兔龙蛇\n后六肖：马羊猴鸡狗猪')
        ranking, rows = score_posts([post(body, title='287期前后生肖')])
        self.assertEqual(len(rows),1,rows)
        self.assertTrue(rows[0]['counted_for_ranking'], rows)
        self.assertEqual(rows[0]['picks'],list('马羊猴鸡狗猪虎牛'))
        self.assertAlmostEqual(sum(r['score'] for r in ranking),1,places=5)


if __name__=='__main__': unittest.main()
