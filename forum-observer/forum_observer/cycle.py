"""Independent append-only forum observation cycle. Never writes V7 files."""
import argparse,hashlib,json,re
from datetime import datetime,timezone
from pathlib import Path
from zoneinfo import ZoneInfo
from .core import ZODIACS

TZ=ZoneInfo('Asia/Shanghai')

def atomic(path,obj):
    path.parent.mkdir(parents=True,exist_ok=True)
    temp=path.with_suffix(path.suffix+'.tmp')
    temp.write_text(json.dumps(obj,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    temp.replace(path)

def write_once(path,obj):
    if path.exists():return False
    path.parent.mkdir(parents=True,exist_ok=True)
    payload=(json.dumps(obj,ensure_ascii=False,indent=2)+'\n').encode('utf-8')
    with path.open('xb') as f:f.write(payload)
    return True

def draw_time(record):
    raw=str(record.get('open_time') or record.get('date') or '').strip()
    try:
        dt=datetime.fromisoformat(raw)
        return dt.replace(tzinfo=TZ) if dt.tzinfo is None else dt
    except ValueError:return None

def sync(out,archive,history,public_dir=None):
    archive=Path(archive);out=Path(out)
    status_path=out/'status.json'
    if status_path.exists():
        status=json.loads(status_path.read_text(encoding='utf-8'))
        issue=status.get('issue')
        if not isinstance(issue,str) or not re.fullmatch(r'20\d{5}',issue):
            # A failed startup must not overwrite the last valid issue or stop
            # settlement processing for existing immutable snapshots.
            atomic(archive/'collection-failure.json',status)
            status=None
    else:
        status=None
    if status is not None:
        atomic(archive/'latest.json',{
            'issue':issue,'status':status['status'],
            'raw_count':status.get('raw_count',0),
            'valid_prediction_authors':status.get('author_selection',{}).get('valid_prediction_authors',0),
            'updated_at':status['fetched_at'],'top3':status.get('top3',[]),
            'top6':status.get('top6',[]),
            'notes':status.get('note','')})
        folder=archive/'issues'/issue
        atomic(folder/'observation.json',{
            'issue':issue,'fetched_at':status['fetched_at'],'status':status['status'],
            'audit':status.get('audit',{}),'top3':status.get('top3',[]),
            'top6':status.get('top6',[]),'ranking':status.get('ranking',[]),
            'author_selection':status.get('author_selection',{}),
            'parsed_predictions':status.get('parsed_predictions',[])})
        if status['status']=='ready_observation' and len(status.get('ranking',[]))==12:
            # Immutable experimental snapshot, not a production model freeze.
            core={k:status[k] for k in ('issue','fetched_at','ranking','top1','top3','top6')}
            # Preserve the generation-time counts and rule version in the immutable snapshot.
            for key in ('valid_materials','scoring_rule_version','scoring_rules','material_counts'):
                if key in status:core[key]=status[key]
            core['classification']='experimental_unverified_leaderboard'
            core['evidence_refs']=[{'author':p['author'],'url':p['detail_url']}
                                    for p in status.get('evidence',[]) if p.get('detail_url')]
            core['digest']=hashlib.sha256(json.dumps(core,ensure_ascii=False,sort_keys=True).encode()).hexdigest()
            # Refuse to snapshot after a confirmed draw.
            drawn=set()
            if Path(history).exists():
                drawn={str(r['issue']) for r in json.loads(Path(history).read_text(encoding='utf-8')).get('records',[])}
            if issue not in drawn:write_once(folder/'snapshot.json',core)
    if not Path(history).exists():
        raise ValueError('verified draw history file absent')
    draws=json.loads(Path(history).read_text(encoding='utf-8')).get('records',[])
    by_issue={str(r['issue']):r for r in draws}
    summary=[]
    for folder in sorted((archive/'issues').glob('20?????')) if (archive/'issues').exists() else []:
        snap_path=folder/'snapshot.json'
        if not snap_path.exists():continue
        snap=json.loads(snap_path.read_text(encoding='utf-8'))
        issue=str(snap['issue'])
        check=dict(snap);digest=check.pop('digest',None)
        calculated=hashlib.sha256(json.dumps(check,ensure_ascii=False,sort_keys=True).encode()).hexdigest()
        if digest!=calculated:raise ValueError('immutable snapshot digest mismatch for '+issue)
        actual=by_issue.get(issue)
        if actual and not (folder/'settlement.json').exists():
            drawn_at=draw_time(actual)
            snap_at=datetime.fromisoformat(snap['fetched_at'])
            if drawn_at is None or snap_at>=drawn_at:
                # Never settle a prediction saved after the official draw.
                atomic(folder/'settlement-blocked.json',{'issue':issue,'reason':'snapshot_not_provably_pre_draw'})
                continue
            zodiac=actual.get('special_zodiac')
            if zodiac not in ZODIACS:
                atomic(folder/'settlement-blocked.json',{'issue':issue,'reason':'invalid_actual_zodiac'})
                continue
            rank=next((v['rank'] for v in snap['ranking'] if v['zodiac']==zodiac),None)
            if rank is None:raise ValueError('rank missing for actual zodiac')
            write_once(folder/'settlement.json',{
                'issue':issue,'actual':zodiac,'actual_source':'site/data/history.json',
                'drawn_at':drawn_at.isoformat(),'snapshot_digest':digest,
                'snapshot_at':snap['fetched_at'],'rank':rank,
                'top1_hit':zodiac in snap['top1'],
                'top3_hit':zodiac in snap['top3'],
                'top6_hit':zodiac in snap['top6'],
                'classification':'experimental_unverified_leaderboard'})
        settlement=folder/'settlement.json'
        if settlement.exists():summary.append(json.loads(settlement.read_text(encoding='utf-8')))
    atomic(archive/'summary.json',{
        'settled_count':len(summary),
        'top3_hits':sum(bool(s['top3_hit']) for s in summary),
        'top6_hits':sum(bool(s['top6_hit']) for s in summary),
        'settlements':[{'issue':s['issue'],'rank':s['rank'],
                        'actual':s['actual'],'top3_hit':s['top3_hit'],'top6_hit':s['top6_hit']} for s in summary]})
    # Read-only, separately namespaced feed. No existing V7 prediction/history
    # paths are written. The frontend must distinguish drafts from snapshots.
    if public_dir is not None:
        public_dir=Path(public_dir)
        latest_path=archive/'latest.json'
        latest=json.loads(latest_path.read_text(encoding='utf-8')) if latest_path.exists() else {}
        current_issue=str(latest.get('issue',''))
        issue_dir=archive/'issues'/current_issue
        snap_path=issue_dir/'snapshot.json'
        settlement_path=issue_dir/'settlement.json'
        snap=json.loads(snap_path.read_text(encoding='utf-8')) if snap_path.exists() else None
        settled=json.loads(settlement_path.read_text(encoding='utf-8')) if settlement_path.exists() else None
        result={
            'schema_version':1,'model':'forum-observer',
            'classification':'experimental_unverified_leaderboard',
            'issue':current_issue or None,
            'collection_status':latest.get('status','not_collected'),
            'updated_at':latest.get('updated_at'),
            'distinct_prediction_authors':latest.get('valid_prediction_authors',0),
            'preview_top3':latest.get('top3',[]),
            'preview_top6':latest.get('top6',[]),
            'frozen':bool(snap),
            'prediction_top3':snap.get('top3',[]) if snap else [],
            'prediction_top6':snap.get('top6',[]) if snap else [],
            'snapshot_digest':snap.get('digest') if snap else None,
            'settlement':settled,
            'settled_count':len(summary),
            'note':'preview fields are unverified observations; only prediction fields are immutable experimental snapshots'
        }
        atomic(public_dir/'latest.json',result)
        atomic(public_dir/'history.json',{
            'schema_version':1,'model':'forum-observer',
            'classification':'experimental_unverified_leaderboard',
            'settled_count':len(summary),
            'top3_hits':sum(bool(s['top3_hit']) for s in summary),
            'top6_hits':sum(bool(s['top6_hit']) for s in summary),
            'settlements':sorted(summary,key=lambda s:str(s['issue']))
        })
        for folder in sorted((archive/'issues').glob('20?????')) if (archive/'issues').exists() else []:
            sp=folder/'snapshot.json'
            if not sp.exists():
                continue
            saved=json.loads(sp.read_text(encoding='utf-8'))
            st=folder/'settlement.json'
            atomic(public_dir/'issues'/(folder.name+'.json'),{
                'issue':folder.name,'classification':saved.get('classification'),
                'frozen_at':saved.get('fetched_at'),
                'ranking':saved.get('ranking',[]),'top3':saved.get('top3',[]),
                'top6':saved.get('top6',[]),'digest':saved.get('digest'),
                'settlement':json.loads(st.read_text(encoding='utf-8')) if st.exists() else None
            })
    return len(summary)

if __name__=='__main__':
    p=argparse.ArgumentParser()
    p.add_argument('--out',default='out')
    p.add_argument('--archive',default='archive')
    p.add_argument('--history',default='../site/data/history.json')
    p.add_argument('--public',default=None)
    a=p.parse_args()
    print('settled records:',sync(a.out,a.archive,a.history,a.public))

