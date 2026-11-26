"""Summarize an existing 49 forum article OCR report without collecting or scoring."""
from __future__ import annotations

import argparse
from collections import Counter
from datetime import datetime
import json
from pathlib import Path
import re
import unicodedata
from zoneinfo import ZoneInfo

PERIOD = re.compile(r"(?<!\d)(?:(20\d{2})\s*年?\s*)?(?:第\s*)?(\d\s*\d\s*\d)\s*期")
ZODIACS = "鼠牛虎兔龙蛇马羊猴鸡狗猪"
ANIMALS = re.compile(f"[{ZODIACS}]{{2,}}")
PLAY = re.compile(r"[一二两三四五六七八九十单双1-9]肖|肖中特|杀.{0,3}肖|特肖|[前后家野]肖|平特|特码|围特|平码")
SHELL = re.compile(r"历史帖子|历史推荐|开奖直播|导航|彩票|点击立即|期的号码")
VARIANTS = str.maketrans("龍馬雞豬鷄", "龙马鸡猪鸡")
SHA256 = re.compile(r"[a-f0-9]{64}")


def inspect_lines(lines: list, issue: str) -> tuple[list[str], list[dict]]:
    """Find period labels and candidate lines for review, without treating them as picks."""
    periods = set()
    candidates = []
    active = None
    anchor = recent_play = -100
    for number, line in enumerate(lines, 1):
        text = unicodedata.normalize("NFKC", line["text"]).translate(VARIANTS)
        marks = list(PERIOD.finditer(text))
        if marks:
            active = None
            for mark in marks:
                year, short = mark.groups()
                active = (year or issue[:4]) + re.sub(r"\s+", "", short)
                periods.add(active)
            anchor = number
            recent_play = -100
            if len(marks) != 1:
                active = None
                continue
            text = text[marks[0].end():]
        if active != issue or number - anchor > 8 or not text.strip() or SHELL.search(text):
            continue
        if PLAY.search(text):
            recent_play = number
        if number - recent_play > 2:
            continue
        text = re.sub(r"\s+", "", text)
        single = re.search("杀|一肖|平特", text) and any(x in text for x in ZODIACS)
        if ANIMALS.search(text) or single:
            candidates.append({
                "line": number, "period_line": anchor, "text": line["text"],
                "confidence": line.get("confidence"),
            })
    return sorted(periods), candidates


def summarize(report: dict, issue: str) -> dict:
    if not re.fullmatch(r"20\d{2}00[1-9]|20\d{2}0[1-9]\d|20\d{2}[1-9]\d{2}", issue):
        raise ValueError("issue must be a seven-digit YYYYNNN period")
    if report.get("test_only") is not True or report.get("used_for_freeze") is not False:
        raise ValueError("input must be an independent OCR test report")
    if report.get("issue") is not None and str(report["issue"]) != issue:
        raise ValueError("input report issue does not match the requested issue")
    groups = {}
    for row in report["images"]:
        digest = row["sha256"]
        if not SHA256.fullmatch(digest):
            raise ValueError("invalid image SHA-256")
        group = groups.setdefault(digest, {"source_urls": set(), "image_urls": set(), "ocr_rows": []})
        group["source_urls"].add(row["source_url"])
        group["image_urls"].add(row["image_url"])
        if row.get("lines"):
            group["ocr_rows"].append(row["lines"])
    evidence = []
    for digest, group in groups.items():
        lines = max(group["ocr_rows"], key=len, default=[])
        periods, candidates = inspect_lines(lines, issue)
        state = ("no_text" if not lines else "target_period" if issue in periods
                 else "other_period" if periods else "no_period")
        evidence.append({
            "sha256": digest, "source_urls": sorted(group["source_urls"]),
            "image_urls": sorted(group["image_urls"]), "state": state,
            "image_period_markers": periods, "ocr_line_count": len(lines),
            "candidates": candidates,
        })
    states = Counter(row["state"] for row in evidence)
    # Historic imports may supply listing coverage. A sampled OCR report leaves it unset.
    figures = {
        "issue_post_coverage": report.get("issue_post_coverage"),
        "audited_image_posts": len({url for row in evidence for url in row["source_urls"]}),
        "unique_images": len(evidence),
        "images_with_ocr_text": len(evidence) - states["no_text"],
        "images_showing_target_issue": states["target_period"],
        "images_showing_other_issues_only": states["other_period"],
        "images_without_period_marker": states["no_period"],
        "images_without_ocr_text": states["no_text"],
        "images_with_review_candidates": sum(bool(row["candidates"]) for row in evidence),
        "failed_downloads": len(report.get("download_failures", [])),
    }
    return {
        "schema_version": 1, "issue": issue, "mode": "forum49_image_shadow",
        "test_only": True, "used_for_freeze": False, "source_generated_at": report.get("generated_at"),
        "coverage": figures, "images": evidence,
    }


def markdown(result: dict) -> str:
    labels = {
        "issue_post_coverage": "来源报告覆盖帖子",
        "audited_image_posts": "有已采图片的帖子",
        "unique_images": "SHA-256 去重后的图片",
        "images_with_ocr_text": "有 OCR 文字的图片",
        "images_showing_target_issue": "有目标期号的图片",
        "images_showing_other_issues_only": "仅有其他期号的图片",
        "images_without_period_marker": "有文字但无期号的图片",
        "images_without_ocr_text": "无 OCR 文字的图片",
        "images_with_review_candidates": "有待复核候选的图片",
        "failed_downloads": "下载失败链接",
    }
    output = [
        f"# 49 论坛 {result['issue']} 期图片影子统计", "",
        f"北京时间 {datetime.now(ZoneInfo('Asia/Shanghai')):%Y-%m-%d %H:%M:%S}。",
        "候选供人工复核。图例、断行和 OCR 错字可能影响候选，统计不参与正式预测。", "",
        "| 项目 | 数量 |", "|---|---:|",
    ]
    for name, value in result["coverage"].items():
        if value is not None:
            output.append(f"| {labels[name]} | {value} |")
    output += ["", "## 待复核图片", ""]
    for row in result["images"]:
        if not row["candidates"]:
            continue
        urls = [f"[来源帖 {i}]({url})" for i, url in enumerate(row["source_urls"], 1)]
        urls += [f"[原图 {i}]({url})" for i, url in enumerate(row["image_urls"], 1)]
        output.append("- " + " · ".join(urls))
        for candidate in row["candidates"]:
            text = candidate["text"].replace("\n", " ").replace("\r", " ")
            safe = text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
            safe = re.sub(r"([\\`*_\[\]])", r"\\\1", safe)
            output.append(f"  - 第 {candidate['line']} 行 · {safe}")
    return "\n".join(output) + "\n"


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--issue", required=True)
    parser.add_argument("--input", type=Path, required=True, help="Existing OCR report JSON")
    parser.add_argument("--out", type=Path, required=True, help="New output directory for this run")
    args = parser.parse_args()
    report = json.loads(args.input.read_text(encoding="utf-8"))
    result = summarize(report, args.issue)
    args.out.mkdir(parents=True, exist_ok=False)
    (args.out / "shadow.json").write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    (args.out / "shadow.md").write_text(markdown(result), encoding="utf-8")
    print(json.dumps(result["coverage"], ensure_ascii=False))


if __name__ == "__main__":
    main()
