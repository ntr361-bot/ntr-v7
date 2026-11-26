from datetime import datetime
import json
from pathlib import Path
import shutil
from tempfile import TemporaryDirectory
import unittest
from unittest.mock import patch

import settlement as service

ISSUE = "2026330"
OLD = "2026328"
NOW = datetime.fromisoformat("2026-11-27T00:30:00+08:00")
DRAW = {"issue": ISSUE, "special_number": "26", "special_zodiac": "蛇",
        "numbers": "253520083022", "open_time": "2026-11-26T21:35:31+08:00"}


class SettlementTests(unittest.TestCase):
    def test_draw_delay_and_conflicting_source(self):
        archive = {"status": "success", "records": []}
        early = datetime.fromisoformat("2026-11-26T21:35:00+08:00")
        self.assertEqual(service.verified_draws(archive, [DRAW], early), ({}, set()))
        archive["records"] = [DRAW]
        alternate = dict(DRAW, special_number="38")
        self.assertEqual(service.verified_draws(archive, [alternate], NOW), ({}, {ISSUE}))
        self.assertEqual(service.verified_draws(archive, [DRAW], NOW)[0][ISSUE], DRAW)

    def test_historical_backfill_idempotence_and_author_increment(self):
        archive = service.ROOT / "forum-observer" / "forum49"
        model = next(row for row in service.registry()["models"] if row["id"] == "forum49")
        with TemporaryDirectory() as tmp:
            root = Path(tmp)
            copied = root / "forum-observer" / "forum49"
            source = archive / "issues" / ISSUE
            target = copied / "issues" / ISSUE
            target.mkdir(parents=True)
            for file in ("snapshot.json", "observation.json", "report.md"):
                shutil.copyfile(source / file, target / file)
            snapshot = (target / "snapshot.json").read_bytes()
            history = root / "history.json"
            history.write_text(json.dumps({"records": [DRAW]}, ensure_ascii=False), encoding="utf-8")
            former = copied / "issues" / OLD
            former.mkdir()
            (former / "snapshot.json").write_text("broken", encoding="utf-8")
            with patch.multiple(service, ROOT=root, DATA=root / "data"):
                outcomes, count = service.reconcile_forum(model, {ISSUE, OLD}, {ISSUE: DRAW},
                                                           set(), history, apply=True)
                self.assertEqual(count, 1)
                self.assertEqual(outcomes[OLD]["state"], "failed")
                self.assertEqual(outcomes[ISSUE]["author_statistics"], "settled")
                self.assertEqual(outcomes[ISSUE]["rank"], 6)
                original = (target / "settlement.json").read_bytes()
                self.assertEqual(snapshot, (target / "snapshot.json").read_bytes())
                outcomes, count = service.reconcile_forum(model, {ISSUE, OLD}, {ISSUE: DRAW},
                                                           set(), history, apply=True)
                self.assertEqual(count, 0)
                self.assertEqual(outcomes[ISSUE]["state"], "settled")
                self.assertEqual(original, (target / "settlement.json").read_bytes())

    def test_no_frozen_record(self):
        model = next(row for row in service.registry()["models"] if row["id"] == "forum49")
        with TemporaryDirectory() as tmp, patch.object(service, "ROOT", Path(tmp)):
            outcome, count = service.reconcile_forum(model, {ISSUE}, {ISSUE: DRAW}, set(),
                                                      Path("unused"), apply=False)
            self.assertEqual((outcome[ISSUE]["reason"], count), ("snapshot_missing", 0))

    def test_runtime_registration_is_data_driven(self):
        model = {"id": "new", "version": "new_version", "periods": 42}
        row = {"modelVersion": "new_version", "analysisPeriods": 42}
        self.assertEqual(service.targets(model, {}, [row]), (None, [row]))

    def test_formal_runner_cannot_generate_or_refresh(self):
        with patch.object(service.subprocess, "run") as run:
            service.run_formal({ISSUE})
        args = [call.args[0] for call in run.call_args_list]
        self.assertEqual(args[0][-2:], ["--rebuild-db", "--rebuild-only"])
        self.assertEqual(args[1][-3:-1], ["--settle-only", "--settlement-issues"])
        self.assertTrue(all("--generate-all" not in call and "--refresh-data" not in call for call in args))

    def test_sync_retry_is_read_only(self):
        status = {"issue": ISSUE, "models": [{"id": "forum49", "state": "settled"}]}
        expected = {"digest": "x", "settlement": {"issue": ISSUE}}
        with patch.object(service, "load", side_effect=[expected, expected]), \
             patch.object(service, "registry", return_value={"models": [{"id": "forum49", "archive": "forum49"}]}), \
             patch.object(service, "read_live", side_effect=[expected, OSError("timeout"), expected, expected]):
            self.assertEqual(service.verify_ledger(status)["state"], "failed")
            self.assertEqual(service.verify_ledger(status)["state"], "verified")


if __name__ == "__main__":
    unittest.main()
