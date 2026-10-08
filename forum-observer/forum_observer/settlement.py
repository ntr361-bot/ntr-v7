"""Append-only, independent settlement with freeze digest verification."""
import hashlib,json
from pathlib import Path
from .core import write_once,ZODIACS

def settle(folder: Path, actual: str, settled_at: str) -> Path:
    if actual not in ZODIACS:
        raise ValueError('invalid actual zodiac')
    frozen=folder/'freeze.json'
    raw=frozen.read_bytes()
    expected=(folder/'freeze.sha256').read_text().strip()
    if hashlib.sha256(raw).hexdigest()!=expected:
        raise ValueError('frozen evidence checksum mismatch')
    data=json.loads(raw)
    if actual not in [x['zodiac'] for x in data['ranking']]:
        raise ValueError('incomplete ranking')
    from datetime import datetime
    if datetime.fromisoformat(settled_at)<datetime.fromisoformat(data['draw_at']):
        raise ValueError('settlement precedes draw')
    result={'issue':data['issue'],'actual':actual,'settled_at':settled_at,'freeze_sha256':expected,'actual_rank':next(x['rank'] for x in data['ranking'] if x['zodiac']==actual),'top1_hit':actual in data['top1'],'top3_hit':actual in data['top3'],'top6_hit':actual in data['top6']}
    path=folder/'settlement.json'
    write_once(path,(json.dumps(result,ensure_ascii=False,indent=2)+'\n').encode())
    return path
