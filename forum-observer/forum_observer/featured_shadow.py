"""118论坛模型「只用精华帖」影子榜（用户 2026-10-28 授权，只记录观察）。

在 21:00 冻结时，用与正式快照同一份草稿里的加精帖（is_featured 为 True），
按完全相同的计分规则（score_posts，同一作者一期一份）重算排名，写入
issues/<期号>/featured-shadow.json（只写一次，used_for_freeze=false）。
开奖后由 cycle.sync 结算到 featured-shadow-settlement.json，并汇总到
archive/featured-shadow-summary.json。永不影响正式快照、结算或账本。
"""
import hashlib
import json
from pathlib import Path

SHADOW_FILE = 'featured-shadow.json'
SETTLEMENT_FILE = 'featured-shadow-settlement.json'
SUMMARY_FILE = 'featured-shadow-summary.json'
RULE = 'featured_posts_only_same_scoring'


def _digest(obj):
    return hashlib.sha256(json.dumps(obj, ensure_ascii=False, sort_keys=True).encode()).hexdigest()


def build(status, frozen_at):
    """Score only featured posts of the frozen draft. Pure; never raises on empty input."""
    from .page_collector import score_posts
    excluded = {str(e.get('evidence_id')) for e in
                ((status.get('pre_freeze_exclusions') or {}).get('applied') or [])}
    posts = [dict(p) for p in status.get('evidence') or []
             if p.get('is_featured') is True and str(p.get('detail_url')) not in excluded]
    ranking, mats = score_posts([dict(p) for p in posts], False)
    counted = [m for m in mats if m.get('counted_for_ranking')]
    zodiacs = [r['zodiac'] for r in ranking]
    shadow = {
        'issue': status.get('issue'), 'model_id': 'forum-observer', 'rule': RULE,
        'used_for_freeze': False, 'classification': 'shadow_observation_only',
        'fetched_at': status.get('fetched_at'), 'draw_at': status.get('draw_at'),
        'scoring_rule_version': status.get('scoring_rule_version'),
        'featured_posts': len(posts), 'valid_materials': len(counted),
        'ranking': ranking, 'top1': zodiacs[:1], 'top3': zodiacs[:3], 'top6': zodiacs[:6],
        'materials': [{'author': m.get('author'), 'play': m.get('play'), 'mode': m.get('mode'),
                       'picks': m.get('picks'), 'source_url': m.get('source_url')} for m in counted],
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
    except Exception as exc:  # the shadow must never break the formal freeze
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
        return None  # never settle evidence captured after the draw
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
