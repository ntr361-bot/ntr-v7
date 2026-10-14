from __future__ import annotations
import hashlib, json, re
from collections import defaultdict
from dataclasses import dataclass, asdict
from datetime import datetime, timezone
from pathlib import Path
from typing import Any
from .evidence_audit import admit_one_material_per_author, audit_material

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


SCORING_RULE_VERSION = '2026-10-14-double-audit-named-group-v6'
SCORING_RULES = {
    'minimum_recommendation_zodiacs': 6,
    'kill_zodiac_vote': 'remaining_zodiacs',
    'kill_numbers_as_zodiacs': False,
    'numbers_as_whole_zodiacs': False,
    'explicit_zodiac_evidence': True,
    'weight_per_material': 1,
    'max_materials_per_author_per_issue': 1,
    'independent_text_audit': True,
    'named_group_plus_two': 'same_article_verified_six_plus_two',
    'uncertain_materials': 'excluded',
    'author_ratio': None,
}


def vote_zodiacs(picks, mode, play='', evidence=''):
    picks = list(dict.fromkeys(picks))
    if not picks or any(x not in ZODIACS for x in picks):
        raise ValueError('invalid zodiac picks')
    if re.search(r'映射|换算|号码表', evidence):
        raise ValueError('number inference is not explicit zodiac evidence')
    if mode == 'exclude':
        if re.search(r'(?:杀|排除)[^\n:：]*?(?:码|号码|尾|头|波)', play):
            raise ValueError('kill numbers are not kill zodiacs')
        picks = [z for z in ZODIACS if z not in picks]
        if not picks:
            raise ValueError('no remaining zodiac')
    elif mode == 'include':
        if len(picks) < 6:
            raise ValueError('recommendation requires at least 6 distinct zodiacs')
        count=re.search(r'([一二三四五六七八九十]|\d{1,2})肖',play)
        if count:
            label=count.group(1)
            expected=int(label) if label.isdigit() else '一二三四五六七八九十'.index(label)+1
            if expected!=len(picks):
                raise ValueError('declared zodiac count does not match picks')
    else:
        raise ValueError('unknown or unverified mode')
    return picks


def material_counts(predictions):
    return {
        'recommend': sum(p.get('counted_for_ranking', False) and p['mode'] == 'include' for p in predictions),
        'kill_zodiac': sum(p.get('counted_for_ranking', False) and p['mode'] == 'exclude' for p in predictions),
    }


def normalize(work: Work) -> dict[str,float]:
    picks = vote_zodiacs(work.picks, work.mode, work.play, work.evidence)
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
    from .page_collector import UNCERTAIN_PATTERN
    if UNCERTAIN_PATTERN.search(w.play+'\n'+w.evidence):
        raise ValueError('uncertain or unsupported evidence')
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
    # Both structured input and scraped text use the same author admission gate.
    candidate_rows=[dict(author=w.author, issue=w.issue, play=w.play,
                         mode=w.mode, vote_zodiacs=vote_zodiacs(w.picks,w.mode,w.play,w.evidence),
                         source_url=w.source_url, counted_for_ranking=True) for w in accepted]
    admit_one_material_per_author(candidate_rows)
    for work,row in zip(accepted,candidate_rows):
        if not row['counted_for_ranking']:
            rejected.append({'reason':row['exclusion_reason'], 'source_url':work.source_url,
                             'author':work.author})
    accepted=[work for work,row in zip(accepted,candidate_rows) if row['counted_for_ranking']]
    groups = {'leaderboard':[], 'outside':[]}
    for w in accepted:
        groups[w.source_group].append(w)
    a,b = len(groups['leaderboard']),len(groups['outside'])
    # No leaderboard/outside quota. Every verified material contributes one unit.
    selected = sorted(accepted,key=lambda w:(0 if w.source_group=='leaderboard' else 1,
                                           w.published_at,w.author,w.play,w.source_url)) if a+b>=25 else []
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
        'selected_count':len(selected),'selected_leaderboard':sum(w.source_group=='leaderboard' for w in selected),
        'selected_outside':sum(w.source_group=='outside' for w in selected),
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


def verify_scored_result(result, min_materials=25):
    """Verify the unchanged scoring rules; sample count is an audit policy, not a vote rule."""
    rows=[p for p in result.get('parsed_predictions',[]) if p.get('counted_for_ranking')]
    if len(rows)<min_materials or result.get('valid_materials')!=len(rows):
        raise ValueError('unverified material count at freeze gate')
    nine_authors = {(row['author'].strip().casefold(), row.get('issue', result.get('issue')))
                    for row in rows if row.get('mode') == 'include'
                    and re.search(r'(?:九|9)肖', row.get('play',''))}
    for row in rows:
        if (row.get('mode') == 'include'
                and re.search(r'(?:六|6)肖', row.get('play',''))
                and (row['author'].strip().casefold(), row.get('issue', result.get('issue'))) in nine_authors):
            raise ValueError('same author six-zodiac vote duplicated with nine-zodiac vote')
    votes={z:0.0 for z in ZODIACS};seen=set()
    for row in rows:
        audit_error=audit_material(row,row.get('excerpt',''))
        if audit_error:raise ValueError('independent audit failed at freeze gate: '+audit_error)
        picks=vote_zodiacs(row['picks'],row['mode'],row['play'],row.get('excerpt',''))
        if row.get('vote_zodiacs')!=picks:raise ValueError('invalid normalized vote at freeze gate')
        key=(str(row['author']).strip().casefold(), row.get('issue', result.get('issue')))
        if key in seen:raise ValueError('duplicate author at freeze gate')
        seen.add(key)
        for z in picks:votes[z]+=1/len(picks)
    order=sorted(ZODIACS,key=lambda z:(-votes[z],ZODIACS.index(z)))
    ranking=result.get('ranking',[])
    if [r['zodiac'] for r in ranking]!=order or any(abs(r['score']-votes[r['zodiac']])>0.00001 for r in ranking):
        raise ValueError('ranking does not match eligible evidence')
    if any(result.get(key)!=order[:size] for key,size in [('top1',1),('top3',3),('top6',6)]):
        raise ValueError('prediction prefix does not match ranking')
    deadline=datetime.fromisoformat(result['draw_at'])
    fetched=datetime.fromisoformat(result['fetched_at'])
    if deadline.tzinfo is None or fetched.tzinfo is None or fetched>=deadline or datetime.now(timezone.utc)>=deadline:
        raise ValueError('freeze gate is past the verified draw cutoff')
    return True
