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
        # Current-issue cards already supply authors; spend daily time on
        # priority article/reply text rather than opening author histories.
        daily_limit = max(0, min(8, int(policy.get("daily_profile_authors", 4))))
        if daily_limit == 0:
            return []
        limit = min(limit, daily_limit)
    return author_order[:limit]


def comment_thread_budget_seconds(parent, policy):
    """Bound each thread so early slow posts cannot starve later featured posts.

    The featured queue is examined first. Ordinary posts receive a smaller
    budget; unread or incomplete threads stay explicitly unverified.
    """
    key = "max_featured_post_seconds" if parent.get("is_featured") is True else "max_post_seconds"
    default = 35 if parent.get("is_featured") is True else 20
    cap = 180 if policy.get("retrospective_exhaustive") else 60
    return max(10, min(cap, int(policy.get(key, default))))


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
        # Reading access-gated text cannot be inferred from its preview.
        # Put it behind public threads without trying to bypass access controls.
        gated = bool(re.search(r"帖子加密区|购买后可见|付费解锁", str(post.get("body", ""))))
        # Prefer public current-post text containing >=6 distinct zodiacs as
        # a sampling hint only; actual scoring still validates every play.
        has_six = len(set(c for c in title_body if c in "鼠牛虎兔龙蛇马羊猴鸡狗猪")) >= 6
        candidates.append((post, item, featured, hint, direct,
                           post.get("author") in favored, gated, has_six))
    # Public, truly featured cards first. Then comment hints, substantial
    # text, a direct link, favored authors, and stable forum order.
    candidates.sort(key=lambda row: (row[6], not row[2], not row[3],
                                     not row[7], not row[4], not row[5]))
    cap = 10000 if policy.get("retrospective_exhaustive") else 200
    limit = max(1, min(cap, int(policy.get("max_comment_threads", 30))))
    return [(p, item) for p, item, *_ in candidates[:limit]], len(candidates)
