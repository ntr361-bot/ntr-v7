"""Pre-freeze audit exclusions: remove reviewed rule violations at the 21:00
freeze without re-collecting (user decision 2026-10-25)."""
import json
import shutil
import unittest
from datetime import datetime
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest.mock import patch

from forum_observer import pre_freeze
from forum_observer.cycle import sync
from forum_observer.core import verify_scored_result

REAL_118 = Path(__file__).resolve().parents[1] / 'archive' / 'issues' / '2026298' / 'draft-status.json'
TABLES = [('觅青森', '鼠牛虎兔马羊'), ('觅青森', '龙蛇猴鸡狗猪'),
          ('心头的小情儿', '鼠牛虎兔马羊'), ('心头的小情儿', '龙蛇猴鸡狗猪')]


def table_entries(draft, decided_at='2026-10-25T20:45:00+08:00'):
    out = []
    for row in draft['parsed_predictions']:
        if (row['author'], ''.join(row['picks'])) in TABLES:
            out.append({'evidence_id': row['evidence_id'], 'mode': row['mode'], 'picks': row['picks'],
                        'reason': 'complementary zodiac table, not a selection', 'rule': 4,
                        'decided_at': decided_at})
    return out


def clock_for(value):
    class Clock(datetime):
        @classmethod
        def now(cls, tz=None):
            fixed = datetime.fromisoformat(value)
            return fixed.astimezone(tz) if tz else fixed
    return Clock


@unittest.skipUnless(REAL_118.exists(), 'real 2026298 draft not present')
class PreFreezeExclusionTests(unittest.TestCase):
    def setUp(self):
        self.draft = json.loads(REAL_118.read_text(encoding='utf-8'))

    def test_real_298_tables_excluded_and_ranking_recomputed(self):
        spec = {'issue': '2026298', 'exclusions': table_entries(self.draft)}
        status, record = pre_freeze.apply(self.draft, spec)
        self.assertEqual(len(record['applied']), 4)
        self.assertEqual(status['valid_materials'], 81)
        self.assertEqual(status['top6'], list('狗鸡鼠猴蛇猪'))
        self.assertEqual(record['before']['top6'], list('鼠狗鸡猴蛇羊'))
        # The recomputed draft passes the unchanged freeze gate.
        status['draw_at'] = '2099-01-01T00:00:00+08:00'
        self.assertTrue(verify_scored_result(status))
        # Input draft untouched.
        self.assertEqual(self.draft['valid_materials'], 83)

    def test_excluding_one_half_lets_the_other_half_back_and_is_reported(self):
        # Rule 2 fallback is mechanical: reviewers must list every bad row.
        spec = {'issue': '2026298', 'exclusions': table_entries(self.draft)[:1]}
        status, record = pre_freeze.apply(self.draft, spec)
        authors = {r['author']: ''.join(r['picks']) for r in status['parsed_predictions']
                   if r['counted_for_ranking'] and r['author'] in ('觅青森',)}
        self.assertEqual(authors.get('觅青森'), '龙蛇猴鸡狗猪')
        self.assertEqual(status['valid_materials'], 83)

    def test_late_or_unknown_entries_never_apply(self):
        entries = table_entries(self.draft, decided_at='2026-10-25T21:00:00+08:00')
        entries.append({'evidence_id': 'https://nowhere', 'mode': 'include', 'picks': list('鼠牛虎兔马羊'),
                        'reason': 'x', 'decided_at': '2026-10-25T20:00:00+08:00'})
        status, record = pre_freeze.apply(self.draft, {'issue': '2026298', 'exclusions': entries})
        self.assertEqual(record['applied'], [])
        self.assertEqual(len(record['rejected_late']), 4)
        self.assertEqual(len(record['not_found']), 1)
        self.assertEqual(status['top6'], self.draft['top6'])

    def test_wrong_issue_refused(self):
        with self.assertRaises(ValueError):
            pre_freeze.apply(self.draft, {'issue': '2026297', 'exclusions': []})

    def test_add_refused_at_or_after_2100(self):
        with TemporaryDirectory() as tmp:
            folder = Path(tmp)
            shutil.copy(REAL_118, folder / 'draft-status.json')
            row = table_entries(self.draft)[0]
            with self.assertRaisesRegex(ValueError, 'before 21:00'):
                pre_freeze.add_exclusion(folder, '2026298', row['evidence_id'], 'include', row['picks'],
                                         'x', now=datetime.fromisoformat('2026-10-25T21:00:00+08:00'))
            with self.assertRaisesRegex(ValueError, 'no such material'):
                pre_freeze.add_exclusion(folder, '2026298', 'https://nowhere', 'include', row['picks'],
                                         'x', now=datetime.fromisoformat('2026-10-25T20:40:00+08:00'))
            pre_freeze.add_exclusion(folder, '2026298', row['evidence_id'], 'include', row['picks'],
                                     'x', rule=4, now=datetime.fromisoformat('2026-10-25T20:40:00+08:00'))
            saved = json.loads((folder / pre_freeze.FILENAME).read_text(encoding='utf-8'))
            self.assertEqual(len(saved['exclusions']), 1)

    def test_finalizer_freezes_the_excluded_result_and_explains_it(self):
        with TemporaryDirectory() as tmp:
            base = Path(tmp)
            archive = base / 'archive'
            folder = archive / 'issues' / '2026298'
            folder.mkdir(parents=True)
            shutil.copy(REAL_118, folder / 'draft-status.json')
            (folder / 'report.md').write_text('# 论坛 2026298 采集成绩单\n\n状态：ready_observation\n', encoding='utf-8')
            (folder / pre_freeze.FILENAME).write_text(json.dumps(
                {'issue': '2026298', 'exclusions': table_entries(self.draft)}, ensure_ascii=False), encoding='utf-8')
            history = base / 'history.json'
            history.write_text(json.dumps({'records': []}), encoding='utf-8')
            clock = clock_for('2026-10-25T21:01:30+08:00')
            with patch('forum_observer.cycle.datetime', clock), patch('forum_observer.core.datetime', clock):
                sync(base / 'out', archive, history, finalize=True)
            snap = json.loads((folder / 'snapshot.json').read_text(encoding='utf-8'))
            self.assertEqual(snap['top6'], list('狗鸡鼠猴蛇猪'))
            self.assertEqual(snap['valid_materials'], 81)
            self.assertEqual(len(snap['pre_freeze_exclusions']['applied']), 4)
            observation = json.loads((folder / 'observation.json').read_text(encoding='utf-8'))
            self.assertEqual(sum(r['counted_for_ranking'] for r in observation['parsed_predictions']), 81)
            report = (folder / 'report.md').read_text(encoding='utf-8')
            self.assertIn('冻结前审查排除', report)
            self.assertIn('觅青森', report)
            # The original draft is kept unchanged for audit.
            self.assertEqual(json.loads((folder / 'draft-status.json').read_text(encoding='utf-8'))['valid_materials'], 83)


if __name__ == '__main__':
    unittest.main()
