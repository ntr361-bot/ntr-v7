"""Independent append-only forum observation cycle. Never writes V7 files."""
import argparse,hashlib,json,re,shutil,os
from tempfile import NamedTemporaryFile
from datetime import datetime,timezone
from pathlib import Path
from zoneinfo import ZoneInfo
from .core import ZODIACS, SCORING_RULE_VERSION, verify_scored_result

TZ=ZoneInfo('Asia/Shanghai')

def atomic_bytes(path, payload):
    path.parent.mkdir(parents=True, exist_ok=True)
    with NamedTemporaryFile(dir=path.parent, delete=False) as temp:
        staged = Path(temp.name)
        try:
            temp.write(payload)
            temp.flush()
            os.fsync(temp.fileno())
            temp.close()
            staged.replace(path)
        finally:
            staged.unlink(missing_ok=True)


def atomic(path, obj):
    atomic_bytes(path, (json.dumps(obj, ensure_ascii=False, indent=2) + '\n').encode('utf-8'))

def write_once(path,obj):
    if path.exists():return False
    path.parent.mkdir(parents=True,exist_ok=True)
    payload=(json.dumps(obj,ensure_ascii=False,indent=2)+'\n').encode('utf-8')
    from tempfile import NamedTemporaryFile
    with NamedTemporaryFile(dir=path.parent) as temp:
        temp.write(payload)
        temp.flush()
        os.fsync(temp.fileno())
        try:
            os.link(temp.name, path)
        except FileExistsError:
            return False
    return True

def verified_snapshot(folder):
    snap=json.loads((folder/'snapshot.json').read_text(encoding='utf-8'))
    check=dict(snap)
    digest=check.pop('digest',None)
    calculated=hashlib.sha256(json.dumps(check,ensure_ascii=False,sort_keys=True).encode()).hexdigest()
    if digest!=calculated or str(snap.get('issue'))!=folder.name:
        raise ValueError('immutable snapshot identity or digest mismatch for '+folder.name)
    return snap


def settlement_record(snap, actual, *, strict=False):
    issue=str(snap['issue'])
    digest=snap['digest']
    drawn_at=draw_time(actual)
    raw_frozen=snap.get('frozen_at') or (None if strict else snap.get('fetched_at'))
    if not raw_frozen:
        raise ValueError('snapshot_freeze_time_missing')
    snap_at=datetime.fromisoformat(raw_frozen)
    if drawn_at is None or snap_at.utcoffset() is None or snap_at>=drawn_at:
        raise ValueError('snapshot_not_provably_pre_draw')
    zodiac=actual.get('special_zodiac')
    if zodiac not in ZODIACS:
        raise ValueError('invalid_actual_zodiac')
    if strict:
        captured=datetime.fromisoformat(snap['fetched_at'])
        ordered=sorted(snap['ranking'],key=lambda row:row['rank'])
        if (captured.utcoffset() is None or captured>snap_at
                or [row['rank'] for row in ordered]!=list(range(1,13))
                or set(row['zodiac'] for row in ordered)!=set(ZODIACS)
                or any(snap[f'top{size}']!=[row['zodiac'] for row in ordered[:size]]
                       for size in (1,3,6))):
            raise ValueError('invalid_frozen_ranking_or_capture_time')
    rank=next((v['rank'] for v in snap['ranking'] if v['zodiac']==zodiac),None)
    if rank is None:
        raise ValueError('rank missing for actual zodiac')
    return {
        'issue':issue,'actual':zodiac,'actual_source':actual.get('actual_source','site/data/history.json'),
        'drawn_at':drawn_at.isoformat(),'snapshot_digest':digest,
        'snapshot_at':snap['fetched_at'],'rank':rank,
        'top1_hit':zodiac in snap['top1'],
        'top3_hit':zodiac in snap['top3'],
        'top6_hit':zodiac in snap['top6'],
        'classification':'experimental_unverified_leaderboard'}


def public_issue(issue, saved, settlement):
    return {
        'issue':issue,'classification':saved.get('classification'),
        'frozen_at':saved.get('frozen_at') or saved.get('fetched_at'),
        'freeze_time_source':('snapshot_frozen_at' if saved.get('frozen_at')
                              else 'legacy_collection_time_fallback'),
        'ranking':saved.get('ranking',[]),'top3':saved.get('top3',[]),
        'top6':saved.get('top6',[]),'digest':saved.get('digest'),
        'quality_status':saved.get('quality_status',
            saved.get('freeze_classification','legacy_unclassified')),
        'valid_materials':saved.get('valid_materials'),
        'minimum_valid_materials':saved.get('minimum_valid_materials'),
        'settlement':settlement
    }


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
    additions={k:v for k,v in accepted.items() if k not in existing}
    if additions:
        atomic(destination, {'issue':issue,'rule_version':SCORING_RULE_VERSION,
                             'threads':{**existing,**additions}})
    return len(additions)


def sync(out,archive,history,public_dir=None,live_draws=None,*,defer_freeze=False,finalize=False,model_id='forum-observer',before_snapshot=None,freeze_receipt=None,allow_settlement=True,valid_snapshots=None):
    """Publish reports immediately or freeze an explicitly selected current draft.

    Existing callers retain the original immediate-freeze behavior. Production
    GitHub Actions uses defer_freeze for collection and finalize for manual freeze.
    """
    if defer_freeze and finalize:
        raise ValueError('defer_freeze and finalize cannot be combined')
    archive=Path(archive);out=Path(out)
    now_local=datetime.now(TZ)
    issue=f"{now_local.year}{now_local.timetuple().tm_yday:03d}"
    folder=archive/'issues'/issue
    if finalize:
        # Enforce the cutoff even when cycle is invoked directly rather than
        # through the workflow's schedule gate.
        if (now_local.hour, now_local.minute) < (21, 0):
            raise ValueError('formal freeze requires 21:00 Asia/Shanghai or later')
        if issue=='2026327':
            raise ValueError('2026327 is excluded from normal freeze')
        draft=folder/'draft-status.json'
        status=json.loads(draft.read_text(encoding='utf-8')) if draft.exists() and not (folder/'snapshot.json').exists() else None
        if status is not None:
            if status.get('issue')!=issue:
                raise ValueError('draft period mismatch; refusing freeze')
            fetched=datetime.fromisoformat(status['fetched_at'])
            cutoff=now_local.replace(hour=21,minute=0,second=0,microsecond=0)
            if fetched.tzinfo is None or fetched.astimezone(TZ).date()!=now_local.date() or fetched>=cutoff:
                raise ValueError('post-cutoff draft cannot be frozen')
            from .pre_freeze import apply as apply_pre_freeze, load as load_pre_freeze
            status,pre_freeze_record=apply_pre_freeze(status,load_pre_freeze(folder))
    else:
        status_path=out/'status.json'
        status=json.loads(status_path.read_text(encoding='utf-8')) if status_path.exists() else None
        if status is not None:
            issue=status.get('issue')
            if not isinstance(issue,str) or not re.fullmatch(r'20\d{5}',issue):
                atomic(archive/'collection-failure.json',status)
                status=None
            elif (archive/'issues'/issue/'snapshot.json').exists():
                status=None
    if defer_freeze and status is not None:
        expected=f"{now_local.year}{now_local.timetuple().tm_yday:03d}"
        captured=datetime.fromisoformat(status['fetched_at'])
        cutoff=now_local.replace(hour=21,minute=0,second=0,microsecond=0)
        if issue!=expected:
            raise ValueError('collector report is not for the current local-day issue')
        if captured.tzinfo is None or captured.astimezone(TZ)>=cutoff:
            raise ValueError('collector completed at/after 21:00; preserved prior draft')
        valid_count=int(status.get('valid_materials') or 0)
        ranking=status.get('ranking') or []
        full_ranking=(len(ranking)==12
                      and len({r.get('zodiac') for r in ranking})==12
                      and len(status.get('top3') or [])==3
                      and len(status.get('top6') or [])==6)
        valid_observation=(status.get('status')=='ready_observation'
                           or (status.get('status')=='incomplete_observation'
                               and 0<valid_count<25
                               and status.get('pre_draw_verified') is True))
        if not valid_observation or not full_ranking or valid_count<=0:
            print('Non-qualifying collection was retained in workflow artifacts; '
                  'last eligible pre-freeze draft remains unchanged.',flush=True)
            status=None
        else:
            verify_scored_result(status,min_materials=1 if valid_count<25 else 25)
            if not (out / issue / 'report.md').is_file():
                raise ValueError('collector produced no matching current-issue report')
            captured_round=os.environ.get('FORUM_COLLECTION_ROUND')
            if captured_round in ('1','2'):
                status['collection_round']=int(captured_round)
            else:
                stamp=captured.astimezone(TZ)
                status['collection_round']=2 if (stamp.hour,stamp.minute)>=(20,30) else 1
            previous_path=archive/'issues'/issue/'draft-status.json'
            if previous_path.exists():
                try:
                    previous=json.loads(previous_path.read_text(encoding='utf-8'))
                    if previous['issue'] != issue or not (previous_path.parent/'report.md').is_file():
                        raise ValueError('incomplete previous draft')
                    verify_scored_result(previous, min_materials=1)
                    previous_time=datetime.fromisoformat(previous['fetched_at'])
                except (OSError,ValueError,TypeError,KeyError,AttributeError):
                    previous_time=None
                if previous_time is not None and previous_time.tzinfo is not None and captured<=previous_time:
                    print('Older or duplicate collector result: preserved newest verified draft',flush=True)
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
        if defer_freeze:
            run_id=re.sub(r'[^0-9a-zA-Z_-]','',os.environ.get('GITHUB_RUN_ID','local'))
            attempt=re.sub(r'[^0-9]','',os.environ.get('GITHUB_RUN_ATTEMPT','1')) or '1'
            if run_id.isdigit():
                status['collection_run_id']=run_id
                status['collection_run_attempt']=int(attempt)
            stamp=datetime.fromisoformat(status['fetched_at']).astimezone(TZ)
            report_name=f"round{status['collection_round']}-{stamp.strftime('%Y%m%dT%H%M%S%f')}-{run_id}"
            round_folder=folder/'rounds'/report_name
            round_folder.mkdir(parents=True,exist_ok=True)
            atomic(round_folder/'summary.json',{
                'issue':issue,'collection_round':status['collection_round'],
                'run_id':run_id,'fetched_at':status['fetched_at'],
                'valid_materials':status['valid_materials'],
                'top3':status.get('top3',[]),'top6':status.get('top6',[]),
                'status':status['status'],'frozen':False
            })
            round_report=out/issue/'report.md'
            if round_report.exists():
                atomic_bytes(round_folder/'report.md', round_report.read_bytes())
            atomic(folder/'draft-status.json',status)
            if run_id.isdigit():
                saved_draft = round_folder/'draft-status.json'
                write_once(saved_draft, status)
                authors = {p['author'] for p in status['parsed_predictions'] if p.get('counted_for_ranking')}
                # Append-only, run-specific proof. A green skipped gate or
                # stale draft from another run cannot satisfy manual acceptance.
                write_once(folder/'collection-runs'/f'{run_id}-{attempt}.json',{
                    'issue':issue,'run_id':run_id,'run_attempt':int(attempt),
                    'fetched_at':status['fetched_at'],
                    'valid_materials':status['valid_materials'],
                    'status':status['status'],'draft_published':True,
                    'report_published':True, 'valid_authors':len(authors),
                    'round':report_name,
                    'draft_sha256':hashlib.sha256(saved_draft.read_bytes()).hexdigest(),
                    'report_sha256':hashlib.sha256((round_folder/'report.md').read_bytes()).hexdigest()
                })
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
        if report.exists():
            atomic_bytes(folder/'report.md', report.read_bytes())
        valid_count=int(status.get('valid_materials') or 0)
        standard_minimum=int(status.get('author_selection',{}).get('min_valid_materials',25))
        full_ranking=(len(status.get('ranking',[]))==12
                      and len({r.get('zodiac') for r in status['ranking']})==12
                      and len(status.get('top3',[]))==3
                      and len(status.get('top6',[]))==6)
        partial=(status.get('status')=='incomplete_observation' and
                 0<valid_count<standard_minimum and full_ranking and
                 status.get('pre_draw_verified') is True and
                 bool(status.get('draw_at')))
        eligible=(status.get('status')=='ready_observation' and full_ranking) or partial
        if eligible and not defer_freeze:
            core={k:status[k] for k in ('issue','fetched_at','ranking','top1','top3','top6')}
            for key in ('valid_materials','scoring_rule_version','scoring_rules','material_counts'):
                if key in status:core[key]=status[key]
            core['classification']='experimental_unverified_leaderboard'
            core['minimum_valid_materials']=standard_minimum
            core['quality_status']='below_minimum_samples' if valid_count<standard_minimum else 'meets_minimum_samples'
            core['collection_status']=status['status']
            core['evidence_refs']=[{'author':p['author'],'url':p['detail_url']}
                                    for p in status.get('evidence',[]) if p.get('detail_url')]
            if status.get('pre_freeze_exclusions'):
                core['pre_freeze_exclusions']=status['pre_freeze_exclusions']
            now=datetime.now(timezone.utc)
            draw_cutoff=datetime.fromisoformat(status['draw_at']) if status.get('draw_at') else None
            captured=datetime.fromisoformat(status['fetched_at'])
            in_time=((draw_cutoff is None and not finalize) or
                     (draw_cutoff is not None and draw_cutoff.tzinfo is not None and captured.tzinfo is not None
                      and captured<=now and captured<draw_cutoff and now<draw_cutoff))
            if finalize:
                cutoff=now.astimezone(TZ).replace(hour=21,minute=0,second=0,microsecond=0)
                in_time=in_time and captured.astimezone(TZ)<=cutoff
            drawn=set()
            if Path(history).exists():
                drawn={str(r['issue']) for r in json.loads(Path(history).read_text(encoding='utf-8'))['records']}
            elif finalize:
                raise ValueError('verified draw history file absent')
            drawn.update(str(r['issue']) for r in (live_draws or []))
            if in_time and issue not in drawn and not (folder/'snapshot.json').exists():
                if status.get('scoring_rule_version')==SCORING_RULE_VERSION:
                    verify_scored_result(status,min_materials=1 if partial else 25)
                if before_snapshot is not None:
                    now=before_snapshot()
                if finalize and (now>=draw_cutoff or now.astimezone(TZ).date()!=now_local.date()):
                    raise ValueError('freeze deadline passed before snapshot write')
                if freeze_receipt is not None:
                    core['freeze_provenance']=freeze_receipt
                core['frozen_at']=now.isoformat()
                core['digest']=hashlib.sha256(json.dumps(core,ensure_ascii=False,sort_keys=True).encode()).hexdigest()
                write_once(folder/'snapshot.json',core)
        saved_snapshot=folder/'snapshot.json'
        frozen_report=folder/'report.md'
        if saved_snapshot.exists() and frozen_report.exists():
            saved=json.loads(saved_snapshot.read_text(encoding='utf-8'))
            source=frozen_report.read_text(encoding='utf-8')
            marker='本期冻结状态：'
            if marker not in source:
                count=saved.get('valid_materials','未知')
                minimum=saved.get('minimum_valid_materials',25)
                quality=('未达标实验冻结' if isinstance(count,int) and count<minimum
                         else '实验冻结')
                annotation=(f"**本期冻结状态：{quality}**；有效资料{count}份，"
                            f"标准门槛{minimum}份；冻结时间"
                            f"{saved.get('frozen_at',saved.get('fetched_at','未知'))}；"
                            f"不可变摘要：{saved.get('digest','未知')}\n")
                if '\n\n' in source:
                    source=source.replace('\n\n','\n\n'+annotation+'\n',1)
                else:
                    source+=annotation
                if saved.get('pre_freeze_exclusions'):
                    from .pre_freeze import report_section
                    source+=report_section(saved['pre_freeze_exclusions'])
                frozen_report.write_text(source,encoding='utf-8')
    if finalize and not (folder/'report.md').exists():
        from .schedule import ensure_daily_report
        ensure_daily_report(archive,now_local)
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
            continue
        by_issue[issue]=candidate
    summary=[]
    new_settlements=0
    for folder in sorted((archive/'issues').glob('20?????')) if (archive/'issues').exists() else []:
        snap_path=folder/'snapshot.json'
        if not snap_path.exists():continue
        if valid_snapshots is not None and folder.name not in valid_snapshots:
            continue
        snap=verified_snapshot(folder) if valid_snapshots is None else valid_snapshots[folder.name]
        issue=folder.name
        actual=by_issue.get(issue)
        if actual and not (folder/'settlement.json').exists() and allow_settlement:
            try:
                result=settlement_record(snap,actual)
            except ValueError as error:
                if str(error) not in ('snapshot_not_provably_pre_draw','invalid_actual_zodiac'):
                    raise
                atomic(folder/'settlement-blocked.json',{'issue':issue,'reason':str(error)})
            else:
                if write_once(folder/'settlement.json',result):
                    new_settlements += 1
        settlement=folder/'settlement.json'
        summary.append({
            'issue':issue,'actual':actual.get('special_zodiac') if actual else None,
            'top3':snap.get('top3',[]),'top6':snap.get('top6',[]),
            'top3_hit':bool(settlement.exists() and json.loads(settlement.read_text(encoding='utf-8')).get('top3_hit')),
            'top6_hit':bool(settlement.exists() and json.loads(settlement.read_text(encoding='utf-8')).get('top6_hit')),
            'rank':json.loads(settlement.read_text(encoding='utf-8')).get('rank') if settlement.exists() else None,
            'frozen_at':snap.get('frozen_at') or snap.get('fetched_at'),
            'quality_status':snap.get('quality_status',snap.get('freeze_classification','legacy_unclassified')),
            'valid_materials':snap.get('valid_materials'),
            'minimum_valid_materials':snap.get('minimum_valid_materials')
        })
    if public_dir is not None:
        public_dir=Path(public_dir)
        latest_path=archive/'latest.json'
        latest=json.loads(latest_path.read_text(encoding='utf-8')) if latest_path.exists() else {}
        current_issue=str(latest.get('issue',''))
        issue_dir=archive/'issues'/current_issue
        snap_path=issue_dir/'snapshot.json'
        settlement_path=issue_dir/'settlement.json'
        snap=(valid_snapshots.get(current_issue) if valid_snapshots is not None else
              json.loads(snap_path.read_text(encoding='utf-8')) if snap_path.exists() else None)
        settled=json.loads(settlement_path.read_text(encoding='utf-8')) if snap and settlement_path.exists() else None
        result={
            'schema_version':1,'model':model_id,
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
            'schema_version':1,'model':model_id,
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
            if valid_snapshots is not None and folder.name not in valid_snapshots:
                continue
            saved=(valid_snapshots[folder.name] if valid_snapshots is not None else
                   json.loads(sp.read_text(encoding='utf-8')))
            st=folder/'settlement.json'
            atomic(public_dir/'issues'/(folder.name+'.json'),public_issue(folder.name,saved,
                   json.loads(st.read_text(encoding='utf-8')) if st.exists() else None))
            report_src = folder / 'report.md'
            if report_src.exists():
                dest = public_dir / 'issues' / folder.name / 'report.md'
                dest.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(report_src, dest)

    return new_settlements

if __name__=='__main__':
    p=argparse.ArgumentParser()
    p.add_argument('--out',default='out')
    p.add_argument('--archive',default='archive')
    p.add_argument('--history',default='../site/data/history.json')
    p.add_argument('--public',default=None)
    p.add_argument('--defer-freeze',action='store_true',help='Publish a replaceable report immediately; defer immutable freeze')
    p.add_argument('--no-settlement',action='store_true',help='Publish without settling')
    p.add_argument('--finalize',action='store_true',help='Freeze eligible current-issue draft as a write-once snapshot')
    p.add_argument('--model-id',default='forum-observer',
                   help='Public feed model id (49论坛模型 uses forum49); default unchanged for 118')
    p.add_argument('--ledger-live-results',default=None,
                   help='Read the existing smart ledger live results API, never its fallback data')
    a=p.parse_args()
    if a.finalize and os.environ.get('GITHUB_ACTIONS') == 'true':
        raise SystemExit('Production freeze must use the guarded forum_observer.freeze_operation entry point')
    live=[]
    if a.ledger_live_results:
        try:
            from .live_draw import fetch_live_draws
            live=fetch_live_draws(a.ledger_live_results)
            print('Verified live ledger draw records:',len(live))
        except Exception as error:
            print('Live result unavailable; using verified committed V7 history only:',error)
    defer=a.defer_freeze or not a.finalize
    print('settled records:',sync(a.out,a.archive,a.history,a.public,live_draws=live,defer_freeze=defer,finalize=a.finalize,model_id=a.model_id,allow_settlement=not a.no_settlement))
