"""Standalone public article-image OCR experiment. Run with python -m."""
from __future__ import annotations

import argparse
from collections import Counter
from contextlib import closing
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import sqlite3
from statistics import mean
import tempfile
import time

from .forum49_ocr_engine import CACHE_VERSION, PaddleWorker
from .forum49_ocr_sources import discover_images, download_image

SAMPLE_SIZE = 20


def elapsed(start: float) -> float:
    return round(time.monotonic() - start, 3)


def validate_image(path: Path) -> None:
    from PIL import Image

    with Image.open(path, formats=["JPEG", "PNG", "WEBP", "GIF", "BMP"]) as image:
        if image.width * image.height > 20_000_000:
            raise ValueError("decoded image exceeds 20 megapixels")
        image.verify()


def retry(operation, *, backoff=time.sleep):
    errors = []
    for attempt in (1, 2):
        started = time.monotonic()
        try:
            return operation(attempt), errors
        except Exception as exc:
            errors.append({"attempt": attempt, "seconds": elapsed(started),
                           "reason": f"{type(exc).__name__}: {exc}"[:800]})
        if attempt == 1:
            backoff(0.5)
    return None, errors


def collect_and_recognize(candidates, scratch, database, worker, *,
                          download=download_image, limit=SAMPLE_SIZE, retain_duplicate_sources=False):
    database.execute("""CREATE TABLE IF NOT EXISTS results (
        engine TEXT, sha256 TEXT, lines TEXT NOT NULL, PRIMARY KEY (engine, sha256)
    )""")
    rows, download_failures = [], []
    counts = Counter()
    seen_urls, seen_hashes = {}, set()
    path = scratch / "image"
    for candidate in candidates:
        url, source = candidate["image_url"], candidate["source_url"]
        if url in seen_urls:
            counts["duplicate_urls"] += 1
            if retain_duplicate_sources and seen_urls[url]:
                rows.append({**candidate, "sha256": seen_urls[url], "status": "duplicate", "lines": []})
            continue
        seen_urls[url] = None
        started = time.monotonic()

        def fetch(_):
            path.unlink(missing_ok=True)
            path.with_suffix(".ocr.png").unlink(missing_ok=True)
            download(url, source, path)
            validate_image(path)
            return hashlib.sha256(path.read_bytes()).hexdigest()

        digest, download_errors = retry(fetch)
        if digest is None:
            download_failures.append({**candidate, "elapsed_seconds": elapsed(started),
                                      "download_errors": download_errors})
            continue
        seen_urls[url] = digest
        if digest in seen_hashes:
            counts["duplicate_sha256"] += 1
            if retain_duplicate_sources:
                rows.append({**candidate, "sha256": digest, "status": "duplicate", "lines": []})
            continue
        seen_hashes.add(digest)
        download_seconds = elapsed(started)
        cached = database.execute("SELECT lines FROM results WHERE engine=? AND sha256=?",
                                  (CACHE_VERSION, digest)).fetchone()
        ocr_started = time.monotonic()

        def infer(attempt):
            lines = worker(path, attempt == 1)
            if not lines:
                raise ValueError("no_text_detected")
            return lines

        if cached is not None:
            lines, ocr_errors = json.loads(cached[0]), []
        else:
            lines, ocr_errors = retry(infer)
            if lines:
                with database:
                    database.execute("INSERT OR IGNORE INTO results VALUES (?, ?, ?)",
                                     (CACHE_VERSION, digest, json.dumps(lines, ensure_ascii=False)))

        rows.append({
            **candidate, "sha256": digest, "status": "recognized" if lines else "ocr_failed",
            "raw_text": "\n".join(line["text"] for line in lines or []),
            "lines": lines or [], "cache_hit": cached is not None,
            "mean_confidence": mean(line["confidence"] for line in lines) if lines else None,
            "download_seconds": download_seconds, "ocr_seconds": elapsed(ocr_started),
            "elapsed_seconds": elapsed(started), "download_errors": download_errors,
            "ocr_errors": ocr_errors,
            "failure_reason": ocr_errors[-1]["reason"] if not lines else None,
        })
        if limit is not None and len(seen_hashes) >= limit:
            break
    return rows, download_failures, counts


def save_report(out: Path, report: dict) -> None:
    (out / "report.json").write_text(
        json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    rate = report["success_rate"]
    rate_text = f"{rate:.1%}" if rate is not None else "无有效样本"
    lines = [
        "# 49 论坛公开正文图片 OCR 测试",
        "",
        f"- 样本覆盖：{report['sampled']}/{report['requested']}",
        f"- 有文字输出：{report['recognized']}/{report['sampled']}（{rate_text}）",
        f"- 图像下载或校验失败：{len(report['download_failures'])}",
        f"- SHA-256 重复：{report['counts'].get('duplicate_sha256', 0)}",
        f"- OCR 缓存命中：{report['cache_hits']}",
        f"- 总耗时：{report['elapsed_seconds']:.3f} 秒",
        "- 成功率按返回非空文字的样本计。置信度是模型分数，字符准确率需要人工标注。",
    ]
    if report["discovery_error"]:
        lines.append(f"- 采样错误：{report['discovery_error']}")
    lines.extend(["", "逐图原文、来源、逐行置信度、耗时及重试详情见 report.json。", ""])
    for number, row in enumerate(report["images"], 1):
        confidence = row["mean_confidence"]
        metric = f"{confidence:.1%}" if confidence is not None else row["failure_reason"]
        lines.append(f"{number}. [帖子]({row['source_url']}) · {row['elapsed_seconds']:.3f} 秒 · {metric}")
    (out / "report.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, default=Path("forum49-ocr-test-out"))
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    started = time.monotonic()
    diagnostics = {}
    rows, failures, counts = [], [], Counter()
    discovery_error = None
    sources = discover_images(deadline=started + 360, diagnostics=diagnostics)

    def guarded_sources():
        nonlocal discovery_error
        try:
            yield from sources
        except Exception as exc:
            discovery_error = f"{type(exc).__name__}: {exc}"[:500]

    with closing(sources), tempfile.TemporaryDirectory() as tmp, PaddleWorker() as worker:
        with closing(sqlite3.connect(args.out / "recognition-cache.sqlite3")) as db:
            rows, failures, counts = collect_and_recognize(guarded_sources(), Path(tmp), db, worker)
    recognized = sum(row["status"] == "recognized" for row in rows)
    report = {
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "test_only": True, "used_for_freeze": False, "engine": CACHE_VERSION,
        "requested": SAMPLE_SIZE, "sampled": len(rows), "recognized": recognized,
        "success_rate": recognized / len(rows) if rows else None,
        "cache_hits": sum(row["cache_hit"] for row in rows),
        "elapsed_seconds": elapsed(started), "counts": counts, **diagnostics,
        "discovery_error": discovery_error, "download_failures": failures, "images": rows,
    }
    save_report(args.out, report)
    print(f"49 OCR: {recognized} recognized, {len(rows)}/{SAMPLE_SIZE} sampled, "
          f"{len(failures)} failed downloads, {report['elapsed_seconds']}s", flush=True)
    return 0 if len(rows) == SAMPLE_SIZE and recognized else 1


if __name__ == "__main__":
    raise SystemExit(main())
