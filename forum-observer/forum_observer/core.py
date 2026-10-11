from __future__ import annotations
import hashlib, json, re
from collections import defaultdict
from dataclasses import dataclass, asdict
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

ZODIACS = ('鼠','牛','虎','兔','龙','蛇','马','羊','猴','鸡','狗','猪')

@dataclass
class Work:
    author: str
    source_group: str  # leaderboard or outside
    issue: str
    play: str
    source_url: str
    published_at: str
    picks: list[str]
    mode: str = 'include'  # include or exclude
    evidence: str = ''


SCORING_RULE_VERSION = '2026-10-11-six-plus-kill-complement-v1'
SCORING_RULES = {
    'minimum_recommendation_zodiacs': 6,
    'kill_zodiac_vote': 'remaining_zodiacs',
    'kill_numbers_as_zodiacs': False,
    'weight_per_material': 1,
}


def vote_zodiacs(picks, mode, play=''):
    picks = list(dict.fromkeys(picks))
    if not picks or any(x not in ZODIACS for x in picks):
        raise ValueError('invalid zodiac picks')
    if mode == 'exclude':
        if re.search(r'(?:杀|排除)[^\n:：]*?(?:码|号码)', play):
            raise ValueError('kill numbers are not kill zodiacs')
        picks = [z for z in ZODIACS if z not in picks]
        if not picks:
            raise ValueError('no remaining zodiac')
    elif mode == 'include':
        if len(picks) < 6:
            raise ValueError('recommendation requires at least 6 distinct zodiacs')
    else:
        raise ValueError('unknown or unverified mode')
    return picks


def material_counts(predictions):
    return {
        'recommend': sum(p.get('counted_for_ranking', False) and p['mode'] == 'include' for p in predictions),
        'kill_zodiac': sum(p.get('counted_for_ranking', False) and p['mode'] == 'exclude' for p in predictions),
    }


def normalize(work: Work) -> dict[str,float]:
    picks = vote_zodiacs(work.picks, work.mode, work.play)
    return {z: (1/len(picks) if z in picks else 0.0) for z in ZODIACS}


def validate_work(raw: dict[str,Any], issue: str, cutoff: str) -> Work:
    w = Work(**raw)
    if w.issue != issue or w.source_group not in ('leaderboard','outside'):
        raise ValueError('wrong issue or source group')
    if not w.author.strip() or not w.play.strip() or not w.evidence.strip():
        raise ValueError('missing author/play/evidence')
    if not w.source_url.startswith(('https://','http://')):
        raise ValueError('invalid source URL')
    posted = datetime.fromisoformat(w.published_at)
    end = datetime.fromisoformat(cutoff)
    if posted.tzinfo is None or end.tzinfo is None or posted >= end or posted > datetime.now(timezone.utc):
        raise ValueError('not verifiably pre-draw')
    normalize(w)
    return w


def calculate(issue: str, cutoff: str, candidates: list[dict[str,Any]], leaderboard_authors: set[str]) -> dict[str,Any]:
    accepted, rejected, seen = [], [], set()
    for i, raw in enumerate(candidates):
        try:
            w = validate_work(raw, issue, cutoff)
            expected_group = 'leaderboard' if w.author in leaderboard_authors else 'outside'
            if w.source_group != expected_group:
                raise ValueError('author group does not match verified leaderboard')
            key = (w.author.strip().casefold(), w.play.strip().casefold())
            if key in seen:
                raise ValueError('duplicate author/play')
            seen.add(key)
            accepted.append(w)
        except (TypeError, ValueError, KeyError) as e:
            rejected.append({'index':i,'reason':str(e),'source_url':raw.get('source_url')})
    groups = {'leaderboard':[], 'outside':[]}
    for w in accepted:
        groups[w.source_group].append(w)
    a,b = len(groups['leaderboard']),len(groups['outside'])
    eligible = a>=15 and b>=10
    # Only a strictly 3:2 subset is used; select deterministically by publication time and source.
    n = min(a//3,b//2)
    selected = []
    if eligible and n>=5:
        for g,count in [('leaderboard',3*n),('outside',2*n)]:
            selected.extend(sorted(groups[g],key=lambda w:(w.published_at,w.author,w.play,w.source_url))[:count])
    score = defaultdict(float)
    for w in selected:
        for zodiac,value in normalize(w).items():
            score[zodiac]+=value
    ranking = sorted(ZODIACS,key=lambda z:(-score[z],ZODIACS.index(z))) if selected else []
    return {
        'scoring_rule_version':SCORING_RULE_VERSION,'scoring_rules':SCORING_RULES,
        'material_counts':{'recommend':sum(w.mode=='include' for w in selected),'kill_zodiac':sum(w.mode=='exclude' for w in selected)},
        'issue':issue,'status':'ready_for_pre_draw_freeze' if selected else 'insufficient_verified_samples',
        'raw_count':len(candidates),'valid_leaderboard':a,'valid_outside':b,
        'selected_count':len(selected),'selected_leaderboard':3*n if selected else 0,
        'selected_outside':2*n if selected else 0,
        'rejected':rejected,
        'ranking':[{'rank':i+1,'zodiac':z,'score':round(score[z],8)} for i,z in enumerate(ranking)],
        'top1':ranking[:1], 'top3':ranking[:3], 'top6':ranking[:6],
        'evidence':[asdict(w) for w in selected],
        'note':'Ranking is an equal-weight tally, not a calibrated probability.'
    }


def write_once(path: Path, content: bytes) -> None:
    path.parent.mkdir(parents=True,exist_ok=True)
    with path.open('xb') as f:
        f.write(content)


def freeze(result: dict[str,Any], directory: Path, now: str, draw_at: str) -> Path:
    if result['status'] != 'ready_for_pre_draw_freeze':
        raise ValueError('insufficient valid samples')
    current,draw = datetime.fromisoformat(now),datetime.fromisoformat(draw_at)
    if current.tzinfo is None or draw.tzinfo is None or current>=draw:
        raise ValueError('cannot freeze after draw')
    output = directory/result['issue']/'freeze.json'
    payload = dict(result, frozen_at=now, draw_at=draw_at)
    content = (json.dumps(payload,ensure_ascii=False,indent=2)+'\n').encode('utf-8')
    write_once(output,content)
    write_once(output.with_name('freeze.sha256'),(hashlib.sha256(content).hexdigest()+'\n').encode())
    return output
