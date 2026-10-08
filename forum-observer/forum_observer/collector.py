"""Configurable public JSON collector. No guessed forum endpoints or bypasses."""
from __future__ import annotations
import json, os, time
from datetime import datetime
from pathlib import Path
from urllib.parse import urlparse
from urllib.request import Request, build_opener, HTTPRedirectHandler
from urllib.error import HTTPError
from .core import calculate


def fetch_json(url: str, allowed_hosts: set[str]) -> object:
    p=urlparse(url)
    if p.scheme != 'https' or p.hostname not in allowed_hosts:
        raise ValueError('URL host is not explicitly permitted')
    # Never follow a redirect to an unapproved host.
    class NoRedirect(HTTPRedirectHandler):
        def redirect_request(self, req, fp, code, msg, headers, newurl):
            raise ValueError('redirect blocked: endpoint must be explicitly approved')
    req=Request(url,headers={'User-Agent':'ForumObserver/3.0 (+evidence audit)'})
    with build_opener(NoRedirect()).open(req,timeout=15) as r:
        if r.status != 200 or 'json' not in r.headers.get('Content-Type','').lower():
            raise ValueError('not a JSON response')
        return json.loads(r.read(2_000_000))


def atomic_json(path: Path, obj: object) -> None:
    path.parent.mkdir(parents=True,exist_ok=True)
    temp=path.with_suffix(path.suffix+'.tmp')
    temp.write_text(json.dumps(obj,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    os.replace(temp,path)


def collect(config: dict, state_dir: Path, fetcher=fetch_json) -> dict:
    """Requires verified endpoint templates and a published issue/draw metadata feed."""
    hosts=set(config['allowed_hosts'])
    meta=fetcher(config['issue_url'],hosts)
    issue=str(meta['issue'])
    if int(issue)<2026281 or meta.get('drawn') is not False:
        raise ValueError('issue is prior to 281 or not verifiably undrawn')
    draw_at=meta['draw_at']
    cutoff=datetime.fromisoformat(draw_at)
    if cutoff.tzinfo is None or datetime.now(cutoff.tzinfo)>=cutoff:
        raise ValueError('draw cutoff passed or lacks timezone')
    folder=state_dir/issue
    checkpoint=folder/'checkpoint.json'
    prior=json.loads(checkpoint.read_text(encoding='utf-8')) if checkpoint.exists() else {}
    if prior and prior.get('issue') != issue:
        raise ValueError('checkpoint issue mismatch')
    state=prior or {'issue':issue,'authors':{},'works':[],'errors':[]}
    leaderboard_source='live'
    try:
        leaderboard=fetcher(config['leaderboard_url'].format(issue=issue),hosts)
        if not isinstance(leaderboard,list):
            raise ValueError('leaderboard must be a list')
        top=[str(a['name']).strip() for a in leaderboard[:20]]
        if len(top)!=20 or len(set(top))!=20:
            raise ValueError('cannot verify 20 distinct leaderboard authors')
    except Exception as exc:
        # A historic screenshot is a discovery lead, NOT proof of live rank.
        top=[str(a).strip() for a in config.get('candidate_authors',[])]
        if len(top)!=20 or len(set(top))!=20:
            raise ValueError('live leaderboard failed and no valid 20-author candidate list') from exc
        leaderboard_source='historic_candidate_unverified'
        state['errors'].append({'stage':'leaderboard','reason':str(exc)})
    state['leaderboard_authors']=top
    state['leaderboard_source']=leaderboard_source
    outside=config.get('outside_authors',[])
    for group,authors in [('leaderboard',top),('outside',outside)]:
        for author in authors:
            key=group+':'+author
            if state['authors'].get(key,{}).get('status')=='checked':
                continue
            try:
                from urllib.parse import quote
                url=config['author_url'].format(issue=quote(issue,safe=''),author=quote(author,safe=''))
                works=fetcher(url,hosts)
                if not isinstance(works,list):
                    raise ValueError('author endpoint did not return list')
                # All records must include original verifiable evidence; never synthesize it.
                state['works'].extend(dict(w,source_group=group) for w in works)
                state['authors'][key]={'status':'checked','candidate_count':len(works),'checked_at':datetime.now().astimezone().isoformat()}
            except Exception as exc:
                state['authors'][key]={'status':'blocked','reason':str(exc)}
                state['errors'].append({'author':author,'group':group,'error':str(exc)})
            atomic_json(checkpoint,state)
            time.sleep(max(0,float(config.get('request_interval_seconds',1))))
    result=calculate(issue,draw_at,state['works'],set(top))
    result['draw_at']=draw_at
    # Historical candidate membership cannot establish today's 3:2 leaderboard pool.
    if leaderboard_source != 'live':
        result['status']='unverified_leaderboard_observation'
        result['ranking']=[]
        result['top1']=[]; result['top3']=[]; result['top6']=[]
        result['selected_count']=0
        result['selected_leaderboard']=0; result['selected_outside']=0
        result['evidence']=[]
    result['progress']={'leaderboard_source':leaderboard_source,'leaderboard_checked':sum(state['authors'].get('leaderboard:'+a,{}).get('status')=='checked' for a in top),'leaderboard_total':20,'outside_checked':sum(state['authors'].get('outside:'+a,{}).get('status')=='checked' for a in outside),'blocked':state['errors']}
    atomic_json(folder/'status.json',result)
    return result
