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
    gpt_trigger_wait_seconds, ROUND_ONE_CRON, ROUND_TWO_CRON,
    BACKUP_COLLECT_CRON, forum49_collect_gate,
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

    def test_single_2020_collection_then_automatic_repeats_skip(self):
        # User decision 2026-10-26: one collection at 20:20, no 20:00/20:30 rounds.
        with TemporaryDirectory() as tmp:
            issue="2026285"
            self.assertEqual(current_issue(self.at(20,20)),issue)
            go,_,seconds,reason=decide(self.at(20,20),tmp,scheduled=True,requested_round='1')
            self.assertTrue(go,reason)
            # Runs until the 21:00 cutoff (capped), not the old 20:26 round-one end.
            self.assertEqual(seconds,1800)
            folder=Path(tmp)/"issues"/issue
            folder.mkdir(parents=True)
            (folder/"draft-status.json").write_text(json.dumps({
                "issue":issue,"status":"ready_observation","valid_materials":40,
                "fetched_at":self.at(20,31).isoformat(),"collection_round":1
            }),encoding="utf-8")
            self.assertEqual(decide(self.at(20,35),tmp,scheduled=True,requested_round='1')[3],
                             "current_round_already_completed")
            self.assertEqual(decide(self.at(20,40),tmp)[3],"current_round_already_completed")
            self.assertTrue(decide(self.at(20,45),tmp,manual=True)[0])
            self.write_snapshot(tmp,issue)
            self.assertEqual(decide(self.at(20,50),tmp,manual=True)[3],
                             "immutable_snapshot_exists")

    def test_cutoff_and_next_day_never_backfill(self):
        with TemporaryDirectory() as tmp:
            self.assertTrue(decide(self.at(20, 15), tmp)[0])
            self.assertTrue(decide(self.at(20, 57), tmp)[0])
            self.assertEqual(decide(self.at(20, 59), tmp)[3],
                             "daily_collection_cutoff_passed")
            self.assertFalse(decide(self.at(21, 0), tmp)[0])
            self.write_snapshot(tmp, "2026285")
            self.assertTrue(decide(self.at(19, 10, 13), tmp)[0])
            self.assertEqual(current_issue(self.at(19, 10, 13)), "2026286")

    def test_all_collection_cron_slots_and_round_two_are_retired(self):
        with TemporaryDirectory() as tmp:
            for minute, slot in ((0, ROUND_ONE_CRON), (20, ROUND_ONE_CRON),
                                 (30, ROUND_TWO_CRON), (35, ROUND_TWO_CRON),
                                 (15, '40,45,50 11 * * *'), (55, '0 12 * * *')):
                self.assertEqual(decide(self.at(20, minute), tmp, scheduled=True, slot=slot)[3],
                                 "retired_schedule_trigger", (minute, slot))
            self.assertEqual(decide(self.at(20, 33), tmp, scheduled=True, slot='',
                                    requested_round='2')[3], "retired_schedule_trigger")
            # The 20:20/20:25 backup cron collects only inside the window.
            self.assertTrue(decide(self.at(20, 20), tmp, scheduled=True, slot=BACKUP_COLLECT_CRON)[0])
            self.assertTrue(decide(self.at(20, 41), tmp, scheduled=True, slot=BACKUP_COLLECT_CRON)[0])
            self.assertEqual(decide(self.at(21, 30), tmp, scheduled=True, slot=BACKUP_COLLECT_CRON)[3],
                             "scheduled_event_outside_collection_window")
            self.assertEqual(decide(self.at(19,40),tmp,scheduled=True)[3],
                             "scheduled_event_outside_collection_window")
            self.assertEqual(decide(self.at(20,5),tmp,scheduled=True)[3],
                             "scheduled_event_outside_collection_window")
            self.assertEqual(decide(self.at(20,59),tmp,scheduled=True)[3],
                             "scheduled_event_outside_collection_window")
            self.assertEqual(decide(self.at(1,46,13),tmp,scheduled=True)[3],
                             "scheduled_event_outside_collection_window")

    def test_collect_marker_window_is_2020_to_2058(self):
        # Push markers expand ${{ github.event.schedule }} to "" (2026-10-21 regression).
        with TemporaryDirectory() as tmp:
            self.assertTrue(decide(self.at(20, 20), tmp, scheduled=True, slot='',
                                   requested_round='1')[0])
            self.assertTrue(decide(self.at(20, 45), tmp, scheduled=True, slot='',
                                   requested_round='1')[0])
            self.assertEqual(decide(self.at(20, 3), tmp, scheduled=True, slot='',
                                    requested_round='1')[3],
                             "scheduled_event_outside_collection_window")
            self.assertEqual(decide(self.at(20, 58), tmp, scheduled=True,
                                    requested_round='1')[3],
                             "requested_round_outside_window")
            self.assertEqual(decide(self.at(21, 5), tmp, scheduled=True, slot='',
                                    requested_round='1')[3],
                             "scheduled_event_outside_collection_window")

    def test_gpt_trigger_aligns_to_2020_and_round_two_never_aligns(self):
        self.assertEqual(gpt_trigger_wait_seconds(self.at(20,15)),300)
        self.assertEqual(gpt_trigger_wait_seconds(self.at(20,19)),60)
        self.assertEqual(gpt_trigger_wait_seconds(self.at(20,20)),0)
        self.assertIsNone(gpt_trigger_wait_seconds(self.at(19,56)))
        self.assertIsNone(gpt_trigger_wait_seconds(self.at(20,25),2))
        self.assertIsNone(gpt_trigger_wait_seconds(self.at(20,30),2))

    def test_overnight_ready_draft_is_refreshed_instead_of_blocking_tonight(self):
        with TemporaryDirectory() as tmp:
            issue="2026285"
            folder=Path(tmp)/"issues"/issue
            folder.mkdir(parents=True)
            path=folder/"draft-status.json"
            for stale in (self.at(1, 51), self.at(19, 50)):
                # Overnight or pre-20:00 scans are not tonight's collection.
                path.write_text(json.dumps({
                    "issue":issue,"status":"ready_observation","valid_materials":50,
                    "fetched_at":stale.isoformat(),"collection_round":1
                }),encoding="utf-8")
                self.assertTrue(decide(self.at(20,20),tmp,scheduled=True,requested_round='1')[0])
            # A manual collection from 20:00 is tonight's: automatic ones skip.
            evening=json.loads(path.read_text())
            evening["fetched_at"]=self.at(20,10).isoformat()
            path.write_text(json.dumps(evening),encoding="utf-8")
            self.assertEqual(decide(self.at(20,20),tmp,scheduled=True,requested_round='1')[3],
                             "current_round_already_completed")
            self.assertEqual(decide(self.at(20,25),tmp,scheduled=True,slot=BACKUP_COLLECT_CRON)[3],
                             "current_round_already_completed")
            self.assertTrue(decide(self.at(20,25),tmp,manual=True)[0])

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
            fresh["fetched_at"]=self.at(19, 55).isoformat()
            path.write_text(json.dumps(fresh),encoding="utf-8")
            self.assertFalse(freeze_window_allowed(now,tmp))
            fresh["fetched_at"]=self.at(20, 5).isoformat()
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
                'fetched_at':self.at(20,31).isoformat(),'collection_round':1
            },ensure_ascii=False),encoding='utf-8')
            self.assertEqual(decide(self.at(20,35),tmp)[3],
                             "current_round_already_completed")
            self.assertFalse(decide(self.at(20,50),tmp)[0])
            self.assertTrue(decide(self.at(20,35),tmp,manual=True)[0])
            self.write_snapshot(tmp,issue)
            self.assertEqual(decide(self.at(20,35),tmp,manual=True)[3],
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
        self.assertIn("--align-gpt-trigger", workflow)
        # Single 20:20 marker collection since 2026-10-26: no collection cron.
        self.assertNotIn("cron: '0,5,10 12 * * *'",workflow)
        self.assertNotIn("cron: '30,35,40 12 * * *'",workflow)
        self.assertIn("cron: '20,25 12 * * *'",workflow)
        self.assertIn("workflow_dispatch:",workflow)
        self.assertNotIn("cron: '40,45,50 11 * * *'",workflow)
        self.assertIn("trigger(forum-collect-1):",workflow)
        self.assertIn("trigger(forum-collect-2):",workflow)

    def test_workflow_freeze_marker_uses_same_window_as_cron(self):
        """Freeze marker may finalize only through the guarded 21:00-21:20 window."""
        workflow = (Path(__file__).resolve().parents[2] / ".github" /
                    "workflows" / "forum-observer.yml").read_text(encoding="utf-8")
        finalize_lines = [line.strip() for line in workflow.splitlines()
                          if line.strip().startswith("FINALIZE_CYCLE:")]
        self.assertEqual(len(finalize_lines), 2)
        for line in finalize_lines:
            self.assertIn("trigger(forum-freeze):", line)
            self.assertNotIn("trigger(forum-audit):", line)
            self.assertNotIn("trigger(forum-collect", line)
        window_if = [line.strip() for line in workflow.splitlines()
                     if "trigger(forum-freeze):" in line and line.strip().startswith("if:")]
        self.assertEqual(len(window_if), 1)
        self.assertIn("0,5,10 13 * * *", window_if[0])
        gate = [line for line in workflow.splitlines()
                if line.strip().startswith("if:") and "trigger(forum-collect-1):" in line
                and "workflow_dispatch" in line]
        self.assertEqual(len(gate), 1)
        self.assertNotIn("trigger(forum-freeze):", gate[0])

    def test_forum49_gate_automatic_once_manual_always(self):
        with TemporaryDirectory() as tmp:
            issue = current_issue(self.at(20, 20))
            self.assertEqual(forum49_collect_gate(tmp, self.at(20, 20), True), (True, "automatic_collection"))
            self.assertEqual(forum49_collect_gate(tmp, self.at(20, 5), True)[1],
                             "automatic_collection_outside_window")
            self.assertEqual(forum49_collect_gate(tmp, self.at(20, 50), True)[1],
                             "automatic_collection_outside_window")
            folder = Path(tmp) / "issues" / issue
            folder.mkdir(parents=True)
            (folder / "draft-status.json").write_text(json.dumps({
                "issue": issue, "status": "ready_observation", "valid_materials": 90,
                "fetched_at": self.at(20, 38).isoformat()}), encoding="utf-8")
            self.assertEqual(forum49_collect_gate(tmp, self.at(20, 40), True)[1],
                             "already_collected_tonight")
            # Manual dispatch keeps working at any time.
            self.assertEqual(forum49_collect_gate(tmp, self.at(20, 40), False), (True, "manual_collection"))
            self.assertEqual(forum49_collect_gate(tmp, self.at(19, 0), False), (True, "manual_collection"))

    def test_forum49_workflow_keeps_manual_and_adds_backups(self):
        workflow = (Path(__file__).resolve().parents[2] / ".github" /
                    "workflows" / "forum49-model.yml").read_text(encoding="utf-8")
        self.assertIn("workflow_dispatch:", workflow)
        self.assertIn("skip_if_collected:", workflow)
        self.assertIn("cron: '20,25 12 * * *'", workflow)
        self.assertIn("cron: '0,5,10 13 * * *'", workflow)
        self.assertIn("cron: '50 * * * *'", workflow)
        self.assertIn("--forum49-collect-gate", workflow)


if __name__ == "__main__":
    unittest.main()
