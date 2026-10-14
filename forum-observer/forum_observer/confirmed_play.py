"""Confirmed 287 text plays. No browsing, number inference, or persistence."""
import re
Z="鼠牛虎兔龙蛇马羊猴鸡狗猪"
Q=r"[鼠牛虎兔龙蛇马羊猴鸡狗猪]"
TOK=r"(?:[鼠牛虎兔龙蛇马羊猴鸡狗猪][ \t、，,]*)"
SINGLE=re.compile(r"(单肖|双肖)\s*[:：]\s*("+TOK+r"{6})(?!"+Q+r")")
PLUS=re.compile(r"(单肖|双肖)\s*[+＋]\s*("+TOK+r"{2})(?!"+Q+r")")
BOLD=re.compile(r"胆(大|小)\s*[:：]\s*("+TOK+r"{6})(?!"+Q+r")")
CHOICE=re.compile(r"(?:20\d{5}|\d{3})期胆大胆小【胆(大|小)】")
KNIFE=re.compile(r"🔪\s*("+TOK+r"{1,12})(?!"+Q+r")")
TITLE=re.compile(r"([六七八九6789])\s*(?:肖中特|肖)|([6789])️⃣肖")
SEQ=re.compile(r"(?:"+Q+r"[ \t、，,～~]*){6,9}(?!"+Q+r")")
NUMBER={"六":6,"七":7,"八":8,"九":9,"6":6,"7":7,"8":8,"9":9}


def picks(raw):
    return [x for x in raw if x in Z]


def group_defs(text, pattern, names):
    found={n:[] for n in names}
    for m in pattern.finditer(text):
        found[m.group(1)].append(picks(m.group(2)))
    if any(len(found[n])!=1 or len(set(found[n][0]))!=6 for n in names):
        return None
    if len(set(found[names[0]][0]+found[names[1]][0]))!=12:
        return None
    return found


def extract_confirmed(text):
    text=str(text);lines=text.splitlines();out=[]
    sd=group_defs(text,SINGLE,("单肖","双肖"))
    for line in lines:
        if "杀码" not in line and "杀号码" not in line:
            for m in KNIFE.finditer(line):
                out.append(dict(play="🔪杀肖",picks=picks(m.group(1)),
                                mode="exclude",excerpt=line[:450],raw_expression=m.group(0)))
        for m in PLUS.finditer(line):
            extra=picks(m.group(2));base=sd[m.group(1)][0] if sd else []
            valid=bool(sd and len(extra)==2 and len(set(base+extra))==8
                       and not re.search(r"杀|排除|可能",line[:m.start()]))
            evidence=" | ".join(x.group(0) for x in SINGLE.finditer(text))
            out.append(dict(play="八肖（单双组合）",picks=base+extra,
                            mode="include" if valid else "unverified",
                            excerpt=(line[:240]+" | "+evidence)[:450],
                            raw_expression=m.group(0),
                            parse_error=None if valid else "ambiguous single/double group"))
    bd=group_defs(text,BOLD,("大","小"));select=CHOICE.findall(text)
    if select:
        selected=select[0];value=bd[selected][0] if bd else []
        valid=bool(bd and len(select)==1)
        evidence=" | ".join(x.group(0) for x in BOLD.finditer(text))
        out.append(dict(play="六肖（胆"+selected+"）",picks=value,
                        mode="include" if valid else "unverified",
                        excerpt=(CHOICE.search(text).group(0)+" | "+evidence)[:450],
                        parse_error=None if valid else "ambiguous bold selection"))
    title=lines[0] if lines else ""
    heading=TITLE.search(title)
    target=NUMBER.get(heading.group(1) or heading.group(2)) if heading else None
    for line in lines[1:]:
        # A naked question mark, negation or uncertainty is NOT a selection.
        # A regular unopened result placeholder after 开 is allowed.
        normalized = re.sub(r"开\s*[:：]?\s*[《【(（]?\s*[?？]+\s*(?:00)?\s*[》】)）]?\s*(?:准|中|对)?\s*$","",line)
        if re.search(r"不确定|不是|取消|可能|待核实|待确认|疑似|或许|不推荐|不杀|[?？]",normalized):
            continue
        if re.search(r"家禽|家畜|野兽|野獸|前肖|后肖|前六肖|后六肖|单肖|双肖|胆大|胆小|灭庄|杀码|杀肖|🔪",line):
            continue
        local=TITLE.search(line)
        expected=NUMBER.get(local.group(1) or local.group(2)) if local else target
        if not expected:continue
        for match in SEQ.finditer(line):
            z=picks(match.group(0))
            if len(z)==expected and len(set(z))==expected:
                out.append(dict(play={6:"六肖",7:"七肖",8:"八肖",9:"九肖"}[expected],
                                picks=z,mode="include",
                                excerpt=(title[:60]+" | "+line[:380])[:450],
                                raw_expression=match.group(0)))
    return out
