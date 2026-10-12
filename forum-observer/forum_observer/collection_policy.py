"""Bounded source discovery for the experimental forum collector only.

Selecting a source is not accepting a prediction. All votes still pass through
the existing current-issue, publication-time and six-plus-zodiacs validators.
"""
from __future__ import annotations

import re

_COMMENT_HINT = re.compile(r"评论区|评论.*(?:更新|资料)|(?:更新|资料).*评论")


def choose_profile_samples(author_order, target_issue, feed_mode, policy):
    """Daily sampling must not silently skip every author profile.

    Keep the retrospective audit's existing upper bound. Reserve daily time
    for current-period comments instead of following hundreds of histories.
    """
    if not target_issue and not feed_mode:
        return []
    limit = max(1, min(60, int(policy.get("max_profile_authors", 30))))
    if feed_mode:
        limit = min(limit, max(1, min(8, int(policy.get("daily_profile_authors", 4)))))
    return author_order[:limit]


def choose_comment_threads(posts, scoped, policy):
    """Return independently sourced current-issue posts worth checking.

    Comment authors are verified only after entering the thread. We never
    infer a comment from the thread owner's card and never vote on a hint.
    """
    source = {(p.get("author"), p.get("issue"), p.get("title")): p for p in scoped}
    favored = set(policy.get("top_authors", []))
    seen = set()
    candidates = []
    for post in posts:
        if post.get("evidence_scope") != "current_feed_card":
            continue
        key = (post.get("author"), post.get("issue"), post.get("title"))
        if key in seen or key not in source:
            continue
        seen.add(key)
        item = source[key]
        title_body = str(post.get("title", "")) + "\n" + str(post.get("body", ""))
        hint = bool(_COMMENT_HINT.search(title_body))
        direct = "corpusdetail" in (item.get("href") or "")
        featured = post.get("is_featured") is True and item.get("is_featured") is True
        candidates.append((post, item, featured, hint, direct, post.get("author") in favored))
    # "精" is an actual card badge, not a title keyword or proof of accuracy.
    # Remaining order: explicit reply hint, direct URL, favored author, DOM order.
    candidates.sort(key=lambda row: (not row[2], not row[3], not row[4], not row[5]))
    limit = max(1, min(200, int(policy.get("max_comment_threads", 30))))
    return [(p, item) for p, item, *_ in candidates[:limit]], len(candidates)
