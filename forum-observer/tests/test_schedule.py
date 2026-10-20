import hashlib
import json
import unittest
from datetime import datetime
from pathlib import Path
from tempfile import TemporaryDirectory
from zoneinfo import ZoneInfo

from forum_observer.schedule import (
    current_issue, decide, snapshot_exists, ensure_daily_report,
    collection_window_allowed, freeze_window_allowed, draft_is_current_evening,
)

TZ = ZoneInfo("Asia/Shanghai")


class ScheduleTests(unittest.TestCase):
    def at(self, hour, minute, day=12):
        return datetime(2026, 10, day, hour, minute, tzinfo=TZ)

    def write_snapshot(self, archive, issue):
        path = Path(archive) / "issues" / issue / "snapshot.json"
        path.parent.mkdir(parents=True, exist_ok=True)
        payload = {"issue": issue, "top6": ["鼠", "牛", "虎", "兔", "龙", "蛇"]}
        payload["digest"] = hashlib.sha256(json.dumps(
            payload, ensure_ascii=False, sort_keys=True).encode()).hexdigest()
        path.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")
        return path

    def test_early_and_backup_only_when_missing(self):
        with TemporaryDirectory() as tmp:
            self.assertEqual(current_issue(self.at(19, 10)), "2026285")
            self.assertEqual(decide(self.at(19, 10), tmp)[:3],
                             (True, "2026285", 1800))
            self.assertEqual(decide(self.at(20, 0), tmp)[:3],
                             (True, "2026285", 1800))
            self.write_snapshot(tmp, "2026285")
            self.assertEqual(decide(self.at(19, 40), tmp)[3],
                             "immutable_snapshot_exists")
            self.assertFalse(decide(self.at(20, 0), tmp)[0])

    def test_cutoff_and_next_day_never_backfill(self):
        with TemporaryDirectory() as tmp:
            self.assertTrue(decide(self.at(20, 24), tmp)[0])
            self.assertTrue(decide(self.at(20, 57), tmp)[0])
            self.assertEqual(decide(self.at(20, 59), tmp)[3],
                             "daily_collection_cutoff_passed")
            self.assertFalse(decide(self.at(21, 0), tmp)[0])
            self.write_snapshot(tmp, "2026285")
            self.assertTrue(decide(self.at(19, 10, 13), tmp)[0])
            self.assertEqual(current_issue(self.at(19, 10, 13)), "2026286")

    def test_scheduled_collection_rejects_early_and_stale_events(self):
        with TemporaryDirectory() as tmp:
            self.assertEqual(decide(self.at(19, 39), tmp, scheduled=True)[3],
                             "scheduled_event_outside_collection_window")
            self.assertTrue(decide(self.at(19, 40), tmp, scheduled=True)[0])
            self.assertTrue(decide(self.at(20, 25), tmp, scheduled=True)[0])
            self.assertEqual(decide(self.at(20, 26), tmp, scheduled=True)[3],
                             "scheduled_event_outside_collection_window")
            self.assertEqual(decide(self.at(1, 46, 13), tmp, scheduled=True)[3],
                             "scheduled_event_outside_collection_window")
            # A user-requested manual rerun still follows the existing 21:00 cutoff.
            self.assertTrue(decide(self.at(1, 46, 13), tmp, manual=True,
                                   scheduled=True)[0])

    def test_overnight_ready_draft_is_refreshed_instead_of_blocking_tonight(self):
        with TemporaryDirectory() as tmp:
            issue="2026285"
            folder=Path(tmp)/"issues"/issue
            folder.mkdir(parents=True)
            early=self.at(1, 51).isoformat()
            path=folder/"draft-status.json"
            path.write_text(json.dumps({
                "issue":issue,"status":"ready_observation","valid_materials":50,
                "fetched_at":early
            }),encoding="utf-8")
            self.assertTrue(decide(self.at(19, 40),tmp,scheduled=True)[0])
            evening=json.loads(path.read_text())
            evening["fetched_at"]=self.at(19, 50).isoformat()
            path.write_text(json.dumps(evening),encoding="utf-8")
            self.assertEqual(decide(self.at(20, 0),tmp,scheduled=True)[3],
                             "completed_draft_waiting_for_21_freeze")

    def test_freeze_requires_a_current_evening_draft(self):
        with TemporaryDirectory() as tmp:
            now=self.at(21, 0)
            issue=current_issue(now)
            folder=Path(tmp)/"issues"/issue
            folder.mkdir(parents=True)
            path=folder/"draft-status.json"
            path.write_text(json.dumps({
                "issue":issue,"status":"ready_observation","valid_materials":50,
                "fetched_at":self.at(1, 51).isoformat()
            }),encoding="utf-8")
            self.assertFalse(freeze_window_allowed(now,tmp))
            fresh=json.loads(path.read_text())
            fresh["fetched_at"]=self.at(20, 0).isoformat()
            path.write_text(json.dumps(fresh),encoding="utf-8")
            self.assertTrue(freeze_window_allowed(now,tmp))

    def test_freeze_window_rejects_delayed_next_day_events(self):
        self.assertFalse(freeze_window_allowed(self.at(20, 59)))
        self.assertTrue(freeze_window_allowed(self.at(21, 0)))
        self.assertTrue(freeze_window_allowed(self.at(21, 15)))
        self.assertFalse(freeze_window_allowed(self.at(21, 20)))
        self.assertFalse(freeze_window_allowed(self.at(1, 51, 13)))

    def test_completed_draft_skips_automatic_backups_but_manual_can_rerun(self):
        with TemporaryDirectory() as tmp:
            issue="2026285"
            folder=Path(tmp)/'issues'/issue
            folder.mkdir(parents=True)
            (folder/'draft-status.json').write_text(json.dumps({
                'issue':issue,'status':'ready_observation','valid_materials':27,
                'fetched_at':self.at(19, 50).isoformat()
            },ensure_ascii=False),encoding='utf-8')
            self.assertEqual(decide(self.at(20,10),tmp)[3],
                             "completed_draft_waiting_for_21_freeze")
            self.assertFalse(decide(self.at(20,10),tmp)[0])
            self.assertTrue(decide(self.at(20,10),tmp,manual=True)[0])
            self.write_snapshot(tmp,issue)
            self.assertEqual(decide(self.at(20,10),tmp,manual=True)[3],
                             "immutable_snapshot_exists")

    def test_nightly_report_is_saved_even_without_any_verified_material(self):
        with TemporaryDirectory() as tmp:
            archive=Path(tmp)
            self.assertFalse(ensure_daily_report(archive,self.at(20,30,13)))
            report=archive/'issues'/'2026286'/'report.md'
            self.assertTrue(report.is_file())
            text=report.read_text(encoding='utf-8')
            self.assertIn('没有本期冻结预测',text)
            self.assertEqual(ensure_daily_report(archive,self.at(20,30,13)),True)
            self.assertEqual(report.read_text(encoding='utf-8'),text)
            self.assertFalse((archive/'issues'/'2026286'/'snapshot.json').exists())
            self.assertEqual(json.loads((archive/'latest.json').read_text())['issue'],'2026286')

    def test_invalid_existing_snapshot_is_not_replaced(self):
        with TemporaryDirectory() as tmp:
            path=self.write_snapshot(tmp, "2026285")
            data=json.loads(path.read_text(encoding="utf-8"))
            data["top6"] = ["鸡"]
            path.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "refusing overwrite"):
                snapshot_exists(tmp, "2026285")
            with self.assertRaisesRegex(ValueError, "refusing overwrite"):
                decide(self.at(19, 40), tmp)


    def test_workflow_audit_never_activates_freeze(self):
        """Regression: audit pushes must not finalize before 21:00."""
        workflow = (Path(__file__).resolve().parents[2] / ".github" /
                    "workflows" / "forum-observer.yml").read_text(encoding="utf-8")
        finalize_lines = [line.strip() for line in workflow.splitlines()
                          if line.strip().startswith("FINALIZE_CYCLE:")]
        self.assertEqual(len(finalize_lines), 2)
        for line in finalize_lines:
            self.assertIn("github.event_name == 'schedule'", line)
            self.assertIn("0,5,10 13 * * *", line)
            self.assertNotIn("trigger(forum-audit):", line)
        self.assertIn("trigger(forum-audit):", workflow)
        self.assertIn("--ensure-report", workflow)
        self.assertIn("--defer-freeze", workflow)
        self.assertIn("--finalize", workflow)
        self.assertIn("--scheduled", workflow)
        self.assertIn("--check-freeze-window", workflow)
        self.assertIn("scheduled_event_outside_collection_window", workflow)

if __name__ == "__main__":
    unittest.main()
