import tempfile
import unittest
from datetime import datetime
from unittest.mock import patch
from pathlib import Path
from forum_observer.audit import run
from forum_observer.page_collector import current_feed_boundary, current_period_text, score_posts, LOCAL_TZ


class HistoricalAuditTests(unittest.TestCase):
    def test_front_back_legend_does_not_become_separate_six_vote(self):
        post=dict(author='甲',issue='2026284',title='特码前后生肖',
                  body='283期前后六肖【前六肖+狗猪】开牛18准284期前后六肖【前六肖+猴鸡】开？00\n前六肖：鼠牛虎兔龙蛇后六肖：马羊猴鸡狗猪',
                  images=[],detail_url='https://example.org')
        _, rows=score_posts([post])
        valid=[p for p in rows if p['counted_for_ranking']]
        self.assertEqual(len(valid),1)
        self.assertEqual(valid[0]['play'],'八肖（前后二肖）')
        self.assertEqual(set(valid[0]['picks']),set('鼠牛虎兔龙蛇猴鸡'))

    def test_inline_previous_period_kills_never_vote_for_current_period(self):
        for body in ('270杀龙 271杀猪 284杀虎',
                     '269。杀蛇。开鸡22 283。杀牛。开牛18 284。杀虎。开？',
                     '271期：杀鼠鸡猪开猴35准 284期：杀虎开？',
                     '284期六肖:猪马牛鸡虎龙六码:32.13.42.10.29.03281期六肖:鼠牛鸡虎兔马'):
            clean=current_period_text(body,'2026284')
            self.assertNotIn('271',clean)
            self.assertNotIn('283',clean)
            self.assertNotIn('281',clean)
            post=dict(author='甲',issue='2026284',title='284期',body=body,images=[],detail_url='https://example.org')
            _, rows=score_posts([post])
            counted=[r for r in rows if r['counted_for_ranking']]
            self.assertEqual(len(counted),1)
            self.assertEqual(counted[0]['picks'],list('猪马牛鸡虎龙') if '六肖' in body else ['虎'])

    def test_explicit_period_does_not_stop_at_next_period_boundary(self):
        stamp=datetime.now(LOCAL_TZ).strftime('%Y-%m-%d %H:%M')
        rows=[dict(author=str(i),issue='2026285',published_at=stamp,title='资料') for i in range(2)]
        rows += [dict(author=str(i),issue='2026284',published_at=stamp,title='资料') for i in range(5)]
        self.assertEqual(current_feed_boundary(rows),'current_issue_boundary_reached')
        self.assertIsNone(current_feed_boundary(rows,'2026284'))
        rows += [dict(author=str(i),issue='2026283',published_at=stamp,title='资料') for i in range(5)]
        self.assertEqual(current_feed_boundary(rows,'2026284'),'current_issue_boundary_reached')

    def test_test_output_cannot_be_ready_or_frozen(self):
        result=dict(issue='2026284',status='ready_observation',selected_count=30,
                    ranking=[],raw_count=30,source_url='https://example.org')
        with tempfile.TemporaryDirectory() as folder, patch('forum_observer.audit.collect_page',return_value=result) as collector:
            saved=run(dict(forum_url='https://example.org',freeze_enabled=True),'2026284',folder)
            self.assertEqual(saved['status'],'historical_test_only')
            self.assertFalse(saved['prediction_eligible'])
            self.assertTrue(saved['test_only'])
            self.assertEqual(saved['selected_count'],0)
            self.assertFalse(collector.call_args.args[-1]['freeze_enabled'])
            self.assertEqual({p.name for p in Path(folder).iterdir()},{'status.json','2026284'})
