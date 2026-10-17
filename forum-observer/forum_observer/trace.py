"""Local run journal: available before dependencies, network and GitHub publication."""
import json
import os
import threading
import uuid
from datetime import datetime, timezone
from pathlib import Path

_lock = threading.RLock()


def record(out, state, data=None, error=None, issue=None):
    out = Path(out)
    with _lock:
        folder = out / 'monitor'
        folder.mkdir(parents=True, exist_ok=True)
        latest = folder / 'latest.json'
        try:
            previous = json.loads(latest.read_text())
        except (OSError, ValueError):
            previous = {}
        # A new invocation never inherits the previous invocation's counters.
        if state == 'starting':
            previous = {}
        value = dict(previous)
        value.update(schema_version=1, state=state,
                     event_id=uuid.uuid4().hex,
                     sequence=int(previous.get('sequence', 0)) + 1,
                     trace_id=previous.get('trace_id') or uuid.uuid4().hex,
                     updated_at=datetime.now(timezone.utc).isoformat(),
                     run_id=os.environ.get('GITHUB_RUN_ID'),
                     attempt=os.environ.get('GITHUB_RUN_ATTEMPT'), error=error)
        if issue is not None:
            value['issue'] = str(issue)
        if data:
            for key in ('issue', 'stage', 'raw_count', 'valid_materials',
                        'successful_post_opens', 'article_details',
                        'article_texts_extracted', 'article_details_completed',
                        'last_verified_post', 'last_attempted_post', 'priority_audit'):
                if data.get(key) is not None:
                    value[key] = data[key]
            value['progress_updated_at'] = data.get('fetched_at')
            value['collection_status'] = data.get('status')
        # Preserve every invocation even when its latest pointer is replaced.
        line = json.dumps(value, ensure_ascii=False) + '\n'
        with (folder / 'events.jsonl').open('a', encoding='utf-8') as stream:
            stream.write(line)
            stream.flush()
        temp = folder / 'latest.tmp'
        temp.write_text(line, encoding='utf-8')
        temp.replace(latest)
        return value


def heartbeat(out, stop, interval=15):
    while not stop.wait(interval):
        try:
            with _lock:
                latest = json.loads((Path(out) / 'monitor/latest.json').read_text())
                if latest.get('state') in ('starting', 'running', 'heartbeat'):
                    record(out, 'heartbeat')
        except Exception as exc:
            print('Monitor heartbeat failed: ' + str(exc), flush=True)
