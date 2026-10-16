"""Isolated author history tests; no formal prediction or database writes."""
import json
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory

from forum_observer.author_profiles import build, frozen_evidence, sha256
from forum_observer.core import ZODIACS, SCORING_RULE_VERSION, SCORING_RULES

ISSUE = "2026290"
FETCHED = "2026-10-17T11:45:00+00:00"
FROZEN = "2026-10-17T11:45:02+00:00"


def save(path, payload):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, ensure_ascii=False), encoding="utf-8")


def fixture(archive, duplicate=False, include_settlement=False):
    folder = archive / "issues" / ISSUE
    votes1 = list(ZODIACS[:6])
    votes2 = list(ZODIACS[2:])
    predictions = [
        dict(issue=ISSUE, author="作者甲", mode="include", play="六肖",
             picks=votes1, vote_zodiacs=votes1, weight_per_zodiac=1/6,
             excerpt="推荐六肖", counted_for_ranking=True,
             source_url="https://forum.example/#/corpusdetail?id=111&type=3",
             published_at="2026-10-17 15:00"),
        dict(issue=ISSUE, author="作者甲" if duplicate else "作者乙",
             mode="exclude", play="杀二肖", picks=list(ZODIACS[:2]),
             vote_zodiacs=votes2, weight_per_zodiac=1/10,
             excerpt="杀二肖", counted_for_ranking=True,
             source_url="https://forum.example/#/corpusdetail?id=112&type=3",
             published_at="2026-10-17 15:00"),
    ]
    total = {z: (1/6 if z in votes1 else 0) + (1/10 if z in votes2 else 0) for z in ZODIACS}
    ranking = [
        {"zodiac": z, "score": round(total[z], 6), "rank": i}
        for i, z in enumerate(sorted(ZODIACS, key=lambda z: (-total[z], ZODIACS.index(z))), 1)
    ]
    snapshot = dict(issue=ISSUE, fetched_at=FETCHED, frozen_at=FROZEN,
                    ranking=ranking, valid_materials=2,
                    classification="experimental_unverified_leaderboard",
                    scoring_rule_version=SCORING_RULE_VERSION,
                    scoring_rules=SCORING_RULES,
                    material_counts={"recommend": 1, "kill_zodiac": 1},
                    quality_status="below_minimum_samples")
    snapshot["digest"] = sha256(snapshot)
    save(folder / "snapshot.json", snapshot)
    save(folder / "observation.json", dict(
        issue=ISSUE, fetched_at=FETCHED, parsed_predictions=predictions))
    if include_settlement:
        save(folder / "settlement.json", dict(
            issue=ISSUE, snapshot_digest=snapshot["digest"], snapshot_at=FETCHED,
            drawn_at="2026-10-17T21:32:00+08:00", actual="虎"))
    return folder


class AuthorProfilesTests(unittest.TestCase):
    def test_verified_freeze_is_idempotent_and_settlement_is_derived(self):
        with TemporaryDirectory() as tmp:
            archive = Path(tmp) / "archive"
            folder = fixture(archive)
            result = build(archive)
            self.assertEqual(result["created_issue_records"], 1)
            self.assertEqual(result["profiles"], 2)
            record_path = archive / "author-profiles" / "records" / (ISSUE + ".json")
            first = record_path.read_bytes()
            self.assertEqual(build(archive)["created_issue_records"], 0)
            self.assertEqual(record_path.read_bytes(), first)
            index = archive / "author-profiles" / "index.json"
            first_profiles = json.loads(index.read_text())["authors"]
            self.assertTrue(all(row["records"][0]["outcome"] == "pending" for row in first_profiles))
            # A genuine settlement changes only the derived index, never the frozen author record.
            snap = json.loads((folder / "snapshot.json").read_text())
            save(folder / "settlement.json", dict(issue=ISSUE, actual="虎",
                 snapshot_digest=snap["digest"], snapshot_at=FETCHED,
                 drawn_at="2026-10-17T21:32:00+08:00"))
            build(archive)
            settled = json.loads(index.read_text())["authors"]
            self.assertTrue(all(row["records"][0]["outcome"] == "hit" for row in settled))
            self.assertEqual(record_path.read_bytes(), first)
            self.assertTrue(all(x["identity_status"] == "nickname_only_unverified_account" for x in settled))

    def test_conflicting_settlement_is_not_a_hit(self):
        with TemporaryDirectory() as tmp:
            archive = Path(tmp) / "archive"
            folder = fixture(archive, include_settlement=True)
            settlement = json.loads((folder / "settlement.json").read_text())
            settlement["snapshot_digest"] = "wrong"
            save(folder / "settlement.json", settlement)
            build(archive)
            profiles = json.loads((archive / "author-profiles" / "index.json").read_text())["authors"]
            self.assertTrue(all(p["records"][0]["outcome"] == "pending" for p in profiles))

    def test_duplicate_author_skipped_without_guessing_a_winner(self):
        with TemporaryDirectory() as tmp:
            archive = Path(tmp) / "archive"
            fixture(archive, duplicate=True)
            result = build(archive)
            self.assertEqual(result["created_issue_records"], 0)
            self.assertEqual(result["profiles"], 0)
            self.assertIn("multiple counted materials", result["skipped_issues"][0]["reason"])

    def test_tampered_freeze_is_rejected(self):
        with TemporaryDirectory() as tmp:
            archive = Path(tmp) / "archive"
            folder = fixture(archive)
            snap = json.loads((folder / "snapshot.json").read_text())
            snap["ranking"][0]["score"] += 1
            save(folder / "snapshot.json", snap)
            self.assertIn("digest mismatch", build(archive)["skipped_issues"][0]["reason"])
            self.assertFalse((archive / "author-profiles" / "records" / (ISSUE + ".json")).exists())

    def test_mismatched_capture_or_score_is_rejected(self):
        with TemporaryDirectory() as tmp:
            archive = Path(tmp) / "archive"
            folder = fixture(archive)
            obs = json.loads((folder / "observation.json").read_text())
            obs["fetched_at"] = "2026-10-17T12:30:00+00:00"
            save(folder / "observation.json", obs)
            self.assertIn("not captured with the freeze", build(archive)["skipped_issues"][0]["reason"])

    def test_existing_author_record_never_overwritten(self):
        with TemporaryDirectory() as tmp:
            archive = Path(tmp) / "archive"
            fixture(archive)
            build(archive)
            path = archive / "author-profiles" / "records" / (ISSUE + ".json")
            path.write_text(path.read_text() + "tamper", encoding="utf-8")
            result = build(archive)
            self.assertEqual(result["profiles"], 0)
            self.assertTrue(result["skipped_issues"])


if __name__ == "__main__":
    unittest.main()
