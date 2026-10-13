"""Read-only, fail-closed adapter for the smart ledger's existing live results.

No historical V7 files or D1 rows are modified. The endpoint's fallback payload
is never accepted for a new settlement.
"""
import json,os,re
from datetime import datetime,timezone
from urllib.request import Request,urlopen
from zoneinfo import ZoneInfo
from .core import ZODIACS

TZ=ZoneInfo("Asia/Shanghai")


def normalize_live_draws(payload, now=None):
    if not isinstance(payload,dict) or payload.get("fallback") is True:
        raise ValueError("unverified or fallback results response")
    rows=payload.get("records")
    if not isinstance(rows,list) or not rows:
        raise ValueError("empty live results")
    now=now or datetime.now(timezone.utc)
    accepted={}
    for row in rows[:80]:
        if not isinstance(row,dict):continue
        issue=str(row.get("issue",""))
        if not re.fullmatch(r"20\d{5}",issue):continue
        try:
            when=datetime.fromisoformat(str(row["openTime"]))
            when=when.replace(tzinfo=TZ) if when.tzinfo is None else when
            if when>now or issue!=f"{when.astimezone(TZ).year}{when.astimezone(TZ).timetuple().tm_yday:03d}":
                continue
            numbers=row["numbers"]
            special=int(row["special"])
            year_pet=row["yearPet"]
            zodiac=row["specialZodiac"]
            if not (isinstance(numbers,list) and len(numbers)==7 and
                    all(str(n).isdigit() and 1<=int(n)<=49 for n in numbers) and
                    int(numbers[-1])==special and year_pet in ZODIACS and zodiac in ZODIACS):
                continue
            index=ZODIACS.index(year_pet)
            order=list(reversed(ZODIACS[:index+1]))+list(reversed(ZODIACS[index+1:]))
            expected=year_pet if special==49 else order[(special-1)%12]
            if expected!=zodiac:continue
        except (ValueError,KeyError,TypeError):
            continue
        accepted[issue]={"issue":issue,"special_zodiac":zodiac,
                         "open_time":when.isoformat(),
                         "actual_source":"smart-ledger:/api/results"}
    return list(accepted.values())


def fetch_live_draws(url,timeout=12):
    if not url.startswith("https://"):
        raise ValueError("HTTPS required for read-only ledger result source")
    headers={"Accept":"application/json","User-Agent":"forum-observer-settlement/1"}
    oidc=os.environ.get("FORUM_LEDGER_AUTH_BEARER")
    bypass=os.environ.get("SITES_SIWC_BYPASS_TOKEN")
    if oidc and bypass:
        headers["Authorization"]="Bearer "+oidc
        headers["OAI-Sites-Authorization"]="Bearer "+bypass
    request=Request(url,headers=headers)
    with urlopen(request,timeout=timeout) as response:
        if response.status!=200:raise ValueError("ledger results HTTP "+str(response.status))
        data=response.read(2_000_001)
    if len(data)>2_000_000:raise ValueError("ledger results payload too large")
    return normalize_live_draws(json.loads(data.decode("utf-8")))
