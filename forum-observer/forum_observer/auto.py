import argparse,json,sys
from pathlib import Path
from .collector import collect,atomic_json
from .report import render
from .core import freeze
from .page_collector import collect_page
from datetime import datetime,timezone

def main():
    p=argparse.ArgumentParser()
    p.add_argument('--config',default='collector-config.json')
    p.add_argument('--out',default='out')
    a=p.parse_args()
    out=Path(a.out)
    try:
        cfg=json.loads(Path(a.config).read_text(encoding='utf-8'))
        result=collect_page(cfg['forum_url'],out,cfg.get('issue'),cfg.get('category'),cfg) if cfg.get('forum_url') else collect(cfg,out)
        render(result,out/result['issue']/'report.md')
        if result['status']=='ready_for_pre_draw_freeze':
            if cfg.get('freeze_enabled',False):
                freeze(result,out,datetime.now(timezone.utc).isoformat(),result['draw_at'])
            return 0
        if result['status']=='html_observation':
            # A rendered-page observation is a valid run, but is not a formal
            # freeze because the live author leaderboard is not verified.
            return 0
        return 2
    except Exception as exc:
        atomic_json(out/'collection-error.json',{'status':'failed','reason':str(exc)})
        print('Collection failed:',exc,file=sys.stderr)
        return 2
if __name__=='__main__': sys.exit(main())

