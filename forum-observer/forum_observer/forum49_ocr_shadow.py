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
MIN_ZODIACS = 6
COUNTS = {"一": 1, "二": 2, "两": 2, "三": 3, "四": 4, "五": 5, "六": 6, "七": 7, "八": 8, "九": 9, "十": 10, "十一": 11, "十二": 12}
RECOMMENDATION = re.compile(r"(?<![\d一二三四五六七八九十])(十二|十一|十|九|八|七|六|1[0-2]|[6-9])肖(?:中特|王)?|必中肖|推荐(?:生肖|肖)?")
KILL = re.compile(r"🔪(?:([一二两三四五六七八九十1-9])(?:生)?肖)?|杀号|杀码|([一二两三四五六七八九十1-9])肖杀特|杀(?:掉|除)?([一二两三四五六七八九十1-9])(?:生)?肖|杀(?:特|生)?肖(?:好中)?")
BRACKETS = "【】[]〖〗「」『』()《》"
PICKS = re.compile(f"[{ZODIACS}](?:[、,·+／/]*[{ZODIACS}])*")
NON_RECOMMENDATION = re.compile(r"杀|杀码|前后|家野|四季|生肖对照|生肖表|图例")
DRAW_RESULT = re.compile(r"(?:开|开奖)[^\d]*?(?:0?[1-9]|[1-9]\d)(?!\d)")
SHELL = re.compile(r"历史帖子|历史推荐|开奖直播|导航|彩票|点击立即|期的号码")
VARIANTS = str.maketrans("龍馬雞豬鷄", "龙马鸡猪鸡")
SHA256 = re.compile(r"[a-f0-9]{64}")


def inspect_lines(lines: list, issue: str) -> tuple[list[str], list[dict]]:
    """Find period labels and candidate lines for review, without treating them as picks."""
    periods = set()
    candidates = []
    active = None
    pending = None
    anchor = -100
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
            pending = None
            if len(marks) != 1:
                active = None
                continue
            text = text[marks[0].end():]
        if active != issue or number - anchor > 8:
            continue
        text = re.sub(r"\s+", "", text)
        if not text.strip(BRACKETS + ":：·•●"):
            continue
        kill_label = KILL.search(text)
        label = kill_label or RECOMMENDATION.search(text)
        prior, pending = pending, None
        if SHELL.search(text) or DRAW_RESULT.search(text):
            continue
        if kill_label is not None and kill_label[0] in {"杀号", "杀码"}:
            continue
        if kill_label is None and NON_RECOMMENDATION.search(text):
            continue
        if label:
            mode = "kill" if kill_label else "recommend"
            raw_count = next((group for group in label.groups() if group), None)
            expected = int(raw_count) if raw_count and raw_count.isdecimal() else COUNTS.get(raw_count)
            payload = text[label.end():]
        elif prior is not None and number - prior[0] <= 2:
            _, expected, mode = prior
            payload = text
        else:
            continue
        payload = re.split(r"开[奖：:]?|[+＋]\d", payload, maxsplit=1)[0].strip(BRACKETS + ":：·•●")
        if not payload and label:
            pending = (number, expected, mode)
            continue
        if not PICKS.fullmatch(payload):
            continue
        picks = list(dict.fromkeys(symbol for symbol in payload if symbol in ZODIACS))
        if expected is not None and len(picks) != expected:
            continue
        excluded = picks if mode == "kill" else []
        recommended = [symbol for symbol in ZODIACS if symbol not in excluded] if excluded else picks
        if len(recommended) < MIN_ZODIACS:
            continue
        candidates.append({
            "line": number, "period_line": anchor, "text": line["text"],
            "confidence": line.get("confidence"), "mode": mode,
            "zodiacs": recommended, "excluded_zodiacs": excluded,
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
        group = groups.setdefault(digest, {
            "source_urls": set(), "image_urls": set(), "ocr_rows": [], "comment_refs": set(),
        })
        group["source_urls"].add(row["source_url"])
        group["image_urls"].add(row["image_url"])
        if row.get("comment_id"):
            group["comment_refs"].add((row["source_url"], str(row["comment_id"]), row["image_url"]))
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
            "comment_sources": [{"source_url": post, "comment_id": cid, "image_url": url}
                                for post, cid, url in sorted(group["comment_refs"])],
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
        "images_with_kill_candidates": sum(any(x["mode"] == "kill" for x in row["candidates"])
                                           for row in evidence),
        "failed_downloads": len(report.get("download_failures", [])),
    }
    return {
        "schema_version": 3, "min_recommended_zodiacs": MIN_ZODIACS, "issue": issue, "mode": "forum49_image_shadow",
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
        "images_with_review_candidates": "有至少 6 个明确推荐生肖的候选图片",
        "images_with_kill_candidates": "有明确杀肖候选的图片（包含在上项）",
        "failed_downloads": "下载失败链接",
    }
    output = [
        f"# 49 论坛 {result['issue']} 期图片影子统计", "",
        f"北京时间 {datetime.now(ZoneInfo('Asia/Shanghai')):%Y-%m-%d %H:%M:%S}。",
        "图片须有目标期号，并明确推荐至少 6 个不同生肖。明确杀 1 至 6 肖时，按排除后剩余的生肖记录。影子统计保留同一作者的多份资料，图片按 SHA-256 去重。候选供人工复核，统计不参与正式预测。", "",
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
        for ref in row["comment_sources"]:
            output.append(f"  - 评论 {ref['comment_id']} · [所在帖]({ref['source_url']})")
        for candidate in row["candidates"]:
            text = candidate["text"].replace("\n", " ").replace("\r", " ")
            safe = text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
            safe = re.sub(r"([\\`*_\[\]])", r"\\\1", safe)
            suffix = ""
            if candidate["mode"] == "kill":
                suffix = f" · 排除 {''.join(candidate['excluded_zodiacs'])}，余 {len(candidate['zodiacs'])} 肖"
            output.append(f"  - 第 {candidate['line']} 行 · {safe}{suffix}")
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
