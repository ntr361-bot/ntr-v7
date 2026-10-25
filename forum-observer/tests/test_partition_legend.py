"""Complementary zodiac groups (独字/合字, 有边/无边): only the group the post
explicitly selects for the current issue counts (user confirmed 2026-10-26,
same principle as 胆大小); without a selection neither half is a vote (rule 4)."""
import unittest
from forum_observer.page_collector import (extract_picks, score_posts, PARTITION_LEGEND_REASON,
                                           PARTITION_UNSELECTED_REASON)
from forum_observer.questions import _question


def post(body, author='觅青森', title='298期实力打造独合肖'):
    return dict(author=author, issue='2026298', title=title, body=body,
                images=[], detail_url='https://example.org/' + author)


class PartitionLegendTests(unittest.TestCase):
    def test_tables_without_a_selection_never_count(self):
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
        self.assertIn('明确选择', _question('独字生肖:鼠牛虎兔马羊', PARTITION_LEGEND_REASON))

    def test_kill_line_is_never_a_title_scoped_recommendation(self):
        ranking, rows = score_posts([post('杀六肖:龙蛇猴鸡狗猪', '杀手', title='298期六肖')])
        self.assertFalse(any(r['mode'] == 'include' and r['counted_for_ranking'] for r in rows), rows)

    def test_real_298_posts_count_only_the_selected_group(self):
        # Exact 2026298 article texts (history rows 294-297 are dropped by period).
        cases = (('心头的小情儿', '298期:有无生肖',
                  '294期有无生肖：  无肖 开兔28准\n297期有无生肖：  有肖 开马13准\n'
                  '298期有无生肖：  无肖 开00准\n有边生肖:龙蛇猴鸡狗猪 无边生肖:鼠牛虎兔马羊',
                  '六肖·无边组'),
                 ('觅青森', '298期实力打造独合肖',
                  '295期(独合肖)【合肖】开马25错\n297期(独合肖)【独肖】开马13准\n'
                  '298期(独合肖)【独肖】开00准\n独字生肖:鼠牛虎兔马羊 合字生肖:龙蛇猴鸡狗猪',
                  '六肖·独字组'))
        for author, title, body, play in cases:
            ranking, rows = score_posts([post(body, author, title)])
            counted = [r for r in rows if r['counted_for_ranking']]
            self.assertEqual(len(counted), 1, rows)
            self.assertEqual(counted[0]['play'], play)
            self.assertEqual(counted[0]['picks'], list('鼠牛虎兔马羊'))
            other = [r for r in rows if not r['counted_for_ranking']]
            self.assertEqual([r['exclusion_reason'] for r in other], [PARTITION_UNSELECTED_REASON])

    def test_selection_of_the_other_side_and_history_only_selection(self):
        rows = extract_picks('298期(独合肖)【合肖】开00准\n独字生肖:鼠牛虎兔马羊 合字生肖:龙蛇猴鸡狗猪')
        self.assertEqual([''.join(r['picks']) for r in rows if r['mode'] == 'include'], ['龙蛇猴鸡狗猪'])
        # Only an older issue names a side: current_period_text drops it, so no vote.
        ranking, rows = score_posts([post('297期(独合肖)【合肖】开马13准\n298期(独合肖)开00准\n'
                                          '独字生肖:鼠牛虎兔马羊 合字生肖:龙蛇猴鸡狗猪', '甲', '298期独合肖')])
        self.assertEqual(ranking, [], rows)
        # Both sides named for the current issue is ambiguous.
        rows = extract_picks('298期有无生肖： 有肖 无肖\n有边生肖:龙蛇猴鸡狗猪 无边生肖:鼠牛虎兔马羊')
        self.assertEqual({r['mode'] for r in rows}, {'unverified'})


if __name__ == '__main__':
    unittest.main()
