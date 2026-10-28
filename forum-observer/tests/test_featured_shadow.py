import json
import unittest
from datetime import datetime
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest.mock import patch

from forum_observer.core import SCORING_RULE_VERSION
from forum_observer.cycle import sync
from forum_observer.page_collector import score_posts


class FeaturedShadowTests(unittest.TestCase):
    """118论坛模型「只用精华帖」影子榜：只记录，不影响正式冻结与结算。"""

    def _run(self, model_id='forum-observer'):
        tmp = TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        base = Path(tmp.name)
        out = base / 'out'; out.mkdir()
        archive = base / 'archive'; history = base / 'history.json'
        history.write_text(json.dumps({'records': []}), encoding='utf-8')
        issue = '2026291'
        clock = {'now': '2026-10-18T21:00:30+08:00'}

        class Clock(datetime):
            @classmethod
            def now(cls, tz=None):
                fixed = datetime.fromisoformat(clock['now'])
                return fixed.astimezone(tz) if tz else fixed

        sources = [
            dict(author='甲', issue=issue, title='291期推荐六肖：鼠牛虎兔龙蛇', body='', images=[],
                 detail_url='https://example.org/1', is_featured=True),
            dict(author='乙', issue=issue, title='291期推荐六肖：马羊猴鸡狗猪', body='', images=[],
                 detail_url='https://example.org/2', is_featured=False),
            dict(author='丙', issue=issue, title='291期推荐六肖：马羊猴鸡狗猪', body='', images=[],
                 detail_url='https://example.org/3', is_featured=False),
        ]
        ranking, rows = score_posts([dict(s) for s in sources])
        status = dict(issue=issue, status='incomplete_observation', ranking=ranking,
                      top1=[r['zodiac'] for r in ranking[:1]], top3=[r['zodiac'] for r in ranking[:3]],
                      top6=[r['zodiac'] for r in ranking[:6]], scoring_rule_version=SCORING_RULE_VERSION,
                      valid_materials=3, parsed_predictions=rows, evidence=sources,
                      fetched_at='2026-10-18T20:30:00+08:00', draw_at='2026-10-18T21:32:00+08:00',
                      pre_draw_verified=True,
                      author_selection={'min_valid_materials': 25, 'valid_prediction_authors': 3})
        folder = archive / 'issues' / issue
        folder.mkdir(parents=True)
        (folder / 'draft-status.json').write_text(json.dumps(status, ensure_ascii=False), encoding='utf-8')
        (folder / 'report.md').write_text('# 291\n\n有效资料：3份\n', encoding='utf-8')
        with patch('forum_observer.cycle.datetime', Clock), patch('forum_observer.core.datetime', Clock):
            sync(out, archive, history, finalize=True, model_id=model_id)
        return archive, folder, history, issue, out

    def test_shadow_written_once_and_formal_snapshot_unchanged(self):
        archive, folder, history, issue, out = self._run()
        snap = json.loads((folder / 'snapshot.json').read_text())
        shadow = json.loads((folder / 'featured-shadow.json').read_text())
        # Formal snapshot keeps all posts: 马羊猴鸡狗猪 has two votes.
        self.assertEqual(snap['valid_materials'], 3)
        self.assertEqual(snap['top6'], list('马羊猴鸡狗猪'))
        self.assertNotIn('featured_shadow', snap)
        # Shadow only scores the featured post.
        self.assertFalse(shadow['used_for_freeze'])
        self.assertEqual(shadow['featured_posts'], 1)
        self.assertEqual(shadow['valid_materials'], 1)
        self.assertEqual(shadow['top6'], list('鼠牛虎兔龙蛇'))
        self.assertEqual(shadow['fetched_at'], snap['fetched_at'])

        # Settle after the draw: both settled independently from the same draw.
        history.write_text(json.dumps({'records': [
            {'issue': issue, 'special_zodiac': '鼠', 'open_time': '2026-10-18T21:35:00+08:00'}]}),
            encoding='utf-8')
        before = (folder / 'featured-shadow.json').read_bytes()
        sync(out, archive, history)
        formal = json.loads((folder / 'settlement.json').read_text())
        shadow_set = json.loads((folder / 'featured-shadow-settlement.json').read_text())
        self.assertFalse(formal['top6_hit'])
        self.assertTrue(shadow_set['top6_hit'])
        self.assertEqual(shadow_set['rank'], 1)
        self.assertEqual((folder / 'featured-shadow.json').read_bytes(), before)
        summary = json.loads((archive / 'featured-shadow-summary.json').read_text())
        self.assertEqual(summary['settled_count'], 1)
        self.assertEqual(summary['shadow_top6_hits'], 1)
        self.assertEqual(summary['formal_top6_hits'], 0)

    def test_forum49_never_writes_shadow(self):
        archive, folder, *_ = self._run(model_id='forum49')
        self.assertTrue((folder / 'snapshot.json').exists())
        self.assertFalse((folder / 'featured-shadow.json').exists())
        self.assertFalse((archive / 'featured-shadow-summary.json').exists())


if __name__ == '__main__':
    unittest.main()
