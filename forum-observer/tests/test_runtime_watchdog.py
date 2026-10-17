import unittest
from datetime import datetime, timezone
from unittest.mock import patch, Mock

from forum_observer.runtime import GitHub, metrics, activity, supervise
from forum_observer.progress import ArticleProgress
from forum_observer.watchdog import check, classify

NOW = datetime(2026, 10, 17, 12, 0, tzinfo=timezone.utc)


class FakeGitHub:
    def __init__(self, runs=None, jobs=None):
        self.files = {}
        self.calls = []
        self.runs = runs or []
        self.jobs = jobs or []

    def content(self, path):
        return {}, self.files.get(path, {})

    def write(self, path, value, **kwargs):
        import copy
        self.files[path] = copy.deepcopy(value)

    def request(self, path, method='GET', data=None):
        self.calls.append((path, method, data))
        if '/jobs?' in path:
            return {'jobs': self.jobs}
        return {'workflow_runs': self.runs}


class RuntimeWatchdogTests(unittest.TestCase):
    def setUp(self):
        output = patch('builtins.print')
        output.start()
        self.addCleanup(output.stop)

    def test_telemetry_never_substitutes_comment_metrics(self):
        values = metrics({'comment_checked_threads': 99, 'valid_materials': 1,
                          'parsed_predictions': [{'author': '甲', 'counted_for_ranking': True},
                                                 {'author': '乙', 'counted_for_ranking': False}]})
        self.assertIsNone(values['successful_post_opens'])
        self.assertEqual(values['valid_authors'], 1)

    def test_code_failure_is_never_blindly_retried(self):
        run = {'status': 'completed', 'conclusion': 'failure'}
        jobs = [{'steps': [{'name': 'Unit tests', 'conclusion': 'failure'}]}]
        self.assertEqual(classify(run, jobs, {}, NOW), ('code_or_validation_failure', False))
        self.assertEqual(classify(run, [], {}, NOW), ('unclassified_failure_requires_review', False))

    def test_infrastructure_failure_can_be_retried(self):
        run = {'status': 'completed', 'conclusion': 'failure'}
        jobs = [{'steps': [{'name': 'Install text collector', 'conclusion': 'failure'}]}]
        self.assertEqual(classify(run, jobs, {}, NOW), ('infrastructure_failure', True))

    def test_trigger_is_not_collection_success(self):
        run = {'status': 'in_progress', 'created_at': '2026-10-17T11:40:00Z'}
        self.assertEqual(classify(run, [], {}, NOW), ('startup_delayed', False))
        jobs = [{'steps': [{'name': 'Observe current issue', 'status': 'in_progress'}]}]
        self.assertEqual(classify(run, jobs, {}, NOW), ('collection_step_running_unverified', False))
        record = {'progress_updated_at': '2026-10-17T11:50:00+00:00'}
        self.assertEqual(classify(run, jobs, record, NOW), ('progress_stale', False))
        jobs[0]['steps'][0]['started_at'] = '2026-10-17T11:50:00Z'
        self.assertEqual(classify(run, jobs, {}, NOW), ('no_verified_article_yet', False))

    def test_only_verified_nonempty_article_counts_as_effective_activity(self):
        progress = ArticleProgress()
        item = {'author': '甲', 'is_featured': True}
        progress.queue([item])
        progress.mark(item, 'attempted')
        self.assertEqual(activity(progress.snapshot(), NOW), 'collecting_unverified')
        progress.mark(item, 'opened')
        progress.extracted(item, 'https://example.org/thread?id=1&type=3', '')
        self.assertEqual(activity(progress.snapshot(), NOW), 'collecting_unverified')
        progress.extracted(item, 'https://example.org/thread?id=1&type=3', '正文推荐六肖')
        data = progress.snapshot()
        data['last_verified_post']['verified_at'] = NOW.isoformat()
        self.assertEqual(activity(data, NOW), 'collecting_effectively')
        self.assertEqual(data['article_details']['featured']['opened'], 1)
        self.assertEqual(data['article_details']['ordinary']['opened'], 0)
        self.assertEqual(data['article_texts_extracted'], 1)
        self.assertTrue(data['last_verified_post']['text_sha256'])
        data['last_verified_post']['verified_at'] = '2026-10-17T11:50:00+00:00'
        self.assertEqual(activity(data, NOW), 'no_recent_verified_article')

    @patch('forum_observer.watchdog.decide', return_value=(True, '2026290', 1800, 'missing_snapshot_retry'))
    def test_missing_start_is_recorded_and_dispatches_are_limited(self, gate):
        github = FakeGitHub()
        for _ in range(4):
            result = check(github, NOW, True)
        self.assertEqual(result['state'], 'recovery_limit_reached')
        self.assertEqual(result['dispatches'], 3)
        self.assertEqual(sum(method == 'POST' for _, method, _ in github.calls), 3)
        self.assertEqual(len(result['events']), 4)

    @patch('forum_observer.watchdog.decide', return_value=(False, '2026290', 0, 'immutable_snapshot_exists'))
    def test_frozen_issue_never_dispatches(self, gate):
        github = FakeGitHub()
        self.assertEqual(check(github, NOW, True)['state'], 'frozen')
        self.assertFalse(github.calls)

    @patch('forum_observer.watchdog.decide', return_value=(True, '2026290', 1800, 'missing_snapshot_retry'))
    def test_running_collector_prevents_duplicate_dispatch(self, gate):
        run = {'id': 1, 'status': 'in_progress', 'event': 'workflow_dispatch',
               'created_at': '2026-10-17T11:59:00Z'}
        github = FakeGitHub([run])
        check(github, NOW, True)
        self.assertFalse(any(method == 'POST' for _, method, _ in github.calls))

    @patch('forum_observer.watchdog.decide', return_value=(False, '2026290', 0, 'daily_collection_cutoff_passed'))
    def test_no_recovery_after_hard_cutoff(self, gate):
        github = FakeGitHub()
        self.assertEqual(check(github, NOW, True)['state'], 'collection_window_closed')
        self.assertFalse(any(method == 'POST' for _, method, _ in github.calls))

    def test_publisher_cannot_write_frozen_data(self):
        github = object.__new__(GitHub)
        with self.assertRaises(ValueError):
            github.write('forum-observer/archive/issues/2026290/snapshot.json', {})

    @patch('forum_observer.runtime.safe_publish')
    @patch('forum_observer.runtime.decide', return_value=(False, '2026290', 0, 'immutable_snapshot_exists'))
    @patch('forum_observer.runtime.subprocess.Popen')
    def test_supervisor_does_not_start_child_for_frozen_issue(self, popen, gate, publish):
        self.assertEqual(supervise(), 0)
        popen.assert_not_called()

    @patch('forum_observer.runtime.safe_publish')
    @patch('forum_observer.runtime.threading.Thread')
    @patch('forum_observer.runtime.time.monotonic', side_effect=[0, 60])
    @patch('forum_observer.runtime.decide', return_value=(True, '2026290', 60, 'missing_snapshot_retry'))
    @patch('forum_observer.runtime.subprocess.Popen')
    def test_supervisor_enforces_actual_process_time_bound(self, popen, gate, clock, thread, publish):
        process = Mock()
        process.poll.return_value = None
        popen.return_value = process
        self.assertEqual(supervise(), 124)
        process.terminate.assert_called()
        process.wait.assert_called_with(timeout=10)


if __name__ == '__main__':
    unittest.main()
