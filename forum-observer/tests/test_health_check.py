"""Offline safety and correctness tests for cheap forum-health automation."""
import hashlib
import json
import unittest
import urllib.error
from datetime import datetime, timedelta, timezone
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest.mock import patch

from forum_observer import health_check as health

UTC = timezone.utc


def parent(index, status="completed", conclusion="success", target="both", mode="collect"):
    started = datetime(2026, 11, 23, 12, 0, tzinfo=UTC) - timedelta(minutes=index * 2)
    return {
        "id": 1000 + index,
        "event": "workflow_dispatch",
        "display_title": f"Manual forum {target} - {mode} · request#"
                         f"ffffffff-ffff-4fff-8fff-{index:012x}",
        "path": ".github/workflows/manual-forum-operations.yml",
        "status": status,
        "conclusion": conclusion,
        "created_at": started.isoformat(),
        "run_started_at": started.isoformat(),
        "updated_at": (started + timedelta(minutes=10)).isoformat(),
    }


class FakeGithub:
    def __init__(self, snapshot=None, issue=2026327, report=True, alert_open=False):
        self.issue = str(issue)
        self.snapshot = snapshot
        self.report = report
        self.alert_open = alert_open
        self.writes = []
        self.calls = []
        self.latest_data = {}

    def content(self, path):
        if path.endswith("/snapshot.json"):
            return ({"sha": "snap"}, self.snapshot) if self.snapshot else ({}, {})
        if path.endswith("/draft-status.json"):
            return ({"sha": "draft"}, {"issue": self.issue, "valid_materials": 25})
        if path.endswith("status/health/latest.json"):
            return ({"sha": "old"}, self.latest_data) if self.latest_data else ({}, {})
        return ({}, {})

    def request(self, path, method="GET", data=None):
        self.calls.append((path, method, data))
        if path.startswith("contents/") and path.endswith("report.md?ref=main"):
            return {"sha": "report"} if self.report else {}
        if path.startswith("actions/workflows/manual-forum-operations.yml/runs?"):
            return {"workflow_runs": [parent(0), parent(1, conclusion="failure")]}
        if path.startswith("actions/workflows/forum-observer.yml/runs?") or path.startswith("actions/workflows/forum49-model.yml/runs?"):
            return {"workflow_runs": []}
        if path == "issues?state=open&per_page=100":
            return ([{"title": health.ALERT_TITLE, "number": 7}]
                    if self.alert_open else [])
        if path == "issues" and method == "POST":
            return {"number": 8}
        if path.endswith("/comments") or method == "PATCH":
            return {}
        raise AssertionError("Unexpected external request " + path)

    def write(self, path, value, latest=False):
        assert path.startswith(health.HEALTH_DIR)
        self.writes.append((path, value, latest))


def snapshot(issue):
    core = {"issue": issue, "frozen_at": "2026-11-23T13:02:00+00:00",
            "top6": list("鼠牛虎兔龙蛇"), "valid_materials": 25}
    core["digest"] = hashlib.sha256(
        json.dumps(core, ensure_ascii=False, sort_keys=True).encode()).hexdigest()
    return core


class HealthCheckTests(unittest.TestCase):
    def test_last_30_uses_verified_parent_only_and_excludes_pending_denominator(self):
        rows = [parent(i) for i in range(40)]
        rows[0]["conclusion"] = "failure"
        rows[1]["status"] = "in_progress"
        rows[1]["conclusion"] = None
        rows[2]["event"] = "push"
        rows[3]["path"] = ".github/workflows/forum-observer.yml"
        sample = health.manual_sample(rows)
        self.assertEqual(sample["sample_size"], 30)
        self.assertEqual(sample["completed"], 29)
        self.assertEqual(sample["verified"], 28)
        self.assertEqual(sample["failed_or_cancelled"], 1)
        self.assertEqual(sample["in_progress"], 1)
        self.assertEqual(sample["verified_rate_pct"], 96.6)
        self.assertEqual(sample["mean_completed_minutes"], 10)
        self.assertTrue(sample["sample_complete"])

    def test_empty_manual_history_has_no_fabricated_success_rate(self):
        row = health.manual_sample([{"path": ".github/workflows/forum49-model.yml",
                                     "conclusion": "success"}])
        self.assertEqual(row["sample_size"], 0)
        self.assertIsNone(row["verified_rate_pct"])
        self.assertIsNone(row["mean_completed_minutes"])

    def test_last30_per_model_rates_require_matching_run_specific_receipts(self):
        g = FakeGithub()
        good = parent(0)
        failed = parent(1, conclusion="failure", target="118")
        def child(model, upstream, valid=True):
            return {
                "id": 800 if upstream["id"] == 1000 else 801,
                "run_attempt": 1,
                "event": "workflow_dispatch",
                "display_title": f"Forum {model} collect manual#{upstream['id']}",
                "path": (".github/workflows/forum-observer.yml" if model == "118"
                         else ".github/workflows/forum49-model.yml"),
                "status": "completed",
                "conclusion": "success" if valid else "failure",
            }
        children = {"118": [child("118", good), child("118", failed, False)],
                    "49": [child("49", good)]}
        original_request = g.request
        def get(path, method="GET", data=None):
            if path.startswith("actions/workflows/forum-observer.yml/runs?"):
                return {"workflow_runs": children["118"]}
            if path.startswith("actions/workflows/forum49-model.yml/runs?"):
                return {"workflow_runs": children["49"]}
            return original_request(path, method, data)
        g.request = get
        original_content = g.content
        def get_content(path):
            if "collection-runs/800-1.json" in path:
                model = "118" if "/archive/" in path else "49"
                return {"sha": "receipt"}, {
                    "issue": "2026327", "run_id": "800", "run_attempt": 1,
                    "valid_materials": 25, "draft_published": True,
                    "report_published": True,
                }
            return original_content(path)
        g.content = get_content
        quality = health._model_quality(g, [good, failed])
        self.assertEqual(quality["118"]["requested"], 2)
        self.assertEqual(quality["118"]["started"], 2)
        self.assertEqual(quality["118"]["effective_collections"], 1)
        self.assertEqual(quality["118"]["archived_reports"], 1)
        self.assertEqual(quality["118"]["startup_rate_pct"], 100.0)
        self.assertEqual(quality["118"]["report_archive_rate_pct"], 50.0)
        self.assertEqual(quality["49"]["requested"], 1)
        self.assertEqual(quality["49"]["archived_reports"], 1)

    def test_explicit_118_and_49_configured_sources(self):
        with TemporaryDirectory() as d:
            file = Path(d) / "collector-config.json"
            file.write_text(json.dumps({"forum_url": "https://abc.example/#/forum"}))
            urls = health.model_urls(file)
            self.assertEqual(urls["118"], "https://abc.example/#/forum")
            self.assertEqual(urls["49"], "https://49217.com/#/forum")

    def test_readiness_probe_retries_transient_errors_without_scraping(self):
        success = {"state": "reachable", "host": "example.org", "http_status": 200}
        with patch.object(health, "probe_once", side_effect=[
                urllib.error.URLError("temporary timeout"),
                urllib.error.URLError("temporary timeout"), success]) as probe:
            sleeps = []
            result = health.probe_with_retry("https://example.org/#/forum", sleep=sleeps.append)
        self.assertEqual(result, success)
        self.assertEqual(probe.call_count, 3)
        self.assertEqual(sleeps, [1, 2])

    def test_unexpected_document_does_not_retry_and_cannot_be_healthy(self):
        bad = {"state": "unexpected_document", "host": "example.org", "http_status": 200}
        with patch.object(health, "probe_once", return_value=bad) as probe:
            result = health.probe_with_retry("https://example.org", sleep=lambda _: None)
        self.assertEqual(result, bad)
        self.assertEqual(probe.call_count, 1)

    def test_nightly_snapshot_digest_and_report_are_strictly_validated(self):
        g = FakeGithub(snapshot("2026327"))
        result = health.safe_snapshot_audit(g, "2026327")
        self.assertEqual(result["118"]["state"], "frozen_verified")
        self.assertEqual(result["49"]["state"], "frozen_verified")
        corrupted = dict(snapshot("2026327"), valid_materials=99)
        broken = health.safe_snapshot_audit(FakeGithub(corrupted), "2026327")
        self.assertEqual(broken["118"]["state"], "invalid_snapshot_or_report")
        missing = health.safe_snapshot_audit(FakeGithub(), "2026327")
        self.assertEqual(missing["49"]["state"], "draft_not_frozen")

    def test_invalid_draft_does_not_hide_other_forum(self):
        g = FakeGithub(snapshot("2026327"))
        original = g.content
        def bad(path):
            if "/archive/issues/" in path:
                raise ValueError("bad JSON")
            return original(path)
        g.content = bad
        results = health.safe_snapshot_audit(g, "2026327")
        self.assertEqual(results["118"]["state"], "archive_read_error")
        self.assertEqual(results["49"]["state"], "frozen_verified")

    def test_delayed_audit_checks_previous_issue(self):
        g = FakeGithub(snapshot("2026327"))
        with patch.object(health, "fetch_manual_sample",
                          return_value={**health.manual_sample([]), "by_model": {}}):
            report = health.run("audit", publish=False, github=g,
                                now=datetime(2026, 11, 24, 6, 0, tzinfo=health.TZ))
        self.assertEqual(report["issue"], "2026327")
        self.assertEqual(report["archive_audit"]["118"]["state"], "frozen_verified")

    def test_small_sample_is_not_mislabeled_low_reliability(self):
        sample = health.manual_sample([parent(0, conclusion="failure")])
        self.assertNotIn("manual_verified_rate_below_80_pct",
                         health.findings_for("probe", {}, {}, sample))

    def test_probe_and_nightly_audit_cannot_clear_each_others_alerts(self):
        old = ["118_probe_network_error", "49_audit_draft_not_frozen"]
        self.assertEqual(health.merge_findings("probe", [], old),
                         ["49_audit_draft_not_frozen"])
        self.assertEqual(health.merge_findings("audit", [], old),
                         ["118_probe_network_error"])
        self.assertEqual(health.merge_findings("all", [], old), [])

        g = FakeGithub(snapshot("2026327"), alert_open=True)
        g.latest_data = {"findings": ["118_probe_http_denied"]}
        with patch.object(health, "fetch_manual_sample",
                          return_value={**health.manual_sample([]),
                                        "by_model": {"118": {}, "49": {}}}):
            report = health.run("audit", publish=True, github=g,
                                now=datetime(2026, 11, 23, 22, 15, tzinfo=health.TZ))
        self.assertEqual(report["current_check_findings"], [])
        self.assertEqual(report["findings"], ["118_probe_http_denied"])
        self.assertFalse(any(method == "PATCH" for _, method, _ in g.calls),
                         "a healthy archive audit does not prove website recovery")

    def test_alert_issue_is_deduplicated_and_closed_when_recovered(self):
        g = FakeGithub(alert_open=False)
        health.update_alert(g, ["118_probe_network_error"], [], {})
        self.assertEqual(len([v for v in g.calls if v[1] == "POST"]), 1)
        g = FakeGithub(alert_open=True)
        health.update_alert(g, ["118_probe_network_error"],
                            ["118_probe_network_error"], {})
        self.assertEqual(len([v for v in g.calls if v[1] == "POST"]), 0)
        health.update_alert(g, [], ["118_probe_network_error"], {})
        self.assertTrue(any(v[1] == "PATCH" and v[2] == {"state": "closed"}
                            for v in g.calls))

    def test_dry_run_does_not_publish_or_dispatch_anything(self):
        g = FakeGithub(snapshot("2026327"))
        with patch.object(health, "model_urls", return_value={"118": "https://example.org"}), \
             patch.object(health, "probe_with_retry", return_value={"state": "reachable"}):
            report = health.run("all", publish=False, github=g,
                                now=datetime(2026, 11, 23, 22, 15, tzinfo=health.TZ))
        self.assertEqual(report["state"], "healthy")
        self.assertEqual(report["last_30_manual_operations"]["verified"], 1)
        self.assertFalse(g.writes)
        self.assertFalse(any(method in ("PUT", "POST", "PATCH")
                             for _, method, _ in g.calls))

    def test_publish_only_health_status_not_archive_or_ledger(self):
        g = FakeGithub(snapshot("2026327"))
        with patch.object(health, "model_urls", return_value={"118": "https://example.org"}), \
             patch.object(health, "probe_with_retry", return_value={"state": "reachable"}):
            health.run("all", publish=True, github=g,
                       now=datetime(2026, 11, 23, 22, 15, tzinfo=health.TZ))
        self.assertEqual(len(g.writes), 2)
        self.assertTrue(all(v[0].startswith("forum-observer/status/health/")
                            for v in g.writes))
        self.assertTrue(g.writes[1][2])


if __name__ == "__main__":
    unittest.main()
