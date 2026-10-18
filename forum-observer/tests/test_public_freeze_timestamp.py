"""Regression: public forum display time must come from immutable freeze, not collection."""
import hashlib
import json
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory

from forum_observer.cycle import sync


def json_write(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


class PublicFreezeTimestampTests(unittest.TestCase):
    def _run_case(self, frozen_at):
        with TemporaryDirectory() as tmp:
            base = Path(tmp)
            archive = base / "archive"
            folder = archive / "issues" / "2026291"
            out = base / "out"
            out.mkdir()
            history = base / "history.json"
            public = base / "public"
            json_write(history, {"records": []})
            snap = {
                "issue": "2026291",
                "fetched_at": "2026-10-18T12:23:57.319657+00:00",
                "ranking": [{"rank": n + 1, "zodiac": zodiac} for n, zodiac in enumerate("鼠牛虎兔龙蛇马羊猴鸡狗猪")],
                "top1": ["鼠"], "top3": ["鼠", "牛", "虎"],
                "top6": ["鼠", "牛", "虎", "兔", "龙", "蛇"],
            }
            if frozen_at is not None:
                snap["frozen_at"] = frozen_at
            snap["digest"] = hashlib.sha256(
                json.dumps(snap, ensure_ascii=False, sort_keys=True).encode()
            ).hexdigest()
            json_write(folder / "snapshot.json", snap)
            original = (folder / "snapshot.json").read_bytes()
            sync(out, archive, history, public)
            published = json.loads((public / "issues" / "2026291.json").read_text(encoding="utf-8"))
            self.assertEqual((folder / "snapshot.json").read_bytes(), original)
            self.assertEqual(published["digest"], snap["digest"])
            self.assertEqual(published["ranking"], snap["ranking"])
            return published

    def test_current_snapshot_uses_actual_freeze_not_capture(self):
        actual = "2026-10-18T13:00:35.072908+00:00"
        published = self._run_case(actual)
        self.assertEqual(published["frozen_at"], actual)
        self.assertNotEqual(published["frozen_at"], "2026-10-18T12:23:57.319657+00:00")
        self.assertEqual(published["freeze_time_source"], "snapshot_frozen_at")

    def test_old_snapshot_without_freeze_field_is_explicitly_marked(self):
        published = self._run_case(None)
        self.assertEqual(published["frozen_at"], "2026-10-18T12:23:57.319657+00:00")
        self.assertEqual(published["freeze_time_source"], "legacy_collection_time_fallback")

    def test_real_291_published_fields_match_existing_immutable_snapshot(self):
        root = Path(__file__).resolve().parents[1]
        snap = json.loads((root / "archive/issues/2026291/snapshot.json").read_text(encoding="utf-8"))
        exposed = json.loads((root.parent / "site/data/forum-observer/issues/2026291.json").read_text(encoding="utf-8"))
        self.assertEqual(exposed["frozen_at"], snap["frozen_at"])
        self.assertEqual(exposed["digest"], snap["digest"])
        self.assertEqual(exposed["top3"], snap["top3"])
        self.assertEqual(exposed["top6"], snap["top6"])
        self.assertEqual(exposed["ranking"], snap["ranking"])
        self.assertEqual(exposed["freeze_time_source"], "snapshot_frozen_at")


if __name__ == "__main__":
    unittest.main()
