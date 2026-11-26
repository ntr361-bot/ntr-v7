"""Read anonymous 49 forum article details and select public body images."""
from __future__ import annotations

from collections import Counter
from html.parser import HTMLParser
import json
import re
import subprocess
import time
from urllib.parse import unquote, urlencode, urljoin, urlsplit

from .forum49 import DEFAULT_URL

API = urljoin(DEFAULT_URL, "/unite49/h5/article/")
ORIGINAL_PATH = re.compile(r"/(?:unite49files|tk118files)/article/[a-zA-Z0-9_./-]+")
IMAGE_ORIGINS = {("49217.com", 443), ("jmz.jlidesign.com", 4949)}
MAX_IMAGE_BYTES = 12_000_000


class InlineImages(HTMLParser):
    def __init__(self):
        super().__init__()
        self.urls = []

    def handle_starttag(self, tag, attrs):
        if tag == "img":
            attrs = dict(attrs)
            url = attrs.get("data-src") or attrs.get("src")
            if url:
                self.urls.append(url)


def article_image_url(src: str, post_url: str) -> str | None:
    try:
        url = urljoin(post_url, src)
        parsed = urlsplit(url)
        origin = (parsed.hostname, parsed.port or 443)
        path = unquote(parsed.path)
    except ValueError:
        return None
    if (parsed.scheme != "https" or origin not in IMAGE_ORIGINS or parsed.username
            or parsed.password or parsed.fragment or not ORIGINAL_PATH.fullmatch(path)
            or ".." in path.split("/")):
        return None
    return url


def _get(url, post_url, max_bytes, target="-"):
    # No credentials and no redirect follow. The subprocess deadline covers
    # DNS, connection, and slow reads together.
    result = subprocess.run(
        ["curl", "--disable", "--fail", "--silent", "--show-error", "--proto", "=https",
         "--connect-timeout", "8", "--max-time", "18", "--max-filesize",
         str(max_bytes), "--referer", post_url.partition("#")[0],
         "--output", str(target), "--", url],
        capture_output=True, timeout=20, check=False,
    )
    if result.returncode:
        reason = result.stderr.decode("utf-8", errors="replace").strip()
        raise RuntimeError(f"curl exit {result.returncode}: {reason}")
    return result.stdout


def download_image(url: str, post_url: str, target) -> None:
    if article_image_url(url, post_url) != url:
        raise ValueError("unapproved image source")
    _get(url, post_url, MAX_IMAGE_BYTES, target)
    if not 0 < target.stat().st_size <= MAX_IMAGE_BYTES:
        raise ValueError("empty or oversized image response")


def read_article_data(endpoint, **query):
    result = json.loads(_get(f"{API}{endpoint}?{urlencode(query)}", DEFAULT_URL, 2_000_000))
    if result.get("success") is not True or not isinstance(result.get("data"), dict):
        raise ValueError(f"article API rejected {endpoint}: code {result.get('code')}")
    return result["data"]


def is_public(article):
    flag = article.get("pay")
    return flag is False or type(flag) is int and flag == 0 or flag == "0"


def body_image_urls(detail, article_id, source, counts):
    if (str(detail.get("articleId")) != article_id or str(detail.get("type")) != "3"
            or str(detail.get("lotteryType")) != "2"):
        counts["detail_identity_mismatch"] += 1
        return
    if not is_public(detail):
        counts["detail_not_confirmed_public"] += 1
        return
    counts["public_details"] += 1
    inline = InlineImages()
    inline.feed(str(detail.get("description") or ""))
    inline.close()
    attachments = [picture.get("pictureUrl") for picture in detail.get("pictureList") or []
                   if isinstance(picture, dict)]
    # Both fields are authored body content. Exclude covers, avatars, ads,
    # comment images and anything outside the site's article upload path.
    for candidate in inline.urls + attachments:
        safe = article_image_url(candidate, source) if isinstance(candidate, str) else None
        if safe:
            yield safe
        else:
            counts["rejected_image_url"] += 1


def discover_images(*, deadline: float, diagnostics: dict, read=read_article_data):
    counts = Counter()
    diagnostics["discovery_counts"] = counts
    errors = diagnostics["source_errors"] = []
    visited = set()
    for page in range(1, 19):
        if time.monotonic() >= deadline:
            counts["deadline_reached"] += 1
            return
        try:
            listing = read("search", type=3, sort=1, pageSize=20, pageNum=page,
                           lotteryType=2, speak="", compete="")
        except (OSError, ValueError, RuntimeError, subprocess.TimeoutExpired) as exc:
            counts["feed_errors"] += 1
            errors.append({"page": page, "reason": str(exc)[:400]})
            if counts["feed_errors"] >= 3:
                raise RuntimeError("three article listing requests failed") from exc
            continue
        items = listing.get("list") or []
        if not items:
            return
        counts["listed_posts"] += len(items)
        for item in items:
            if time.monotonic() >= deadline:
                counts["deadline_reached"] += 1
                return
            article_id = str(item.get("articleId"))
            if not re.fullmatch(r"[1-9][0-9]*", article_id) or article_id in visited:
                continue
            visited.add(article_id)
            if not is_public(item):
                counts["listing_not_confirmed_public"] += 1
                continue
            if item.get("username") in {"论坛管理", "管理员"}:
                continue
            if not item.get("pictureList") and not re.search(
                    r"<img\b", str(item.get("description") or ""), re.IGNORECASE):
                continue
            source = urljoin(DEFAULT_URL, f"/#/corpusdetail?id={article_id}&type=3")
            try:
                detail = read("detail", articleId=article_id)
            except (OSError, ValueError, RuntimeError, subprocess.TimeoutExpired) as exc:
                counts["detail_errors"] += 1
                errors.append({"source_url": source, "reason": str(exc)[:400]})
                continue
            for url in body_image_urls(detail, article_id, source, counts):
                yield {"source_url": source, "image_url": url,
                       "article_id": article_id, "title": str(detail.get("title") or "")}
