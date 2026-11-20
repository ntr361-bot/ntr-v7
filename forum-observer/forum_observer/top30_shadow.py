"""118论坛模型「只用前30份资料」影子榜（只记录观察）。

在 21:00 冻结时，用与正式快照同一份草稿里已计分的资料，
按优先级（9肖 > 8肖 > 7肖 > 6肖 > 其他推荐 > 有效杀肖）排序后取前 30 份，
按完全相同的等权计分规则重算排名，写入 issues/<期号>/top30-shadow.json
（只写一次，used_for_freeze=false）。开奖后结算到 top30-shadow-settlement.json。
永不影响正式快照、结算或账本。
"""
import hashlib
import json
import re
from pathlib import Path

SHADOW_FILE = 'top30-shadow.json'
SETTLEMENT_FILE = 'top30-shadow-settlement.json'
SUMMARY_FILE = 'top30-shadow-summary.json'
RULE = 'top30_priority_materials_same_scoring'
TOP_N = 30

_PRIORITY = {
    'nine': 1, 'eight': 2, 'seven': 3, 'six': 4,
    'other_recommend': 5, 'kill': 6,
}


def _play_priority(row):
    play = str(row.get('play') or '')
    mode = row.get('mode')
    if mode == 'exclude':
        return _PRIORITY['kill']
    if re.search(r'(?:九|9)肖', play):
        return _PRIORITY['nine']
    if re.search(r'(?:八|8)肖', play):
        return _PRIORITY['eight']
    if re.search(r'(?:七|7)肖', play):
        return _PRIORITY['seven']
    if re.search(r'(?:六|6)肖', play):
        return _PRIORITY['six']
    return _PRIORITY['other_recommend']


def _digest(obj):
    return hashlib.sha256(json.dumps(obj, ensure_ascii=False, sort_keys=True).encode()).hexdigest()


def select_top30(status):
    """Return the first 30 counted materials after priority sort. Pure."""
    rows = [dict(p) for p in status.get('parsed_predictions') or []
            if p.get('counted_for_ranking')]
    rows.sort(key=lambda r: (_play_priority(r), r.get('published_at') or '', r.get('author') or ''))
    return rows[:TOP_N]


def build(status, frozen_at):
    """Re-score the top-30 subset. Pure; never raises on empty input."""
    from .page_collector import score_posts
    selected = select_top30(status)
    posts = []
    for r in selected:
        posts.append({
            'author': r.get('author'),
            'play': r.get('play'),
            'mode': r.get('mode'),
            'picks': r.get('picks') or [],
            'detail_url': r.get('source_url') or r.get('detail_url'),
            'published_at': r.get('published_at'),
            'is_featured': False,
        })
    ranking, mats = score_posts(posts, False)
    counted = [m for m in mats if m.get('counted_for_ranking')]
    zodiacs = [r['zodiac'] for r in ranking]
    shadow = {
        'issue': status.get('issue'), 'model_id': 'forum-observer', 'rule': RULE,
        'used_for_freeze': False, 'classification': 'shadow_observation_only',
        'fetched_at': status.get('fetched_at'), 'draw_at': status.get('draw_at'),
        'scoring_rule_version': status.get('scoring_rule_version'),
        'top_n': TOP_N, 'selected_materials': len(selected),
        'valid_materials': len(counted),
        'ranking': ranking, 'top1': zodiacs[:1], 'top3': zodiacs[:3], 'top6': zodiacs[:6],
        'materials': [{'author': m.get('author'), 'play': m.get('play'), 'mode': m.get('mode'),
                       'picks': m.get('picks')} for m in counted],
        'frozen_at': frozen_at,
    }
    shadow['digest'] = _digest(shadow)
    return shadow


def record_at_freeze(folder, status, frozen_at, write_once):
    """Write the shadow once, next to a freshly written snapshot. Errors are recorded, not raised."""
    folder = Path(folder)
    if (folder / SHADOW_FILE).exists():
        return False
    try:
        return write_once(folder / SHADOW_FILE, build(status, frozen_at))
    except Exception as exc:
        return write_once(folder / SHADOW_FILE, {
            'issue': status.get('issue'), 'rule': RULE, 'used_for_freeze': False,
            'error': f'{type(exc).__name__}: {exc}'[:300], 'frozen_at': frozen_at})


def settle(folder, zodiac, drawn_at, write_once):
    """Settle the shadow against the same verified draw as the formal snapshot."""
    folder = Path(folder)
    path = folder / SHADOW_FILE
    if not path.exists() or (folder / SETTLEMENT_FILE).exists():
        return None
    from datetime import datetime
    shadow = json.loads(path.read_text(encoding='utf-8'))
    check = dict(shadow)
    digest = check.pop('digest', None)
    if not shadow.get('ranking') or digest != _digest(check):
        return None
    if datetime.fromisoformat(shadow['fetched_at']) >= drawn_at:
        return None
    rank = next((r['rank'] for r in shadow['ranking'] if r['zodiac'] == zodiac), None)
    if rank is None:
        return None
    result = {'issue': shadow['issue'], 'actual': zodiac, 'drawn_at': drawn_at.isoformat(),
              'shadow_digest': digest, 'rank': rank, 'valid_materials': shadow.get('valid_materials'),
              'top1_hit': zodiac in shadow['top1'], 'top3_hit': zodiac in shadow['top3'],
              'top6_hit': zodiac in shadow['top6'], 'used_for_freeze': False}
    write_once(folder / SETTLEMENT_FILE, result)
    return result


def summarize(archive, atomic):
    """Aggregate shadow settlements next to the formal ones for comparison."""
    archive = Path(archive)
    rows = []
    for folder in sorted((archive / 'issues').glob('20?????')) if (archive / 'issues').exists() else []:
        s = folder / SETTLEMENT_FILE
        if not s.exists():
            continue
        shadow = json.loads(s.read_text(encoding='utf-8'))
        formal_path = folder / 'settlement.json'
        formal = json.loads(formal_path.read_text(encoding='utf-8')) if formal_path.exists() else {}
        rows.append({'issue': shadow['issue'], 'actual': shadow['actual'],
                     'shadow_rank': shadow['rank'], 'shadow_top3_hit': shadow['top3_hit'],
                     'shadow_top6_hit': shadow['top6_hit'],
                     'shadow_valid_materials': shadow.get('valid_materials'),
                     'formal_rank': formal.get('rank'), 'formal_top3_hit': formal.get('top3_hit'),
                     'formal_top6_hit': formal.get('top6_hit')})
    n = len(rows)
    atomic(archive / SUMMARY_FILE, {
        'rule': RULE, 'used_for_freeze': False, 'settled_count': n,
        'shadow_top3_hits': sum(bool(r['shadow_top3_hit']) for r in rows),
        'shadow_top6_hits': sum(bool(r['shadow_top6_hit']) for r in rows),
        'formal_top3_hits': sum(bool(r['formal_top3_hit']) for r in rows),
        'formal_top6_hits': sum(bool(r['formal_top6_hit']) for r in rows),
        'shadow_mean_rank': round(sum(r['shadow_rank'] for r in rows) / n, 2) if n else None,
        'formal_mean_rank': (round(sum(r['formal_rank'] for r in rows if r['formal_rank']) / n, 2)
                             if n and all(r['formal_rank'] for r in rows) else None),
        'random_baseline': {'top3': 0.25, 'top6': 0.5, 'mean_rank': 6.5},
        'settlements': rows})
    return n
