import argparse, json, sys
from datetime import datetime
from pathlib import Path
from .core import calculate, freeze


def main():
    p=argparse.ArgumentParser(description='Evidence-first zodiac forum observer')
    p.add_argument('--input',required=True,help='verified current-issue JSON collected from permitted sources')
    p.add_argument('--output',default='out')
    p.add_argument('--freeze',action='store_true',help='freeze only if eligible and before draw')
    args=p.parse_args()
    data=json.loads(Path(args.input).read_text(encoding='utf-8'))
    result=calculate(data['issue'],data['draw_at'],data['works'],set(data['leaderboard_authors']))
    out=Path(args.output); out.mkdir(parents=True,exist_ok=True)
    status_path=out/f"{data['issue']}-status.json"
    status_path.write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(f"Issue {data['issue']}: {result['status']}, verified leaderboard={result['valid_leaderboard']}, outside={result['valid_outside']}, selected={result['selected_count']}")
    if args.freeze:
        try:
            path=freeze(result,out,datetime.now().astimezone().isoformat(),data['draw_at'])
            print(f'Frozen: {path}')
        except (ValueError,FileExistsError) as e:
            print(f'NOT FROZEN: {e}',file=sys.stderr)
            return 2
    return 0

if __name__=='__main__':
    sys.exit(main())
