import json
import os
import subprocess
import sys
import tempfile
import threading
import unittest
from pathlib import Path
from unittest.mock import patch

from forum_observer.trace import record, heartbeat
from forum_observer import runtime


class TraceTests(unittest.TestCase):
    def test_failed_config_still_has_real_start_and_failure_journal(self):
        with tempfile.TemporaryDirectory() as td:
            result = subprocess.run([sys.executable, '-m', 'forum_observer.auto',
                                     '--issue', '2026290', '--config', td+'/missing.json',
                                     '--out', td+'/out'], capture_output=True, text=True)
            self.assertEqual(result.returncode, 2)
            journal = [json.loads(x) for x in Path(td+'/out/monitor/events.jsonl').read_text().splitlines()]
            self.assertEqual([x['state'] for x in journal], ['starting', 'failed'])
            self.assertEqual(journal[-1]['issue'], '2026290')
            self.assertIn('missing.json', journal[-1]['error'])
            self.assertFalse(list(Path(td).rglob('snapshot.json')))

    def test_heartbeat_keeps_last_evidence_and_stops_after_terminal_state(self):
        with tempfile.TemporaryDirectory() as td:
            record(td, 'starting', issue='2026290')
            record(td, 'running', {'stage':'browser_launch', 'raw_count':0})
            stop=threading.Event()
            with patch.object(stop, 'wait', side_effect=[False, True]):
                heartbeat(td, stop, 0)
            first=json.loads(Path(td+'/monitor/latest.json').read_text())
            self.assertEqual(first['state'], 'heartbeat')
            self.assertEqual(first['stage'], 'browser_launch')
            record(td, 'failed', error='browser unavailable')
            with patch.object(stop, 'wait', side_effect=[False, True]):
                heartbeat(td, stop, 0)
            last=json.loads(Path(td+'/monitor/latest.json').read_text())
            self.assertEqual(last['state'], 'failed')
            self.assertEqual(last['sequence'], first['sequence']+1)

    def test_repeated_invocation_keeps_old_journal_without_old_counts(self):
        with tempfile.TemporaryDirectory() as td:
            a=record(td, 'starting', issue='2026289')
            record(td, 'completed', {'valid_materials':20})
            b=record(td, 'starting', issue='2026290')
            self.assertNotEqual(a['trace_id'], b['trace_id'])
            self.assertNotIn('valid_materials', b)
            self.assertEqual(len(Path(td+'/monitor/events.jsonl').read_text().splitlines()),3)

    def test_missing_github_credentials_does_not_destroy_local_evidence(self):
        with tempfile.TemporaryDirectory() as td:
            cwd=os.getcwd()
            try:
                os.chdir(td)
                with patch.dict(os.environ, {}, clear=True):
                    runtime.safe_publish('starting')
                journal=[json.loads(x) for x in Path('out/runtime-monitor/monitor/events.jsonl').read_text().splitlines()]
                self.assertEqual([x['state'] for x in journal],['starting','publication_failed'])
                self.assertTrue(Path('out/runtime.json').exists())
            finally:
                os.chdir(cwd)

    def test_runtime_api_cannot_write_freezes(self):
        github=object.__new__(runtime.GitHub)
        with self.assertRaises(ValueError):
            github.write('forum-observer/archive/issues/2026290/snapshot.json',{})

    def test_explicit_test_runs_audit_without_production_gate_or_freeze_command(self):
        with patch.dict(os.environ, {'FORUM_TEST_ISSUE':'2026290'}), patch.object(runtime,'safe_publish') as publish, patch.object(runtime,'decide') as gate, patch.object(runtime.subprocess,'Popen',side_effect=OSError('launch failed')) as child:
            self.assertEqual(runtime.supervise(),2)
            gate.assert_not_called()
            command=child.call_args.args[0]
            self.assertIn('forum_observer.audit',command)
            self.assertNotIn('forum_observer.auto',command)
            self.assertNotIn('--freeze',command)
            self.assertEqual(publish.call_args.args[0], 'failed')
