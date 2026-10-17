"""Verify actual Actions steps; retry infrastructure errors, never bypass tests."""
import argparse
import json
import os
import sys
from datetime import datetime, timezone

from .runtime import GitHub
from .schedule import TZ, decide

WORKFLOW = 'forum-observer.yml'
MAX_RECOVERIES = 3
CODE_STEPS = ('Unit tests', 'Decide current-issue collection',
              'Cycle snapshots and settlements from verified V7 history',
              'Enforce collection quality')


def classify(run, jobs, record, now):
    steps = [s for job in jobs for s in job.get('steps', [])]
    failed = [s['name'] for s in steps if s.get('conclusion') == 'failure']
    if any(name in CODE_STEPS for name in failed):
        return 'code_or_validation_failure', False
    observe = next((s for s in steps if s['name'] == 'Observe current issue'), {})
    if run.get('status') != 'completed':
        if observe.get('status') == 'in_progress':
            if record.get('progress_updated_at'):
                age = (now - datetime.fromisoformat(record['progress_updated_at'])).total_seconds()
                if age > 300:
                    return 'progress_stale', False
            post = record.get('last_verified_post') or {}
            if post.get('verified_at'):
                age = (now - datetime.fromisoformat(post['verified_at'])).total_seconds()
                if 0 <= age <= 120 and post.get('text_sha256') and post.get('text_length',0)>0:
                    return 'collecting_effectively', False
                if age > 300:
                    return 'no_recent_verified_article', False
            elif observe.get('started_at'):
                age = (now - datetime.fromisoformat(observe['started_at'].replace('Z', '+00:00'))).total_seconds()
                if age > 300:
                    return 'no_verified_article_yet', False
            return 'collection_step_running_unverified', False
        age = (now - datetime.fromisoformat(run['created_at'].replace('Z', '+00:00'))).total_seconds()
        return ('startup_delayed' if age > 300 else 'starting'), False
    if run.get('conclusion') == 'success':
        return 'completed_without_verified_snapshot', False
    # Only explicit infrastructure failures are safe for an automatic full retry.
    infrastructure = ('Set up job', 'Run actions/checkout@v4',
                      'Run actions/setup-python@v5', 'Install text collector')
    if failed and all(name in infrastructure for name in failed):
        return 'infrastructure_failure', True
    # Observe failures need the structured error; an arbitrary exception is not a network error.
    error = str(record.get('error', '')).lower()
    if observe.get('conclusion') == 'failure' and any(
            token in error for token in ('err_connection', 'err_name_not_resolved',
                                         'net::err_timed_out', 'connection reset')):
        return 'network_failure', True
    return 'unclassified_failure_requires_review', False


def check(github, now, allow_recovery=False):
    local = now.astimezone(TZ)
    issue = f'{local.year}{local.timetuple().tm_yday:03d}'
    day = local.date().isoformat()
    path = f'forum-observer/status/recovery/{day}.json'
    _, saved = github.content(path)
    events = saved.get('events', [])
    dispatched = saved.get('dispatches', 0)
    result = {'issue': issue, 'updated_at': now.isoformat(), 'dispatches': dispatched,
              'check_run_id': os.environ.get('GITHUB_RUN_ID'), 'events': events}
    # Validate local frozen digest as well as existence; the schedule gate fails closed.
    collect, _, _, gate_reason = decide(now, 'archive')
    if gate_reason == 'immutable_snapshot_exists':
        result.update(state='frozen', retryable=False)
    else:
        start = local.replace(hour=0, minute=0, second=0, microsecond=0).astimezone(timezone.utc)
        response = github.request(f'actions/workflows/{WORKFLOW}/runs?per_page=100&created=%3E%3D{start.strftime("%Y-%m-%dT%H:%M:%SZ")}')
        runs = [r for r in response.get('workflow_runs', []) if r.get('event') in ('schedule', 'workflow_dispatch', 'push')
                and datetime.fromisoformat(r['created_at'].replace('Z', '+00:00')) >= start
                and (r.get('event') != 'push' or str(r.get('display_title', '')).startswith(('trigger(forum-collect):', 'trigger(forum):')))]
        runs.sort(key=lambda r: r.get('updated_at', r['created_at']), reverse=True)
        # Do not dispatch alongside any queued/running collector.
        active = next((r for r in runs if r.get('status') != 'completed'), None)
        run = active or (runs[0] if runs else None)
        if run:
            jobs = github.request(f"actions/runs/{run['id']}/jobs?filter=latest&per_page=100").get('jobs', [])
            _, record = github.content(f"forum-observer/status/runs/{run['id']}-{run.get('run_attempt',1)}.json")
            state, retryable = classify(run, jobs, record, now)
            result.update(state=state, retryable=retryable, run_id=run['id'],
                          run_url=run.get('html_url'), failed_steps=[s['name'] for j in jobs for s in j.get('steps', []) if s.get('conclusion') == 'failure'])
        else:
            result.update(state='missing_start', retryable=True)
        if result['retryable'] and allow_recovery and collect and (local.hour, local.minute) >= (19, 40):
            if dispatched < MAX_RECOVERIES:
                # Persist intent before dispatch. If the request times out, never blindly resend it.
                result['dispatches'] = dispatched + 1
                result['state'] = 'dispatch_requested'
                github.write(path, result)
                github.request(f'actions/workflows/{WORKFLOW}/dispatches', 'POST', {'ref': 'main'})
                result['state'] = 'dispatch_accepted_unverified'
            else:
                result['state'] = 'recovery_limit_reached'
        elif result['retryable'] and not collect:
            result['state'] = 'collection_window_closed'
    result['events'] = (events + [{k:v for k,v in result.items() if k != 'events'}])[-100:]
    github.write(path, result)
    if result['state'] == 'dispatch_accepted_unverified':
        print('::warning::Recovery dispatch accepted; actual collection is not yet verified', flush=True)
    elif result['state'] not in ('frozen', 'collecting_effectively', 'collection_step_running_unverified', 'starting'):
        print('::error::Forum watchdog: ' + result['state'], flush=True)
    summary = os.environ.get('GITHUB_STEP_SUMMARY')
    if summary:
        with open(summary, 'a') as stream:
            stream.write('### Forum startup verification\n\n```json\n'+json.dumps(result, ensure_ascii=False, indent=2)+'\n```\n')
    print(json.dumps(result, ensure_ascii=False), flush=True)
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--recover', action='store_true')
    args = parser.parse_args()
    result = check(GitHub(), datetime.now(timezone.utc), args.recover)
    return 0 if result['state'] in ('frozen', 'collecting_effectively', 'collection_step_running_unverified', 'starting', 'dispatch_accepted_unverified') else 2


if __name__ == '__main__':
    sys.exit(main())
