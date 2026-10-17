import argparse,json,sys,threading,signal
from pathlib import Path
from .collector import collect,atomic_json
from .report import render
from .core import freeze
from .page_collector import collect_page
from datetime import datetime,timezone
from .trace import record, heartbeat

def main():
    p=argparse.ArgumentParser()
    p.add_argument('--config',default='collector-config.json')
    p.add_argument('--out',default='out')
    p.add_argument('--issue',default=None,help='Strict YYYYNNN override for read-only historical audit')
    p.add_argument('--max-collection-seconds',type=int,default=None,
                   help='Bound a scheduled scan to the local daily publication cutoff')
    a=p.parse_args()
    out=Path(a.out)
    record(out, 'starting', issue=a.issue)
    stop=threading.Event()
    worker=threading.Thread(target=heartbeat,args=(out,stop),daemon=True)
    worker.start()
    def interrupted(signum, frame):
        raise KeyboardInterrupt('signal ' + str(signum))
    old_term=signal.signal(signal.SIGTERM, interrupted) if threading.current_thread() is threading.main_thread() else None
    try:
        cfg=json.loads(Path(a.config).read_text(encoding='utf-8'))
        if a.max_collection_seconds is not None:
            if a.max_collection_seconds < 60: raise ValueError('collection budget must be >= 60 seconds')
            cfg['max_collection_seconds']=min(int(cfg.get('max_collection_seconds',1800)),a.max_collection_seconds)
        result=collect_page(cfg['forum_url'],out,a.issue or cfg.get('issue'),cfg.get('category'),cfg) if cfg.get('forum_url') else collect(cfg,out)
        render(result,out/result['issue']/'report.md')
        if result['status']=='ready_for_pre_draw_freeze':
            if cfg.get('freeze_enabled',False):
                freeze(result,out,datetime.now(timezone.utc).isoformat(),result['draw_at'])
            record(out, 'completed', data=result)
            return 0
        if result['status']=='ready_observation':
            record(out, 'completed', data=result)
            return 0
        record(out, 'incomplete', data=result)
        print('Incomplete forum observation:',result['status'],result['issue'],file=sys.stderr)
        return 2
    except Exception as exc:
        atomic_json(out/'collection-error.json',{'status':'failed','reason':str(exc)})
        record(out, 'failed', error=str(exc))
        print('Collection failed:',exc,file=sys.stderr)
        return 2
    except BaseException as exc:
        record(out, 'interrupted', error=type(exc).__name__ + ': ' + str(exc))
        raise
    finally:
        stop.set()
        worker.join(timeout=2)
        if old_term is not None:
            signal.signal(signal.SIGTERM, old_term)
if __name__=='__main__': sys.exit(main())

