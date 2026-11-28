"""Independent run telemetry and bounded supervision; never modifies predictions."""
import argparse
import base64
import json
import os
import subprocess
import sys
import time
import threading
import uuid
import re
import urllib.error
import urllib.request
from datetime import datetime, timezone
from pathlib import Path

from .schedule import MAX_SECONDS, TZ, current_issue, decide, forum49_collect_gate
from .trace import record as trace_record


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
            # The Contents API returns encoding=none for files above 1 MiB.
            # Fetch the exact immutable blob rather than a moving raw URL.
            source = item if item.get('encoding') == 'base64' else self.request('git/blobs/' + item['sha'])
            if source.get('encoding') != 'base64':
                raise ValueError('unsupported GitHub blob encoding: ' + path)
            return item, json.loads(base64.b64decode(source['content']))
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
            if latest and previous:
                same_issue = previous.get('issue') == value.get('issue')
                # GitHub run ids are monotonically increasing. A delayed older
                # run must never replace a newer run's success/failure status,
                # even when its final telemetry is written later.
                try:
                    previous_order = (int(previous.get('run_id')), int(previous.get('attempt') or 1))
                    current_order = (int(value.get('run_id')), int(value.get('attempt') or 1))
                except (TypeError, ValueError):
                    previous_order = current_order = None
                prior_issue, current = previous.get('issue'), value.get('issue')
                if isinstance(prior_issue, str) and isinstance(current, str) and prior_issue > current:
                    return
                if same_issue and previous_order and current_order and previous_order > current_order:
                    return
                if same_issue and (previous_order is None or previous_order == current_order) and previous.get('updated_at', '') > value['updated_at']:
                    return
                if same_issue and previous.get('state') == 'frozen' and value.get('state') != 'frozen':
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
            'last_attempted_post': data.get('last_attempted_post'),
            'priority_audit': data.get('priority_audit'),
            'raw_count': data.get('raw_count'), 'stage': data.get('stage'),
            'progress_updated_at': data.get('fetched_at'),
            'collection_status': data.get('collection_status') or data.get('status'),
            'quality_summary': {
                'detail_errors': len(data.get('audit', {}).get('detail_errors', [])),
                'truncated': data.get('audit', {}).get('truncated'),
                'unextracted_current_posts': max(0, data['successful_post_opens'] - data['article_texts_extracted'])
                    if data.get('successful_post_opens') is not None and data.get('article_texts_extracted') is not None else None},
            'coverage_label': data.get('coverage_label')}


def quality_failure_reason(data):
    audit=data.get('audit') or {}
    reasons=[]
    if audit.get('detail_errors'):
        reasons.append('detail_errors=' + str(len(audit['detail_errors'])))
    if audit.get('truncated'):
        reasons.append('coverage_truncated=true')
    opened=data.get('successful_post_opens')
    extracted=data.get('article_texts_extracted')
    if opened is not None and extracted is not None and opened > extracted:
        reasons.append('unextracted_current_posts=' + str(opened-extracted))
    if data.get('raw_count') == 0:
        reasons.append('no_target_article_records')
    return 'collection_quality_failed: ' + '; '.join(reasons) if reasons else None


def activity(data, now):
    post = data.get('last_verified_post') or {}
    if post.get('verified_at') and post.get('text_sha256') and post.get('text_length', 0) > 0:
        age = (now - datetime.fromisoformat(post['verified_at'])).total_seconds()
        return 'collecting_effectively' if 0 <= age <= 120 else 'no_recent_verified_article'
    return 'collecting_unverified'


def publish(state, error=None):
    now = datetime.now(timezone.utc)
    issue = f'{now.astimezone(TZ).year}{now.astimezone(TZ).timetuple().tm_yday:03d}'
    test_mode = os.environ.get('FORUM_TEST_ISSUE')
    if test_mode:
        if not re.fullmatch(r'20\d{5}', test_mode):
            raise ValueError('test issue must use YYYYNNN')
        issue = test_mode
    elif os.environ.get('SCHEDULE_GATE_REASON') == 'manual_test_after_freeze':
        # Manual test collection after the freeze time: report under tests/.
        test_mode = issue
    model = os.environ.get('FORUM_MODEL', '118')
    issue = os.environ.get('FORUM_GATE_ISSUE') or issue
    out = Path('forum49-out' if model == '49' else 'out')
    data = read_json(out / 'status.json')
    if state == 'starting':
        data = {}
    gate = os.environ.get('GATE_COLLECT')
    if data.get('issue') not in (None, issue):
        data = {}
    snapshot = Path('forum49/issues' if model == '49' else 'archive/issues') / issue / 'snapshot.json'
    if state == 'finished' and not test_mode and os.environ.get('FINALIZE_CYCLE') == 'true':
        data = read_json(snapshot.parent / 'draft-status.json') or read_json(snapshot.parent / 'observation.json')
    if state == 'collecting':
        state = activity(data, now)
    local = read_json(out / 'monitor/latest.json')
    if state != 'starting' and not data.get('stage') and local.get('issue') == issue and local.get('run_id') == os.environ.get('GITHUB_RUN_ID'):
        data = dict(data, stage=local.get('stage'))
    if state == 'finished':
        receipt = snapshot.parent / 'collection-runs' / f"{os.environ.get('GITHUB_RUN_ID')}-{os.environ.get('GITHUB_RUN_ATTEMPT', '1')}.json"
        if os.environ.get('JOB_STATUS') == 'cancelled':
            state, error = 'interrupted', 'Actions cancelled or workflow timeout'
        elif os.environ.get('JOB_STATUS') == 'failure':
            state = 'failed'
            error = error or read_json(out / 'collection-error.json').get('reason') or read_json(out / 'forum49-summary.json').get('error') or quality_failure_reason(data) or 'Workflow step failed before a specific collector error was available; inspect Actions steps'
        elif test_mode:
            state = 'test_completed' if (os.environ.get('OBSERVE_OUTCOME') == 'success'
                                         and (data.get('valid_materials') or 0) > 0) else 'test_incomplete'
        elif os.environ.get('FINALIZE_CYCLE') == 'true':
            state = 'frozen' if snapshot.exists() else 'reported_no_freeze'
        elif os.environ.get('OBSERVE_OUTCOME') == 'success':
            state = 'report_published' if receipt.exists() else 'missing_current_run_receipt'
        elif gate == 'false' or os.environ.get('OBSERVE_OUTCOME') == 'skipped':
            state = 'skipped'
        else:
            state = 'incomplete'
            error = error or read_json('out/collection-error.json').get('reason') or 'No verified frozen snapshot'
    record = {'schema_version': 2, 'issue': issue, 'state': state,
              'run_id': os.environ.get('GITHUB_RUN_ID', 'local'),
              'attempt': int(os.environ.get('GITHUB_RUN_ATTEMPT', '1')),
              'event': os.environ.get('GITHUB_EVENT_NAME'), 'test_only': bool(test_mode), 'model': model,
              'run_url': f"https://github.com/{os.environ.get('GITHUB_REPOSITORY', '')}/actions/runs/{os.environ.get('GITHUB_RUN_ID', '')}" if os.environ.get('GITHUB_RUN_ID') else None,
              'updated_at': now.isoformat(), 'error': error, **metrics(data)}
    out.mkdir(exist_ok=True)
    (out / 'runtime.json').write_text(json.dumps(record, ensure_ascii=False, indent=2)+'\n')
    trace_record(out / 'runtime-monitor', state, data=data, error=error, issue=issue)
    github = GitHub()
    # Append-only events retain intermediate states instead of just overwriting
    # the final status of a run. Local journal survives remote publication errors.
    event = now.strftime('%Y%m%dT%H%M%S%f') + '-' + uuid.uuid4().hex
    namespace = ('forum49/' if model == '49' else '') + ('tests/' if test_mode else '')
    github.write(f"forum-observer/status/{namespace}events/{record['run_id']}-{record['attempt']}/{event}.json", record)
    github.write(f"forum-observer/status/{namespace}runs/{record['run_id']}-{record['attempt']}.json", record, latest=True)
    github.write(f'forum-observer/status/{namespace}latest.json', record, latest=True)
    if error:
        print('::error::' + error.replace('\n', ' '), flush=True)
    return record


def safe_publish(state, error=None):
    try:
        return publish(state, error)
    except Exception as exc:
        out = 'forum49-out' if os.environ.get('FORUM_MODEL') == '49' else 'out'
        trace_record(Path(out) / 'runtime-monitor', 'publication_failed', error=str(exc))
        print('::warning::Runtime telemetry publication failed: ' + str(exc), flush=True)


def supervise():
    # Recalculate at actual process start, after dependency installation/tests.
    model = os.environ.get('FORUM_MODEL', '118')
    out = Path('forum49-out' if model == '49' else 'out')
    safe_publish('starting')
    test_issue = os.environ.get('FORUM_TEST_ISSUE')
    if test_issue:
        if not re.fullmatch(r'20\d{5}', test_issue):
            safe_publish('failed', 'test issue must use YYYYNNN')
            return 2
        collect, issue, seconds, reason = True, test_issue, 1800, 'explicit_test_no_freeze'
    elif model == '49':
        now = datetime.now(TZ)
        issue, seconds = current_issue(now), MAX_SECONDS
        collect, reason, _ = forum49_collect_gate('forum49', now, False)
    else:
        collect, issue, seconds, reason = decide(
            datetime.now(TZ), 'archive',
            manual=os.environ.get('MANUAL_DISPATCH') == 'true',
            scheduled=os.environ.get('SCHEDULED_RUN') == 'true',
            slot=os.environ.get('FORUM_SCHEDULE_CRON'),
            requested_round=os.environ.get('FORUM_REQUESTED_ROUND'))
    pinned = os.environ.get('FORUM_GATE_ISSUE')
    if pinned and pinned != issue:
        safe_publish('failed', 'collection_issue_changed_while_queued')
        return 2
    if (pinned and reason == 'manual_test_after_freeze'
            and os.environ.get('SCHEDULE_GATE_REASON') != reason):
        safe_publish('failed', 'formal_collection_crossed_freeze_cutoff; retry in observation mode')
        return 2
    if not collect:
        print(f'Collection not started: {issue} {reason}', flush=True)
        safe_publish('skipped', reason)
        return 0
    try:
        command = [sys.executable, '-u', '-m', 'forum_observer.auto',
                                    '--config', 'collector-config.json', '--out', 'out',
                                    '--max-collection-seconds', str(max(60, seconds - 30))]
        if model == '49':
            command = [sys.executable, '-u', '-m', 'forum_observer.forum49',
                       '--url', os.environ['F49_URL'], '--issue', issue, '--category', '澳门',
                       '--out', str(out), '--archive', str(out / 'run-evidence')]
        elif pinned:
            command.extend(('--issue', issue))
        if test_issue:
            command = [sys.executable, '-u', '-m', 'forum_observer.audit', '--config',
                       'collector-config.json', '--out', 'out', '--issue', test_issue]
        process = subprocess.Popen(command)
    except Exception as exc:
        safe_publish('failed', 'collector_start_failed: ' + str(exc))
        return 2
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
                     (read_json(out / 'collection-error.json').get('reason')
                      or read_json(out / 'forum49-summary.json').get('error')
                      or quality_failure_reason(read_json(out / 'status.json'))) if process.returncode else None)
        data = read_json(out / 'status.json')
        if (model == '118' and pinned and process.returncode == 2
                and not (out / 'collection-error.json').exists()
                and data.get('issue') == issue and data.get('valid_materials', 0) > 0
                and (out / issue / 'report.md').is_file()):
            # The legacy collector returns 2 for a rendered under-25 report.
            # The publisher independently validates eligibility.
            return 0
        return process.returncode
    except BaseException as exc:
        safe_publish('interrupted', type(exc).__name__ + ': ' + str(exc))
        raise
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
