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
    gpt_trigger_wait_seconds, BACKUP_COLLECT_CRON, forum49_collect_gate,
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
            (folder/"report.md").write_text("# verified report")
            self.assertEqual(decide(self.at(20,35),tmp,scheduled=True,requested_round='1')[3],
                             "current_round_already_completed")
            self.assertEqual(decide(self.at(20,40),tmp)[3],"current_round_already_completed")
            self.assertTrue(decide(self.at(20,45),tmp,manual=True)[0])
            self.write_snapshot(tmp,issue)
            self.assertEqual(decide(self.at(20,50),tmp,manual=True)[3],
                             "manual_test_after_freeze")
            self.assertEqual(decide(self.at(21,50),tmp,scheduled=True,requested_round='1')[3],
                             "manual_test_after_freeze")

    def test_cutoff_and_next_day_never_backfill(self):
        with TemporaryDirectory() as tmp:
            self.assertTrue(decide(self.at(20, 15), tmp)[0])
            self.assertTrue(decide(self.at(20, 57), tmp)[0])
            self.assertTrue(decide(self.at(20, 59), tmp)[0])
            self.assertTrue(decide(self.at(21, 0), tmp)[0])
            self.assertEqual(decide(self.at(21, 0), tmp)[3], "manual_test_after_freeze")
            self.write_snapshot(tmp, "2026285")
            self.assertTrue(decide(self.at(19, 10, 13), tmp)[0])
            self.assertEqual(current_issue(self.at(19, 10, 13)), "2026286")

    def test_scheduled_collection_has_no_time_or_delay_gate(self):
        with TemporaryDirectory() as tmp:
            for hour, minute in ((0, 0), (1, 46), (9, 10), (19, 0), (20, 5), (21, 30), (23, 59)):
                go, issue, seconds, reason = decide(
                    self.at(hour, minute, day=13 if hour == 0 else 12), tmp,
                    scheduled=True, slot='delayed-or-legacy-cron', requested_round='2')
                self.assertTrue(go, (hour, minute, reason))
                self.assertGreater(seconds, 0)
                self.assertIn(issue, ('2026285', '2026286'))

    def test_legacy_marker_and_delayed_trigger_are_accepted(self):
        with TemporaryDirectory() as tmp:
            for hour, minute in ((1, 46), (20, 3), (20, 58), (21, 5)):
                self.assertTrue(decide(self.at(hour, minute), tmp, scheduled=True,
                                       requested_round='1')[0])
            self.assertTrue(decide(self.at(20, 33), tmp, scheduled=True,
                                   requested_round='2')[0])

    def test_gpt_trigger_never_waits_or_rejects_legacy_rounds(self):
        for h, m in ((1, 0), (19, 56), (20, 25), (23, 59)):
            self.assertEqual(gpt_trigger_wait_seconds(self.at(h, m)), 0)
            self.assertEqual(gpt_trigger_wait_seconds(self.at(h, m), 2), 0)

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
            (folder/"report.md").write_text("# verified report")
            # A manual collection from 20:00 is tonight's: automatic ones skip.
            evening=json.loads(path.read_text())
            evening["fetched_at"]=self.at(20,10).isoformat()
            path.write_text(json.dumps(evening),encoding="utf-8")
            self.assertEqual(decide(self.at(20,20),tmp,scheduled=True,requested_round='1')[3],
                             "current_round_already_completed")
            self.assertEqual(decide(self.at(20,25),tmp,scheduled=True,slot=BACKUP_COLLECT_CRON)[3],
                             "current_round_already_completed")
            self.assertTrue(decide(self.at(20,25),tmp,manual=True)[0])

    def test_freeze_requires_a_same_day_draft(self):
        with TemporaryDirectory() as tmp:
            now=self.at(21, 0)
            issue=current_issue(now)
            folder=Path(tmp)/"issues"/issue
            folder.mkdir(parents=True)
            path=folder/"draft-status.json"
            path.write_text(json.dumps({
                "issue":issue,"status":"ready_observation","valid_materials":50,
                "fetched_at":self.at(23, 51, 11).isoformat()
            }),encoding="utf-8")
            self.assertFalse(freeze_window_allowed(now,tmp))
            fresh=json.loads(path.read_text())
            fresh["fetched_at"]=self.at(20, 5, 11).isoformat()
            path.write_text(json.dumps(fresh),encoding="utf-8")
            self.assertFalse(freeze_window_allowed(now,tmp))
            fresh["fetched_at"]=self.at(20, 5).isoformat()
            path.write_text(json.dumps(fresh),encoding="utf-8")
            self.assertTrue(freeze_window_allowed(now,tmp))

    def test_manual_freeze_must_wait_until_2100(self):
        with TemporaryDirectory() as tmp:
            folder=Path(tmp)/'issues'/current_issue(self.at(20, 59))
            folder.mkdir(parents=True)
            (folder/'draft-status.json').write_text(json.dumps({
                'issue':current_issue(self.at(20, 59)),
                'status':'ready_observation', 'valid_materials':25,
                'fetched_at':self.at(20, 40).isoformat()
            }),encoding='utf-8')
            self.assertFalse(freeze_window_allowed(self.at(20, 59),tmp,manual=True))
            self.assertTrue(freeze_window_allowed(self.at(21, 0),tmp,manual=True))
            self.assertTrue(freeze_window_allowed(self.at(21, 25),tmp,manual=True))

    def test_freeze_window_rejects_delayed_next_day_events(self):
        self.assertFalse(freeze_window_allowed(self.at(20, 59)))
        self.assertTrue(freeze_window_allowed(self.at(21, 0)))
        self.assertTrue(freeze_window_allowed(self.at(21, 15)))
        self.assertTrue(freeze_window_allowed(self.at(21, 20)))
        self.assertFalse(freeze_window_allowed(self.at(21, 31)))
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
            (folder/"report.md").write_text("# verified report")
            self.assertEqual(decide(self.at(20,35),tmp)[3],
                             "current_round_already_completed")
            self.assertFalse(decide(self.at(20,50),tmp)[0])
            self.assertTrue(decide(self.at(20,35),tmp,manual=True)[0])
            self.write_snapshot(tmp,issue)
            # Once frozen, a manual run is only a test collection.
            self.assertEqual(decide(self.at(20,35),tmp,manual=True)[3],
                             "manual_test_after_freeze")

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
        """Regression: audit pushes must not finalize; freeze needs dispatch
        or the authorized nightly freeze cron (user decision 2026-11-19)."""
        workflow = (Path(__file__).resolve().parents[2] / ".github" /
                    "workflows" / "forum-observer.yml").read_text(encoding="utf-8")
        freeze_job = workflow.split("\n  freeze:\n", 1)[1]
        self.assertIn("inputs.mode == 'freeze'", freeze_job)
        self.assertNotIn("trigger(forum-audit):", freeze_job)
        self.assertNotIn("forum_observer.runtime supervise", freeze_job)
        # Nightly automation (user-authorized 2026-11-19): only the authorized
        # cron slots exist — 20:25 collect, 21:00/21:05 freeze (Beijing).
        self.assertIn("schedule:", workflow)
        for cron in ('"25 12 * * *"', '"0 13 * * *"', '"5 13 * * *"'):
            self.assertIn("- cron: " + cron, workflow)
        for retired in ('0,5,10 12', '30,35,40 12'):
            self.assertNotIn(retired, workflow)
        self.assertIn("workflow_dispatch:", workflow)
        self.assertIn("forum_observer.freeze_operation", workflow)
        self.assertIn("forum_observer.publish_collection forum-observer", workflow)
        self.assertIn("any delayed or", workflow)
        self.assertNotIn("Reject delayed scheduled collection", workflow)
        self.assertNotIn("Align GPT collection trigger", workflow)

    def test_workflow_nightly_cron_matches_freeze_window(self):
        """Nightly cron (user-authorized revised): collects at 20:25 as a backup,
        freeze at 21:00/21:05 Beijing; manual dispatch keeps working."""
        workflow = (Path(__file__).resolve().parents[2] / ".github" /
                    "workflows" / "forum-observer.yml").read_text(encoding="utf-8")
        self.assertIn("mode:", workflow)
        self.assertIn("freeze", workflow)
        self.assertIn("github.event.schedule == '25 12 * * *'", workflow)
        self.assertIn("github.event.schedule == '0 13 * * *'", workflow)
        self.assertIn("github.event.schedule == '5 13 * * *'", workflow)
        # External freeze is gated in exactly the same pre-draw window as cron.
        self.assertIn("trigger(forum-freeze):", workflow)
        self.assertIn("forum_observer.freeze_operation", workflow)
        self.assertIn("--require-snapshot", workflow)

    def test_forum49_gate_automatic_once_manual_always(self):
        with TemporaryDirectory() as tmp:
            issue = current_issue(self.at(20, 20))
            self.assertEqual(forum49_collect_gate(tmp, self.at(20, 20), True), (True, "automatic_collection", False))
            self.assertEqual(forum49_collect_gate(tmp, self.at(1, 5), True),
                             (True, "automatic_collection", False))
            self.assertEqual(forum49_collect_gate(tmp, self.at(23, 50), True),
                             (True, "manual_test_after_freeze", True))
            folder = Path(tmp) / "issues" / issue
            folder.mkdir(parents=True)
            (folder / "draft-status.json").write_text(json.dumps({
                "issue": issue, "status": "ready_observation", "valid_materials": 90,
                "fetched_at": self.at(20, 38).isoformat()}), encoding="utf-8")
            (folder/"report.md").write_text("# verified report")
            self.assertEqual(forum49_collect_gate(tmp, self.at(20, 40), True)[1],
                             "already_collected_tonight")
            # Manual dispatch keeps working at any time.
            self.assertEqual(forum49_collect_gate(tmp, self.at(20, 40), False), (True, "manual_collection", False))
            self.assertEqual(forum49_collect_gate(tmp, self.at(19, 0), False), (True, "manual_collection", False))
            # From 21:00 a manual run is a test only; automatic runs never start.
            self.assertEqual(forum49_collect_gate(tmp, self.at(21, 5), False), (True, "manual_test_after_freeze", True))
            self.assertEqual(forum49_collect_gate(tmp, self.at(23, 0), False)[2], True)
            self.assertEqual(forum49_collect_gate(tmp, self.at(21, 5), True),
                             (True, "manual_test_after_freeze", True))

    def test_manual_118_collection_any_time_before_freeze_then_test_only(self):
        with TemporaryDirectory() as tmp:
            # Unlimited manual runs before the cutoff, even after tonight's draft.
            for hour, minute in ((9, 0), (19, 30), (20, 40), (20, 57)):
                go, _, _, reason = decide(self.at(hour, minute), tmp, manual=True)
                self.assertTrue(go, (hour, minute))
                self.assertEqual(reason, "manual_rerun")
            issue = current_issue(self.at(20, 20))
            folder = Path(tmp) / "issues" / issue
            folder.mkdir(parents=True)
            (folder / "draft-status.json").write_text(json.dumps({
                "issue": issue, "status": "ready_observation", "valid_materials": 80,
                "fetched_at": self.at(20, 31).isoformat()}), encoding="utf-8")
            self.assertEqual(decide(self.at(20, 45), tmp, manual=True)[3], "manual_rerun")
            # After 21:00, a run is isolated test collection and never delays freeze.
            self.assertEqual(decide(self.at(21, 2), tmp, manual=True)[3], "manual_test_after_freeze")
            self.assertTrue(decide(self.at(21, 2), tmp, manual=True)[0])
            # After the window, or once frozen: test collection allowed.
            self.assertEqual(decide(self.at(21, 30), tmp, manual=True)[3], "manual_test_after_freeze")
            self.write_snapshot(tmp, issue)
            go, _, seconds, reason = decide(self.at(21, 5), tmp, manual=True)
            self.assertEqual((go, reason, seconds), (True, "manual_test_after_freeze", 1800))
            # Delayed automatic triggers also use the isolated test path.
            self.assertEqual(decide(self.at(21, 30), tmp, scheduled=True,
                                    slot=BACKUP_COLLECT_CRON)[3], "manual_test_after_freeze")

    def test_any_same_day_manual_draft_is_freezable(self):
        with TemporaryDirectory() as tmp:
            now = self.at(21, 1)
            issue = current_issue(now)
            folder = Path(tmp) / "issues" / issue
            folder.mkdir(parents=True)
            path = folder / "draft-status.json"
            path.write_text(json.dumps({"issue": issue, "status": "ready_observation",
                                        "valid_materials": 50,
                                        "fetched_at": self.at(15, 0).isoformat()}), encoding="utf-8")
            self.assertTrue(freeze_window_allowed(now, tmp))
            path.write_text(json.dumps({"issue": issue, "status": "ready_observation",
                                        "valid_materials": 50,
                                        "fetched_at": self.at(20, 0, 11).isoformat()}), encoding="utf-8")
            self.assertFalse(freeze_window_allowed(now, tmp))

    def test_forum49_workflow_keeps_manual_and_adds_nightly_cron(self):
        """Manual dispatch stays; nightly cron (user-authorized 2026-11-19) is
        limited to 20:20 collect and 21:00/21:05 freeze (Beijing)."""
        workflow = (Path(__file__).resolve().parents[2] / ".github" /
                    "workflows" / "forum49-model.yml").read_text(encoding="utf-8")
        self.assertIn("workflow_dispatch:", workflow)
        self.assertIn("mode:", workflow)
        self.assertIn("freeze", workflow)
        # Manual dispatch remains explicit; a late cron must not be marked green.
        self.assertIn("github.event_name == 'workflow_dispatch'", workflow)
        self.assertNotIn("Reject delayed scheduled collection", workflow)
        # Backup push trigger belongs to an isolated marker branch; never
        # reuses 118's trigger branch or initiates a freeze.
        self.assertIn("branches: [forum49-trigger]", workflow)
        self.assertIn("forum-observer/triggers/forum49/**", workflow)
        self.assertIn("trigger(forum49-collect):", workflow)
        self.assertIn("github.event_name != 'workflow_dispatch' && 'true'", workflow)
        self.assertIn("Publish and verify committed current-run draft", workflow)
        self.assertIn("forum_observer.publish_collection forum49", workflow)
        self.assertIn("trigger(forum49-freeze):", workflow)
        self.assertIn("forum_observer.freeze_operation forum49", workflow)
        for cron in ('"20 12 * * *"', '"0 13 * * *"', '"5 13 * * *"'):
            self.assertIn("- cron: " + cron, workflow)
        # Collect gate refuses the freeze crons; freeze job accepts only them.
        self.assertIn("github.event.schedule != '0 13 * * *'", workflow)
        self.assertIn("github.event.schedule == '0 13 * * *'", workflow)


if __name__ == "__main__":
    unittest.main()
