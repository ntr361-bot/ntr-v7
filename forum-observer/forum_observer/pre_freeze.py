"""Pre-freeze audit exclusions (user decision 2026-10-25).

When the nightly pre-freeze review finds a material that the parser counted
in violation of AGENTS.md rules (e.g. a 独字/合字 classification table scored
as a recommendation), the reviewer records it BEFORE 21:00 in

    <archive>/issues/<issue>/pre-freeze-exclusions.json

The 21:00 finalizer removes exactly those materials from the last pre-21:00
draft, re-applies the unchanged one-author/one-material admission (so an
author's next valid material can take the place of an excluded one), recomputes
the ranking with the unchanged equal-weight rule, and explains every exclusion
in the report and the immutable snapshot. No re-collection happens.

Outcome-blind: an entry decided at/after 21:00 (or after the draw) is never
applied. Exclusions only ever remove votes; they never add new evidence.
"""
from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone
from pathlib import Path
from zoneinfo import ZoneInfo

from .core import ZODIACS, vote_zodiacs, material_counts
from .evidence_audit import admit_one_material_per_author, audit_material

TZ = ZoneInfo('Asia/Shanghai')
FILENAME = 'pre-freeze-exclusions.json'
REASON_PREFIX = 'pre-freeze audit: '
DEDUPE_REASONS = ('duplicate author/equal priority earlier source',
                  'superseded_by_same_author_higher_priority')


def _key(row):
    return (str(row.get('evidence_id') or row.get('source_url') or ''),
            str(row.get('mode') or ''), ''.join(row.get('picks') or []))


def _cutoff(issue):
    seq = int(issue[-3:])
    day = datetime(int(issue[:4]), 1, 1, tzinfo=TZ).toordinal() + seq - 1
    d = datetime.fromordinal(day)
    return datetime(d.year, d.month, d.day, 21, 0, tzinfo=TZ)


def load(folder):
    path = Path(folder) / FILENAME
    if not path.exists():
        return None
    return json.loads(path.read_text(encoding='utf-8'))


def _entry_time_ok(entry, issue, draw_at):
    try:
        decided = datetime.fromisoformat(entry['decided_at'])
    except (KeyError, TypeError, ValueError):
        return False
    if decided.tzinfo is None:
        return False
    if decided >= _cutoff(issue):
        return False
    if draw_at:
        draw = datetime.fromisoformat(draw_at)
        if draw.tzinfo is None or decided >= draw:
            return False
    return True


def _rank(rows):
    votes = {z: 0.0 for z in ZODIACS}
    for row in rows:
        if row.get('counted_for_ranking'):
            for z in row['vote_zodiacs']:
                votes[z] += row['weight_per_zodiac']
    order = sorted(ZODIACS, key=lambda z: (-votes[z], ZODIACS.index(z)))
    return [{'rank': i + 1, 'zodiac': z, 'score': round(votes[z], 6)} for i, z in enumerate(order)]


def apply(status, spec):
    """Return (new_status, record). `status` is not modified in place."""
    issue = status.get('issue')
    if not spec:
        return status, None
    if spec.get('issue') != issue:
        raise ValueError('pre-freeze exclusions are for another issue')
    status = json.loads(json.dumps(status, ensure_ascii=False))
    rows = status.get('parsed_predictions', [])
    before = {'valid_materials': status.get('valid_materials'),
              'top6': list(status.get('top6', [])),
              'ranking': list(status.get('ranking', []))}
    by_key = {}
    for row in rows:
        by_key.setdefault(_key(row), []).append(row)
    applied, not_found, rejected_late = [], [], []
    excluded_keys = set()
    for entry in spec.get('exclusions', []):
        key = (str(entry.get('evidence_id', '')), str(entry.get('mode', '')),
               ''.join(entry.get('picks') or []))
        if not _entry_time_ok(entry, issue, status.get('draw_at')):
            rejected_late.append(entry)
            continue
        matches = by_key.get(key, [])
        if not matches:
            not_found.append(entry)
            continue
        excluded_keys.add(key)
        was_counted = any(r.get('counted_for_ranking') for r in matches)
        for row in matches:
            row.update(counted_for_ranking=False,
                       exclusion_reason=REASON_PREFIX + str(entry.get('reason', '')),
                       vote_zodiacs=[], weight_per_zodiac=0.0)
        applied.append(dict(entry, was_counted=was_counted,
                            author=matches[0].get('author'), excerpt=matches[0].get('excerpt', '')[:200]))
    # Rule 2: an author whose adopted material was excluded may fall back to
    # their next verified material. Only rows dropped by the dedupe itself are
    # reconsidered, each re-checked by the same vote rule and text audit.
    affected = {(str(e.get('author', '')).strip().casefold()) for e in applied if e['was_counted']}
    for row in rows:
        if (row.get('exclusion_reason') in DEDUPE_REASONS and _key(row) not in excluded_keys
                and str(row.get('author', '')).strip().casefold() in affected):
            try:
                if audit_material(row, row.get('excerpt', '')):
                    continue
                picks = vote_zodiacs(row['picks'], row['mode'], row.get('play', ''), row.get('excerpt', ''))
            except (ValueError, KeyError, TypeError):
                continue
            row.update(counted_for_ranking=True, exclusion_reason=None,
                       vote_zodiacs=picks, weight_per_zodiac=1.0 / len(picks))
    admit_one_material_per_author(rows)
    cap = status.get('material_cap')
    if cap:
        # 49论坛模型 cap (user 2026-10-29): exclusions free room, so ordinary
        # materials cut only by the cap are re-offered and the same outcome-blind
        # click-order cap is applied again.
        from .page_collector import MATERIAL_CAP_REASON, apply_material_cap
        for row in rows:
            if row.get('exclusion_reason') == MATERIAL_CAP_REASON and _key(row) not in excluded_keys:
                try:
                    picks = vote_zodiacs(row['picks'], row['mode'], row.get('play', ''), row.get('excerpt', ''))
                except (ValueError, KeyError, TypeError):
                    continue
                row.update(counted_for_ranking=True, exclusion_reason=None,
                           vote_zodiacs=picks, weight_per_zodiac=1.0 / len(picks))
        admit_one_material_per_author(rows)
        apply_material_cap(rows, status.get('evidence') or [], cap)
    counted = [r for r in rows if r.get('counted_for_ranking')]
    ranking = _rank(rows) if counted else []
    minimum = int(status.get('author_selection', {}).get('min_valid_materials', 25))
    status.update(
        ranking=ranking, top1=[r['zodiac'] for r in ranking[:1]],
        top3=[r['zodiac'] for r in ranking[:3]], top6=[r['zodiac'] for r in ranking[:6]],
        valid_materials=len(counted), material_counts=material_counts(rows),
        distinct_valid_authors=len({r.get('author') for r in counted}),
        excluded_materials=len(rows) - len(counted))
    if 'selected_count' in status:
        status['selected_count'] = len(counted) if status.get('selected_count') else 0
    selection = status.get('author_selection')
    if isinstance(selection, dict):
        selection['valid_materials'] = len(counted)
        selection['valid_prediction_authors'] = len({r.get('author') for r in counted})
    if status.get('status') == 'ready_observation' and len(counted) < minimum:
        status['status'] = 'incomplete_observation'
    record = {
        'issue': issue, 'source_file': FILENAME,
        'applied': [{k: e.get(k) for k in ('author', 'evidence_id', 'mode', 'picks', 'reason',
                                           'rule', 'decided_at', 'was_counted', 'excerpt')}
                    for e in applied],
        'not_found': not_found, 'rejected_late': rejected_late,
        'before': {'valid_materials': before['valid_materials'], 'top6': before['top6']},
        'after': {'valid_materials': len(counted), 'top6': status['top6']},
    }
    status['pre_freeze_exclusions'] = record
    return status, record


def report_section(record):
    if not record:
        return ''
    lines = ['', '## 冻结前审查排除（不重新采集）', '',
             f"排除前：有效资料{record['before']['valid_materials']}份，前六 {'、'.join(record['before']['top6'])}",
             f"排除后：有效资料{record['after']['valid_materials']}份，前六 {'、'.join(record['after']['top6'])}",
             '审查只依据资料本身与 AGENTS.md 规则，全部在 21:00 前、开奖前决定。', '']
    for e in record['applied']:
        state = '原已计分，已排除' if e.get('was_counted') else '原未计分（仅记录）'
        lines.append(f"- {e.get('author')}：{''.join(e.get('picks') or [])}（{e.get('mode')}）——{e.get('reason')}"
                     f"{'；规则' + str(e['rule']) if e.get('rule') else ''}；{state}；原文：{e.get('excerpt', '')}")
    for e in record['not_found']:
        lines.append(f"- 未找到对应资料，未生效：{e.get('evidence_id')} {''.join(e.get('picks') or [])}")
    for e in record['rejected_late']:
        lines.append(f"- 决定时间不在 21:00/开奖前，未生效：{e.get('evidence_id')} {''.join(e.get('picks') or [])}")
    return '\n'.join(lines) + '\n'


def add_exclusion(folder, issue, evidence_id, mode, picks, reason, rule=None, now=None):
    """Record one exclusion; refuses at/after 21:00 and unknown materials."""
    now = now or datetime.now(timezone.utc)
    if now.astimezone(TZ) >= _cutoff(issue):
        raise ValueError('pre-freeze exclusions must be decided before 21:00')
    folder = Path(folder)
    draft = json.loads((folder / 'draft-status.json').read_text(encoding='utf-8'))
    if draft.get('issue') != issue:
        raise ValueError('draft is for another issue')
    key = (evidence_id, mode, ''.join(picks))
    if not any(_key(r) == key for r in draft.get('parsed_predictions', [])):
        raise ValueError('no such material in the current draft')
    spec = load(folder) or {'issue': issue, 'exclusions': []}
    if any((e['evidence_id'], e['mode'], ''.join(e['picks'])) == key for e in spec['exclusions']):
        return spec
    spec['exclusions'].append({'evidence_id': evidence_id, 'mode': mode, 'picks': list(picks),
                               'reason': reason, 'rule': rule, 'decided_at': now.isoformat()})
    (folder / FILENAME).write_text(json.dumps(spec, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    return spec


def main(argv=None):
    p = argparse.ArgumentParser(description='Record or preview pre-freeze audit exclusions')
    p.add_argument('--archive', default='archive')
    p.add_argument('--issue', required=True)
    sub = p.add_subparsers(dest='cmd', required=True)
    a = sub.add_parser('add')
    a.add_argument('--evidence-id', required=True)
    a.add_argument('--mode', required=True, choices=['include', 'exclude'])
    a.add_argument('--picks', required=True, help='zodiacs exactly as stored, e.g. 鼠牛虎兔马羊')
    a.add_argument('--reason', required=True)
    a.add_argument('--rule')
    sub.add_parser('preview')
    args = p.parse_args(argv)
    folder = Path(args.archive) / 'issues' / args.issue
    if args.cmd == 'add':
        add_exclusion(folder, args.issue, args.evidence_id, args.mode, list(args.picks), args.reason, args.rule)
    draft = json.loads((folder / 'draft-status.json').read_text(encoding='utf-8'))
    _, record = apply(draft, load(folder))
    print(json.dumps(record, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
