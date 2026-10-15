"""Independent, local-only evidence checks and per-author vote admission.

Never fetch a web page or infer生肖 from numbers. Unclear evidence is rejected.
"""
from __future__ import annotations
import re
from collections import defaultdict

ZODIACS = tuple("鼠牛虎兔龙蛇马羊猴鸡狗猪")
SEPARATORS = frozenset(" \t、，,/|+.-，。")


def _literal_sequence(source, label):
    """Extract *all* adjacent zodiac tokens, independent of parser regexes."""
    at = source.find(label)
    if at < 0:
        return []
    tail = source[at + len(label):].lstrip(" \t:：=【[（(")
    result = []
    started = False
    for ch in tail:
        if ch in ZODIACS:
            result.append(ch)
            started = True
        elif ch in SEPARATORS and started:
            continue
        else:
            break
    return result


def audit_material(row, evidence_text):
    """A second pass: fail closed on mismatched literal choices and direction."""
    play = str(row.get("play", ""))
    picks = list(row.get("picks", []))
    excerpt = str(row.get("excerpt", ""))
    if not picks or len(picks) != len(set(picks)) or any(z not in ZODIACS for z in picks):
        return "audit: invalid or duplicated zodiac picks"
    if "胆大" in excerpt or "胆小" in excerpt or "胆大" in play or "胆小" in play:
        if play not in ("六肖（胆大）", "六肖（胆小）"):
            return "audit: unverified big/small direction"
        direction = "大" if "胆大" in play else "小"
        selections = (re.findall(
            r"(?:20\d{5}|\d{3})\s*期\s*胆大小肖\s*[:：]\s*胆([大小])生肖(?=\s*开|[\s，。,])",
            excerpt) + re.findall(
            r"(?:20\d{5}|\d{3})\s*期\s*胆大胆小\s*【\s*胆([大小])\s*】",
            excerpt))
        if selections != [direction]:
            return "audit: ambiguous big/small selection"
        groups = {}
        for side in ("大", "小"):
            matches = re.findall(
                r"胆"+side+r"(?:生肖|肖)?\s*[:：]\s*"
                r"((?:[鼠牛虎兔龙蛇马羊猴鸡狗猪][ \t、，,]*){6})"
                r"(?![鼠牛虎兔龙蛇马羊猴鸡狗猪])", excerpt)
            groups[side] = [[z for z in m if z in ZODIACS] for m in matches]
            if len(groups[side]) != 1 or len(set(groups[side][0])) != 6:
                return "audit: invalid big/small group"
        if len(set(groups["大"][0]+groups["小"][0])) != 12:
            return "audit: incomplete or conflicting big/small definitions"
        if picks != groups[direction][0]:
            return "audit: big/small zodiac group mismatch"
    elif play == "八肖（单双组合）":
        expr = str(row.get("raw_expression",""))
        if not expr or expr not in excerpt:
            return "audit: missing single/double expression"
        m = re.fullmatch(
            r"(单肖|双肖)\s*[+＋]\s*((?:[鼠牛虎兔龙蛇马羊猴鸡狗猪][ \t、，,]*){2})",
            expr)
        if not m:
            return "audit: invalid single/double combination"
        defs={}
        for side in ("单肖","双肖"):
            matches=re.findall(side+r"\s*[:：]\s*((?:[鼠牛虎兔龙蛇马羊猴鸡狗猪][ \t、，,]*){6})(?![鼠牛虎兔龙蛇马羊猴鸡狗猪])",excerpt)
            defs[side]=[[z for z in x if z in ZODIACS] for x in matches]
            if len(defs[side])!=1 or len(set(defs[side][0]))!=6:
                return "audit: invalid single/double definitions"
        if len(set(defs["单肖"][0]+defs["双肖"][0]))!=12:
            return "audit: single/double definitions not complementary"
        extra=[z for z in m.group(2) if z in ZODIACS]
        chosen=defs[m.group(1)][0]+extra
        if len(set(chosen))!=8 or chosen!=picks:
            return "audit: single/double combination mismatch"
    elif play.startswith("六肖（选"):
        expression=str(row.get("raw_expression",""))
        if not expression or expression not in excerpt:
            return "audit: missing selected six-zodiac category"
        f=re.fullmatch(r"(?:20\d{5}|\d{3})\s*期\s*前后六肖\s*【\s*(前肖|后肖)\s*】",expression)
        h=re.fullmatch(r"(?:20\d{5}|\d{3})\s*期\s*家野六肖\s*【\s*(家畜|家禽|野兽|野獸)\s*】",expression)
        if not (f or h):
            return "audit: unrecognized selected category expression"
        labels=("前六肖","后六肖") if f else ("家","野")
        found={}
        if f:
            for k in labels:
                terms=re.findall(k+r"\s*[:：]\s*((?:[鼠牛虎兔龙蛇马羊猴鸡狗猪][ \t、，,]*){6})",excerpt)
                found[k]=[[z for z in term if z in ZODIACS] for term in terms]
            wanted="前六肖" if f.group(1)=="前肖" else "后六肖"
        else:
            terms=re.findall(r"(家禽|家畜|野兽|野獸)\s*[:：]\s*((?:[鼠牛虎兔龙蛇马羊猴鸡狗猪][ \t、，,]*){6})",excerpt)
            found["家"]=[[z for z in term if z in ZODIACS] for k,term in terms if k in ("家禽","家畜")]
            found["野"]=[[z for z in term if z in ZODIACS] for k,term in terms if k in ("野兽","野獸")]
            wanted="家" if h.group(1) in ("家禽","家畜") else "野"
        if any(len(found[k])!=1 or len(set(found[k][0]))!=6 for k in labels):
            return "audit: invalid group definitions"
        if len(set(found[labels[0]][0]+found[labels[1]][0]))!=12:
            return "audit: category definitions do not partition all zodiacs"
        if picks!=found[wanted][0]:
            return "audit: selected category does not match picks"
    elif play == "🔪杀肖":
        m=re.search(r"🔪\s*((?:[鼠牛虎兔龙蛇马羊猴鸡狗猪][ \t、，,]*){1,12})",excerpt)
        if not m or [z for z in m.group(1) if z in ZODIACS]!=picks:
            return "audit: truncated knife kill zodiac list"
    elif (play.endswith("（明确推荐）") or
          (play in ("六肖","七肖","八肖","九肖") and row.get("raw_expression"))):
        raw=str(row.get("raw_expression",""))
        if not raw or raw not in excerpt or [z for z in raw if z in ZODIACS]!=picks:
            return "audit: direct recommendation disagrees with source"
    elif play == "八肖（家野组合）":
        # Independently reconstruct the vote from the report evidence, rather
        # than trusting the extractor's picks / group expansion.
        expression = str(row.get("raw_expression", ""))
        if not expression or expression not in excerpt or expression not in evidence_text:
            return "audit: missing named-group expression"
        m = re.fullmatch(
            r"(家禽|家畜|野兽)[ \t]*[+＋][ \t]*"
            r"([鼠牛虎兔龙蛇马羊猴鸡狗猪 \t、，,]+)", expression)
        if not m:
            return "audit: invalid named-group expression"
        name = m.group(1)
        extras = [z for z in m.group(2) if z in ZODIACS]
        # Read literal source definitions using an independently written
        # pattern. Refuse ambiguous/missing counterparts and corrupt lists.
        found = re.findall(
            r"(家禽|家畜|野兽)[ \t]*[:：=][ \t]*"
            r"((?:[鼠牛虎兔龙蛇马羊猴鸡狗猪][ \t、，,]*){6})"
            r"(?![鼠牛虎兔龙蛇马羊猴鸡狗猪])", excerpt)
        definitions = {}
        for label, raw in found:
            definitions.setdefault(label, []).append(
                [z for z in raw if z in ZODIACS])
        if "家禽" in definitions and "家畜" in definitions:
            return "audit: competing domestic-group definitions"
        domestic = "家禽" if "家禽" in definitions else "家畜"
        other = "野兽" if name != "野兽" else domestic
        if len(definitions.get(name, [])) != 1 or len(definitions.get(other, [])) != 1:
            return "audit: missing or duplicate named-group definition"
        base = definitions[name][0]
        other_set = definitions[other][0]
        if (len(base) != 6 or len(set(base)) != 6
                or len(other_set) != 6 or len(set(base + other_set)) != 12):
            return "audit: incorrect named-group partition"
        if len(extras) != 2 or len(set(extras)) != 2 or set(extras) & set(base):
            return "audit: invalid group extras"
        if picks != list(dict.fromkeys([*base, *extras])) or len(picks) != 8:
            return "audit: named-group vote does not match literal article"
    elif play == "杀":
        # The old parser counted only 鸡 in "杀鸡 猴 龙 鼠 牛 马".
        explicit = _literal_sequence(excerpt, "杀")
        if explicit != picks:
            return "audit: truncated bare kill zodiac list"
        if "杀码" in excerpt or "杀号码" in excerpt:
            return "audit: number kill is not zodiac kill"
    return None


def _admission_priority(row):
    """Lower value is preferred, based only on an independently verified play."""
    if row.get("mode") == "include":
        # Structured works may only have normalized votes, while scraped works
        # retain the literal picks. Never treat an exclude complement as a tip.
        count = len(row.get("picks") or row.get("vote_zodiacs") or [])
        if count in (9, 8, 7, 6):
            return 9 - count
        return 4
    if row.get("mode") == "exclude":
        return 5
    return 6


def admit_one_material_per_author(rows):
    """Choose exactly ONE verified material per author per issue.

    Preference: nine, eight, seven, six, other valid recommendation, valid
    zodiac kill. For equal priority select the earliest ORIGINAL publication,
    then a stable source URL / play / literal picks tiebreak. No actual result,
    historical hit rate, or zodiac identity is used for selection.

    Invalid and uncertain rows were excluded by the independent parser before
    this function. Unselected rows remain as auditable, zero-weight records.
    """
    by_author = defaultdict(list)
    for row in rows:
        if row.get("counted_for_ranking"):
            key = (str(row.get("issue", "")).strip(),
                   str(row.get("author", "")).strip().casefold())
            by_author[key].append(row)

    for group in by_author.values():
        if len(group) <= 1:
            continue

        chosen = min(group, key=lambda r: (
            _admission_priority(r),
            str(r.get("published_at") or "9999-12-31T23:59:59"),
            str(r.get("source_url") or ""),
            str(r.get("play") or ""),
            "".join(r.get("picks") or r.get("vote_zodiacs") or []),
        ))
        for row in group:
            if row is chosen:
                continue
            reason = ("superseded_by_same_author_higher_priority"
                      if _admission_priority(row) > _admission_priority(chosen)
                      else "duplicate author/equal priority earlier source")
            row.update(counted_for_ranking=False, exclusion_reason=reason,
                       vote_zodiacs=[], weight_per_zodiac=0.0)
    return rows
