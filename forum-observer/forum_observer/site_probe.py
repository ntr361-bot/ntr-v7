"""Bounded, read-only 118图库 test using the existing Scrapling assist parser.

No import of scoring, schedules, settlement, archive or publishing modules.
Only the site's observed navigation templates are decoded; scripts are not eval'd.
"""
from __future__ import annotations

import argparse
import ipaddress
import json
import re
import time
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import urljoin, urlsplit

from .scrapling_assist import inspect_listing, snapshot_selector, write_comparison


def public_url(url: str) -> str:
    parts = urlsplit(url)
    if parts.scheme not in {"https", "http"} or not parts.hostname or parts.username or parts.password:
        raise ValueError("Only public HTTP(S) URLs are allowed")
    host = parts.hostname.encode("idna").decode("ascii")
    if host.lower() == "localhost" or host.lower().endswith((".local", ".localhost")):
        raise ValueError("Local destination rejected")
    try:
        address = ipaddress.ip_address(host)
    except ValueError:
        address = None
    if address is not None and not address.is_global:
        raise ValueError("Non-public destination rejected")
    return parts._replace(netloc=host + (f":{parts.port}" if parts.port else ""),
                          path=parts.path or "/").geturl()


def navigation_targets(html: str, base_url: str) -> list[str]:
    """Recognize the observed binary/static/XOR gateway formats, without eval."""
    candidates = []
    scripts = re.findall(r"<script\b[^>]*>(.*?)</script>", html, re.S | re.I)
    for script in scripts:
        script = re.sub(r"(?m)^\s*//[^\n]*", "", script)
        script = re.sub(r"/\*.*?\*/", "", script, flags=re.S)
        # Binary destination arrays on the supplied entrance and /four page.
        if "urlArray" in script and "String.fromCharCode" in script:
            for bits in re.findall(r"['\"]([01]{80,8192})['\"]", script):
                if len(bits) % 8 == 0:
                    candidates.append("".join(chr(int(bits[i:i+8], 2)) for i in range(0, len(bits), 8)))
        # Only follow arrays explicitly consumed by the observed navigation code.
        if "hostArr" in script and "window.location.href = host" in script:
            array = re.search(r"var\s+hostArr\s*=\s*\[(.*?)\]", script, re.S)
            if array:
                candidates.extend(re.findall(r"['\"](https?://[^'\"]+)['\"]", array[1]))
        for pattern in (
            r"window\.location\.href\s*=\s*['\"]([^'\"]+)['\"]",
            r"const\s+next\s*=\s*['\"]([^'\"]+)['\"]",
            r"var\s+next\s*=\s*\[\s*['\"]([^'\"]+)['\"]",
        ):
            candidates.extend(re.findall(pattern, script))
        # 118d5's '继续访问网页版' handler: decode its own prefix/suffix.
        key = re.search(r'var\s+enkey\s*=\s*[\'"]([^\'"\n]+)[\'"]', script)
        if key and "l_xl_3" in script and "randomString(7)" in script:
            def decode(name):
                value = re.search(r"const\s+" + name + r"\s*=\s*d\('([0-9a-f]+)'\)", script)
                if not value:
                    return ""
                data = bytes.fromhex(value[1])
                return "".join(chr(n ^ ord(key[1][i % len(key[1])])) for i, n in enumerate(data))
            head, foot = decode("uHead"), decode("uFoot")
            if head and foot:
                candidates.append(head + "probe01" + foot)
    targets = []
    for value in candidates:
        if value.startswith(("https://", "http://")) and not urlsplit(value).hostname:
            continue
        try:
            target = public_url(urljoin(base_url, value))
        except ValueError:
            continue
        if target not in targets:
            targets.append(target)
    return targets


def parse_snapshot(html: str, url: str, out: Path) -> dict:
    document = snapshot_selector(html, url, out / "selectors.sqlite3")
    text = document.get_all_text(ignore_tags=("script", "style", "noscript"))
    title = document.css("title::text").get() or ""
    scripts = document.css("script[src]::attr(src)").getall()
    links = [{"url": urljoin(url, a.attrib["href"]), "text": a.get_all_text().strip()}
             for a in document.css("a[href]") if a.attrib["href"].startswith(("http", "/", "#"))]
    images = [{"url": urljoin(url, node.attrib.get("src") or node.attrib.get("data-src", "")),
               "alt": node.attrib.get("alt", "")}
              for node in document.css("img[src], img[data-src]")]
    navigation = bool(navigation_targets(html, url)) or "继续访问网页版" in text
    unavailable = any(s in text for s in ("Site Unavailable", "Unable to access this site"))
    status = ("unavailable" if unavailable else "navigation_only" if navigation else
              "dynamic_render_required" if len(text.strip()) < 50 and scripts else "snapshot_captured")
    return {"status": status, "title": title, "text": text, "links": links,
            "image_candidates": images, "script_urls": [urljoin(url, s) for s in scripts],
            "forum_diagnostic": inspect_listing(html, url, "", out / "forum-selectors.sqlite3"),
            # Generic DOM content and images have not been verified as current-issue material.
            "verified_material_count": 0}


def collect(url: str, out: Path, *, max_hops=12, timeout=15, renderer="http", fetcher=None) -> dict:
    out.mkdir(parents=True, exist_ok=True)
    if (out / "report.json").exists():
        raise ValueError("Use a new output directory for each test")
    report = {"mode": "isolated_site_test", "parser": "existing_scrapling_assist",
              "started_at": datetime.now(timezone.utc).isoformat(), "renderer": renderer,
              "input_url": url, "status": "running", "hops": [], "verified_material_count": 0}
    browser = context = manager = None
    try:
        if renderer == "http" and fetcher is None:
            from scrapling.fetchers import Fetcher
            fetcher = Fetcher.get
        if renderer == "browser":
            from playwright.sync_api import sync_playwright
            manager = sync_playwright().start()
            browser = manager.chromium.launch(headless=True)
            context = browser.new_context(accept_downloads=False)
            page = context.new_page()
        current = public_url(url)
        visited = set()
        for index in range(max_hops):
            if current in visited:
                report["status"] = "navigation_loop"
                break
            visited.add(current)
            hop = {"requested_url": current, "started_at": datetime.now(timezone.utc).isoformat()}
            report["hops"].append(hop)
            write_comparison(out / "report.json", report)
            started = time.monotonic()
            if renderer == "browser":
                response = page.goto(current, wait_until="domcontentloaded", timeout=timeout * 1000)
                # Bounded wait for the site's SPA or automatic gateway navigation.
                page.wait_for_timeout(3000)
                html, final_url = page.content(), page.url
                hop["http_status"] = response.status if response else None
            else:
                response = fetcher(current, timeout=timeout, retries=0)
                html, final_url = str(response.html_content), str(response.url)
                hop["http_status"] = response.status
            public_url(final_url)
            hop.update({"url": final_url, "elapsed_seconds": round(time.monotonic()-started, 2)})
            (out / f"page-{index:02d}.html").write_text(html, encoding="utf-8")
            parsed = parse_snapshot(html, final_url, out)
            hop.update({"title": parsed["title"], "page_status": parsed["status"]})
            (out / f"page-{index:02d}.json").write_text(json.dumps(parsed, ensure_ascii=False, indent=2), encoding="utf-8")
            report["final_url"] = final_url
            report["last_snapshot"] = f"page-{index:02d}.json"
            targets = navigation_targets(html, final_url)
            hop["navigation_targets"] = targets
            if hop["http_status"] and hop["http_status"] >= 400:
                report["status"] = "http_error"
                break
            if not targets:
                report["status"] = parsed["status"]
                break
            current = targets[0]
            time.sleep(0.5)
        else:
            report["status"] = "hop_limit_reached"
    except Exception as exc:
        report.update(status="error", error_type=type(exc).__name__, error=str(exc)[:1000])
    finally:
        for resource in (context, browser):
            if resource:
                resource.close()
        if manager:
            manager.stop()
        report["finished_at"] = datetime.now(timezone.utc).isoformat()
        write_comparison(out / "report.json", report)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("url")
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--renderer", choices=("http", "browser"), default="http")
    parser.add_argument("--max-hops", type=int, choices=range(1, 16), default=12)
    parser.add_argument("--timeout", type=int, choices=range(1, 31), default=15)
    args = parser.parse_args()
    report = collect(args.url, args.out, max_hops=args.max_hops, timeout=args.timeout, renderer=args.renderer)
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 1 if report["status"] in {"error", "http_error", "unavailable", "navigation_loop", "hop_limit_reached"} else 0


if __name__ == "__main__":
    raise SystemExit(main())
