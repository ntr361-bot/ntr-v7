import unittest
from forum_observer.runtime import GitHub


class RuntimeStatusTests(unittest.TestCase):
    def _write(self, previous, current):
        client = object.__new__(GitHub)
        calls = []
        client.content = lambda path: ({"sha": "existing"}, previous)
        client.request = lambda path, method, payload: calls.append((path, method, payload)) or {}
        client.write("forum-observer/status/latest.json", current, latest=True)
        return calls

    def test_delayed_older_run_cannot_replace_newer_success(self):
        previous = {"issue": "2026329", "run_id": "900", "attempt": 1,
                    "state": "report_published", "updated_at": "2026-11-25T12:00:00+00:00"}
        current = {"issue": "2026329", "run_id": "899", "attempt": 1,
                   "state": "failed", "updated_at": "2026-11-25T12:30:00+00:00"}
        self.assertEqual(self._write(previous, current), [])

    def test_delayed_failure_cannot_replace_new_issue_success(self):
        previous = {"issue": "2026330", "run_id": "901", "attempt": 1,
                    "state": "report_published", "updated_at": "2026-11-26T02:00:00+00:00"}
        current = {"issue": "2026329", "run_id": "900", "attempt": 1,
                   "state": "failed", "updated_at": "2026-11-26T03:00:00+00:00"}
        self.assertEqual(self._write(previous, current), [])

    def test_newer_attempt_of_same_run_can_replace_failure(self):
        previous = {"issue": "2026329", "run_id": "900", "attempt": 1,
                    "state": "failed", "updated_at": "2026-11-25T12:00:00+00:00"}
        current = {"issue": "2026329", "run_id": "900", "attempt": 2,
                   "state": "report_published", "updated_at": "2026-11-25T11:00:00+00:00"}
        self.assertEqual(len(self._write(previous, current)), 1)

    def test_frozen_status_is_immutable_to_non_frozen_events(self):
        previous = {"issue": "2026329", "run_id": "900", "attempt": 1,
                    "state": "frozen", "updated_at": "2026-11-25T12:00:00+00:00"}
        current = {"issue": "2026329", "run_id": "901", "attempt": 1,
                   "state": "failed", "updated_at": "2026-11-25T12:30:00+00:00"}
        self.assertEqual(self._write(previous, current), [])


if __name__ == "__main__":
    unittest.main()
