"""Strict current-issue line gate (49论坛模型 only; opt-in, default off).

User rules (2026-10-24), applied to every parsed candidate material:
1. Only lines for the current issue count; lines naming an older (or later)
   issue are never valid materials.
2. Multi-issue plans (ranges such as "295-297--...", "3期计划", "二期六肖")
   are excluded outright, even when the range contains the current issue.
3. A candidate is valid only when its own line, or the heading it sits under
   inside the same post section, explicitly names exactly the current issue
   ("297期", "第297期", "297:", "297--", "2026297"). The forum's listing label
   ("澳门第297期") is NOT an explicit issue for each line. The author's own post
   title counts as the heading of the article body.
4. Excluded candidates keep a clear exclusion reason for the reports.

The 118论坛模型 never enables this gate: score_posts() only calls it when
strict_issue_lines=True, which only forum49 sets.
"""
from __future__ import annotations

import re

REASON_HISTORICAL = 'historical issue line'
REASON_OTHER = 'non-current issue line'
REASON_MULTI = 'multi-issue plan not allowed'
REASON_NOT_EXPLICIT = 'issue number not explicit'
REASON_NOT_LOCATED = 'issue number not explicit (source line not located)'

_NOT_UNIT = r'(?!\d|\s*(?:尾|码|号|元|块|%|倍|注|头|波|段|行|合|位|肖|个|次|天))'
# "280-282", "295-297--", "296/297", "297至298"
RANGE = re.compile(r'(?<!\d)(\d{3})\s*(?:[-—–~～至到/]+)\s*(\d{2,3})(?!\d)')
# "3期计划", "(规律4肖A*3期)", "(规律-2期六肖)", "二期六肖", "两期必中".
# Not track-record stats such as "连准:7期", "错2期", "近5期", "第7期".
MULTI_PERIOD = re.compile(
    r'(?<![\d第连准中错对近前:：])(?:[2-9]|[二两三四五六七八九十])\s*期')
FULL_ISSUE = re.compile(r'(?<!\d)(20\d{2})(\d{3})(?!\d)')
# "297期", "第297期"; not a count such as "150期错2期" or "100期准95期".
SHORT_WITH_QI = re.compile(r'(?<![\d连准中错对近前共])(\d{3})\s*期(?!\s*(?:错|内|准\d|中\d|对\d))')
# A bare issue number opening the line: "297:", "297--", "294  兔马...",
# "269。。。杀兔牛", "第297期". Leading punctuation/emoji/space is allowed.
LEADING_ISSUE = re.compile(r'^[^\d一-鿿]*(?:第\s*)?(\d{3})' + _NOT_UNIT)
PREVIOUS_WORDS = re.compile(r'上\s*(?:一\s*)?期|上一回|上回|往期')
SEPARATOR = re.compile(r'^\s*[-—_=…·.*~～─━]{5,}\s*$')
# Positions where a new issue-scoped unit starts inside a collapsed line.
_SPLIT = re.compile(r'(?<!\d)(?:20\d{5}|\d{3}\s*(?:[-—–~～至到/]+\s*\d{2,3}(?!\d)|\s*期))'
                    r'|(?<=\s\s)\d{3}(?!\d)')


def split_issue_units(text: str) -> str:
    """Put every issue-scoped unit on its own line (DOM text can collapse
    several track-record rows such as "280-282--... 283-285--..." into one)."""
    value = str(text or '')
    starts = {m.start() for m in _SPLIT.finditer(value)}
    out = []
    for i, ch in enumerate(value):
        if i in starts and i and value[i - 1] != '\n':
            out.append('\n')
        out.append(ch)
    return ''.join(out)


def line_markers(line: str, issue: str):
    """Return ('multi'|'issue'|'previous'|None, issue_or_None) for one line."""
    line = str(line)
    year = str(issue)[:4]
    if RANGE.search(line) or MULTI_PERIOD.search(line):
        return 'multi', None
    found = []
    for m in FULL_ISSUE.finditer(line):
        found.append(m.group(1) + m.group(2))
    for m in SHORT_WITH_QI.finditer(line):
        found.append(year + m.group(1))
    lead = LEADING_ISSUE.match(line)
    if lead:
        found.append(year + lead.group(1))
    found = list(dict.fromkeys(found))
    if len(found) > 1:
        return 'multi', None
    if found:
        return 'issue', found[0]
    if PREVIOUS_WORDS.search(line):
        return 'previous', None
    return None, None


def _status(kind, value, issue):
    if kind == 'multi':
        return REASON_MULTI
    if kind == 'previous':
        return REASON_HISTORICAL
    if kind == 'issue':
        if value == str(issue):
            return None
        return REASON_HISTORICAL if value < str(issue) else REASON_OTHER
    return REASON_NOT_EXPLICIT


def classify_segment(text: str, issue: str, inherited=(None, None)):
    """Return [(line, reason_or_None)] for one post section.

    A line's own explicit marker decides; otherwise it inherits the most recent
    marker line (heading) of the same section; a separator line ("-----")
    ends the section and falls back to `inherited` (the post title).
    """
    rows = []
    context = inherited
    for line in str(text or '').splitlines():
        if SEPARATOR.match(line):
            context = inherited
            continue
        kind, value = line_markers(line, issue)
        if kind:
            context = (kind, value)
            rows.append((line, _status(kind, value, issue)))
        else:
            rows.append((line, _status(*context, issue)))
    return rows


def title_context(title: str, issue: str):
    kind, value = line_markers(title, issue)
    return (kind, value) if kind else (None, None)


def _norm(value: str) -> str:
    value = re.sub(r'\s+', '', str(value or ''))
    return value.replace('前六肖', '前肖').replace('后六肖', '后肖')


def candidate_reason(candidate: dict, rows, title_multi: bool = False):
    """Strict reason for one parsed candidate, or None when it is valid.

    The candidate's source line is located by its excerpt (each " | " piece is
    tried) and, when present, its raw expression. A post whose own title is a
    multi-issue plan ("二期六肖", "两期必中") makes every candidate a plan.
    """
    if title_multi:
        return REASON_MULTI
    excerpt = str(candidate.get('excerpt') or '')
    parts = excerpt.split(' | ')
    pieces = [_norm(p) for p in [excerpt, *parts] if _norm(p)]
    # A selection phrase (e.g. big/small choice) may sit inside its line.
    lead = _norm(parts[0]) if parts else ''
    raw = _norm(candidate.get('raw_expression') or '')
    matched = []
    for line, reason in rows:
        n = _norm(line)
        if not n:
            continue
        if raw and raw not in n:
            continue
        if any(n.startswith(p) for p in pieces) or (len(lead) >= 4 and lead in n):
            matched.append(reason)
    if not matched:
        return REASON_NOT_LOCATED
    if None in matched:
        return None
    return matched[0]
