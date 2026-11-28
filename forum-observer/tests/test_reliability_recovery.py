import base64
import hashlib
import json
import unittest
from datetime import datetime, timedelta
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest.mock import Mock, patch
from zoneinfo import ZoneInfo

from forum_observer import health_check, publish_collection, runtime, schedule
from forum_observer import freeze_operation


class ReliabilityRecoveryTests(unittest.TestCase):
    now = datetime(2026, 11, 28, 21, 15, tzinfo=ZoneInfo("Asia/Shanghai"))
    issue = "2026332"

    def test_large_github_contents_uses_exact_blob(self):
        github = runtime.GitHub.__new__(runtime.GitHub)
        item = {"encoding": "none", "content": "", "sha": "exact"}
        payload = base64.b64encode(b'{"issue":"2026332"}').decode()
        github.request = Mock(side_effect=[item, {"encoding": "base64", "content": payload}])
        self.assertEqual(github.content("archive/draft-status.json"), (item, {"issue": self.issue}))
        github.request.assert_called_with("git/blobs/exact")

    def test_corrupt_draft_allows_backup_retry_for_either_model(self):
        with TemporaryDirectory() as directory:
            folder = Path(directory, "issues", self.issue)
            folder.mkdir(parents=True)
            (folder / "draft-status.json").write_text("{")
            now = self.now.replace(hour=20)
            self.assertTrue(schedule.decide(now, directory, scheduled=True)[0])
            self.assertTrue(schedule.forum49_collect_gate(directory, now, True)[0])

    def test_restore_uses_only_verified_round_and_never_a_corrupt_copy(self):
        with TemporaryDirectory() as directory:
            folder = Path(directory)
            round_name = "round1-20261128T203000000000-123"
            path = folder / "rounds" / round_name
            path.mkdir(parents=True)
            runs = folder / "collection-runs"
            runs.mkdir()
            report = b"valid captured report"
            draft = json.dumps({"collection_run_id": "123", "collection_run_attempt": 1}).encode()
            (path / "draft-status.json").write_bytes(draft)
            (path / "report.md").write_bytes(report)
            record = {
                "issue": self.issue, "round": round_name, "run_id": "123", "run_attempt": 1,
                "valid_authors": 1, "valid_materials": 1, "report_published": True,
                "draft_published": True, "draft_sha256": hashlib.sha256(draft).hexdigest(),
                "report_sha256": hashlib.sha256(report).hexdigest(),
            }
            (runs / "123-1.json").write_text(json.dumps(record))
            self.assertEqual(publish_collection.verify_publication(
                folder, self.issue, "123", 1, Path.read_bytes), record)
            (folder / "draft-status.json").write_text("{")
            (folder / "report.md").write_bytes(b"")
            with patch.object(freeze_operation, "validate_draft") as validate:
                status, restored = freeze_operation.restore_draft(folder, self.issue, self.now)
                self.assertEqual(status["collection_run_id"], "123")
                self.assertEqual(restored, report)
                self.assertEqual((folder / "draft-status.json").read_bytes(), draft)
                validate.assert_called_once()
            (path / "report.md").write_bytes(b"corrupt")
            (folder / "draft-status.json").unlink()
            with self.assertRaisesRegex(ValueError, "checksum mismatch"):
                publish_collection.verify_publication(folder, self.issue, "123", 1, Path.read_bytes)
            with self.assertRaises(FileNotFoundError):
                freeze_operation.restore_draft(folder, self.issue, self.now)
            self.assertFalse((folder / "draft-status.json").exists())

    def test_health_late_audit_and_seven_consecutive_real_days(self):
        self.assertEqual(health_check.latest_scheduled_day(
            datetime(2026, 11, 28, 6, 52, tzinfo=self.now.tzinfo), 22, 35).day, 27)
        github = Mock()
        saved = {}
        milestones = dict.fromkeys(("started", "collected", "draft_saved", "frozen", "settled", "synced"), True)
        models = {model: {**milestones, "manual_requests": 0} for model in ("118", "49")}
        for step in range(7):
            day = self.now + timedelta(days=step)
            github.content.return_value = ({}, saved)
            saved = health_check.rolling_acceptance(
                github, day.date().isoformat(), schedule.current_issue(day), models, [])
            self.assertEqual(saved["passed"], step == 6)

    def test_health_recovers_freeze_only_before_safe_cutoff(self):
        github = Mock()
        github.request.return_value = {"workflow_runs": []}
        audits = {"118": {"state": "draft_not_frozen"}, "49": {"state": "no_verified_snapshot"}}
        self.assertEqual(health_check.recover(github, self.now, self.issue, audits, {}), ["118_freeze"])
        github.request.assert_called_with("actions/workflows/forum-observer.yml/dispatches", "POST",
                                          {"ref": "main", "inputs": {"mode": "freeze", "issue": self.issue}})
        github.reset_mock()
        self.assertEqual(health_check.recover(github, self.now.replace(minute=37), self.issue, audits, {}), [])
        github.request.assert_not_called()
