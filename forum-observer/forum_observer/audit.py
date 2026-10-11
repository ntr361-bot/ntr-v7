"""Explicit-period collection test. Never calls freeze, cycle or publication."""
import argparse
import json
from pathlib import Path
from .collector import atomic_json
from .page_collector import collect_page
from .report import render


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
    try:
        result=run(json.loads(Path(a.config).read_text(encoding='utf-8')),a.issue,a.out)
        keys=('issue','status','test_only','raw_count','valid_materials','material_counts','audit','pending_rule_question_count')
        print(json.dumps({k:result.get(k) for k in keys},ensure_ascii=False),flush=True)
        return 0 if result.get('raw_count',0)>0 and not result.get('audit',{}).get('detail_errors') and not result.get('audit',{}).get('truncated') else 2
    except Exception as exc:
        atomic_json(Path(a.out)/'collection-error.json',{'status':'failed','test_only':True,'reason':str(exc)})
        print(str(exc),flush=True)
        return 2


if __name__=='__main__':
    raise SystemExit(main())
