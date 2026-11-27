import hashlib
import json
import os
import tempfile
import unittest
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime
from pathlib import Path
from unittest.mock import Mock, patch

from forum_observer import freeze_operation as op
from forum_observer.cycle import write_once


class FreezeOperationTests(unittest.TestCase):
    issue = "2026328"
    now = datetime.fromisoformat("2026-11-24T21:05:00+08:00")
    draw = now.replace(minute=32)

    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        previous = Path.cwd()
        os.chdir(temp.name)
        self.addCleanup(os.chdir, previous)
        self.git = self.enterContext(patch.object(op, "git"))
        self.enterContext(patch.object(op.time, "sleep"))
        self.history = Path("history.json")
        self.history.write_text('{"records":[{"issue":"2026327"}]}')
        self.enterContext(patch.object(op, "HISTORY", self.history))

    def folder(self, model):
        folder = Path(op.ARCHIVES[model]) / "issues" / self.issue
        folder.mkdir(parents=True, exist_ok=True)
        return folder

    def frozen(self, model, source="manual"):
        folder = self.folder(model)
        snap = {"issue": self.issue, "fetched_at": self.now.replace(hour=20).isoformat(),
                "frozen_at": self.now.isoformat(),
                "ranking": [{"rank": i, "zodiac": z} for i, z in enumerate(op.ZODIACS, 1)],
                **{f"top{size}": list(op.ZODIACS[:size]) for size in (1, 3, 6)},
                "freeze_provenance": {"source": source, "run_id": "one",
                                      "report_sha256": "original report digest"}}
        snap["digest"] = hashlib.sha256(json.dumps(snap, ensure_ascii=False, sort_keys=True).encode()).hexdigest()
        (folder / "snapshot.json").write_text(json.dumps(snap))
        report = f"# 论坛 {self.issue} 采集成绩单\n本期冻结状态：测试；冻结时间{snap['frozen_at']}；不可变摘要：{snap['digest']}".encode()
        (folder / "report.md").write_bytes(report)

    def call(self, model, phase, **kwargs):
        journal = {"run_id": "two", "run_attempt": 1, "failures": []}
        op.operate(model, phase, self.issue, journal, clock=lambda: self.now, **kwargs)
        return journal

    def test_both_unfrozen_each_has_its_own_writer(self):
        def create(model, *args):
            self.frozen(model, "automatic")
        with patch.object(op, "create_snapshot", side_effect=create) as writer, patch.object(op, "verify_feed"):
            for model in op.ARCHIVES:
                journal = self.call(model, "auto")
                self.assertEqual(journal["source"], "automatic")
            self.assertEqual([call.args[0] for call in writer.call_args_list], list(op.ARCHIVES))

    def test_both_manual_and_repeat_do_not_write(self):
        with patch.object(op, "create_snapshot") as writer, patch.object(op, "verify_feed"):
            for model in op.ARCHIVES:
                self.frozen(model)
                path = self.folder(model) / "snapshot.json"
                before = path.read_bytes()
                for phase in ("auto", "manual", "verify"):
                    journal = self.call(model, phase)
                    self.assertEqual(journal["source"], "manual")
                    self.assertEqual(journal["run_id"], "two")
                    self.assertEqual(journal["frozen_by_run_id"], "one")
                self.assertEqual(path.read_bytes(), before)
            writer.assert_not_called()

    def test_already_frozen_model_is_independent_of_missing_model(self):
        self.frozen("forum-observer")
        with patch.object(op, "verify_feed"):
            self.call("forum-observer", "auto")
            with self.assertRaises(FileNotFoundError):
                self.call("forum49", "auto")

    def test_atomic_write_once_with_concurrent_writers(self):
        path = Path("snapshot.json")
        with ThreadPoolExecutor(2) as pool:
            outcomes = list(pool.map(lambda value: write_once(path, {"value": value}), (1, 2)))
        self.assertEqual(sum(outcomes), 1)
        original = path.read_bytes()
        self.assertFalse(write_once(path, {"value": 3}))
        self.assertEqual(path.read_bytes(), original)

    def test_corrupt_snapshot_never_overwritten(self):
        folder = self.folder("forum49")
        snap = folder / "snapshot.json"
        snap.write_text("{}")
        with patch.object(op, "create_snapshot") as writer, self.assertRaises(ValueError):
            self.call("forum49", "verify")
        writer.assert_not_called()
        self.assertEqual(snap.read_text(), "{}")

    def draft(self, model, minute, *, count=1, issue=None):
        folder = self.folder(model)
        fetched = self.now.replace(hour=20, minute=minute)
        status = {"issue": issue or self.issue, "fetched_at": fetched.isoformat(),
                  "draw_at": self.draw.isoformat(), "pre_draw_verified": True,
                  "valid_materials": count, "status": "incomplete_observation",
                  "scoring_rule_version": op.SCORING_RULE_VERSION, "top6": list("鼠牛虎兔龙蛇"),
                  "collection_run_id": str(minute), "collection_round": 1}
        report = (f"# 论坛 {self.issue} 采集成绩单\n抓取时间：{status['fetched_at']}\n"
                  f"**有效资料：{count}份**（经期号核对、去重且实际计分）\n观察Top6：鼠、牛、虎、兔、龙、蛇").encode()
        round_dir = folder / "rounds" / f"round1-{fetched:%Y%m%dT%H%M%S%f}-{minute}"
        round_dir.mkdir(parents=True, exist_ok=True)
        (round_dir / "summary.json").write_text(json.dumps(status))
        (round_dir / "report.md").write_bytes(report)
        (folder / "draft-status.json").write_text(json.dumps(status))
        (folder / "report.md").write_bytes(report)
        return folder

    @patch.object(op, "verify_scored_result")
    def test_latest_published_round_and_under_25(self, verify):
        self.draft("forum49", 20)
        folder = self.draft("forum49", 45)
        status, _ = op.load_draft(folder, self.issue, self.now)
        self.assertEqual(status["collection_run_id"], "45")
        verify.assert_called_once_with(status, min_materials=1)
        (folder / "report.md").write_bytes(b"damaged")
        with self.assertRaisesRegex(op.FreezeError, "draft_report_mismatch"):
            op.load_draft(folder, self.issue, self.now)

    def test_wrong_period_and_damaged_json(self):
        folder = self.draft("forum49", 45, issue="2026327")
        with self.assertRaisesRegex(op.FreezeError, "draft_issue_or_time_mismatch"):
            op.load_draft(folder, self.issue, self.now)
        (folder / "draft-status.json").write_text("{")
        with self.assertRaises(json.JSONDecodeError):
            op.load_draft(folder, self.issue, self.now)

    def test_safe_freeze_retry(self):
        def create(model, *args):
            if writer.call_count == 1:
                raise op.FreezeError("transient")
            self.frozen(model, "automatic")
        with patch.object(op, "create_snapshot", side_effect=create) as writer, patch.object(op, "verify_feed"):
            journal = self.call("forum49", "verify")
        self.assertEqual(writer.call_count, 2)
        self.assertEqual(journal["retry_count"], 1)
        self.assertEqual(journal["failures"], ["transient"])

    def test_ledger_retry_does_not_freeze_again(self):
        self.frozen("forum49")
        with patch.object(op, "create_snapshot") as writer, patch.object(
                op, "verify_feed", side_effect=[None, OSError("cache stale"), None]):
            journal = self.call("forum49", "verify")
        writer.assert_not_called()
        self.assertEqual(journal["ledger_retry_count"], 1)
        self.assertEqual(journal["ledger_sync"], "verified_ledger_source")

    def test_delayed_verification_never_creates_new_snapshot(self):
        next_day = self.now.replace(day=25, hour=6)
        journal = {"run_id": "late", "run_attempt": 1, "failures": []}
        with patch.object(op, "create_snapshot") as writer:
            with self.assertRaisesRegex(op.FreezeError, "missed_freeze_window"):
                op.operate("forum49", "verify", self.issue, journal, clock=lambda: next_day)
            writer.assert_not_called()
        self.frozen("forum49")
        with patch.object(op, "create_snapshot") as writer, patch.object(op, "verify_feed") as verify:
            op.operate("forum49", "verify", self.issue, journal, clock=lambda: next_day)
            writer.assert_not_called()
            self.assertTrue(verify.call_args.kwargs["historical"])

    def test_live_results_require_matching_draw_and_block_current_placeholder(self):
        valid = {"lotteryId": 2032, "issue": "2026327",
                 "openTime": "2026-11-23 21:35:31", "openCode": "01,02,03,04,05,06,07"}
        malformed = dict(valid, issue="2026326", openCode="00,02,03,04,05,06,07")
        current = {"lotteryId": 2032, "issue": self.issue}
        response = Mock()
        response.__enter__ = Mock(return_value=response)
        response.__exit__ = Mock(return_value=False)
        response.read.return_value = json.dumps(
            {"code": 0, "data": [valid, malformed, current]}).encode()
        with patch.object(op, "urlopen", return_value=response):
            self.assertEqual(op.live_draw_state(self.now),
                             [{"issue": "2026327"}, {"issue": self.issue}])

    def test_after_deadline_or_missing_draw_state_never_writes(self):
        for clock in (lambda: self.draw, lambda: self.now.replace(minute=20)):
            with self.assertRaisesRegex(op.FreezeError, "outside_safe_freeze_window"):
                op.guard(self.issue, False, self.draw, clock, Mock())
        for data in ([], [{"issue": self.issue}]):
            with self.assertRaises(op.FreezeError):
                op.guard(self.issue, True, self.draw, lambda: self.now, lambda: data)
        self.assertEqual(op.guard(self.issue, True, self.draw, lambda: self.now,
                                  lambda: [{"issue": "2026327"}]), self.now)
        with self.assertRaises(op.FreezeError):
            op.check_window("2026327", self.now.replace(day=23), True)
