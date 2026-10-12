import unittest
from forum_observer.page_collector import extract_picks, score_posts, unparsed_evidence

LEGEND='前肖(鼠牛虎兔龙蛇) 后肖(马羊猴鸡狗猪)'

class FrontBackEightTests(unittest.TestCase):
    def score(self,body,images=None):
        return score_posts([dict(author='作者甲',issue='2026284',title='284期资料',
                                 body=body,images=images or [],detail_url='https://example.org/284')])

    def test_back_plus_tiger_ox_is_one_eight_vote(self):
        ranks,records=self.score('284期:前后二肖【后肖+虎牛】 特开:?00准\n'+LEGEND)
        self.assertEqual(len(records),1)
        self.assertEqual(records[0]['picks'],list('马羊猴鸡狗猪虎牛'))
        self.assertTrue(records[0]['counted_for_ranking'])
        self.assertEqual(records[0]['weight_per_zodiac'],1/8)
        self.assertAlmostEqual(sum(r['score'] for r in ranks),1)
        self.assertTrue(records[0]['definition_evidence'])

    def test_front_plus_dog_goat(self):
        _,records=self.score('284期:前后二肖【前肖＋狗羊】\n'+LEGEND)
        self.assertEqual(records[0]['picks'],list('鼠牛虎兔龙蛇狗羊'))
        self.assertTrue(records[0]['counted_for_ranking'])

    def test_missing_incorrect_conflicting_legends_rejected(self):
        for legend in ('','后肖(马羊猴鸡狗猪)',
                       '前肖(鼠牛虎兔龙蛇) 后肖(马羊猴鸡狗虎)',
                       LEGEND+'\n后肖(马羊猴鸡狗虎)'):
            ranks,records=self.score('284期:前后二肖【后肖+虎牛】\n'+legend)
            self.assertEqual(ranks,[])
            self.assertFalse(records[0]['counted_for_ranking'])

    def test_overlap_or_duplicate_is_not_eight(self):
        for extra in ('虎虎','虎马'):
            ranks,records=self.score('284期:前后二肖【后肖+'+extra+'】\n'+LEGEND)
            self.assertEqual(ranks,[])
            self.assertIn('8 distinct',records[0]['exclusion_reason'])

    def test_uncertainty_or_kill_direction_rejected(self):
        for prefix in ('待确认','可能','杀','排除'):
            self.assertEqual(self.score(prefix+'前后二肖【后肖+虎牛】\n'+LEGEND)[0],[])

    def test_history_excluded_and_ocr_text_uses_same_parser(self):
        text='283期:前后二肖【前肖+狗羊】 特开:牛18准\n284期:前后二肖【后肖+虎牛】 特开:?00准\n'+LEGEND
        verified_image={'src':'https://jmz.chunshengsh.com/tk118files/article/a.png',
                        'ocr':text,'ocr_status':'recognized_consistent',
                        'period_verified':True}
        # Test the history parser with image evidence that has passed both OCR
        # agreement and source/date checks. Raw OCR alone must not be a vote.
        self.assertEqual(self.score('',[dict(verified_image,ocr_status='not_read_from_card')])[0],[])
        _,records=self.score('',[verified_image])
        self.assertEqual(len(records),1)
        self.assertEqual(records[0]['picks'],list('马羊猴鸡狗猪虎牛'))

    def test_definition_alone_never_votes(self):
        self.assertEqual(extract_picks(LEGEND),[])

    def test_accepted_composite_not_reported_as_unparsed(self):
        line='284期:前后二肖【后肖+虎牛】 特开:?00准'
        post=dict(author='甲',issue='2026284',title='284期',body=line+'\n'+LEGEND,
                  images=[],detail_url='https://example.org/a')
        self.assertNotIn(line,[r.get('excerpt') for r in unparsed_evidence([post])])
