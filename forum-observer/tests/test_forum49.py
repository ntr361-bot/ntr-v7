import json
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest import mock

from forum_observer import forum49


def mat(author, mode='include', counted=True, reason=None, picks=('鼠', '牛', '虎', '兔', '龙', '蛇')):
    return {'author': author, 'mode': mode, 'play': '六肖', 'picks': list(picks),
            'counted_for_ranking': counted, 'exclusion_reason': reason, 'source_url': 'u'}


RESULT = {
    'issue': '2026295',
    'evidence': [{'body': 'x'}, {'body': ' '}],
    'parsed_predictions': [mat('甲'), mat('乙', mode='exclude', picks=('鼠',)),
                           mat('丙', counted=False, reason='recommendation requires at least 6')],
    'ranking': [{'zodiac': '牛', 'score': 0.5}],
    'rejected': [],
}


class Forum49Test(unittest.TestCase):
    def test_summary_counts_only_counted_materials(self):
        s = forum49.summarize(RESULT, '2026295')
        self.assertEqual(s['model'], '49论坛模型')
        self.assertTrue(s['issue_matches_request'])
        self.assertEqual(s['valid_materials'], 2)
        self.assertEqual(s['distinct_valid_authors'], 2)
        self.assertEqual(s['material_counts'], {'recommend': 1, 'kill_zodiac': 1})
        self.assertEqual(s['nonempty_article_posts'], 1)
        self.assertEqual(len(s['materials']), 3)
        self.assertFalse(s['frozen'] or s['settled'] or s['published_to_ledger'])

    def test_issue_mismatch_is_flagged(self):
        self.assertFalse(forum49.summarize(RESULT, '2026296')['issue_matches_request'])

    def test_run_writes_one_file_per_run_and_never_overwrites(self):
        with TemporaryDirectory() as d:
            d = Path(d)
            (d / 'collector-config.json').write_text('{}')
            with mock.patch('forum_observer.page_collector.collect_page', return_value=RESULT), \
                 mock.patch('forum_observer.report.render') as render, \
                 mock.patch.object(forum49, 'collection_budget', return_value=600), \
                 mock.patch.dict('os.environ', {'GITHUB_RUN_ID': '42'}):
                rec = forum49.run(forum49.DEFAULT_URL, '2026295', '澳门', d / 'out', d / 'arch', d / 'collector-config.json')
                target = d / 'arch/issues/2026295/run-42.json'
                self.assertTrue(rec['ok'])
                self.assertEqual(rec['max_collection_seconds'], 600)
                render.assert_called_once()
                self.assertEqual(render.call_args[0][1], d / 'out/2026295/report.md')
                self.assertEqual(json.loads(target.read_text())['valid_materials'], 2)
                ev = json.loads((d / 'out/forum49-evidence.json').read_text())
                self.assertEqual((ev['issue'], len(ev['posts'])), ('2026295', 2))
                with self.assertRaises(SystemExit):
                    forum49.run(forum49.DEFAULT_URL, '2026295', '澳门', d / 'out', d / 'arch', d / 'collector-config.json')

    def test_formal_collection_is_unscoped_and_shadow_is_recorded(self):
        with TemporaryDirectory() as d:
            d = Path(d)
            (d / 'collector-config.json').write_text('{}')
            with mock.patch('forum_observer.page_collector.collect_page', return_value=RESULT) as collect, \
                 mock.patch('forum_observer.report.render'), \
                 mock.patch.object(forum49, 'collection_budget', return_value=600), \
                 mock.patch.dict('os.environ', {'GITHUB_RUN_ID': '43'}):
                rec = forum49.run(forum49.DEFAULT_URL, '2026295', '澳门', d / 'out', d / 'arch', d / 'collector-config.json')
            cfg = collect.call_args[0][4]
            for key in ('ordinary_min_clicks', 'ordinary_min_likes', 'scoped_post_cap'):
                self.assertNotIn(key, cfg)
            self.assertEqual(rec['valid_materials'], 2)  # formal record unchanged
            self.assertIn('shadow_scope', rec)
            self.assertFalse(rec['shadow_scope'].get('used_for_freeze', True))

    def test_shadow_scores_only_featured_and_popular_posts(self):
        def post(author, body, **card):
            return {'author': author, 'issue': '2026295', 'title': '295期', 'body': body, 'comments': '',
                    'images': [], 'detail_url': 'https://49217.com/#/corpusdetail?id=' + author,
                    'published_at': '2026-10-22T10:00:00+08:00', **card}
        result = {'evidence': [
            post('精', '295期六肖：鼠牛虎兔龙蛇', is_featured=True, click_count=5, like_count=0),
            post('热', '295期六肖：鼠牛虎兔龙马', is_featured=False, click_count=300, like_count=0),
            post('冷', '295期六肖：猪狗鸡猴羊马', is_featured=False, click_count=299, like_count=9)]}
        sh = forum49.shadow_observation(result)
        self.assertEqual((sh['opened_posts'], sh['kept_posts']), (3, 2))
        self.assertEqual(sh['valid_materials'], 2)
        self.assertIsNone(sh['audit']['scoped_post_cap'])
        self.assertEqual(sh['audit']['ordinary_below_threshold'], 1)
        self.assertNotIn('猪', sh['top6'][:1])

    def test_collector_failure_is_recorded_not_hidden(self):
        with TemporaryDirectory() as d:
            d = Path(d)
            (d / 'collector-config.json').write_text('{}')
            with mock.patch('forum_observer.page_collector.collect_page', side_effect=ValueError('no forum posts')), \
                 mock.patch.object(forum49, 'collection_budget', return_value=600), \
                 mock.patch.dict('os.environ', {'GITHUB_RUN_ID': '7'}):
                rec = forum49.run(forum49.DEFAULT_URL, '2026295', '澳门', d / 'out', d / 'arch', d / 'collector-config.json')
                self.assertFalse(rec['ok'])
                self.assertIn('no forum posts', rec['error'])
                self.assertTrue((d / 'arch/issues/2026295/run-7.json').exists())


class BudgetTest(unittest.TestCase):
    def at(self, h, m):
        from datetime import datetime
        from zoneinfo import ZoneInfo
        return datetime(2026, 10, 22, h, m, tzinfo=ZoneInfo('Asia/Shanghai'))

    def test_evening_scan_ends_before_freeze(self):
        # 20:30 -> 30 min to cutoff minus 90 s reserve
        self.assertEqual(forum49.collection_budget(self.at(20, 30), 1800), 1710)
        self.assertEqual(forum49.collection_budget(self.at(20, 0), 1800), 1800)

    def test_too_close_to_freeze_is_refused(self):
        with self.assertRaises(ValueError):
            forum49.collection_budget(self.at(20, 58), 1800)

    def test_after_freeze_is_observation_only_budget(self):
        self.assertEqual(forum49.collection_budget(self.at(21, 30), 1800), 1800)


if __name__ == '__main__':
    unittest.main()


# --- strict current-issue line gate (user rule 2026-10-24, 49 only) -------
from forum_observer.page_collector import score_posts  # noqa: E402
from forum_observer import issue_lines  # noqa: E402

ICE_BLOCK = """(规律4肖A*3期)
280-282--鼠虎马猴✅23猴  283-285--鼠虎鸡蛇✅19鼠
290-292--蛇虎鼠羊✅17虎  292-294--鼠龙猴狗
295-297--牛蛇羊鸡
(规律-2期六肖)
276-277--龙马鸡猴牛鼠✅猴
279-280--牛虎猪猴蛇龙✅龙"""


def post(body, title='记录', author='作者', issue='2026297'):
    return {'author': author, 'issue': issue, 'title': title, 'body': body,
            'detail_url': 'https://49217.com/#/d/' + author, 'published_at': '2026-10-24T10:00:00+08:00'}


def strict(*posts):
    return score_posts([dict(p) for p in posts], True)


def counted(mats):
    return [m for m in mats if m['counted_for_ranking']]


class StrictIssueLineTest(unittest.TestCase):
    def test_track_record_block_has_no_valid_material(self):
        ranking, mats = strict(post(ICE_BLOCK, title='4肖.6肖中特-杀肖-平特(有连中版)', author='冰淇淋不冷'))
        self.assertEqual(counted(mats), [])
        self.assertEqual(ranking, [])
        self.assertTrue(mats)  # still recorded, with reasons
        self.assertTrue(all(m['exclusion_reason'] == issue_lines.REASON_MULTI for m in mats
                            if any(x in m['excerpt'] for x in ('279-280', '276-277'))))
        self.assertNotIn('duplicate author/equal priority earlier source',
                         {m['exclusion_reason'] for m in mats})

    def test_track_record_block_plus_explicit_current_line(self):
        _, mats = strict(post(ICE_BLOCK + '\n297期:六肖【猴虎羊鸡兔牛】开 ？准', author='冰淇淋不冷'))
        valid = counted(mats)
        self.assertEqual(len(valid), 1)
        self.assertEqual(sorted(valid[0]['picks']), sorted('猴虎羊鸡兔牛'))

    def test_explicit_current_issue_line_is_valid(self):
        _, mats = strict(post('297期:六肖【猴虎羊鸡兔牛】开 ？准'))
        self.assertEqual(len(counted(mats)), 1)
        self.assertEqual(mats[0]['issue_line_check'], 'explicit current issue')

    def test_range_including_current_issue_is_a_plan(self):
        _, mats = strict(post('295-297--牛蛇羊鸡猴狗', title='六肖'),
                         post('296-298--鼠虎马羊猴狗', title='六肖', author='乙'))
        self.assertEqual(len(mats), 2)
        self.assertEqual(counted(mats), [])
        self.assertEqual({m['exclusion_reason'] for m in mats}, {issue_lines.REASON_MULTI})

    def test_line_without_issue_number_is_invalid(self):
        _, mats = strict(post('【蛇牛猪鸡狗鼠】开：00✅', title='赢钱六肖'))
        self.assertEqual(counted(mats), [])
        self.assertEqual(mats[0]['exclusion_reason'], issue_lines.REASON_NOT_EXPLICIT)

    def test_listing_label_is_not_an_explicit_issue(self):
        # post['issue'] (the 澳门第297期 label) is known, yet the line itself is unnamed.
        _, mats = strict(post('六肖：蛇虎牛马猪鸡', title='澳门六肖'))
        self.assertEqual(counted(mats), [])

    def test_historical_issue_lines_are_excluded(self):
        lines = ['279-280--牛虎猪猴蛇龙✅龙', '294  兔马羊猴鸡狗=28√',
                 '254 兔马鸡狗鼠龙羊猴虎 开蛇02 ✘', '249草泥7肖 兔虎狗马羊鼠鸡 开猴❌',
                 '285  牛兔龙蛇猴鸡   开猫', '269。。。杀兔牛。。。开鸡22✅',
                 '296-兔蛇马狗牛鸡猴虎羊-猴23√', '292-兔狗猴鼠蛇猪马龙-36羊-错',
                 '295-鼠牛猴龙蛇猪X', '296 🔪兔猪猴次马鼠龙❌',
                 '294    牛鼠狗鸡虎兔龙蛇马   开兔✔️']
        from forum_observer.confirmed_play import SEQ
        for i, line in enumerate(lines):
            with self.subTest(line=line):
                seq = SEQ.search(line)
                size = len(set(seq.group(0)) & set('鼠牛虎兔龙蛇马羊猴鸡狗猪')) if seq else 0
                # Even under a title naming 297, the line's own older issue decides.
                title = '297期' + ('一二三四五六七八九'[size - 1] + '肖' if size else '')
                _, mats = strict(post(line, title=title, author=f'a{i}'))
                self.assertTrue(mats, line)
                self.assertEqual(counted(mats), [], line)
                self.assertIn(mats[0]['exclusion_reason'],
                              {issue_lines.REASON_HISTORICAL, issue_lines.REASON_MULTI})

    def test_historical_line_no_longer_beats_current_line_in_dedup(self):
        body = '286 兔狗鸡虎龙羊，开羊🀄️\n297蛇马羊鸡狗猪，'
        _, mats = strict(post(body, title='297六肖中特实战自下注'))
        valid = counted(mats)
        self.assertEqual(len(valid), 1)
        self.assertIn('297蛇马羊鸡狗猪', valid[0]['excerpt'])
        old = [m for m in mats if '286' in m['excerpt']][0]
        self.assertEqual(old['exclusion_reason'], issue_lines.REASON_HISTORICAL)

    def test_heading_in_same_section_names_the_issue(self):
        body = '296期: 绝杀一肖:狗 开猴✅\n  主六肖:鼠虎马牛龙羊✗\n297期: 绝杀一肖:鸡\n  主六肖:马猴龙鼠狗蛇'
        _, mats = strict(post(body, title='大数据'))
        valid = counted(mats)
        self.assertEqual(len(valid), 1)
        self.assertEqual(valid[0]['picks'], list('马猴龙鼠狗蛇'))

    def test_author_title_naming_current_issue_is_the_body_heading(self):
        _, mats = strict(post('鼠牛龙蛇虎马', title='297期六肖'))
        self.assertEqual(len(counted(mats)), 1)

    def test_title_multi_issue_plan_is_excluded(self):
        _, mats = strict(post('297期 蛇马羊猴鸡狗', title='二期六肖'))
        self.assertEqual(counted(mats), [])
        self.assertEqual(mats[0]['exclusion_reason'], issue_lines.REASON_MULTI)

    def test_track_record_stats_are_not_plans(self):
        _, mats = strict(post('297期杀:牛 杀肖类 [D序] 特头+平2波 连准:7期',
                              title='297期：150期错2期 公式杀肖'))
        self.assertEqual(len(counted(mats)), 1)

    def test_unit_numbers_are_not_issue_numbers(self):
        _, mats = strict(post('297期平9尾各90元\n024头各20元\n杀兔龙蛇，新规律', title='记录'))
        self.assertEqual(len(counted(mats)), 1)

    def test_118_default_scoring_is_unchanged(self):
        # Without the opt-in flag the old behaviour (and its known flaw) remains.
        _, mats = score_posts([dict(post('295-297--牛蛇羊鸡猴狗', title='六肖'))])
        self.assertEqual(len(counted(mats)), 1)
        self.assertNotIn('issue_line_check', mats[0])

    def test_forum49_enables_strict_gate(self):
        self.assertTrue(forum49.STRICT_ISSUE_LINES)
