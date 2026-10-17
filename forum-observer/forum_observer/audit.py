"""Explicit-period collection test. Never calls freeze, cycle or publication."""
import argparse
import json
import threading
import signal
from pathlib import Path
from .collector import atomic_json
from .page_collector import collect_page
from .report import render
from .trace import record, heartbeat


def run(config, issue, out):
    cfg=dict(config)
    cfg['freeze_enabled']=False
    cfg['collection_mode']='current_feed'
    out=Path(out)
    result=collect_page(cfg['forum_url'],out,issue,cfg.get('category'),cfg)
    if result.get('issue')!=issue:
        raise ValueError('audit issue mismatch')
    result['collection_status']=result['status']
    result['status']='historical_test_only'
    result['test_only']=True
    result['prediction_eligible']=False
    result['selected_count']=0
    atomic_json(out/'status.json',result)
    render(result,out/issue/'report.md')
    report=out/issue/'report.md'
    report.write_text('> 历史测试：开奖后采集，仅用于检查解析；不冻结、不发布到账本、不兑奖。\n\n'+report.read_text(encoding='utf-8'),encoding='utf-8')
    return result


def main():
    p=argparse.ArgumentParser()
    p.add_argument('--config',default='collector-config.json')
    p.add_argument('--issue',required=True)
    p.add_argument('--out',default='test-out')
    a=p.parse_args()
    record(a.out, 'starting', issue=a.issue)
    stop=threading.Event()
    worker=threading.Thread(target=heartbeat,args=(a.out,stop),daemon=True)
    worker.start()
    def interrupted(signum, frame):
        raise KeyboardInterrupt('signal ' + str(signum))
    old_term=signal.signal(signal.SIGTERM, interrupted) if threading.current_thread() is threading.main_thread() else None
    try:
        result=run(json.loads(Path(a.config).read_text(encoding='utf-8')),a.issue,a.out)
        keys=('issue','status','test_only','raw_count','valid_materials','material_counts','audit','pending_rule_question_count')
        print(json.dumps({k:result.get(k) for k in keys},ensure_ascii=False),flush=True)
        code=0 if result.get('raw_count',0)>0 and not result.get('audit',{}).get('detail_errors') and not result.get('audit',{}).get('truncated') else 2
        record(a.out, 'completed' if code==0 else 'incomplete', data=result)
        return code
    except Exception as exc:
        atomic_json(Path(a.out)/'collection-error.json',{'status':'failed','test_only':True,'reason':str(exc)})
        record(a.out, 'failed', error=str(exc))
        print(str(exc),flush=True)
        return 2
    except BaseException as exc:
        record(a.out, 'interrupted', error=type(exc).__name__ + ': ' + str(exc))
        raise
    finally:
        stop.set()
        worker.join(timeout=2)
        if old_term is not None:
            signal.signal(signal.SIGTERM, old_term)


if __name__=='__main__':
    raise SystemExit(main())
