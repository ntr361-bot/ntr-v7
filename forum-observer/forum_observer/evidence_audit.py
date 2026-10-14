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
        if play != "六肖（胆大）" and play != "六肖（胆小）":
            return "audit: unverified big/small direction"
        direction = "大" if "胆大" in play else "小"
        # A source must explicitly choose a group for the current issue.
        chosen = re.findall(
            r"(?:20\d{5}|\d{3})\s*期\s*胆大小肖\s*[:：]\s*胆([大小])生肖(?=\s*开|[\s，。,])",
            evidence_text,
        )
        if chosen != [direction]:
            return "audit: ambiguous big/small selection"
        group = _literal_sequence(excerpt, "胆" + direction + "肖:")
        if not group:
            group = _literal_sequence(excerpt, "胆" + direction + "生肖:")
        if group != picks or len(group) != 6:
            return "audit: big/small zodiac group mismatch"
        other = "小" if direction == "大" else "大"
        opposite = _literal_sequence(excerpt, "胆" + other + "生肖:")
        if not opposite:
            opposite = _literal_sequence(excerpt, "胆" + other + "肖:")
        if len(opposite) != 6 or len(set(group + opposite)) != 12:
            return "audit: incomplete or conflicting big/small definitions"
    elif play == "杀":
        # The old parser counted only 鸡 in "杀鸡 猴 龙 鼠 牛 马".
        explicit = _literal_sequence(excerpt, "杀")
        if explicit != picks:
            return "audit: truncated bare kill zodiac list"
        if "杀码" in excerpt or "杀号码" in excerpt:
            return "audit: number kill is not zodiac kill"
    return None


def admit_one_material_per_author(rows):
    """Mutate scored candidate rows in place without inventing a play priority.

    Identical votes are interchangeable. A unique nine-zodiac recommendation
    overrides six-zodiac recommendations. Any other conflict is excluded
    pending a user's selection; there must never be two votes from one author.
    """
    by_author = defaultdict(list)
    for row in rows:
        if row.get("counted_for_ranking"):
            key = (str(row.get("issue", "")), str(row.get("author", "")).strip().casefold())
            by_author[key].append(row)
    for group in by_author.values():
        if len(group) <= 1:
            continue
        nine = [r for r in group if r.get("mode") == "include"
                and re.search(r"(?:九|9)肖", str(r.get("play", "")))]
        six = [r for r in group if r.get("mode") == "include"
               and re.search(r"(?:六|6)肖", str(r.get("play", "")))]
        signatures = {(r.get("mode"), tuple(r.get("vote_zodiacs", []))) for r in group}
        if len(nine) == 1 and len(nine) + len(six) == len(group):
            chosen = nine[0]
            reason = "superseded_by_same_author_nine_zodiac"
        elif len(signatures) == 1:
            # Equal votes have no selection consequence; choose deterministically.
            chosen = min(group, key=lambda r: (
                str(r.get("source_url", "")), str(r.get("play", ""))))
            reason = "duplicate author/identical vote"
        else:
            chosen = None
            reason = "ambiguous multiple materials from same author"
        for r in group:
            if r is chosen:
                continue
            r.update(counted_for_ranking=False, exclusion_reason=reason,
                     vote_zodiacs=[], weight_per_zodiac=0.0)
    return rows
