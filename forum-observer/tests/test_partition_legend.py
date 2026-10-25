"""2026298 regression: complementary zodiac tables (独字/合字, 有边/无边) are
classification legends with no selected direction, so neither half is a vote
(AGENTS.md rule 4)."""
import unittest
from forum_observer.page_collector import extract_picks, score_posts, PARTITION_LEGEND_REASON
from forum_observer.questions import _question


def post(body, author='觅青森', title='298期实力打造独合肖'):
    return dict(author=author, issue='2026298', title=title, body=body,
                images=[], detail_url='https://example.org/' + author)


class PartitionLegendTests(unittest.TestCase):
    def test_real_298_tables_never_count(self):
        for author, line in (('觅青森', '独字生肖:鼠牛虎兔马羊 合字生肖:龙蛇猴鸡狗猪'),
                             ('心头的小情儿', '有边生肖:龙蛇猴鸡狗猪 无边生肖:鼠牛虎兔马羊')):
            ranking, rows = score_posts([post(line, author)])
            self.assertEqual(ranking, [], rows)
            self.assertEqual(len(rows), 2, rows)
            for row in rows:
                self.assertFalse(row['counted_for_ranking'], row)
                self.assertEqual(row['exclusion_reason'], PARTITION_LEGEND_REASON)
                self.assertEqual(row['vote_zodiacs'], [])

    def test_halves_on_separate_lines_are_also_a_legend(self):
        rows = extract_picks('有边生肖：龙蛇猴鸡狗猪\n无边生肖：鼠牛虎兔马羊')
        self.assertEqual({r['mode'] for r in rows}, {'unverified'})

    def test_single_group_recommendation_still_counts(self):
        ranking, rows = score_posts([post('298期六肖:鼠牛虎兔马羊')])
        self.assertTrue(rows[0]['counted_for_ranking'], rows)
        self.assertAlmostEqual(sum(r['score'] for r in ranking), 1, places=5)

    def test_overlapping_or_partial_groups_are_not_legends(self):
        ranking, rows = score_posts([post('298期六肖:鼠牛虎兔马羊\n298期六肖:鼠牛虎兔马猪')])
        self.assertTrue(any(r['counted_for_ranking'] for r in rows), rows)
        self.assertNotIn(PARTITION_LEGEND_REASON, [r['exclusion_reason'] for r in rows])

    def test_numbered_pick_complementing_a_history_line_is_not_a_legend(self):
        # 49论坛 盈运启川 pattern: an old issue's six and the current six are complementary.
        rows = extract_picks('298六肖中特实战自下注\n286 兔狗鸡虎龙羊，开羊\n298 鼠牛蛇马猴猪')
        self.assertNotIn(PARTITION_LEGEND_REASON, [r.get('parse_error') for r in rows])
        self.assertIn(['鼠', '牛', '蛇', '马', '猴', '猪'],
                      [r['picks'] for r in rows if r['mode'] == 'include'])

    def test_kill_line_with_complementary_recommendation_is_unchanged(self):
        rows = extract_picks('298期六肖:鼠牛虎兔马羊\n298期杀六肖:龙蛇猴鸡狗猪')
        self.assertEqual(sorted(r['mode'] for r in rows), ['exclude', 'include'])

    def test_legend_gets_a_specific_review_question(self):
        self.assertIn('分类对照表', _question('独字生肖:鼠牛虎兔马羊', PARTITION_LEGEND_REASON))

    def test_kill_line_is_never_a_title_scoped_recommendation(self):
        ranking, rows = score_posts([post('杀六肖:龙蛇猴鸡狗猪', '杀手', title='298期六肖')])
        self.assertFalse(any(r['mode'] == 'include' and r['counted_for_ranking'] for r in rows), rows)


if __name__ == '__main__':
    unittest.main()
