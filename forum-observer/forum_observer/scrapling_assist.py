"""Read-only Scrapling comparison for the live forum listing parser.

This module never supplies records to scoring or freezing. It independently
parses the current rendered DOM and writes counts/differences so collector
misses can be investigated before any parser is trusted for production data.
"""
from __future__ import annotations

import json
import re
from datetime import datetime
from pathlib import Path
from urllib.parse import parse_qs, urljoin, urlsplit


_IDENTIFIER = "forum-list-container-v1"
_ZH_SPACE = re.compile(r"\s+")
_FULL_ISSUE = re.compile(r"20\d{5}")
_SHORT_ISSUE = re.compile(r"(?<!\d)(\d{3})(?!\d)")


def _text(node, selector: str) -> str:
    values = node.css(f"{selector}::text").getall()
    return " ".join(" ".join(str(value).split()) for value in values if str(value).strip())


def _issue(value: str) -> str:
    value = " ".join(str(value).split())
    full = _FULL_ISSUE.search(value)
    if full:
        return full.group(0)
    short = _SHORT_ISSUE.search(value)
    return f"{datetime.now().year}{short.group(1)}" if short else value


def _verified_detail_href(card, base_url: str) -> str:
    origin = urlsplit(base_url)
    for href in card.css("a[href]::attr(href)").getall():
        candidate = urlsplit(urljoin(base_url, str(href).strip()))
        if candidate.scheme not in {"http", "https"} or candidate.netloc != origin.netloc:
            continue
        if "/corpusdetail" not in candidate.fragment:
            continue
        route, _, query = candidate.fragment.partition("?")
        params = parse_qs(query)
        article_id = params.get("id", [""])[0]
        if route == "/corpusdetail" and article_id.isdigit() and int(article_id) > 0 and params.get("type") == ["3"]:
            return candidate.geturl()
    return ""


def _key(row: dict) -> tuple[str, str, str, str]:
    return tuple(" ".join(str(row.get(k, "")).split()) for k in
                 ("author", "issue", "published_at", "title"))


def snapshot_selector(html: str, url: str, storage_file: str | Path):
    """Shared Scrapling snapshot parser for isolated site diagnostics."""
    from scrapling.parser import Selector

    storage_path = Path(storage_file)
    storage_path.parent.mkdir(parents=True, exist_ok=True)
    return Selector(html, url=url, adaptive=True,
                    storage_args={"storage_file": str(storage_path)})


def inspect_listing(html: str, url: str, category: str, storage_file: str | Path) -> dict:
    """Parse a rendered feed snapshot with Scrapling and report comparison data."""
    try:
        from scrapling.parser import Selector
    except ImportError:
        return {"status": "scrapling_unavailable", "parser": "scrapling"}

    document = snapshot_selector(html, url, storage_file)
    selector_method = "css"
    container = document.css(".forum-list", identifier=_IDENTIFIER, auto_save=True)
    if not container:
        selector_method = "adaptive"
        container = document.css(
            ".forum-list", identifier=_IDENTIFIER, adaptive=True, percentage=40
        )
        if container:
            document.save(container[0], _IDENTIFIER)

    if not container:
        return {
            "status": "forum_container_not_found",
            "parser": "scrapling",
            "selector_method": selector_method,
            "visible_card_count": 0,
            "candidate_count": 0,
            "unique_authors": [],
            "verified_detail_link_count": 0,
        }

    rows = []
    for card in container[0].css("li"):
        kinds = card.css(".ntool .num::text").getall()
        row = {
            "author": _text(card, ".name"),
            "issue": _issue(_text(card, ".slabel")),
            "published_at": _text(card, ".time"),
            "title": _text(card, ".formtitle"),
            "category": " ".join(str(kinds[-1]).split()) if kinds else "",
            "detail_url": _verified_detail_href(card, url),
        }
        if not row["author"] or not row["issue"] or not row["published_at"]:
            continue
        if category and row["category"] != category:
            continue
        rows.append(row)

    return {
        "status": "ok",
        "parser": "scrapling",
        "selector_method": selector_method,
        "visible_card_count": len(container[0].css("li")),
        "candidate_count": len(rows),
        "unique_authors": sorted({row["author"] for row in rows}),
        "verified_detail_link_count": sum(bool(row["detail_url"]) for row in rows),
        "candidate_keys": [list(_key(row)) for row in rows],
    }


def compare_listing(html: str, url: str, category: str, playwright_rows: list[dict],
                    storage_file: str | Path) -> dict:
    """Compare Scrapling's visible-DOM parse with Playwright; never merge results."""
    probe = inspect_listing(html, url, category, storage_file)
    primary_keys = {_key(row) for row in playwright_rows}
    parsed_keys = {tuple(key) for key in probe.pop("candidate_keys", [])}
    extra = sorted(parsed_keys - primary_keys)
    primary_only = sorted(primary_keys - parsed_keys)
    probe.update({
        "playwright_row_count": len(playwright_rows),
        "scrapling_only_count": len(extra),
        "scrapling_only_authors": sorted({key[0] for key in extra}),
        "playwright_only_count": len(primary_only),
    })
    return probe


def write_comparison(report_file: str | Path, comparison: dict) -> None:
    """Atomically replace the sidecar diagnostic, without touching model reports."""
    path = Path(report_file)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(comparison, ensure_ascii=False, indent=2) + "\n",
                         encoding="utf-8")
    temporary.replace(path)
