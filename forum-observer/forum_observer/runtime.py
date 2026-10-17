"""Independent run telemetry and bounded supervision; never modifies predictions."""
import argparse
import base64
import json
import os
import subprocess
import sys
import time
import threading
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

from .schedule import TZ, decide


def read_json(path):
    try:
        return json.loads(Path(path).read_text(encoding='utf-8'))
    except (OSError, ValueError):
        return {}


class GitHub:
    def __init__(self):
        self.repo = os.environ['GITHUB_REPOSITORY']
        self.token = os.environ['GH_TOKEN']

    def request(self, path, method='GET', data=None):
        req = urllib.request.Request(
            'https://api.github.com/repos/' + self.repo + '/' + path,
            data=None if data is None else json.dumps(data).encode(), method=method,
            headers={'Authorization': 'Bearer ' + self.token,
                     'Accept': 'application/vnd.github+json',
                     'X-GitHub-Api-Version': '2022-11-28',
                     'Content-Type': 'application/json'})
        with urllib.request.urlopen(req, timeout=15) as response:
            raw = response.read()
            return json.loads(raw) if raw else {}

    def content(self, path):
        try:
            item = self.request('contents/' + path + '?ref=main')
            return item, json.loads(base64.b64decode(item['content']))
        except urllib.error.HTTPError as exc:
            if exc.code == 404:
                return {}, {}
            raise

    def write(self, path, value, latest=False):
        # Only independent status files are writable through this helper.
        if not path.startswith('forum-observer/status/') or '..' in path:
            raise ValueError('runtime writes are restricted to independent status')
        for attempt in range(3):
            old, previous = self.content(path)
            if latest and previous.get('updated_at', '') > value['updated_at']:
                return
            if latest and previous.get('issue') == value.get('issue'):
                if previous.get('state') == 'frozen' and value.get('state') in ('starting', 'skipped'):
                    return
            payload = {'branch': 'main', 'message': 'chore(forum): record runtime status',
                       'content': base64.b64encode((json.dumps(value, ensure_ascii=False, indent=2)+'\n').encode()).decode()}
            if old:
                payload['sha'] = old['sha']
            try:
                self.request('contents/' + path, 'PUT', payload)
                return
            except urllib.error.HTTPError as exc:
                if exc.code not in (409, 422) or attempt == 2:
                    raise


def metrics(data):
    adopted = [p for p in data.get('parsed_predictions', []) if p.get('counted_for_ranking')]
    return {'valid_materials': data.get('valid_materials'),
            'valid_authors': len({p['author'] for p in adopted}) if 'parsed_predictions' in data else None,
            # Do not invent an open count or reuse comment counters.
            'successful_post_opens': data.get('successful_post_opens'),
            'article_details': data.get('article_details'),
            'article_texts_extracted': data.get('article_texts_extracted'),
            'article_details_completed': data.get('article_details_completed'),
            'last_verified_post': data.get('last_verified_post'),
            'raw_count': data.get('raw_count'), 'stage': data.get('stage'),
            'progress_updated_at': data.get('fetched_at'),
            'coverage_label': data.get('coverage_label')}


def activity(data, now):
    post = data.get('last_verified_post') or {}
    if post.get('verified_at') and post.get('text_sha256') and post.get('text_length', 0) > 0:
        age = (now - datetime.fromisoformat(post['verified_at'])).total_seconds()
        return 'collecting_effectively' if 0 <= age <= 120 else 'no_recent_verified_article'
    return 'collecting_unverified'


def publish(state, error=None):
    now = datetime.now(timezone.utc)
    issue = f'{now.astimezone(TZ).year}{now.astimezone(TZ).timetuple().tm_yday:03d}'
    data = read_json('out/status.json')
    gate = os.environ.get('GATE_COLLECT')
    if data.get('issue') not in (None, issue):
        data = {}
    snapshot = Path('archive/issues') / issue / 'snapshot.json'
    if snapshot.exists() and not data:
        data = read_json(snapshot.parent / 'observation.json')
    if state == 'collecting':
        state = activity(data, now)
    if state == 'finished':
        if os.environ.get('JOB_STATUS') == 'failure':
            state = 'failed'
            error = error or read_json('out/collection-error.json').get('reason') or 'Workflow step failed; inspect steps and watchdog classification'
        elif snapshot.exists():
            state = 'frozen'
        elif gate == 'false' or os.environ.get('OBSERVE_OUTCOME') == 'skipped':
            state = 'skipped'
        else:
            state = 'incomplete'
            error = error or read_json('out/collection-error.json').get('reason') or 'No verified frozen snapshot'
    record = {'schema_version': 2, 'issue': issue, 'state': state,
              'run_id': os.environ['GITHUB_RUN_ID'],
              'attempt': int(os.environ.get('GITHUB_RUN_ATTEMPT', '1')),
              'event': os.environ.get('GITHUB_EVENT_NAME'),
              'run_url': f"https://github.com/{os.environ['GITHUB_REPOSITORY']}/actions/runs/{os.environ['GITHUB_RUN_ID']}",
              'updated_at': now.isoformat(), 'error': error, **metrics(data)}
    Path('out').mkdir(exist_ok=True)
    Path('out/runtime.json').write_text(json.dumps(record, ensure_ascii=False, indent=2)+'\n')
    github = GitHub()
    github.write(f"forum-observer/status/runs/{record['run_id']}-{record['attempt']}.json", record, latest=True)
    github.write('forum-observer/status/latest.json', record, latest=True)
    if error:
        print('::error::' + error.replace('\n', ' '), flush=True)
    return record


def safe_publish(state, error=None):
    try:
        return publish(state, error)
    except Exception as exc:
        print('::warning::Runtime telemetry publication failed: ' + str(exc), flush=True)


def supervise():
    # Recalculate at actual process start, after dependency installation/tests.
    collect, issue, seconds, reason = decide(datetime.now(TZ), 'archive')
    if not collect:
        print(f'Collection not started: {issue} {reason}', flush=True)
        safe_publish('skipped', reason)
        return 0
    process = subprocess.Popen([sys.executable, '-u', '-m', 'forum_observer.auto',
                                '--config', 'collector-config.json', '--out', 'out',
                                '--max-collection-seconds', str(max(60, seconds - 30))])
    started = time.monotonic()
    stop = threading.Event()
    def telemetry():
        while not stop.is_set():
            safe_publish('collecting')
            stop.wait(60)
    threading.Thread(target=telemetry, daemon=True).start()
    try:
        while process.poll() is None:
            now = time.monotonic()
            # The collector has its own deadline; this is a final process bound.
            if now - started >= seconds:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
                safe_publish('failed', 'collector_process_timeout; partial coverage only')
                return 124
            time.sleep(2)
        safe_publish('collected' if process.returncode == 0 else 'incomplete',
                     read_json('out/collection-error.json').get('reason') if process.returncode else None)
        return process.returncode
    finally:
        stop.set()
        if process.poll() is None:
            process.terminate()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('mode', choices=['starting', 'finished', 'supervise'])
    args = parser.parse_args()
    if args.mode == 'supervise':
        return supervise()
    publish(args.mode)
    return 0


if __name__ == '__main__':
    sys.exit(main())
