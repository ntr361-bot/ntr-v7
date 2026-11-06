import unittest

from forum_observer.page_collector import score_posts


def post(body, title='六肖中特', author='作者', issue='2026310'):
    return {'author': author, 'issue': issue, 'title': title, 'body': body,
            'detail_url': 'https://example.test/#/d/' + author,
            'published_at': '2026-11-06T10:00:00+08:00'}


class OtherIssueLineTest(unittest.TestCase):
    """User decision 2026-11-06: every row fails closed on another issue's
    line or a drawn result, so a multi-issue record post counts its current row."""

    def test_multi_issue_record_counts_current_row_only(self):
        body = ('310六肖中特（牛兔龙狗猪羊）\n'
                '309六肖中特（虎龙狗鼠牛蛇）马13X\n'
                '304六肖中特（牛兔马狗鼠鸡）狗21✔️')
        _, mats = score_posts([post(body)])
        counted = [''.join(m['picks']) for m in mats if m['counted_for_ranking']]
        self.assertEqual(counted, ['牛兔龙狗猪羊'])

    def test_drawn_result_line_is_not_counted(self):
        _, mats = score_posts([post('六肖 羊猴鸡狗龙牛 开虎42✓')])
        self.assertFalse(any(m['counted_for_ranking'] for m in mats))


if __name__ == '__main__':
    unittest.main()
