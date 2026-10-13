"""Independent append-only forum observation cycle. Never writes V7 files."""
import argparse,hashlib,json,re,shutil
from datetime import datetime,timezone
from pathlib import Path
from zoneinfo import ZoneInfo
from .core import ZODIACS, SCORING_RULE_VERSION, verify_scored_result

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

def persist_completed_threads(folder, status):
    """Append only verified same-period/same-rule completed thread checkpoints."""
    incoming=status.get('completed_thread_cache', {})
    issue=status.get('issue')
    if (not isinstance(incoming,dict) or
            status.get('scoring_rule_version')!=SCORING_RULE_VERSION or
            not isinstance(issue,str) or not re.fullmatch(r'20\d{5}',issue)):
        return 0
    destination=folder/'completed-threads.json'
    existing={}
    if destination.exists():
        prior=json.loads(destination.read_text(encoding='utf-8'))
        if prior.get('issue')==issue and prior.get('rule_version')==SCORING_RULE_VERSION:
            existing=prior.get('threads',{})
            if not isinstance(existing,dict):
                raise ValueError('invalid completed thread cache')
        else:
            # Stale rule/period caches cannot be reused. A fresh checkpoint
            # replaces them only when newly verified current evidence exists.
            existing={}
    accepted={}
    for key, entry in incoming.items():
        if (not isinstance(key,str) or not isinstance(entry,dict)
                or entry.get('state')!='checked'
                or not isinstance(entry.get('parent'),dict)
                or entry['parent'].get('issue')!=issue
                or not isinstance(entry.get('children'),list)
                or not isinstance(entry.get('audit'),dict)):
            continue
        accepted[key]=entry
    # Do not rewrite an already checked original entry in another run.
    additions={k:v for k,v in accepted.items() if k not in existing}
    if additions:
        atomic(destination, {'issue':issue,'rule_version':SCORING_RULE_VERSION,
                             'threads':{**existing,**additions}})
    return len(additions)


def sync(out,archive,history,public_dir=None,live_draws=None):
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
            'valid_materials':status.get('valid_materials',0),
            'pending_rule_question_count':status.get('pending_rule_question_count',0),
            'article_image_audit':status.get('article_image_audit',{}),
            'valid_prediction_authors':status.get('author_selection',{}).get('valid_prediction_authors',0),
            'updated_at':status['fetched_at'],'top3':status.get('top3',[]),
            'top6':status.get('top6',[]),
            'notes':status.get('note','')})
        folder=archive/'issues'/issue
        persist_completed_threads(folder, status)
        atomic(folder/'observation.json',{
            'issue':issue,'fetched_at':status['fetched_at'],'status':status['status'],
            'audit':status.get('audit',{}),'top3':status.get('top3',[]),
            'top6':status.get('top6',[]),'ranking':status.get('ranking',[]),
            'author_selection':status.get('author_selection',{}),
            'parsed_predictions':status.get('parsed_predictions',[]),
            'pending_rule_questions':status.get('pending_rule_questions',[])})
        atomic(folder/'pending-rule-questions.json',{'issue':issue,
            'fetched_at':status['fetched_at'],
            'questions':status.get('pending_rule_questions',[])})
        report=out/issue/'report.md'
        if report.exists():shutil.copyfile(report,folder/'report.md')
        valid_count=int(status.get('valid_materials') or 0)
        standard_minimum=int(status.get('author_selection',{}).get('min_valid_materials',25))
        full_ranking=(len(status.get('ranking',[]))==12
                      and len({r.get('zodiac') for r in status['ranking']})==12
                      and len(status.get('top3',[]))==3
                      and len(status.get('top6',[]))==6)
        # Partial evidence may be frozen, but it must still be real, scored,
        # from this issue, and captured before the draw. Never invent a vote.
        partial=(status.get('status')=='incomplete_observation' and
                 0<valid_count<standard_minimum and full_ranking and
                 status.get('pre_draw_verified') is True and
                 bool(status.get('draw_at')))
        eligible=(status.get('status')=='ready_observation' and full_ranking) or partial
        if eligible:
            # Immutable, explicitly quality-labelled experimental snapshot.
            core={k:status[k] for k in ('issue','fetched_at','ranking','top1','top3','top6')}
            for key in ('valid_materials','scoring_rule_version','scoring_rules','material_counts'):
                if key in status:core[key]=status[key]
            core['classification']='experimental_unverified_leaderboard'
            core['minimum_valid_materials']=standard_minimum
            core['quality_status']='below_minimum_samples' if valid_count<standard_minimum else 'meets_minimum_samples'
            core['collection_status']=status['status']
            core['evidence_refs']=[{'author':p['author'],'url':p['detail_url']}
                                    for p in status.get('evidence',[]) if p.get('detail_url')]
            # Refuse to create a new snapshot after either the announced cutoff
            # or a verified draw; existing snapshots remain untouched.
            now=datetime.now(timezone.utc)
            draw_cutoff=datetime.fromisoformat(status['draw_at']) if status.get('draw_at') else None
            captured=datetime.fromisoformat(status['fetched_at'])
            in_time=(draw_cutoff is None or
                     (draw_cutoff.tzinfo is not None and captured.tzinfo is not None
                      and captured<=now and captured<draw_cutoff and now<draw_cutoff))
            drawn=set()
            if Path(history).exists():
                drawn={str(r['issue']) for r in json.loads(Path(history).read_text(encoding='utf-8')).get('records',[])}
            if in_time and issue not in drawn and not (folder/'snapshot.json').exists():
                if status.get('scoring_rule_version')==SCORING_RULE_VERSION:
                    verify_scored_result(status,min_materials=1 if partial else 25)
                core['frozen_at']=now.isoformat()
                core['digest']=hashlib.sha256(json.dumps(core,ensure_ascii=False,sort_keys=True).encode()).hexdigest()
                write_once(folder/'snapshot.json',core)
        # The human-readable report must explicitly say if the immutable
        # snapshot was frozen with fewer than 25 valid samples.
        saved_snapshot=folder/'snapshot.json'
        frozen_report=folder/'report.md'
        if saved_snapshot.exists() and frozen_report.exists():
            saved=json.loads(saved_snapshot.read_text(encoding='utf-8'))
            source=frozen_report.read_text(encoding='utf-8')
            marker='**本期冻结状态：'
            if marker not in source:
                count=saved.get('valid_materials','未知')
                minimum=saved.get('minimum_valid_materials',25)
                quality=('未达标实验冻结' if isinstance(count,int) and count<minimum
                         else '实验冻结')
                annotation=(f"**本期冻结状态：{quality}**；有效资料{count}份，"
                            f"标准门槛{minimum}份；冻结时间"
                            f"{saved.get('frozen_at',saved.get('fetched_at','未知'))}；"
                            f"不可变摘要：{saved.get('digest','未知')}\\n")
                if '\\n\\n' in source:
                    source=source.replace('\\n\\n','\\n\\n'+annotation+'\\n',1)
                else:
                    source+=annotation
                frozen_report.write_text(source,encoding='utf-8')
    if not Path(history).exists():
        raise ValueError('verified draw history file absent')
    draws=json.loads(Path(history).read_text(encoding='utf-8')).get('records',[])
    by_issue={str(r['issue']):r for r in draws}
    for candidate in live_draws or []:
        issue=str(candidate['issue'])
        prior=by_issue.get(issue)
        if prior is not None:
            if prior.get('special_zodiac')!=candidate['special_zodiac']:
                raise ValueError('formal/live draw conflict for '+issue)
            continue  # Formal history wins when both confirm the same result.
        by_issue[issue]=candidate
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
                'issue':issue,'actual':zodiac,'actual_source':actual.get('actual_source','site/data/history.json'),
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
            'raw_count':latest.get('raw_count',0),
            'valid_materials':latest.get('valid_materials',0),
            'pending_rule_question_count':latest.get('pending_rule_question_count',0),
            'article_image_audit':latest.get('article_image_audit',{}),
            'updated_at':latest.get('updated_at'),
            'distinct_prediction_authors':latest.get('valid_prediction_authors',0),
            'preview_top3':latest.get('top3',[]),
            'preview_top6':latest.get('top6',[]),
            'frozen':bool(snap),
            'prediction_top3':snap.get('top3',[]) if snap else [],
            'prediction_top6':snap.get('top6',[]) if snap else [],
            'snapshot_digest':snap.get('digest') if snap else None,
            'freeze_quality_status':snap.get('quality_status',
                snap.get('freeze_classification','legacy_unclassified')) if snap else None,
            'freeze_minimum_valid_materials':snap.get('minimum_valid_materials') if snap else None,
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
                'quality_status':saved.get('quality_status',
                    saved.get('freeze_classification','legacy_unclassified')),
                'valid_materials':saved.get('valid_materials'),
                'minimum_valid_materials':saved.get('minimum_valid_materials'),
                'settlement':json.loads(st.read_text(encoding='utf-8')) if st.exists() else None
            })
    return len(summary)

if __name__=='__main__':
    p=argparse.ArgumentParser()
    p.add_argument('--out',default='out')
    p.add_argument('--archive',default='archive')
    p.add_argument('--history',default='../site/data/history.json')
    p.add_argument('--public',default=None)
    p.add_argument('--ledger-live-results',default=None,
                   help='Read the existing smart ledger live results API, never its fallback data')
    a=p.parse_args()
    live=[]
    if a.ledger_live_results:
        try:
            from .live_draw import fetch_live_draws
            live=fetch_live_draws(a.ledger_live_results)
            print('Verified live ledger draw records:',len(live))
        except Exception as error:
            print('Live result unavailable; using verified committed V7 history only:',error)
    print('settled records:',sync(a.out,a.archive,a.history,a.public,live_draws=live))

