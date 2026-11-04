"""User decisions 2026-11-04 (308期 review), formal scoring of both models.

1. A line that opens with another explicit issue number is a track record,
   never this issue's material ("304六肖中特（牛兔马狗鼠鸡）狗21✔️" in a 308 post).
2. "308，羊蛇龙虎鼠狗兔鸡，开？" under a "N肖" title is a recommendation of the
   zodiacs actually listed.
3. Inside one post, a "替换版" line replaces the author's original line.
"""
import unittest

from forum_observer.page_collector import score_posts


def post(body, title='记录', author='作者', issue='2026308'):
    return {'author': author, 'issue': issue, 'title': title, 'body': body,
            'detail_url': 'https://example.test/#/d/' + author,
            'published_at': '2026-11-04 18:00'}


def counted(mats):
    return [m for m in mats if m['counted_for_ranking']]


class OtherIssueLineTest(unittest.TestCase):
    def test_track_record_rows_never_beat_the_current_line(self):
        body = ('308六肖中特（猪牛虎猴龙马）\n'
                '307六肖中特（猴马狗牛羊虎）牛42✔️\n'
                '304六肖中特（牛兔马狗鼠鸡）狗21✔️')
        rows = counted(score_posts([post(body, title='308', author='赢六千万')])[1])
        self.assertEqual([''.join(r['picks']) for r in rows], ['猪牛虎猴龙马'])

    def test_identical_older_row_does_not_hide_current_line(self):
        body = '284绝杀一肖：蛇✓\n307绝杀一肖：马✓\n308绝杀一肖：蛇'
        rows = counted(score_posts([post(body, title='绝杀一肖', author='偷偷进村')])[1])
        self.assertEqual(len(rows), 1)
        self.assertIn('308', rows[0]['excerpt'])

    def test_post_with_only_track_records_has_no_material(self):
        body = '306 兔龙猴鸡羊马开15\n307 狗牛鼠虎猴马开42'
        self.assertEqual(counted(score_posts([post(body, title='六肖')])[1]), [])

    def test_current_line_without_marker_still_counts(self):
        rows = counted(score_posts([post('六肖：马鼠鸡兔蛇猴', title='试水六肖')])[1])
        self.assertEqual(len(rows), 1)


class CommaIssueBareListTest(unittest.TestCase):
    def test_comma_issue_line_under_n_zodiac_title(self):
        body = ('308，羊蛇龙虎鼠狗兔鸡，开？\n307，马兔虎牛鼠狗猴羊，开牛\n'
                '306，兔猴牛猪鼠虎龙鸡羊 开龙\n297 兔猪羊马牛狗蛇鸡猴开马')
        rows = counted(score_posts([post(body, title='精选9肖', author='岭南特码兴')])[1])
        self.assertEqual(len(rows), 1)
        self.assertEqual(''.join(rows[0]['picks']), '羊蛇龙虎鼠狗兔鸡')
        self.assertEqual(rows[0]['mode'], 'include')


class ReplacementLineTest(unittest.TestCase):
    def test_replacement_line_is_preferred_in_same_post(self):
        body = '308期🔪鼠马蛇猪（原公式）\n替换版🔪虎蛇猴猪'
        rows = counted(score_posts([post(body, title='自用杀四肖', author='八闽郡王')])[1])
        self.assertEqual([''.join(r['picks']) for r in rows], ['虎蛇猴猪'])

    def test_replacement_order_independent(self):
        body = '替换版🔪虎蛇猴猪\n308期🔪鼠马蛇猪（原公式）'
        rows = counted(score_posts([post(body, title='自用杀四肖', author='八闽郡王')])[1])
        self.assertEqual([''.join(r['picks']) for r in rows], ['虎蛇猴猪'])


if __name__ == '__main__':
    unittest.main()
