"""Read-only planning and artifact consistency checks. Never invoke a model."""
import datetime as dt
import json
import os
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1] / 'site' / 'data'


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def plan(root=ROOT, requested='', scheduled=False, now=None):
    history = read(root / 'history.json')
    draw = int(history['latest_issue'])
    target = draw + 1
    if requested and int(requested) != target:
        raise ValueError('Only the next issue may be requested; frozen history is read-only')
    if scheduled:
        now = now or dt.datetime.now(dt.timezone(dt.timedelta(hours=8)))
        expected = now.date() if now.hour >= 22 else now.date() - dt.timedelta(days=1)
        latest = next(x for x in history['records'] if int(x['issue']) == draw)
        actual_date = dt.datetime.fromisoformat(latest['open_time']).date()
        if actual_date != expected:
            raise ValueError(f'Draw source is not current: {actual_date}, expected {expected}')
    paths = [root / folder / f'{target}.json' for folder in ('predictions', 'daily-records')]
    present = [p.exists() for p in paths]
    if any(present) and not all(present):
        raise ValueError('Partial frozen forecast: refusing to rerun a formal model; recover original artifact')
    if all(present):
        for p in paths:
            item = read(p)
            if int(item['issue']) != target or item['status'] != 'success':
                raise ValueError('Existing frozen forecast failed validation')
    return {'target': str(target), 'generate': str(not all(present)).lower()}


def validate(root=ROOT):
    manifest = read(root / 'daily-records' / 'manifest.json')
    names = manifest['records']
    if not names or len(names) != len(set(names)):
        raise ValueError('Empty or duplicated manifest')
    issues = [int(Path(name).stem) for name in names]
    latest = max(issues)
    if any(name != f'{issue}.json' for name, issue in zip(names, issues)):
        raise ValueError('Invalid manifest filename')
    history = read(root / 'history.json')
    if latest != int(history['latest_issue']) + 1 or int(manifest['latest_issue']) != latest:
        raise ValueError('Draw/manifest next-issue mismatch')
    if int(read(root / 'predictions' / 'latest.json')['latest_issue']) != latest:
        raise ValueError('Prediction latest pointer mismatch')
    aggregate = read(root / 'daily-records' / 'history.json')
    rows = aggregate['predictions']
    if int(aggregate['latest_issue']) != latest or len(rows) != len(names):
        raise ValueError('Daily history/manifest mismatch')
    by_issue = {int(row['issue']): row for row in rows}
    if len(by_issue) != len(rows) or set(by_issue) != set(issues):
        raise ValueError('Duplicate or missing daily-history issue')
    draws = {int(row['issue']): row for row in history['records']}
    for name, issue in zip(names, issues):
        row = read(root / 'daily-records' / name)
        if row != by_issue[issue] or int(row['issue']) != issue:
            raise ValueError(f'Published daily history differs from frozen file: {issue}')
        if issue in draws:
            verification = row['verification']
            if verification['status'] != 'verified' or verification['actual_zodiac'] != draws[issue]['special_zodiac'] or verification['actual_number'] != draws[issue]['special_number']:
                raise ValueError(f'Unsettled/conflicting daily forecast: {issue}')
    runtime = read(root / 'runtime-state.json')['predictions']
    keys = [(r['issue'], r['modelVersion'], r.get('analysisPeriods')) for r in runtime]
    if len(keys) != len(set(keys)):
        raise ValueError('Duplicate runtime model/issue')
    if not any(int(r['issue']) == latest and r['modelVersion'].lower() == 'v7' for r in runtime):
        raise ValueError('Latest formal V7 runtime row missing')
    for row in runtime:
        draw = draws.get(int(row['issue']))
        if draw and (row.get('actualZodiac') != draw['special_zodiac'] or row.get('actualNumber') != draw['special_number'] or row.get('hitResult') in ('', '未开奖', None) or row.get('top6HitResult') in ('', '未开奖', None)):
            raise ValueError(f'Formal runtime model remains unsettled: {row["issue"]}/{row["modelVersion"]}')
    return latest


if __name__ == '__main__':
    import sys
    try:
        if sys.argv[1] == 'plan':
            result = plan(requested=os.getenv('REQUESTED_ISSUE', ''), scheduled=os.getenv('SCHEDULED_RUN', '').lower() == 'true')
            print(json.dumps(result))
            if os.getenv('GITHUB_OUTPUT'):
                with open(os.environ['GITHUB_OUTPUT'], 'a', encoding='utf-8') as out:
                    out.writelines(f'{k}={v}\n' for k, v in result.items())
        else:
            print(f'Committed formal snapshot validated: {validate()}')
    except Exception as error:
        print(f'::error::{error}', file=sys.stderr)
        sys.exit(1)
