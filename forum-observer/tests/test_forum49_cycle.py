"""49论坛模型 reuses the 118 freeze/settle cycle with its own archive and feed."""
import json
import unittest
from datetime import datetime
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest.mock import patch
from zoneinfo import ZoneInfo

from forum_observer.core import SCORING_RULE_VERSION
from forum_observer.cycle import sync
from forum_observer.page_collector import score_posts


class Forum49CycleTest(unittest.TestCase):
    def test_own_archive_freezes_at_2100_settles_and_publishes_forum49_feed(self):
        with TemporaryDirectory() as tmp:
            base = Path(tmp)
            out, archive = base / 'forum49-out', base / 'forum49'
            public, other = base / 'site/forum49', base / 'site/forum-observer'
            history = base / 'history.json'
            history.write_text(json.dumps({'records': []}), encoding='utf-8')
            out.mkdir()
            issue = '2026295'
            clock = {'now': '2026-10-22T20:20:00+08:00'}

            class Clock(datetime):
                @classmethod
                def now(cls, tz=None):
                    fixed = datetime.fromisoformat(clock['now'])
                    return fixed.astimezone(tz) if tz else fixed

            sources = [dict(author=a, issue=issue, title=f'295期六肖：{z}', body='', images=[],
                            detail_url=f'https://49217.com/#/corpusdetail?id={i}&type=3')
                       for i, (a, z) in enumerate([('甲', '鼠牛虎兔龙蛇'), ('乙', '马羊猴鸡狗猪')])]
            ranking, rows = score_posts(sources)
            status = dict(issue=issue, status='incomplete_observation', ranking=ranking,
                          top1=[r['zodiac'] for r in ranking[:1]],
                          top3=[r['zodiac'] for r in ranking[:3]],
                          top6=[r['zodiac'] for r in ranking[:6]],
                          scoring_rule_version=SCORING_RULE_VERSION, valid_materials=2,
                          parsed_predictions=rows, evidence=sources,
                          fetched_at='2026-10-22T20:19:00+08:00',
                          draw_at='2026-10-22T21:32:00+08:00', pre_draw_verified=True,
                          author_selection={'min_valid_materials': 25, 'valid_prediction_authors': 2})
            (out / 'status.json').write_text(json.dumps(status, ensure_ascii=False), encoding='utf-8')
            with patch('forum_observer.cycle.datetime', Clock), patch('forum_observer.core.datetime', Clock):
                sync(out, archive, history, public, defer_freeze=True, model_id='forum49')
                folder = archive / 'issues' / issue
                self.assertTrue((folder / 'draft-status.json').exists())
                self.assertFalse((folder / 'snapshot.json').exists())
                with self.assertRaisesRegex(ValueError, 'before 21:00'):
                    sync(out, archive, history, public, finalize=True, model_id='forum49')

                clock['now'] = '2026-10-22T21:00:40+08:00'
                sync(base / 'empty-out', archive, history, public, finalize=True, model_id='forum49')
                snap = json.loads((folder / 'snapshot.json').read_text())
                self.assertEqual(snap['valid_materials'], 2)
                self.assertEqual(snap['quality_status'], 'below_minimum_samples')
                latest = json.loads((public / 'latest.json').read_text())
                self.assertEqual((latest['model'], latest['frozen']), ('forum49', True))
                self.assertEqual(latest['prediction_top6'], snap['top6'])

                # Settle after the verified draw; snapshot stays byte-identical.
                before = (folder / 'snapshot.json').read_bytes()
                actual = snap['top6'][0]
                history.write_text(json.dumps({'records': [
                    {'issue': issue, 'special_zodiac': actual, 'open_time': '2026-10-22T21:32:00+08:00'}]}),
                    encoding='utf-8')
                clock['now'] = '2026-10-22T22:10:00+08:00'
                sync(base / 'empty-out', archive, history, public, model_id='forum49')
                settled = json.loads((folder / 'settlement.json').read_text())
                self.assertTrue(settled['top6_hit'])
                self.assertEqual((folder / 'snapshot.json').read_bytes(), before)
                hist = json.loads((public / 'history.json').read_text())
                self.assertEqual((hist['model'], hist['settled_count']), ('forum49', 1))
                self.assertEqual(hist['settlements'][0]['top6'], snap['top6'])
                self.assertEqual(hist['settlements'][0]['frozen_at'], snap['frozen_at'])
                self.assertTrue((public / 'issues' / f'{issue}.json').exists())
            # 118's feed directory was never written.
            self.assertFalse(other.exists())


if __name__ == '__main__':
    unittest.main()
