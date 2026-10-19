import datetime as dt
import json
import shutil
import tempfile
import unittest
from pathlib import Path
from pipeline_guard import ROOT, plan, validate, read


class PipelineTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name) / 'data'
        shutil.copytree(ROOT, self.root)
        history = read(self.root / 'history.json')
        self.draw = int(history['latest_issue'])
        self.next = self.draw + 1
        self.draw_date = dt.datetime.fromisoformat(next(r for r in history['records'] if int(r['issue']) == self.draw)['open_time']).date()

    def tearDown(self):
        self.temp.cleanup()

    def change(self, path, mutate):
        file = self.root / path
        value = json.loads(file.read_text()); mutate(value); file.write_text(json.dumps(value))

    def test_committed_snapshot(self):
        self.assertEqual(validate(self.root), self.next)
        self.assertEqual(plan(self.root)['generate'], 'false')

    def test_partial_forecast_never_runs_model(self):
        (self.root / f'daily-records/{self.next}.json').unlink()
        with self.assertRaisesRegex(ValueError, 'Partial frozen'):
            plan(self.root)

    def test_repeated_trigger_is_noop_and_next_missing_issue_is_allowed(self):
        for _ in range(3):
            self.assertEqual(plan(self.root)['generate'], 'false')
        # Only remove COPIED files; this checks the planner without any model run.
        for folder in ('predictions', 'daily-records'):
            (self.root / folder / f'{self.next}.json').unlink()
        self.assertEqual(plan(self.root), {'target': str(self.next), 'generate': 'true'})

    def test_old_issue_never_regenerated(self):
        with self.assertRaises(ValueError): plan(self.root, requested=str(self.draw))

    def test_unchanged_current_draw_is_safe_but_stale_draw_fails(self):
        now = dt.datetime.combine(self.draw_date + dt.timedelta(days=1), dt.time(9), dt.timezone(dt.timedelta(hours=8)))
        self.assertEqual(plan(self.root, scheduled=True, now=now)['generate'], 'false')
        with self.assertRaisesRegex(ValueError, 'not current'):
            plan(self.root, scheduled=True, now=now + dt.timedelta(days=1))

    def test_unsettled_runtime_or_duplicate_manifest_fails(self):
        self.change('runtime-state.json', lambda d: next(r for r in d['predictions'] if int(r['issue']) == self.draw).update(hitResult='未开奖'))
        with self.assertRaises(ValueError): validate(self.root)

    def test_duplicate_manifest_fails(self):
        self.change('daily-records/manifest.json', lambda d: d['records'].append(d['records'][0]))
        with self.assertRaises(ValueError): validate(self.root)


if __name__ == '__main__': unittest.main()
