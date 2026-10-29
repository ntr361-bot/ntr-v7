"""User-confirmed parsing rules of 2026-10-29 (forum featured posts, issue 302).

Each case is the literal current-issue line of a real featured post.
"""
import unittest

from forum_observer.core import SCORING_RULE_VERSION, vote_zodiacs
from forum_observer.page_collector import (extract_picks, extract_titled_bare_list,
                                           line_issue_guard, normalize_kill_emoji,
                                           score_posts)


def post(body, title='t', author='a', issue='2026302', url='https://x.test/1'):
    return {'author': author, 'issue': issue, 'title': title, 'body': body,
            'detail_url': url, 'published_at': '2026-10-29 10:00', 'images': []}


def counted(body, title='t', strict=True):
    _, picks = score_posts([post(body, title)], strict_issue_lines=strict)
    return [(p['mode'], ''.join(p['picks'])) for p in picks if p['counted_for_ranking']]


class ConfirmedPlays(unittest.TestCase):
    def test_rule_version_bumped(self):
        self.assertEqual(SCORING_RULE_VERSION, '2026-10-29-one-author-9-8-7-6-priority-v9')

    def test_six_with_number_ignores_number(self):  # 广东壹码神
        self.assertEqual(counted('302期（六肖中特）【马龙狗猴兔牛+24】开：？🀄️'),
                         [('include', '马龙狗猴兔牛')])

    def test_result_placeholder_is_not_doubt(self):  # 迪丽热巴
        self.assertEqual(counted('302期:金牌六肖【猴牛鸡兔龙蛇】開？00准'),
                         [('include', '猴牛鸡兔龙蛇')])

    def test_real_question_still_uncertain(self):
        self.assertEqual(counted('302期:金牌六肖【猴牛鸡兔龙蛇】？不确定'), [])

    def test_kill_variants(self):
        cases = {
            '302期：稳杀两肖｛蛇虎｝开：00🀄️': '蛇虎',           # 功夫女足
            '302期：绝杀两肖【猪蛇】开：00✅': '猪蛇',            # 大赢家发发
            '302期：绝杀三肖《蛇虎猪》开：00✅': '蛇虎猪',
            '302期杀【虎猪】': '虎猪',                          # 策马奔驰
            '302期:∧期期绝杀((猪蛇马))开:？00准杀庄必备': '猪蛇马',  # 我的任性你不懂
            '302期:★绝杀二肖→〖鸡蛇〗开:？00中': '鸡蛇',           # 谁是谁的主角
            '302期:绝杀２肖－〖虎牛〗－開:0000准': '虎牛',           # 奥巴码
        }
        for line, kill in cases.items():
            with self.subTest(line=line):
                self.assertEqual(counted(line), [('exclude', kill)])

    def test_declared_kill_count_must_match(self):
        with self.assertRaises(ValueError):
            vote_zodiacs(['蛇', '虎', '猪'], 'exclude', '杀两肖')

    def test_emoji_kill(self):
        self.assertEqual(normalize_kill_emoji('302期:杀🐯🐎开'), '302期:杀虎马开')
        self.assertEqual(normalize_kill_emoji('295期杀鸡🐔'), '295期杀鸡')
        self.assertEqual(counted('302期杀鸡🐔', strict=False), [('exclude', '鸡')])

    def test_three_colour_two_chosen_is_eight(self):  # 全仓梭哈只赢不输
        body = ('302期:红蓝绿肖【蓝红肖】开:？00准\n'
                '红肖：马、兔、鼠、鸡\n蓝肖：蛇、虎、猪、猴\n绿肖：羊、龙、牛、狗')
        self.assertEqual(counted(body), [('include', '蛇虎猪猴马兔鼠鸡')])

    def test_three_colour_missing_legend_not_counted(self):
        body = '302期:红蓝绿肖【蓝红肖】开:？00准\n红肖：马、兔、鼠、鸡\n蓝肖：蛇、虎、猪、猴'
        self.assertEqual(counted(body), [])

    def test_titled_bare_list_uses_listed_zodiacs(self):  # 我要发工资了
        body = '302期:龙猪牛兔羊狗猴开\n302期:杀🐯🐎开'
        self.assertEqual(counted(body, title='302期：八肖中特➕杀1️⃣肖'),
                         [('include', '龙猪牛兔羊狗猴')])

    def test_bare_list_needs_title_and_six(self):
        self.assertEqual(extract_titled_bare_list('302期:龙猪牛兔羊狗猴开', '精选资料'), [])
        self.assertEqual(extract_titled_bare_list('302期:龙猪牛兔羊开', '八肖中特'), [])

    def test_le_xiang_tian_xia_first_six_kill(self):  # 乐享天下：按前面的六肖杀
        self.assertEqual(counted('302期杀。马蛇兔虎猴龙。牛猪个人不要。杀火行。'),
                         [('exclude', '马蛇兔虎猴龙')])
        self.assertEqual(counted('302期杀。杀鼠龙蛇猴牛马鸡。杀土行。'), [])
        self.assertEqual(counted('302期杀。没上。'), [])


class FailClosedGuards(unittest.TestCase):
    """Only rows recognised because of the 2026-10-29 rules are guarded;
    rows the earlier parser already produced keep their behaviour."""

    def test_previously_recognised_rows_unchanged(self):
        self.assertEqual(counted('302期六肖【鸡龙蛇牛猪鼠】防狗'), [('include', '鸡龙蛇牛猪鼠')])

    def test_other_issue_line(self):
        self.assertEqual(line_issue_guard('296绝杀二肖《兔马》√', '2026299'),
                         'source line names another issue')
        self.assertIsNone(line_issue_guard('230六肖 | 300：龙兔虎狗牛鸡。', '2026300'))
        self.assertEqual(line_issue_guard('16期杀（兔）✔', '2026298'),
                         'source line names another issue')

    def test_drawn_result_line(self):
        self.assertEqual(line_issue_guard('299期：稳杀两肖【马猪】开猪08✅', '2026299'),
                         'source line already shows a drawn result')
        self.assertIsNone(line_issue_guard('302期杀：龙马兔牛➢➢➢➤开：虎XX✅', '2026302'))

    def test_ban_mark_split_bracket_hedge_and_should(self):
        self.assertEqual(counted('302期绝杀兔  🈲虎鼠牛', strict=False), [])
        self.assertEqual(counted('302期：杀（蛇）（猴）开'), [])
        self.assertEqual(counted('302期:金牌六肖【鸡龙蛇牛猪鼠】防狗 開？00准'), [])
        self.assertEqual(counted('302期绝杀兔虎，应该还可以杀', strict=False), [])


if __name__ == '__main__':
    unittest.main()
