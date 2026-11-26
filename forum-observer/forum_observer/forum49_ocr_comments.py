"""Add public comment pictures to an existing 49 forum image shadow report."""
from __future__ import annotations

import argparse
from collections import Counter
from contextlib import closing
import json
from pathlib import Path
import re
import sqlite3
import tempfile
from urllib.parse import unquote, urlencode, urljoin, urlsplit

from .forum49_ocr_engine import PaddleWorker
from .forum49_ocr_shadow import markdown, summarize
from .forum49_ocr_sources import (
    DEFAULT_URL, IMAGE_ORIGINS, MAX_IMAGE_BYTES, InlineImages, _get, is_public, read_article_data,
)
from .forum49_ocr_test import collect_and_recognize, retry

COMMENT_API = urljoin(DEFAULT_URL, "/unite49/h5/comment/")
COMMENT_PATH = re.compile(r"/(?:unite49files|tk118files)/comment/[a-zA-Z0-9_./-]+")
PAGE_SIZE = 10


def image_url(raw, source):
    try:
        url = urljoin(source, raw)
        parsed = urlsplit(url)
        path = unquote(parsed.path)
        if (parsed.scheme == "https" and (parsed.hostname, parsed.port or 443) in IMAGE_ORIGINS
                and not (parsed.username or parsed.password or parsed.fragment)
                and COMMENT_PATH.fullmatch(path) and ".." not in path.split("/")):
            return url
    except ValueError:
        pass
    return None


def download(url, source, target):
    if image_url(url, source) != url:
        raise ValueError("unapproved comment image URL")
    _get(url, source, MAX_IMAGE_BYTES, target)
    if not 0 < target.stat().st_size <= MAX_IMAGE_BYTES:
        raise ValueError("empty or oversized comment image")


def read_comments(endpoint, **query):
    response = json.loads(_get(f"{COMMENT_API}{endpoint}?{urlencode(query)}", DEFAULT_URL, 2_000_000))
    if response.get("success") is not True or not isinstance(response.get("data"), dict):
        raise ValueError(f"comment API rejected {endpoint}: code {response.get('code')}")
    return response["data"]


def public_comment(row, article_id, comment_type):
    return (str(row.get("relatedId")) == article_id and str(row.get("type")) == comment_type
            and str(row.get("status")) == "1" and bool(re.fullmatch(r"[1-9][0-9]*", str(row.get("id")))))


def discover_comments(issue, ids, diagnostics, *, pages, read_article=read_article_data,
                      read_comment=read_comments):
    counts = diagnostics["counts"] = Counter()
    errors = diagnostics["source_errors"] = []

    def request(stage, identity, op):
        data, failures = retry(lambda _: op())
        if data is None:
            errors.append({"stage": stage, "id": identity, "attempts": failures})
        return data

    def candidates(row, article_id, source, kind):
        if not public_comment(row, article_id, kind):
            counts["unverified_comment_rows"] += 1
            return
        key = (article_id, str(row["id"]))
        if key in seen:
            return
        seen.add(key)
        inline = InlineImages()
        inline.feed(str(row.get("content") or ""))
        inline.close()
        attachments = [p.get("pictureUrl") for p in row.get("pictureList") or []
                       if isinstance(p, dict)]
        for link in dict.fromkeys(p for p in inline.urls + attachments if isinstance(p, str)):
            url = image_url(link, source)
            if url:
                yield {"source_url": source, "image_url": url, "article_id": article_id,
                       "comment_id": str(row["id"]), "source_kind": "comment"}
            else:
                counts["rejected_image_urls"] += 1

    seen = set()
    for article_id in dict.fromkeys(ids):
        counts["requested_posts"] += 1
        detail = request("article", article_id, lambda: read_article("detail", articleId=article_id))
        if (detail is None or str(detail.get("articleId")) != article_id or not is_public(detail)
                or str(detail.get("type")) not in {"3", "13"}
                or str(detail.get("lotteryType")) != "2"
                or f"{detail.get('year')}{str(detail.get('period')).zfill(3)}" != issue):
            counts["unverified_posts"] += 1
            continue
        counts["verified_posts"] += 1
        source = urljoin(DEFAULT_URL, f"/#/corpusdetail?id={article_id}&type=3")
        for page in range(1, pages + 1):
            data = request("comments", article_id, lambda: read_comment(
                "listLatest", relatedId=article_id, type=3, pageSize=PAGE_SIZE,
                pageNum=page, postUserId="", sort=0, lotteryType=2))
            if data is None:
                break
            rows = data.get("list") or []
            for row in rows:
                yield from candidates(row, article_id, source, "3")
                if not public_comment(row, article_id, "3"):
                    continue
                previews = row.get("subCommentList") or []
                for reply in previews:
                    yield from candidates(reply, article_id, source, "6")
                if int(row.get("secondLevelCount") or 0) <= len(previews):
                    continue
                parent_id = str(row["id"])
                for reply_page in range(1, pages + 1):
                    replies = request("replies", parent_id, lambda: read_comment(
                        "listSecondComment", commentId=parent_id, pageNum=reply_page, lotteryType=2))
                    if replies is None:
                        break
                    parent = replies.get("firstLevelComment") or {}
                    if str(parent.get("id")) != parent_id or not public_comment(parent, article_id, "3"):
                        counts["unverified_reply_pages"] += 1
                        break
                    for reply in replies.get("list") or []:
                        yield from candidates(reply, article_id, source, "6")
                    if not replies.get("moreSecondComments"):
                        break
                else:
                    counts["reply_page_limit_reached"] += 1
            if len(rows) < PAGE_SIZE:
                break
        else:
            counts["comment_page_limit_reached"] += 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--issue", required=True)
    parser.add_argument("--body-report", type=Path, required=True)
    parser.add_argument("--post-id", action="append", required=True, help="Public parent post ID, repeatable")
    parser.add_argument("--pages", type=int, default=5, help="Maximum pages per comment or reply thread")
    parser.add_argument("--max-images", type=int, help="Optional ceiling for a live sample")
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    if not all(re.fullmatch(r"[1-9][0-9]*", i) for i in args.post_id):
        parser.error("post IDs must be positive integers")
    if args.pages < 1 or (args.max_images is not None and args.max_images < 1):
        parser.error("page and image limits must be positive")

    body = json.loads(args.body_report.read_text(encoding="utf-8"))
    summarize(body, args.issue)
    diagnostics = {"selected_post_ids": list(dict.fromkeys(args.post_id)),
                   "page_limit": args.pages, "image_limit": args.max_images}
    args.out.mkdir(parents=True, exist_ok=False)
    sources = discover_comments(args.issue, args.post_id, diagnostics, pages=args.pages)
    with closing(sources), tempfile.TemporaryDirectory() as work, PaddleWorker() as worker:
        with closing(sqlite3.connect(args.out / "recognition-cache.sqlite3")) as db:
            images, failures, counts = collect_and_recognize(
                sources, Path(work), db, worker, download=download, limit=args.max_images,
                retain_duplicate_sources=True)
    diagnostics["ocr_counts"] = counts
    combined = {
        **body, "issue": args.issue, "comment_diagnostics": diagnostics,
        "images": body["images"] + images,
        "download_failures": body.get("download_failures", []) + failures,
    }
    summary = summarize(combined, args.issue)
    (args.out / "report.json").write_text(json.dumps(combined, ensure_ascii=False, indent=2), encoding="utf-8")
    (args.out / "shadow.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    text = markdown(summary)
    text += "\n正文图和已采公开评论图已合并。"
    text += f"本轮核验了 {diagnostics['counts']['verified_posts']} 篇帖子的评论入口。"
    if args.max_images is not None:
        text += f"本轮评论采样最多处理 {args.max_images} 张不同图片。"
    (args.out / "shadow.md").write_text(text + "\n", encoding="utf-8")
    print(json.dumps({"coverage": summary["coverage"], "collection": diagnostics},
                     ensure_ascii=False), flush=True)


if __name__ == "__main__":
    main()
