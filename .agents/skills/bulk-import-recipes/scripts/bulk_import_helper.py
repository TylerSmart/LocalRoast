#!/usr/bin/env python3
"""CLI helper for LocalRoast bulk recipe import workflows:
  - plan-wave: Topologically orders queued ingestion jobs (sub-recipes first) into batches.
  - submit-batch: Sequentially submits extracted batch JSON to /ingestion/jobs/{id}/submit-extraction.
  - submit-one: Submits a repaired single recipe JSON to /ingestion/jobs/{id}/submit-extraction.
  - audit: Runs deep culinary quality & catalog audit across the wave and prints a summary.
"""

from __future__ import annotations

import argparse
from collections import defaultdict, deque
import json
import os
from pathlib import Path
import re
import statistics
import subprocess
import sys
import unicodedata
import urllib.error
import urllib.parse
import urllib.request
from typing import Any

SKILL_DIR = Path(__file__).resolve().parent.parent
REPO_ROOT = SKILL_DIR.parent.parent.parent
STATE_DIR = SKILL_DIR / ".state"
TOKEN_CACHE = STATE_DIR / ".runner_token.json"

DEFAULT_EMAIL = os.environ.get("LOCALROAST_RUNNER_EMAIL", "dev-runner@localroast.local")
DEFAULT_USER = os.environ.get("LOCALROAST_RUNNER_USER", "devrunner")
DEFAULT_PASS = os.environ.get("LOCALROAST_RUNNER_PASS", "LocalRoast_DevRunner_2026!")

_CONN: dict[str, Any] = {
    "api_base": "http://localhost:5041/api",
    "explicit_token": None,
    "mcp_config": None,
    "verified_token": None,
}

TOKEN_REGEX = re.compile(r"\|\|(\{.*?\})\|\|", re.S)

TARGET_BENCHMARK_PATTERNS = [
    ("Easy-Peel Hard-Cooked Eggs", ["easy-peel hard-cooked eggs"]),
    ("Bacon and Chive Deviled Eggs", ["bacon and chive deviled eggs"]),
    ("Creamy Herbed Spinach Dip", ["creamy herbed spinach dip"]),
    ("Profiteroles", ["profiteroles"]),
    ("Tamatya-Kandyachi Koshimbir", ["tamatya-kandyachi koshimbir"]),
    ("Hearty Lentil Soup", ["hearty lentil soup"]),
    (
        "Hearty Spanish-Style Lentil and Chorizo Soup",
        ["hearty spanish-style lentil and chorizo soup"],
    ),
    ("Carne Deshebrada", ["carne deshebrada"]),
    ("Choux au Craquelin", ["choux au craquelin"]),
    (
        "Hearty Ham and Split Pea Soup with Potatoes",
        ["hearty ham and split pea soup with potatoes"],
    ),
    (
        "Stuffed Chicken Cutlets with Ham and Cheddar",
        ["stuffed chicken cutlets with ham and cheddar"],
    ),
    ("Chicken and Dumplings", ["chicken and dumplings"]),
    ("Lighter Chicken and Dumplings", ["lighter chicken and dumplings"]),
    (
        "Chewy, Fudgy Triple-Chocolate Brownies",
        ["chewy, fudgy triple-chocolate brownies"],
    ),
    (
        "Baharat-Spiced Beef Topping for Hummus",
        ["baharat-spiced beef topping for hummus"],
    ),
    ("Albondigas en Salsa de Almendras", ["albondigas en salsa de almendras"]),
]

PREVIOUSLY_TESTED_SUBSTRINGS = {
    "classic guacamole",
    "chunky guacamole",
    "ultracreamy hummus",
    "easy-peel hard-cooked eggs",
    "curry deviled eggs",
    "garlic mayonnaise",
    "spanish tortilla",
    "one-minute salsa",
    "cheesy nachos",
    "vegan mayonnaise",
    "vegan ranch dressing",
    "buffalo cauliflower bites",
    "slow-roasted bone-in pork rib roast",
    "port wine",
    "rustic free-form apple tart",
    "free-form summer fruit tart",
    "easier roast turkey",
    "pan-seared brined salmon",
    "tourte aux pommes de terre",
    "grilled chicken souvlaki",
    "pizza bianca",
    "new england clambake",
    "authentic baguettes",
    "negimaki",
    "spinach and ricotta gnudi",
    "glazed salmon",
    "boston baked beans",
}

RECIPE_REF_LINE_CUE_RE = re.compile(
    r"\b(?:recipe|recipes|follows|batch|batches|servings?|see\s+(?:this|facing)\s+page|page\s+\d+)\b",
    re.I,
)
RAW_GROCERY_SEE_PAGE_RE = re.compile(
    r"\(\s*see\s+page\s+\d+\s*\)\s*$",
    re.I,
)
MISSING_SUBRECIPE_ISSUE_RE = re.compile(
    r"(?:is\s+not\s+published\s+yet\s+and\s+could\s+not\s+be\s+linked|references?\s+sub[- ]?recipe|is\s+missing\s+a\s+referenced\s+recipe\s+title)",
    re.I,
)

DISTRIBUTIVE_RE = re.compile(
    r"\b(?:each|apiece|per\s+(?:serving|person|piece|slice|toast|portion|plate|dish|cake|muffin|cookie|taco|bowl|roll|bun|burger|sandwich|wrap|tortilla|pita|skewer|cutlet|fillet|steak|chop|breast|thigh|half|halves|egg|tart|biscuit|cut))\b",
    re.I,
)
AT_A_TIME_RE = re.compile(
    r"^\s*(?:at\s+a\s+time\b|in\s+\S+\s+increments?|increments?\b)", re.I
)
FALSE_FIXED_FOOD_RE = re.compile(
    r"\b(?:water|broth|stock|milk|cream|wine|vinegar|juice|oil|butter|fat|drippings|brine|marinade|sauce|glaze)\b",
    re.I,
)
VESSEL_RE = re.compile(
    r"\b(?:pan|pot|skillet|pitcher|measure|measuring\s+(?:cup|spoon)|ladle|scoop|bowl|dish|jar|container|mold|tin|ramekin|starter|kettle)\b",
    re.I,
)
YIELD_CHECK_RE = re.compile(r"\b(?:should\s+have|yields?|yielding|about)\b", re.I)
UNTOK_MEASURE_RE = re.compile(
    r"\b\d+(?:\s+\d+/\d+|/\d+|\.\d+)?\s*(?:tsp|tbsp|teaspoons?|tablespoons?|cups?|oz|ounces?|lbs?|pounds?|quarts?|qt|pints?|grams?|ml)\b",
    re.I,
)
UNTOK_TEMP_RE = re.compile(r"\b\d{3}\s*(?:degrees?|°\s*[FC])\b", re.I)
ROTATE_RE = re.compile(
    r"\b(?:rotate|turn|angle)\b[^.]{0,35}\b(?:90|180|270|360)\s*degrees?\b", re.I
)
UNTOK_TIMER_RE = re.compile(
    r"\b\d+\s*(?:to\s+\d+\s*)?(?:seconds?|minutes?|hours?|days?)\b",
    re.I,
)
UNTOK_DIM_RE = re.compile(r"\b\d+(?:\s+\d+/\d+|/\d+|\.\d+)?\s*-inch\b", re.I)
PLURAL_NOUN_HINTS = {
    "leaves",
    "eggs",
    "cloves",
    "slices",
    "chiles",
    "peppers",
    "tomatoes",
    "potatoes",
    "onions",
    "carrots",
    "scallions",
    "shallots",
    "mushrooms",
    "berries",
    "anchovies",
    "olives",
    "capers",
    "rolls",
    "buns",
    "tortillas",
    "fillets",
    "breasts",
    "thighs",
    "steaks",
    "chops",
    "shrimp",
    "scallops",
    "sheets",
    "sprigs",
    "stalks",
}
UNIT_WORD_TAILS = {
    "cup",
    "cups",
    "tbsp",
    "tablespoon",
    "tablespoons",
    "tsp",
    "teaspoon",
    "teaspoons",
    "oz",
    "ounce",
    "ounces",
    "lb",
    "pound",
    "pounds",
    "g",
    "gram",
    "grams",
    "ml",
    "l",
    "qt",
    "quart",
    "quarts",
    "pt",
    "pint",
    "pints",
}
STOP_HEADS = {
    "water",
    "ice",
    "salt",
    "pepper",
    "liquid",
    "mixture",
    "drippings",
    "fat",
    "oil",
    "butter",
    "sugar",
    "flour",
}


def normalize_api_base(raw_url: str) -> str:
    u = (raw_url or "").strip().rstrip("/")
    if not u:
        return "http://localhost:5041/api"
    lower = u.lower()
    if lower.endswith("/api/mcp"):
        u = u[:-4]
    elif lower.endswith("/mcp"):
        u = u[:-4].rstrip("/") + "/api"
    elif not lower.endswith("/api"):
        u = u + "/api"
    return u


def _strip_bearer(val: str | None) -> str | None:
    if not val or not isinstance(val, str):
        return None
    cleaned = re.sub(r"^bearer\s+", "", val.strip(), flags=re.I).strip()
    return cleaned or None


def _extract_from_server_entry(entry: dict[str, Any]) -> tuple[str | None, str | None]:
    raw_url: str | None = None
    for k in ("url", "serverUrl", "httpUrl", "endpoint"):
        v = entry.get(k)
        if isinstance(v, str) and v.strip():
            raw_url = v.strip()
            break

    args_list = entry.get("args") if isinstance(entry.get("args"), list) else []
    if not raw_url:
        for a in args_list:
            if isinstance(a, str) and re.match(r"^https?://", a.strip(), re.I):
                raw_url = a.strip()
                break

    tok: str | None = None
    headers = entry.get("headers") if isinstance(entry.get("headers"), dict) else {}
    for hk, hv in headers.items():
        if str(hk).lower() == "authorization" and isinstance(hv, str):
            tok = _strip_bearer(hv)
            if tok:
                break
    if not tok:
        for k in ("bearerToken", "token", "apiKey"):
            v = entry.get(k)
            if isinstance(v, str):
                tok = _strip_bearer(v)
                if tok:
                    break
    if not tok and args_list:
        for idx, a in enumerate(args_list):
            if not isinstance(a, str):
                continue
            header_val = None
            if a == "--header" and idx + 1 < len(args_list) and isinstance(args_list[idx + 1], str):
                header_val = args_list[idx + 1]
            elif a.startswith("--header="):
                header_val = a[len("--header=") :]
            elif a.lower().startswith("authorization:"):
                header_val = a
            if header_val:
                m = re.match(r"^authorization\s*:\s*(.+)$", header_val.strip(), re.I)
                if m:
                    tok = _strip_bearer(m.group(1))
                    if tok:
                        break
    return raw_url, tok


def parse_mcp_config(config_path: str) -> tuple[str | None, str | None]:
    raw_text = Path(config_path).read_text(encoding="utf-8")
    data = json.loads(raw_text)
    if not isinstance(data, dict):
        raise ValueError(f"Invalid MCP config JSON in '{config_path}'.")

    servers = data.get("mcpServers") or data.get("servers")
    if isinstance(servers, dict) and servers:
        candidates: list[tuple[int, str, dict[str, Any]]] = []
        for name, entry in servers.items():
            if not isinstance(entry, dict):
                continue
            url_cand, tok_cand = _extract_from_server_entry(entry)
            if not url_cand and not tok_cand:
                continue
            score = 0
            if "localroast" in str(name).lower():
                score += 10
            if url_cand and "localroast" in url_cand.lower():
                score += 8
            if tok_cand and tok_cand.startswith("lr_pat_"):
                score += 5
            if url_cand and url_cand.lower().rstrip("/").endswith("/mcp"):
                score += 2
            candidates.append((score, str(name), entry))

        if candidates:
            candidates.sort(key=lambda x: -x[0])
            return _extract_from_server_entry(candidates[0][2])

    return _extract_from_server_entry(data)


def configure_connection(args: argparse.Namespace) -> None:
    mcp_cfg = getattr(args, "mcp_config", None) or os.environ.get("LOCALROAST_MCP_CONFIG")
    cli_url = getattr(args, "url", None) or os.environ.get("LOCALROAST_API")
    cli_tok = _strip_bearer(getattr(args, "token", None) or os.environ.get("LOCALROAST_TOKEN"))

    mcp_url: str | None = None
    mcp_tok: str | None = None
    if mcp_cfg:
        mcp_url, mcp_tok = parse_mcp_config(mcp_cfg)

    resolved_url = cli_url or mcp_url or "http://localhost:5041"
    resolved_tok = cli_tok or mcp_tok
    if mcp_cfg and not resolved_url:
        raise ValueError(f"Could not find server URL in MCP config '{mcp_cfg}'.")

    _CONN["api_base"] = normalize_api_base(resolved_url)
    _CONN["explicit_token"] = resolved_tok
    _CONN["mcp_config"] = mcp_cfg
    _CONN["verified_token"] = None


def http_json(
    method: str,
    path: str,
    token: str | None = None,
    body: Any = None,
    timeout: int = 60,
) -> Any:
    url = f"{_CONN['api_base']}{path}"
    data = json.dumps(body).encode("utf-8") if body is not None else None
    headers = {"Accept": "application/json"}
    if data is not None:
        headers["Content-Type"] = "application/json"
    if token:
        headers["Authorization"] = f"Bearer {token}"

    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            raw = resp.read().decode("utf-8")
            return json.loads(raw) if raw.strip() else None
    except urllib.error.HTTPError as exc:
        err_body = exc.read().decode("utf-8", errors="replace")
        raise RuntimeError(f"{method} {url} -> HTTP {exc.code}: {err_body}") from exc


def _is_localhost_api(api_base: str) -> bool:
    host = (urllib.parse.urlparse(api_base).hostname or "").lower()
    return host in ("localhost", "127.0.0.1", "::1")


def get_token(force_refresh: bool = False) -> str:
    api_base = _CONN["api_base"]
    explicit = _CONN.get("explicit_token")
    if explicit:
        if _CONN.get("verified_token") == explicit and not force_refresh:
            return explicit
        try:
            http_json("GET", "/bulk-import/ping", token=explicit, timeout=15)
        except Exception as exc:
            raise RuntimeError(
                f"Authentication against {api_base}/bulk-import/ping failed using provided token/MCP config: {exc}"
            ) from exc
        _CONN["verified_token"] = explicit
        return explicit

    STATE_DIR.mkdir(parents=True, exist_ok=True)
    cache_map: dict[str, str] = {}
    if TOKEN_CACHE.exists():
        try:
            raw_cache = json.loads(TOKEN_CACHE.read_text(encoding="utf-8"))
            if isinstance(raw_cache, dict):
                if isinstance(raw_cache.get("tokens"), dict):
                    cache_map = {str(k): str(v) for k, v in raw_cache["tokens"].items() if v}
                if (
                    raw_cache.get("token")
                    and api_base == "http://localhost:5041/api"
                    and api_base not in cache_map
                ):
                    cache_map[api_base] = str(raw_cache["token"])
        except Exception:
            cache_map = {}

    if not force_refresh and api_base in cache_map:
        cached_tok = cache_map[api_base]
        try:
            http_json("GET", "/bulk-import/ping", token=cached_tok, timeout=10)
            return cached_tok
        except Exception:
            pass

    if not _is_localhost_api(api_base):
        raise RuntimeError(
            f"Authentication required for non-local server '{api_base}'. "
            "Pass --mcp-config (with Authorization header) or --token / LOCALROAST_TOKEN."
        )

    try:
        res = http_json(
            "POST",
            "/auth/login",
            body={"email": DEFAULT_EMAIL, "password": DEFAULT_PASS},
        )
    except RuntimeError as exc:
        if "401" not in str(exc):
            raise
        res = http_json(
            "POST",
            "/auth/register",
            body={
                "email": DEFAULT_EMAIL,
                "username": DEFAULT_USER,
                "password": DEFAULT_PASS,
            },
        )

    token = res["token"]
    cache_map[api_base] = token
    TOKEN_CACHE.write_text(json.dumps({"tokens": cache_map}), encoding="utf-8")
    return token


def set_ingestion_enabled(token: str, enabled: bool) -> dict[str, Any]:
    return http_json(
        "PUT",
        "/bulk-import/worker",
        token=token,
        body={"enableIngestion": bool(enabled)},
    )


def norm_title(text: str) -> str:
    nfd = unicodedata.normalize("NFD", (text or "").replace("\u2019", "'").replace("\u2018", "'"))
    stripped = "".join(ch for ch in nfd if unicodedata.category(ch) != "Mn")
    return " ".join(stripped.lower().split())


def extract_ingredient_block_lines(source_text: str) -> list[str]:
    lines = (source_text or "").replace("\r\n", "\n").split("\n")
    in_ingredients = False
    captured: list[str] = []
    for raw in lines:
        trimmed = raw.strip()
        if not trimmed:
            continue
        if re.match(r"^(?:instructions?|directions?|method|preparation)\s*:?\s*$", trimmed, re.I):
            break
        if re.match(r"^\d+\.\s+[A-Z]", trimmed):
            break
        if re.match(r"^(?:base\s+)?ingredients?\s*:?\s*$", trimmed, re.I):
            in_ingredients = True
            continue
        if in_ingredients or trimmed.startswith(("-", "•", "*")):
            captured.append(trimmed)
    return captured


def clean_ref_candidate_line(line: str) -> str:
    s = line.strip()
    s = re.sub(r"^[-•*\s]+", "", s)
    s = re.sub(
        r"^(?:\d+\s+\d+/\d+|\d+/\d+|\d+(?:\.\d+)?)\s*(?:\([^)]*\)\s*)?(?:recipes?|batches?|servings?|cups?|pounds?|lbs?|ounces?|oz|tablespoons?|tbsp|teaspoons?|tsp)?\s*",
        "",
        s,
        flags=re.I,
    )
    s = re.sub(
        r"\s*\(\s*(?:optional|recipe\s+follows|see\s+(?:this|facing)\s+page|facing\s+page|page\s+\d+)[^)]*\)",
        "",
        s,
        flags=re.I,
    )
    s = re.sub(r",.*$", "", s)
    s = re.sub(r"\s*\(\s*optional\s*\)\s*$", "", s, flags=re.I)
    return norm_title(s)


def detect_dependencies(
    jobs: list[dict[str, Any]],
) -> tuple[dict[str, list[str]], dict[str, list[str]]]:
    """Builds queued-job sub-recipe dependency graph: job_id -> [prereq_job_ids]."""
    title_to_ids: dict[str, list[str]] = defaultdict(list)
    base_to_ids: dict[str, list[str]] = defaultdict(list)
    paren_alias_to_ids: dict[str, list[str]] = defaultdict(list)
    searchable_titles: list[tuple[str, str]] = []

    for j in jobs:
        jid = j["id"]
        nt = norm_title(j.get("displayTitle", ""))
        if not nt:
            continue
        title_to_ids[nt].append(jid)
        base = re.sub(r"\s*\([^()]+\)\s*$", "", nt).strip()
        if base and base != nt and len(base) >= 8:
            base_to_ids[base].append(jid)
        paren_m = re.search(r"\(([^()]+)\)\s*$", nt)
        if paren_m:
            alias = paren_m.group(1).strip()
            if len(alias) >= 5:
                paren_alias_to_ids[alias].append(jid)
        if len(nt) >= 12:
            searchable_titles.append((nt, jid))
        if base and base != nt and len(base) >= 12:
            searchable_titles.append((base, jid))

    searchable_titles.sort(key=lambda x: len(x[0]), reverse=True)

    deps_by_job: dict[str, list[str]] = {j["id"]: [] for j in jobs}
    dependents_by_job: dict[str, list[str]] = {j["id"]: [] for j in jobs}

    for j in jobs:
        jid = j["id"]
        seen_prereqs: set[str] = set()
        for line in extract_ingredient_block_lines(j.get("sourceText", "")):
            if not RECIPE_REF_LINE_CUE_RE.search(line):
                continue
            if RAW_GROCERY_SEE_PAGE_RE.search(line):
                continue

            cleaned = clean_ref_candidate_line(line)
            matched_ids: list[str] = []
            if cleaned in title_to_ids:
                matched_ids.extend(title_to_ids[cleaned])
            elif cleaned in base_to_ids:
                matched_ids.extend(base_to_ids[cleaned])
            elif len(paren_alias_to_ids.get(cleaned, [])) == 1:
                matched_ids.extend(paren_alias_to_ids[cleaned])
            else:
                norm_line = norm_title(line)
                for cand_title, cand_id in searchable_titles:
                    if cand_id == jid:
                        continue
                    if re.search(rf"\b{re.escape(cand_title)}\b", norm_line):
                        matched_ids.append(cand_id)
                        break

            for pid in matched_ids:
                if pid != jid and pid not in seen_prereqs:
                    seen_prereqs.add(pid)
                    deps_by_job[jid].append(pid)
                    dependents_by_job[pid].append(jid)

    return deps_by_job, dependents_by_job


def topo_sort_jobs(
    subset_jobs: list[dict[str, Any]],
    deps_by_job: dict[str, list[str]],
) -> tuple[list[dict[str, Any]], dict[str, int]]:
    """Topologically orders subset_jobs so prerequisites precede dependents, grouped by depth."""
    by_id = {j["id"]: j for j in subset_jobs}
    subset_ids = set(by_id.keys())
    in_degree: dict[str, int] = {}
    children: dict[str, list[str]] = defaultdict(list)

    for j in subset_jobs:
        jid = j["id"]
        prereqs = [p for p in deps_by_job.get(jid, []) if p in subset_ids]
        in_degree[jid] = len(prereqs)
        for p in prereqs:
            children[p].append(jid)

    depth: dict[str, int] = {jid: 0 for jid in subset_ids}
    # Ready priority: nodes that unlock downstream dependents first, then original queue position
    order_idx = {j["id"]: i for i, j in enumerate(subset_jobs)}
    ready = deque(
        sorted(
            [jid for jid, deg in in_degree.items() if deg == 0],
            key=lambda x: (-len(children.get(x, [])), order_idx[x]),
        )
    )

    ordered_ids: list[str] = []
    seen: set[str] = set()
    while ready:
        cur = ready.popleft()
        if cur in seen:
            continue
        seen.add(cur)
        ordered_ids.append(cur)
        for ch in children.get(cur, []):
            depth[ch] = max(depth[ch], depth[cur] + 1)
            in_degree[ch] -= 1
            if in_degree[ch] == 0:
                ready.append(ch)

    # Break any cycles deterministically
    for j in subset_jobs:
        if j["id"] not in seen:
            ordered_ids.append(j["id"])

    ordered_ids.sort(key=lambda x: (depth.get(x, 0), -len(children.get(x, [])), order_idx[x]))
    return [by_id[jid] for jid in ordered_ids], depth


def pick_best_benchmark_match(
    queued: list[dict[str, Any]],
    used_ids: set[str],
    needles: list[str],
) -> dict[str, Any] | None:
    candidates: list[tuple[int, dict[str, Any]]] = []
    for job in queued:
        if job["id"] in used_ids:
            continue
        title_norm = norm_title(job.get("displayTitle", ""))
        for needle in needles:
            if title_norm == needle:
                candidates.append((0, job))
                break
            if title_norm.startswith(needle):
                candidates.append((1 + len(title_norm), job))
                break
            if needle in title_norm:
                candidates.append((100 + len(title_norm), job))
                break
    if not candidates:
        return None
    candidates.sort(key=lambda x: x[0])
    return candidates[0][1]


def collect_transitive_prereqs(
    job_id: str,
    deps_by_job: dict[str, list[str]],
    selected_ids: set[str],
) -> list[str]:
    missing: list[str] = []
    visited = set(selected_ids)
    stack = list(deps_by_job.get(job_id, []))
    while stack:
        pid = stack.pop()
        if pid in visited:
            continue
        visited.add(pid)
        missing.append(pid)
        stack.extend(deps_by_job.get(pid, []))
    return missing


def select_pilot_wave(
    queued: list[dict[str, Any]],
    deps_by_job: dict[str, list[str]],
    size: int,
) -> list[dict[str, Any]]:
    by_id = {j["id"]: j for j in queued}
    selected: list[dict[str, Any]] = []
    used_ids: set[str] = set()

    def add_with_prereqs(job: dict[str, Any], enforce_cap: bool = False) -> bool:
        if job["id"] in used_ids:
            return False
        missing = collect_transitive_prereqs(job["id"], deps_by_job, used_ids)
        needed = 1 + len(missing)
        if enforce_cap and len(selected) + needed > size:
            return False
        for pid in missing:
            if pid in by_id and pid not in used_ids:
                selected.append(by_id[pid])
                used_ids.add(pid)
        selected.append(job)
        used_ids.add(job["id"])
        return True

    for _, needles in TARGET_BENCHMARK_PATTERNS:
        if len(selected) >= size:
            break
        match = pick_best_benchmark_match(queued, used_ids, needles)
        if match is None:
            print(f"WARN: Benchmark recipe not found in queue: {needles[0]}", file=sys.stderr)
        else:
            add_with_prereqs(match, enforce_cap=False)

    unseen = [
        j
        for j in queued
        if j["id"] not in used_ids
        and not any(sub in norm_title(j.get("displayTitle", "")) for sub in PREVIOUSLY_TESTED_SUBSTRINGS)
    ]
    if unseen and len(selected) < size:
        remaining = size - len(selected)
        step = max(1, len(unseen) // (remaining + 1))
        idx = step // 2
        scanned = 0
        while len(selected) < size and scanned < len(unseen):
            cand = unseen[idx % len(unseen)]
            add_with_prereqs(cand, enforce_cap=True)
            idx += step
            scanned += 1
        # Top off sequentially if spacing overshot cap with multi-prereqs
        for cand in unseen:
            if len(selected) >= size:
                break
            add_with_prereqs(cand, enforce_cap=True)

    return selected[:size]


def select_standard_wave(
    queued: list[dict[str, Any]],
    deps_by_job: dict[str, list[str]],
    size: int,
) -> list[dict[str, Any]]:
    ordered_all, _ = topo_sort_jobs(queued, deps_by_job)
    by_id = {j["id"]: j for j in queued}
    selected: list[dict[str, Any]] = []
    used_ids: set[str] = set()

    for job in ordered_all:
        if len(selected) >= size:
            break
        if job["id"] in used_ids:
            continue
        missing = collect_transitive_prereqs(job["id"], deps_by_job, used_ids)
        if len(selected) + 1 + len(missing) > size:
            continue
        for pid in missing:
            if pid in by_id and pid not in used_ids:
                selected.append(by_id[pid])
                used_ids.add(pid)
        selected.append(job)
        used_ids.add(job["id"])

    return selected[:size]


def cmd_plan_wave(args: argparse.Namespace) -> int:
    token = get_token()
    set_ingestion_enabled(token, False)

    queued = http_json(
        "POST",
        "/bulk-import/jobs/batch-source",
        token=token,
        body={"status": "Queued", "limit": 2000},
        timeout=120,
    )
    if not isinstance(queued, list) or len(queued) == 0:
        out_data = {
            "waveSize": args.size,
            "batchSize": args.batch_size,
            "totalQueuedBeforeWave": 0,
            "totalSelected": 0,
            "dependencyEdgesInWave": [],
            "batches": [],
        }
        Path(args.out).parent.mkdir(parents=True, exist_ok=True)
        Path(args.out).write_text(json.dumps(out_data, indent=2), encoding="utf-8")
        print(json.dumps({"totalQueuedBeforeWave": 0, "totalSelected": 0, "batchCount": 0}))
        return 0

    deps_by_job, _ = detect_dependencies(queued)
    if args.pilot_benchmark:
        selected = select_pilot_wave(queued, deps_by_job, args.size)
    else:
        selected = select_standard_wave(queued, deps_by_job, args.size)

    ordered, depth = topo_sort_jobs(selected, deps_by_job)
    selected_ids = {j["id"] for j in ordered}
    by_id = {j["id"]: j for j in ordered}
    edges_in_wave = [
        {
            "dependentId": j["id"],
            "dependentTitle": j["displayTitle"],
            "prerequisiteId": p,
            "prerequisiteTitle": by_id[p]["displayTitle"],
        }
        for j in ordered
        for p in deps_by_job.get(j["id"], [])
        if p in selected_ids
    ]

    batches: list[dict[str, Any]] = []
    bsize = max(1, args.batch_size)
    for idx in range(0, len(ordered), bsize):
        chunk = ordered[idx : idx + bsize]
        batches.append(
            {
                "batchIndex": len(batches),
                "maxDepth": max((depth.get(j["id"], 0) for j in chunk), default=0),
                "jobs": [
                    {
                        "id": j["id"],
                        "displayTitle": j["displayTitle"],
                        "sourceBookTitle": j.get("sourceBookTitle"),
                        "sourceBookPage": j.get("sourceBookPage"),
                        "sourceText": j["sourceText"],
                    }
                    for j in chunk
                ],
            }
        )

    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_data = {
        "waveSize": args.size,
        "batchSize": bsize,
        "pilotBenchmark": bool(args.pilot_benchmark),
        "totalQueuedBeforeWave": len(queued),
        "totalSelected": len(ordered),
        "dependencyEdgesInWave": edges_in_wave,
        "batches": batches,
    }
    out_path.write_text(json.dumps(out_data, indent=2), encoding="utf-8")

    summary = {
        "totalQueuedBeforeWave": len(queued),
        "totalSelected": len(ordered),
        "batchCount": len(batches),
        "inWaveDependencyEdges": len(edges_in_wave),
        "wavePath": str(out_path),
    }
    print(json.dumps(summary))
    return 0


def _opt_float(val: Any) -> float | None:
    if val is None or isinstance(val, bool):
        return None
    try:
        return float(val)
    except (TypeError, ValueError):
        return None


def _opt_int(val: Any) -> int | None:
    if val is None or isinstance(val, bool):
        return None
    try:
        return int(round(float(val)))
    except (TypeError, ValueError):
        return None


def _normalize_token(tok: Any) -> Any:
    if isinstance(tok, str):
        return tok
    if not isinstance(tok, dict):
        return str(tok) if tok is not None else ""

    ttype = str(tok.get("type") or "text").strip().lower()
    if ttype == "text":
        return str(tok.get("text") or "")

    if ttype == "ingredient":
        qty = _opt_float(tok.get("quantity"))
        max_qty = _opt_float(tok.get("maxQuantity"))
        if max_qty is not None and qty is not None and max_qty <= qty:
            max_qty = None
        return {
            "type": "ingredient",
            "ingredientIndex": _opt_int(tok.get("ingredientIndex")),
            "name": str(tok.get("name") or "").strip(),
            "quantity": qty,
            "maxQuantity": max_qty,
            "unit": (str(tok["unit"]).strip() if tok.get("unit") else None),
            "fixed": bool(tok.get("fixed", False)),
        }

    if ttype == "mixture":
        qty = _opt_float(tok.get("quantity"))
        if qty is None:
            qty = _opt_float(tok.get("amount")) or 0.0
        max_qty = _opt_float(tok.get("maxQuantity") or tok.get("maxAmount"))
        if max_qty is not None and max_qty <= qty:
            max_qty = None
        return {
            "type": "mixture",
            "name": str(tok.get("name") or "").strip(),
            "quantity": qty,
            "maxQuantity": max_qty,
            "unit": (str(tok["unit"]).strip() if tok.get("unit") else None),
            "fixed": bool(tok.get("fixed", False)),
        }

    if ttype == "timer":
        amt = _opt_float(tok.get("amount")) or _opt_float(tok.get("quantity")) or 0.0
        max_amt = _opt_float(tok.get("maxAmount") or tok.get("maxQuantity"))
        if max_amt is not None and max_amt <= amt:
            max_amt = None
        return {
            "type": "timer",
            "amount": amt,
            "maxAmount": max_amt,
            "unit": (str(tok.get("unit") or "minute").strip().lower()),
            "label": str(tok.get("label") or "").strip(),
        }

    if ttype == "temperature":
        amt = _opt_float(tok.get("amount")) or _opt_float(tok.get("quantity")) or 0.0
        max_amt = _opt_float(tok.get("maxAmount") or tok.get("maxQuantity"))
        if max_amt is not None and max_amt <= amt:
            max_amt = None
        return {
            "type": "temperature",
            "amount": amt,
            "maxAmount": max_amt,
            "unit": (str(tok.get("unit") or "fahrenheit").strip().lower()),
        }

    if ttype == "dimension":
        amt = _opt_float(tok.get("amount")) or _opt_float(tok.get("quantity")) or 0.0
        max_amt = _opt_float(tok.get("maxAmount") or tok.get("maxQuantity"))
        if max_amt is not None and max_amt <= amt:
            max_amt = None
        return {
            "type": "dimension",
            "amount": amt,
            "maxAmount": max_amt,
            "unit": (str(tok.get("unit") or "inch").strip().lower()),
            "hyphenated": bool(tok.get("hyphenated", False)),
        }

    if ttype == "fixed":
        amt = _opt_float(tok.get("amount")) or _opt_float(tok.get("quantity")) or 0.0
        max_amt = _opt_float(tok.get("maxAmount") or tok.get("maxQuantity"))
        if max_amt is not None and max_amt <= amt:
            max_amt = None
        return {
            "type": "fixed",
            "name": (str(tok["name"]).strip() if tok.get("name") else None),
            "amount": amt,
            "maxAmount": max_amt,
            "unit": (str(tok["unit"]).strip() if tok.get("unit") else None),
            "hyphenated": bool(tok.get("hyphenated", False)),
        }

    return tok


def normalize_extraction(raw: dict[str, Any]) -> dict[str, Any]:
    raw_timing = raw.get("timing")
    timing = None
    if isinstance(raw_timing, dict):
        timing = {
            "prep": _opt_int(raw_timing.get("prep")),
            "cook": _opt_int(raw_timing.get("cook")),
            "total": _opt_int(raw_timing.get("total")),
        }

    servings = _opt_float(raw.get("servings"))
    if servings is not None and servings <= 0:
        servings = None

    sections_out: list[dict[str, Any]] = []
    for sec in raw.get("sections") or []:
        if not isinstance(sec, dict):
            continue
        sec_name = str(sec["name"]).strip() if sec.get("name") else None

        ings_out: list[dict[str, Any]] = []
        for ing in sec.get("ingredients") or []:
            if not isinstance(ing, dict):
                continue
            reps_out: list[dict[str, Any]] = []
            for rep in ing.get("replacements") or []:
                if not isinstance(rep, dict) or not str(rep.get("name") or "").strip():
                    continue
                reps_out.append(
                    {
                        "name": str(rep["name"]).strip(),
                        "quantity": _opt_float(rep.get("quantity")),
                        "maxQuantity": _opt_float(rep.get("maxQuantity")),
                        "unit": (str(rep["unit"]).strip() if rep.get("unit") else None),
                        "prepNote": (str(rep["prepNote"]).strip() if rep.get("prepNote") else None),
                        "canonicalFoodName": (
                            str(rep["canonicalFoodName"]).strip()
                            if rep.get("canonicalFoodName")
                            else None
                        ),
                        "category": (str(rep["category"]).strip() if rep.get("category") else None),
                        "isPantryStaple": (
                            bool(rep["isPantryStaple"])
                            if rep.get("isPantryStaple") is not None
                            else None
                        ),
                    }
                )

            alts_out: list[dict[str, Any]] = []
            for alt in ing.get("alternateMeasurements") or []:
                if not isinstance(alt, dict):
                    continue
                aq = _opt_float(alt.get("quantity"))
                if aq is None or aq <= 0:
                    continue
                alts_out.append(
                    {
                        "quantity": aq,
                        "unit": (str(alt["unit"]).strip() if alt.get("unit") else None),
                    }
                )

            ings_out.append(
                {
                    "name": str(ing.get("name") or "").strip(),
                    "quantity": _opt_float(ing.get("quantity")),
                    "maxQuantity": _opt_float(ing.get("maxQuantity")),
                    "unit": (str(ing["unit"]).strip() if ing.get("unit") else None),
                    "prepNote": (str(ing["prepNote"]).strip() if ing.get("prepNote") else None),
                    "isOptional": bool(ing.get("isOptional", False)),
                    "canonicalFoodName": (
                        str(ing["canonicalFoodName"]).strip()
                        if ing.get("canonicalFoodName")
                        else None
                    ),
                    "category": (str(ing["category"]).strip() if ing.get("category") else None),
                    "isPantryStaple": (
                        bool(ing["isPantryStaple"])
                        if ing.get("isPantryStaple") is not None
                        else None
                    ),
                    "isRecipeReference": bool(ing.get("isRecipeReference", False)),
                    "referencedRecipeTitle": (
                        str(ing["referencedRecipeTitle"]).strip()
                        if ing.get("referencedRecipeTitle")
                        else None
                    ),
                    "replacements": reps_out if reps_out else None,
                    "alternateMeasurements": alts_out if alts_out else None,
                }
            )

        steps_out: list[dict[str, Any]] = []
        for st in sec.get("steps") or []:
            if not isinstance(st, dict):
                continue
            instr = str(st.get("instruction") or "").strip()
            if not instr:
                continue
            tokens = [_normalize_token(t) for t in (st.get("tokens") or [])]
            steps_out.append({"instruction": instr, "tokens": tokens})

        sections_out.append(
            {
                "name": sec_name,
                "ingredients": ings_out,
                "steps": steps_out,
            }
        )

    tags = [str(t).strip() for t in (raw.get("tags") or []) if str(t or "").strip()]
    return {
        "title": str(raw.get("title") or "").strip() or "Untitled Recipe",
        "description": (str(raw["description"]).strip() if raw.get("description") else None),
        "servings": servings,
        "yield": (str(raw["yield"]).strip() if raw.get("yield") else None),
        "sections": sections_out,
        "timing": timing,
        "tags": tags,
    }


def classify_submission_outcome(
    res: dict[str, Any],
    extracted_payload: dict[str, Any],
    wave_titles_norm: set[str],
) -> dict[str, Any]:
    val_issues = res.get("validationIssues") or []
    qual_warnings = res.get("qualityWarnings") or []
    meas_warnings = res.get("measurementWarnings") or []
    status = res.get("status")

    non_subrecipe_val_issues = [
        i for i in val_issues if not MISSING_SUBRECIPE_ISSUE_RE.search(i or "")
    ]
    subrecipe_issues = [i for i in val_issues if MISSING_SUBRECIPE_ISSUE_RE.search(i or "")]

    needs_repair = (
        status != "Published"
        and (
            len(non_subrecipe_val_issues) > 0
            or len(qual_warnings) > 0
            or len(meas_warnings) > 0
        )
    )

    only_external_missing_sub = False
    if status != "Published" and not needs_repair and subrecipe_issues:
        # Check if missing sub-recipe is completely outside this wave (already in-wave prereqs will promote later)
        only_external_missing_sub = True
        for issue in subrecipe_issues:
            m = re.search(r"'([^']+)'", issue)
            if m and norm_title(m.group(1)) in wave_titles_norm:
                only_external_missing_sub = False
                break

    return {
        "jobId": res["jobId"],
        "title": (res.get("draft") or {}).get("title") or extracted_payload.get("title"),
        "status": status,
        "confidenceScore": res.get("confidenceScore"),
        "resultingRecipeId": res.get("resultingRecipeId"),
        "resultingRecipeSlug": res.get("resultingRecipeSlug"),
        "promotedWaitingRecipeIds": res.get("promotedWaitingRecipeIds") or [],
        "validationIssues": val_issues,
        "qualityWarnings": qual_warnings,
        "measurementWarnings": meas_warnings,
        "needsRepair": needs_repair,
        "onlyExternalMissingSubRecipe": only_external_missing_sub,
    }


def cmd_submit_batch(args: argparse.Namespace) -> int:
    token = get_token()
    raw_in = json.loads(Path(args.in_file).read_text(encoding="utf-8"))
    items = raw_in.get("recipes") if isinstance(raw_in, dict) else raw_in
    if not isinstance(items, list):
        raise ValueError("Input JSON must be a list or { 'recipes': [...] }")

    wave_titles_norm: set[str] = set()
    if args.wave:
        wave_data = json.loads(Path(args.wave).read_text(encoding="utf-8"))
        for b in wave_data.get("batches") or []:
            for j in b.get("jobs") or []:
                wave_titles_norm.add(norm_title(j.get("displayTitle", "")))

    results: list[dict[str, Any]] = []
    for item in items:
        job_id = str(item.get("jobId") or item.get("id") or "").strip()
        raw_ext = item.get("extraction") or item
        if not job_id:
            continue
        clean_ext = normalize_extraction(raw_ext)
        body = {
            "extraction": clean_ext,
            "extractedModel": args.extracted_model,
            "autoPublish": True,
            "allowOverwritePublished": bool(args.allow_overwrite_published),
            "computeEmbeddings": not bool(args.no_embeddings),
            "repairCount": int(args.repair_count),
        }
        try:
            res = http_json(
                "POST",
                f"/bulk-import/jobs/{job_id}/submit-extraction",
                token=token,
                body=body,
                timeout=120,
            )
            outcome = classify_submission_outcome(res, clean_ext, wave_titles_norm)
            outcome["extraction"] = clean_ext
            results.append(outcome)
        except Exception as exc:
            results.append(
                {
                    "jobId": job_id,
                    "title": clean_ext.get("title"),
                    "status": "Error",
                    "error": str(exc),
                    "needsRepair": True,
                    "onlyExternalMissingSubRecipe": False,
                    "extraction": clean_ext,
                }
            )

    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(results, indent=2), encoding="utf-8")

    summary = {
        "submitted": len(results),
        "published": sum(1 for r in results if r.get("status") == "Published"),
        "needsRepair": sum(1 for r in results if r.get("needsRepair")),
        "externalMissingSubRecipe": sum(
            1 for r in results if r.get("onlyExternalMissingSubRecipe")
        ),
        "promotedCount": sum(
            len(r.get("promotedWaitingRecipeIds") or []) for r in results
        ),
    }
    print(json.dumps(summary))
    return 0


def cmd_submit_one(args: argparse.Namespace) -> int:
    token = get_token()
    raw_in = json.loads(Path(args.in_file).read_text(encoding="utf-8"))
    raw_ext = raw_in.get("extraction", raw_in) if isinstance(raw_in, dict) else raw_in
    clean_ext = normalize_extraction(raw_ext)

    wave_titles_norm: set[str] = set()
    if args.wave:
        wave_data = json.loads(Path(args.wave).read_text(encoding="utf-8"))
        for b in wave_data.get("batches") or []:
            for j in b.get("jobs") or []:
                wave_titles_norm.add(norm_title(j.get("displayTitle", "")))

    body = {
        "extraction": clean_ext,
        "extractedModel": args.extracted_model,
        "autoPublish": True,
        "allowOverwritePublished": bool(args.allow_overwrite_published),
        "computeEmbeddings": not bool(args.no_embeddings),
        "repairCount": int(args.repair_count),
    }
    res = http_json(
        "POST",
        f"/bulk-import/jobs/{args.job_id}/submit-extraction",
        token=token,
        body=body,
        timeout=120,
    )
    outcome = classify_submission_outcome(res, clean_ext, wave_titles_norm)
    outcome["extraction"] = clean_ext

    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(outcome, indent=2), encoding="utf-8")
    print(json.dumps({k: v for k, v in outcome.items() if k != "extraction"}))
    return 0


def parse_tokens(instruction: str) -> list[tuple[bool, Any, str]]:
    segs: list[tuple[bool, Any, str]] = []
    pos = 0
    for m in TOKEN_REGEX.finditer(instruction or ""):
        if m.start() > pos:
            segs.append((False, None, instruction[pos : m.start()]))
        raw = m.group(0)
        try:
            obj = json.loads(m.group(1))
        except Exception:
            obj = None
        segs.append((True, obj, raw))
        pos = m.end()
    if pos < len(instruction or ""):
        segs.append((False, None, instruction[pos:]))
    return segs


def render_plain(raw_token: str) -> str:
    try:
        obj = json.loads(raw_token.strip("|"))
    except Exception:
        return raw_token
    if not isinstance(obj, dict):
        return raw_token
    ttype = (obj.get("type") or "ingredient").lower()
    if ttype == "timer":
        amt = obj.get("seconds", 0)
        unit = "s"
        if amt >= 86400 and amt % 86400 == 0:
            amt //= 86400
            unit = "day" if amt == 1 else "days"
        elif amt >= 3600 and amt % 3600 == 0:
            amt //= 3600
            unit = "hour" if amt == 1 else "hours"
        elif amt >= 60:
            amt //= 60
            unit = "minute" if amt == 1 else "minutes"
        else:
            unit = "second" if amt == 1 else "seconds"
        return f"{amt} {unit}"
    if ttype == "temperature":
        if obj.get("fahrenheit") is not None:
            return f"{obj['fahrenheit']:g} degrees"
        if obj.get("celsius") is not None:
            return f"{obj['celsius']:g} C"
    if ttype == "dimension":
        return f"{obj.get('value', '')} {obj.get('unit', 'inch')}".strip()
    return raw_token


def is_inside_parens(segments: list[tuple[bool, Any, str]], tok_idx: int) -> bool:
    depth = 0
    for is_tok, _, txt in segments[:tok_idx]:
        if not is_tok:
            for ch in txt:
                if ch == "(":
                    depth += 1
                elif ch == ")" and depth > 0:
                    depth -= 1
    if depth <= 0:
        return False
    for is_tok, _, txt in segments[tok_idx + 1 :]:
        if not is_tok and ")" in txt:
            return True
    return False


def has_unbalanced_or_outer_parens(note: str | None) -> str | None:
    if not note:
        return None
    s = note.strip()
    if s.startswith("(") and s.endswith(")"):
        depth = 0
        outer_closes_at_end = True
        for i, ch in enumerate(s):
            if ch == "(":
                depth += 1
            elif ch == ")":
                depth -= 1
                if depth == 0 and i < len(s) - 1:
                    outer_closes_at_end = False
                    break
        if outer_closes_at_end and depth == 0:
            return "outer_parens"
    depth = 0
    for ch in s:
        if ch == "(":
            depth += 1
        elif ch == ")":
            depth -= 1
            if depth < 0:
                return "unbalanced_close_paren"
    if depth != 0:
        return "unbalanced_open_paren"
    return None


def sentence_around_segment(
    segments: list[tuple[bool, Any, str]],
    tok_idx: int,
    rendered_segs: list[str],
) -> str:
    left_parts: list[str] = []
    for s in reversed(rendered_segs[:tok_idx]):
        m = re.search(r"[.!?;]([^.!?;]*)$", s)
        if m:
            left_parts.append(m.group(1))
            break
        left_parts.append(s)
    right_parts: list[str] = []
    for s in rendered_segs[tok_idx + 1 :]:
        m = re.match(r"^([^.!?;]*)[.!?;]", s)
        if m:
            right_parts.append(m.group(1))
            break
        right_parts.append(s)
    return "".join(reversed(left_parts)) + rendered_segs[tok_idx] + "".join(right_parts)


def cmd_audit(args: argparse.Namespace) -> int:
    token = get_token()
    wave_data = json.loads(Path(args.wave).read_text(encoding="utf-8"))
    wave_job_ids = [
        j["id"] for b in wave_data.get("batches") or [] for j in b.get("jobs") or []
    ]

    worker_state = http_json("GET", "/bulk-import/ping", token=token)
    food_items_raw = http_json("GET", "/bulk-import/food-items", token=token)
    food_items = (
        food_items_raw.get("items", [])
        if isinstance(food_items_raw, dict)
        else (food_items_raw or [])
    )

    pub_details: list[dict[str, Any]] = []
    non_published_details: list[dict[str, Any]] = []

    categories: dict[str, list[dict[str, Any]]] = {
        "draftOrMeasurementWarnings": [],
        "doubleParenPrepNote": [],
        "falsePositiveFixed": [],
        "missedFixedOnDistributiveOrAtATime": [],
        "singularPluralMismatch": [],
        "unlinkedNonWaterStepToken": [],
        "unquantifiedNonSubRecipeToken": [],
        "missedSingleUseInjection": [],
        "untokenizedQuantitiesInProse": [],
        "proseArtifacts": [],
        "sectionAnomalies": [],
        "subRecipeScalingCheck": [],
    }
    scores: list[dict[str, Any]] = []
    fixed_tokens_seen: list[dict[str, Any]] = []
    external_missing_subrecipe_count = 0

    for jid in wave_job_ids:
        det = http_json("GET", f"/bulk-import/jobs/{jid}", token=token)
        status = det.get("status")
        draft = det.get("extractedDraft") or {}
        if status != "Published":
            v_issues = draft.get("validationIssues") or []
            q_warn = draft.get("qualityWarnings") or []
            m_warn = draft.get("measurementWarnings") or []
            only_missing_sub = (
                len(v_issues) > 0
                and all(MISSING_SUBRECIPE_ISSUE_RE.search(i or "") for i in v_issues)
                and not q_warn
                and not m_warn
            )
            if only_missing_sub:
                external_missing_subrecipe_count += 1
            non_published_details.append(
                {
                    "id": jid,
                    "title": det.get("displayTitle"),
                    "status": status,
                    "confidenceScore": det.get("confidenceScore"),
                    "onlyMissingSubRecipe": only_missing_sub,
                    "validationIssues": v_issues,
                    "qualityWarnings": q_warn,
                    "measurementWarnings": m_warn,
                    "statusDetail": det.get("statusDetail"),
                }
            )
            continue

        rid = det.get("resultingRecipeId")
        if not rid:
            continue
        rec = http_json("GET", f"/bulk-import/recipes/{rid}", token=token)
        conf = det.get("confidenceScore")
        scores.append(
            {
                "title": rec["title"],
                "confidenceScore": conf,
                "recipeId": rid,
                "extractedModel": det.get("extractedModel"),
            }
        )
        pub_details.append({"id": jid, "title": rec["title"], "recipeId": rid, "confidenceScore": conf})

        desc_text = (rec.get("description") or "").strip()
        if not desc_text:
            categories["proseArtifacts"].append(
                {"title": rec["title"], "kind": "missing_description"}
            )
        elif re.search(r"\bWHY\s+THIS\s+RECIPES?\s+WORKS?\b", desc_text, re.I) or re.search(
            r"(?:^|\n)\s*BEFORE\s+YOU\s+BEGIN\b", desc_text
        ):
            categories["proseArtifacts"].append(
                {
                    "title": rec["title"],
                    "kind": "description_header_leak",
                    "snippet": desc_text[:140],
                }
            )

        qw = draft.get("qualityWarnings") or []
        mw = draft.get("measurementWarnings") or []
        if qw or mw:
            categories["draftOrMeasurementWarnings"].append(
                {
                    "title": rec["title"],
                    "recipeId": rid,
                    "qualityWarnings": qw,
                    "measurementWarnings": mw,
                }
            )

        ings = rec.get("ingredients") or []
        steps = rec.get("steps") or []
        sub_map = {sr["id"]: sr for sr in (rec.get("linkedSubRecipes") or [])}
        sub_ing_ids = {
            ing["id"]
            for ing in ings
            if ing.get("linkedRecipeId") or ing.get("unresolvedRecipeTitle")
        }
        ing_by_id = {ing["id"]: ing for ing in ings}

        for ing in ings:
            err = has_unbalanced_or_outer_parens(ing.get("preparationNote"))
            if err:
                categories["doubleParenPrepNote"].append(
                    {
                        "title": rec["title"],
                        "kind": "ingredient",
                        "name": ing["displayName"],
                        "prepNote": ing.get("preparationNote"),
                        "error": err,
                    }
                )
            for rep in ing.get("replacements") or []:
                rerr = has_unbalanced_or_outer_parens(rep.get("preparationNote"))
                if rerr:
                    categories["doubleParenPrepNote"].append(
                        {
                            "title": rec["title"],
                            "kind": "replacement",
                            "name": rep["displayName"],
                            "prepNote": rep.get("preparationNote"),
                            "error": rerr,
                        }
                    )

        ing_has_named = any(bool((x.get("sectionName") or "").strip()) for x in ings)
        ing_has_unnamed = any(not (x.get("sectionName") or "").strip() for x in ings)
        step_has_named = any(bool((x.get("sectionName") or "").strip()) for x in steps)
        step_has_unnamed = any(not (x.get("sectionName") or "").strip() for x in steps)
        non_contig: list[str] = []
        for kind, coll in [("ingredients", ings), ("steps", steps)]:
            seen_s: list[str] = []
            prev_s = None
            for item in coll:
                s_name = (item.get("sectionName") or "").strip().lower()
                if prev_s is None or s_name != prev_s:
                    if s_name in seen_s:
                        non_contig.append(f"{kind}:{s_name or '<unnamed>'}")
                    seen_s.append(s_name)
                    prev_s = s_name
        if (
            (ing_has_named and ing_has_unnamed)
            or (step_has_named and step_has_unnamed)
            or non_contig
            or (ing_has_named != step_has_named and len(steps) >= 2)
        ):
            categories["sectionAnomalies"].append(
                {
                    "title": rec["title"],
                    "recipeId": rid,
                    "ingNamed": ing_has_named,
                    "ingUnnamed": ing_has_unnamed,
                    "stepNamed": step_has_named,
                    "stepUnnamed": step_has_unnamed,
                    "nonContiguous": non_contig,
                }
            )

        for ing in ings:
            if ing.get("linkedRecipeId") or ing.get("unresolvedRecipeTitle"):
                sr = sub_map.get(ing.get("linkedRecipeId"), {})
                categories["subRecipeScalingCheck"].append(
                    {
                        "title": rec["title"],
                        "ingredient": ing["displayName"],
                        "quantity": ing.get("quantity"),
                        "unit": ing.get("unit"),
                        "customUnitName": ing.get("customUnitName"),
                        "linkedRecipeId": ing.get("linkedRecipeId"),
                        "unresolvedRecipeTitle": ing.get("unresolvedRecipeTitle"),
                        "targetTitle": sr.get("title"),
                        "targetBaseServings": sr.get("baseServings"),
                        "targetYieldDisplay": sr.get("yieldDisplay"),
                    }
                )

        linked_ing_ids: set[str] = set()
        plain_steps: list[str] = []

        for step in steps:
            snum = step["stepNumber"]
            instr = step.get("instruction") or ""
            segs = parse_tokens(instr)
            rendered_segs: list[str] = []
            for is_tok, obj, raw in segs:
                if not is_tok:
                    rendered_segs.append(raw)
                elif isinstance(obj, dict):
                    t = (obj.get("type") or "ingredient").lower()
                    if t in ("ingredient", ""):
                        amt = obj.get("amount")
                        max_amt = obj.get("maxAmount")
                        unit = obj.get("customUnitName") or obj.get("unit")
                        nm = obj.get("ingredient") or ""
                        num = (
                            f"{amt:g}" + (f"-{max_amt:g}" if max_amt is not None else "")
                            if amt is not None
                            else ""
                        )
                        rendered_segs.append(" ".join(x for x in [num, unit, nm] if x))
                    elif t == "mixture":
                        amt = obj.get("amount")
                        unit = obj.get("customUnitName") or obj.get("unit")
                        nm = obj.get("name") or ""
                        num = f"{amt:g}" if amt is not None else ""
                        rendered_segs.append(" ".join(x for x in [num, unit, nm] if x))
                    elif t == "fixed":
                        amt = obj.get("amount")
                        unit = obj.get("customUnitName") or obj.get("unit")
                        nm = obj.get("name") or ""
                        num = f"{amt:g}" if amt is not None else ""
                        sep = "-" if obj.get("hyphenated") and num and unit else " "
                        meas = f"{num}{sep}{unit}" if num and unit else (num or unit or "")
                        rendered_segs.append(" ".join(x for x in [meas, nm] if x))
                    else:
                        rendered_segs.append(render_plain(raw))
                else:
                    rendered_segs.append(raw)

            plain = "".join(rendered_segs)
            plain_steps.append(plain)

            for seg_idx, (is_tok, obj, raw) in enumerate(segs):
                if is_tok and isinstance(obj, dict):
                    ttype = (obj.get("type") or "ingredient").lower()
                    prev_raw = (
                        segs[seg_idx - 1][2]
                        if seg_idx > 0 and not segs[seg_idx - 1][0]
                        else ""
                    )
                    next_raw = (
                        segs[seg_idx + 1][2]
                        if seg_idx + 1 < len(segs) and not segs[seg_idx + 1][0]
                        else ""
                    )
                    sent = sentence_around_segment(segs, seg_idx, rendered_segs)

                    if obj.get("fixed") or ttype == "fixed":
                        fixed_tokens_seen.append(
                            {"title": rec["title"], "step": snum, "type": ttype, "token": obj, "sentence": sent}
                        )

                    if ttype == "fixed" and not obj.get("hyphenated"):
                        fx_name = (obj.get("name") or "").strip()
                        probe = f"{fx_name} {next_raw[:35]}".strip()
                        if (
                            FALSE_FIXED_FOOD_RE.search(probe)
                            and not VESSEL_RE.search(probe)
                            and not AT_A_TIME_RE.search(next_raw)
                            and not DISTRIBUTIVE_RE.search(sent)
                            and not is_inside_parens(segs, seg_idx)
                            and not YIELD_CHECK_RE.search(prev_raw[-30:])
                        ):
                            categories["falsePositiveFixed"].append(
                                {"title": rec["title"], "step": snum, "token": raw, "sentence": sent}
                            )

                    if ttype in ("ingredient", "mixture") and not obj.get("fixed"):
                        is_at_a_time = bool(AT_A_TIME_RE.match(next_raw))
                        is_dist = bool(DISTRIBUTIVE_RE.search(sent))
                        if is_at_a_time or is_dist:
                            amt = obj.get("amount")
                            iid = obj.get("ingredientId")
                            listed = ing_by_id.get(iid) if iid else None
                            if amt is not None and (
                                is_at_a_time
                                or listed is None
                                or listed.get("quantity") is None
                                or amt < float(listed["quantity"]) * 0.95
                            ):
                                categories["missedFixedOnDistributiveOrAtATime"].append(
                                    {
                                        "title": rec["title"],
                                        "step": snum,
                                        "type": ttype,
                                        "token": raw,
                                        "reason": "at_a_time" if is_at_a_time else "distributive_sentence",
                                        "sentence": sent,
                                    }
                                )

                    if (
                        ttype in ("ingredient", "mixture")
                        and obj.get("amount") == 1
                        and obj.get("maxAmount") is None
                    ):
                        u = (obj.get("unit") or obj.get("customUnitName") or "").lower()
                        nm = (obj.get("ingredient") or obj.get("name") or "").strip()
                        last_w = nm.lower().split()[-1] if nm else ""
                        if (not u or u == "whole") and last_w in PLURAL_NOUN_HINTS:
                            categories["singularPluralMismatch"].append(
                                {
                                    "title": rec["title"],
                                    "step": snum,
                                    "issue": f"qty=1 with plural '{nm}'",
                                    "token": raw,
                                }
                            )
                        if u and nm.lower().startswith(u + " "):
                            categories["singularPluralMismatch"].append(
                                {
                                    "title": rec["title"],
                                    "step": snum,
                                    "issue": f"unit '{u}' duplicated in '{nm}'",
                                    "token": raw,
                                }
                            )
                        if u in UNIT_WORD_TAILS and last_w in UNIT_WORD_TAILS:
                            categories["singularPluralMismatch"].append(
                                {
                                    "title": rec["title"],
                                    "step": snum,
                                    "issue": f"double unit word in '{u} {nm}'",
                                    "token": raw,
                                }
                            )

                    if ttype in ("ingredient", ""):
                        iid = obj.get("ingredientId")
                        if iid:
                            linked_ing_ids.add(iid)
                        else:
                            ing_nm = (obj.get("ingredient") or "").strip().lower()
                            if (
                                ing_nm
                                and not ing_nm.endswith("water")
                                and ing_nm not in ("ice", "ice cube", "ice cubes")
                            ):
                                categories["unlinkedNonWaterStepToken"].append(
                                    {"title": rec["title"], "step": snum, "token": raw, "sentence": sent}
                                )
                        if obj.get("amount") is None and (not iid or iid not in sub_ing_ids):
                            categories["unquantifiedNonSubRecipeToken"].append(
                                {"title": rec["title"], "step": snum, "token": raw, "sentence": sent}
                            )

                elif not is_tok:
                    if UNTOK_MEASURE_RE.search(raw):
                        categories["untokenizedQuantitiesInProse"].append(
                            {"title": rec["title"], "step": snum, "kind": "measurement", "snippet": raw.strip()[:120]}
                        )
                    if UNTOK_TEMP_RE.search(raw) and not ROTATE_RE.search(raw):
                        categories["untokenizedQuantitiesInProse"].append(
                            {"title": rec["title"], "step": snum, "kind": "temperature", "snippet": raw.strip()[:120]}
                        )
                    if UNTOK_TIMER_RE.search(raw):
                        categories["untokenizedQuantitiesInProse"].append(
                            {"title": rec["title"], "step": snum, "kind": "timer", "snippet": raw.strip()[:120]}
                        )
                    if UNTOK_DIM_RE.search(raw):
                        categories["untokenizedQuantitiesInProse"].append(
                            {"title": rec["title"], "step": snum, "kind": "dimension", "snippet": raw.strip()[:120]}
                        )

            dup = re.search(r"\b([a-z]{3,})\s+\1\b", plain, re.I)
            if dup and dup.group(1).lower() not in ("that", "had", "very"):
                categories["proseArtifacts"].append(
                    {"title": rec["title"], "step": snum, "kind": "duplicate_word", "match": dup.group(0), "plain": plain[:180]}
                )
            if (
                "()" in plain
                or "( )" in plain
                or "{" in plain
                or "||" in plain
                or ".." in plain
                or "((" in plain
                or re.search(r"\b(?:of\s+the)\s*[.,;)]", plain, re.I)
                or re.search(r"[a-z]\.[A-Z]", plain)
            ):
                categories["proseArtifacts"].append(
                    {"title": rec["title"], "step": snum, "kind": "punctuation_or_leak", "plain": plain[:200]}
                )

        full_plain_lower = " ".join(plain_steps).lower()
        for ing in ings:
            if (
                ing["id"] not in linked_ing_ids
                and not ing.get("isOptional")
                and not ing.get("linkedRecipeId")
                and ing.get("quantity") is not None
            ):
                dn = re.sub(r"\([^)]*\)", "", ing["displayName"]).strip().lower()
                head = dn.split()[-1] if dn else ""
                dn_hits = len(re.findall(rf"\b{re.escape(dn)}\b", full_plain_lower)) if len(dn) >= 3 else 0
                head_hits = (
                    len(re.findall(rf"\b{re.escape(head)}\b", full_plain_lower))
                    if len(head) >= 4 and head not in STOP_HEADS
                    else 0
                )
                if dn_hits == 1 or (dn_hits == 0 and head_hits == 1):
                    categories["missedSingleUseInjection"].append(
                        {
                            "title": rec["title"],
                            "ingredient": ing["displayName"],
                            "qty": ing.get("quantity"),
                            "unit": ing.get("unit"),
                        }
                    )

    total_wave = len(wave_job_ids)
    pub_count = len(pub_details)
    self_contained_total = max(0, total_wave - external_missing_subrecipe_count)
    overall_rate = (pub_count / total_wave * 100.0) if total_wave > 0 else 0.0
    self_contained_rate = (
        pub_count / self_contained_total * 100.0 if self_contained_total > 0 else 100.0
    )

    score_vals = [float(s["confidenceScore"]) for s in scores if s["confidenceScore"] is not None]
    report = {
        "totals": {
            "waveJobs": total_wave,
            "published": pub_count,
            "readyForReview": len(non_published_details),
            "externalMissingSubRecipeCount": external_missing_subrecipe_count,
            "selfContainedTotal": self_contained_total,
            "overallAutoPublishRatePct": round(overall_rate, 2),
            "selfContainedAutoPublishRatePct": round(self_contained_rate, 2),
            "enableIngestion": worker_state.get("enableIngestion"),
        },
        "foodCatalog": {
            "totalFoodItems": len(food_items),
            "sampleNames": [fi.get("name") for fi in food_items[:25]],
        },
        "confidenceStats": {
            "min": min(score_vals) if score_vals else None,
            "max": max(score_vals) if score_vals else None,
            "mean": round(statistics.mean(score_vals), 3) if score_vals else None,
            "distinctValues": sorted({round(v, 2) for v in score_vals}),
            "scoresByRecipe": sorted(
                scores, key=lambda x: (x["confidenceScore"] or 0, x["title"])
            ),
        },
        "categoryCounts": {k: len(v) for k, v in categories.items()},
        "categories": categories,
        "fixedTokensCount": len(fixed_tokens_seen),
        "nonPublished": non_published_details,
    }

    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(report, indent=2), encoding="utf-8")

    print("=== BULK IMPORT WAVE AUDIT ===", flush=True)
    print("Totals:", json.dumps(report["totals"]), flush=True)
    print("FoodCatalog:", json.dumps({"totalFoodItems": report["foodCatalog"]["totalFoodItems"]}), flush=True)
    print(
        "Confidence:",
        json.dumps({k: v for k, v in report["confidenceStats"].items() if k != "scoresByRecipe"}),
        flush=True,
    )
    print("CategoryCounts:", json.dumps(report["categoryCounts"], indent=2), flush=True)
    print(f"Audit saved to {out_path}", flush=True)
    return 0


_DRAFT_UNIT_TO_EXT: dict[str, str | None] = {
    "0": "teaspoon",
    "tsp": "teaspoon",
    "1": "tablespoon",
    "tbsp": "tablespoon",
    "2": "fluid ounce",
    "floz": "fluid ounce",
    "3": "cup",
    "cup": "cup",
    "4": "pint",
    "pt": "pint",
    "5": "quart",
    "qt": "quart",
    "6": "gallon",
    "gal": "gallon",
    "7": "milliliter",
    "ml": "milliliter",
    "8": "liter",
    "l": "liter",
    "9": "ounce",
    "oz": "ounce",
    "10": "pound",
    "lb": "pound",
    "11": "gram",
    "g": "gram",
    "12": "kilogram",
    "kg": "kilogram",
    "13": "pinch",
    "pinch": "pinch",
    "14": "dash",
    "dash": "dash",
    "15": "clove",
    "clove": "clove",
    "16": "can",
    "can": "can",
    "17": "bunch",
    "bunch": "bunch",
    "18": "slice",
    "slice": "slice",
    "19": None,
    "whole": None,
}

_DIM_UNIT_TO_EXT: dict[str, str] = {
    "1": "inch",
    "inch": "inch",
    "inches": "inch",
    "2": "foot",
    "foot": "foot",
    "feet": "foot",
    "3": "yard",
    "yard": "yard",
    "yards": "yard",
    "4": "millimeter",
    "mm": "millimeter",
    "5": "centimeter",
    "cm": "centimeter",
    "6": "meter",
    "m": "meter",
}


def _map_draft_unit(unit_val: Any, custom_unit: str | None) -> str | None:
    if unit_val is None:
        return str(custom_unit).strip() if custom_unit and str(custom_unit).strip() else None
    key = str(unit_val).strip().lower()
    if key in ("20", "custom"):
        return str(custom_unit).strip() if custom_unit and str(custom_unit).strip() else None
    if key in _DRAFT_UNIT_TO_EXT:
        return _DRAFT_UNIT_TO_EXT[key]
    return key or None


def _format_qty_range(amt: float, max_amt: float | None) -> str:
    def fmt(v: float) -> str:
        return f"{v:g}" if abs(v - round(v)) < 1e-6 else f"{v:.4f}".rstrip("0").rstrip(".")

    if max_amt is not None and max_amt > amt:
        return f"{fmt(amt)} to {fmt(max_amt)}"
    return fmt(amt)


def _draft_step_to_external(instruction: str) -> tuple[str, list[dict[str, Any]]]:
    segs = parse_tokens(instruction)
    ext_tokens: list[dict[str, Any]] = []
    plain_parts: list[str] = []

    for is_tok, obj, raw in segs:
        if not is_tok:
            ext_tokens.append({"type": "text", "text": raw})
            plain_parts.append(raw)
            continue

        if not isinstance(obj, dict):
            ext_tokens.append({"type": "text", "text": raw})
            plain_parts.append(raw)
            continue

        ttype = str(obj.get("type") or "ingredient").strip().lower()
        if ttype in ("ingredient", "mixture"):
            name = str(obj.get("ingredient") if ttype == "ingredient" else obj.get("name") or "").strip()
            amt = _opt_float(obj.get("amount"))
            max_amt = _opt_float(obj.get("maxAmount"))
            if max_amt is not None and amt is not None and max_amt <= amt:
                max_amt = None
            unit = _map_draft_unit(obj.get("unit"), obj.get("customUnitName"))
            if amt is None or amt <= 0:
                ext_tokens.append({"type": "text", "text": name})
                plain_parts.append(name)
            else:
                qtxt = _format_qty_range(amt, max_amt)
                plain = " ".join(p for p in [qtxt, unit, name] if p)
                plain_parts.append(plain)
                tok_out: dict[str, Any] = {
                    "type": ttype,
                    "name": name,
                    "quantity": amt,
                    "maxQuantity": max_amt,
                    "unit": unit,
                    "fixed": bool(obj.get("fixed", False)),
                }
                if ttype == "ingredient":
                    raw_idx = obj.get("ingredientIndex")
                    tok_out["ingredientIndex"] = int(raw_idx) if raw_idx is not None else None
                ext_tokens.append(tok_out)
        elif ttype == "fixed":
            name = str(obj["name"]).strip() if obj.get("name") else None
            amt = _opt_float(obj.get("amount"))
            max_amt = _opt_float(obj.get("maxAmount"))
            unit = _map_draft_unit(obj.get("unit"), obj.get("customUnitName"))
            hyph = bool(obj.get("hyphenated", False))
            if amt is None or amt <= 0:
                txt = name or raw
                ext_tokens.append({"type": "text", "text": txt})
                plain_parts.append(txt)
            else:
                qtxt = _format_qty_range(amt, max_amt)
                joiner = "-" if hyph else " "
                head = f"{qtxt}{joiner}{unit}" if unit else qtxt
                plain = f"{head} {name}".strip() if name else head
                plain_parts.append(plain)
                ext_tokens.append(
                    {
                        "type": "fixed",
                        "name": name,
                        "amount": amt,
                        "maxAmount": max_amt,
                        "unit": unit,
                        "hyphenated": hyph,
                    }
                )
        elif ttype == "dimension":
            val = _opt_float(obj.get("value")) or 0.0
            max_val = _opt_float(obj.get("maxValue"))
            dim_key = str(obj.get("unit") or "inch").strip().lower()
            dim_unit = _DIM_UNIT_TO_EXT.get(dim_key, "inch")
            hyph = bool(obj.get("hyphenated", False))
            qtxt = _format_qty_range(val, max_val)
            plain_parts.append(f"{qtxt}{'-' if hyph else ' '}{dim_unit}")
            ext_tokens.append(
                {
                    "type": "dimension",
                    "amount": val,
                    "maxAmount": max_val,
                    "unit": dim_unit,
                    "hyphenated": hyph,
                }
            )
        elif ttype == "timer":
            sec = int(obj.get("seconds") or 0)
            max_sec = _opt_int(obj.get("maxSeconds"))
            if sec > 0 and sec % 86400 == 0 and (max_sec is None or max_sec % 86400 == 0):
                t_unit, div = "day", 86400.0
            elif sec > 0 and sec % 3600 == 0 and (max_sec is None or max_sec % 3600 == 0):
                t_unit, div = "hour", 3600.0
            elif sec >= 60 and (max_sec is None or max_sec >= 60):
                t_unit, div = "minute", 60.0
            else:
                t_unit, div = "second", 1.0
            amt = round(sec / div, 4)
            max_amt = round(max_sec / div, 4) if max_sec and max_sec > sec else None
            label = str(obj.get("label") or "").strip() or "Timer"
            plain_parts.append(f"{_format_qty_range(amt, max_amt)} {t_unit}")
            ext_tokens.append(
                {
                    "type": "timer",
                    "amount": amt,
                    "maxAmount": max_amt,
                    "unit": t_unit,
                    "label": label,
                }
            )
        elif ttype == "temperature":
            f_val = _opt_float(obj.get("fahrenheit"))
            c_val = _opt_float(obj.get("celsius"))
            if f_val is not None:
                amt, max_amt, t_unit = f_val, _opt_float(obj.get("maxFahrenheit")), "fahrenheit"
            else:
                amt = c_val or 0.0
                max_amt, t_unit = _opt_float(obj.get("maxCelsius")), "celsius"
            plain_parts.append(f"{_format_qty_range(amt, max_amt)} degrees")
            ext_tokens.append(
                {
                    "type": "temperature",
                    "amount": amt,
                    "maxAmount": max_amt,
                    "unit": t_unit,
                }
            )
        else:
            ext_tokens.append({"type": "text", "text": raw})
            plain_parts.append(raw)

    return "".join(plain_parts).strip(), ext_tokens


def draft_to_external_extraction(draft: dict[str, Any]) -> dict[str, Any]:
    """Reconstruct an ExternalRecipeExtractionDto dict from a server IngestionExtractedDraftDto."""
    sections_map: dict[str | None, dict[str, Any]] = {}
    section_order: list[str | None] = []

    def get_sec(sec_name_raw: Any) -> dict[str, Any]:
        key = str(sec_name_raw).strip() if sec_name_raw and str(sec_name_raw).strip() else None
        if key not in sections_map:
            sections_map[key] = {"name": key, "ingredients": [], "steps": []}
            section_order.append(key)
        return sections_map[key]

    for ing in draft.get("ingredients") or []:
        sec = get_sec(ing.get("sectionName"))
        reps_out: list[dict[str, Any]] = []
        for rep in ing.get("replacements") or []:
            rname = str(rep.get("displayName") or "").strip()
            if not rname:
                continue
            r_unit = _map_draft_unit(rep.get("unit"), rep.get("customUnitName"))
            r_cat = str(rep.get("suggestedCategory") or "").strip() or None
            reps_out.append(
                {
                    "name": rname,
                    "quantity": _opt_float(rep.get("quantity")),
                    "maxQuantity": _opt_float(rep.get("maxQuantity")),
                    "unit": r_unit,
                    "prepNote": (str(rep["preparationNote"]).strip() if rep.get("preparationNote") else None),
                    "canonicalFoodName": (
                        str(rep.get("matchedFoodItemName") or rep.get("proposedFoodItemName") or "").strip().lower()
                        or None
                    ),
                    "category": r_cat,
                    "isPantryStaple": None,
                }
            )

        alt_list: list[dict[str, Any]] = []
        alt_m = ing.get("alternativeMeasurement")
        if isinstance(alt_m, dict) and _opt_float(alt_m.get("quantity")) is not None:
            alt_list.append(
                {
                    "quantity": _opt_float(alt_m.get("quantity")),
                    "unit": _map_draft_unit(alt_m.get("unit"), alt_m.get("customUnitName")),
                }
            )

        is_ref = bool(ing.get("isRecipeReference", False))
        canon_name = None
        if not is_ref:
            raw_canon = ing.get("matchedFoodItemName") or ing.get("proposedFoodItemName")
            canon_name = str(raw_canon).strip().lower() if raw_canon and str(raw_canon).strip() else None

        sec["ingredients"].append(
            {
                "name": str(ing.get("displayName") or "").strip(),
                "quantity": _opt_float(ing.get("quantity")),
                "maxQuantity": _opt_float(ing.get("maxQuantity")),
                "unit": _map_draft_unit(ing.get("unit"), ing.get("customUnitName")),
                "prepNote": (str(ing["preparationNote"]).strip() if ing.get("preparationNote") else None),
                "isOptional": bool(ing.get("isOptional", False)),
                "canonicalFoodName": canon_name,
                "category": (str(ing["suggestedCategory"]).strip() if ing.get("suggestedCategory") else None),
                "isPantryStaple": bool(ing["suggestedIsPantryStaple"]) if "suggestedIsPantryStaple" in ing else None,
                "isRecipeReference": is_ref,
                "referencedRecipeTitle": (
                    str(ing["referencedRecipeTitle"]).strip() if ing.get("referencedRecipeTitle") else None
                ),
                "replacements": reps_out,
                "alternateMeasurements": alt_list,
            }
        )

    for st in draft.get("steps") or []:
        sec = get_sec(st.get("sectionName"))
        plain_instr, ext_tokens = _draft_step_to_external(str(st.get("instruction") or ""))
        sec["steps"].append({"instruction": plain_instr, "tokens": ext_tokens})

    timing = None
    if any(draft.get(k) is not None for k in ("prepTimeMinutes", "cookTimeMinutes", "totalTimeMinutes")):
        timing = {
            "prep": _opt_int(draft.get("prepTimeMinutes")),
            "cook": _opt_int(draft.get("cookTimeMinutes")),
            "total": _opt_int(draft.get("totalTimeMinutes")),
        }

    return normalize_extraction(
        {
            "title": draft.get("title") or "Untitled Recipe",
            "description": draft.get("description"),
            "servings": draft.get("baseServings"),
            "yield": draft.get("yieldDisplay"),
            "sections": [sections_map[k] for k in section_order],
            "timing": timing,
            "tags": draft.get("proposedTagNames") or [],
        }
    )


def _fetch_paged_items(
    token: str,
    base_path: str,
) -> list[dict[str, Any]]:
    all_items: list[dict[str, Any]] = []
    page = 1
    sep = "&" if "?" in base_path else "?"
    while True:
        res = http_json("GET", f"{base_path}{sep}page={page}&pageSize=100", token=token, timeout=60) or {}
        items = res.get("items") or []
        all_items.extend(items)
        if not items or not (res.get("hasNextPage") or res.get("hasMore")):
            break
        page += 1
    return all_items


def _index_cached_extractions() -> dict[str, dict[str, Any]]:
    cached: dict[str, dict[str, Any]] = {}
    search_roots = [
        STATE_DIR,
        REPO_ROOT / ".agents" / "skills" / "repair-review-recipes" / ".state",
    ]
    files: list[Path] = []
    for root in search_roots:
        if root.exists():
            files.extend(root.rglob("batch_*_submitted.json"))
            files.extend(root.rglob("repair_*_submitted.json"))
    files.sort(key=lambda p: p.stat().st_mtime)

    for fp in files:
        try:
            data = json.loads(fp.read_text(encoding="utf-8"))
        except Exception:
            continue
        rows = data if isinstance(data, list) else ([data] if isinstance(data, dict) else [])
        for row in rows:
            if isinstance(row, dict) and row.get("jobId") and isinstance(row.get("extraction"), dict):
                ext = row["extraction"]
                if ext.get("sections"):
                    cached[str(row["jobId"])] = ext
    return cached


def cmd_plan_repairs(args: argparse.Namespace) -> int:
    token = get_token()
    set_ingestion_enabled(token, False)

    review_jobs = _fetch_paged_items(token, "/bulk-import/jobs?status=ReadyForReview")
    published_recipes = _fetch_paged_items(token, "/bulk-import/recipes")
    published_titles_norm = {
        norm_title(r.get("title") or "") for r in published_recipes if r.get("title")
    }
    cached_by_job = _index_cached_extractions()

    limit = max(1, int(args.limit))
    auto_published_precheck = 0
    skipped_waiting_sub = 0
    repair_items: list[dict[str, Any]] = []

    for job_sum in review_jobs[:limit]:
        jid = str(job_sum["id"])
        det = http_json("GET", f"/bulk-import/jobs/{jid}", token=token, timeout=60)
        draft = det.get("extractedDraft") or {}
        source_text = (det.get("normalizedSourceText") or det.get("rawText") or "").strip()
        if not source_text:
            continue

        v_issues = draft.get("validationIssues") or []
        q_warn = draft.get("qualityWarnings") or []
        m_warn = draft.get("measurementWarnings") or []

        def is_only_unpublished_subrecipe(val_list: list[str], qual_list: list[str], meas_list: list[str]) -> bool:
            non_sub = [i for i in val_list if not MISSING_SUBRECIPE_ISSUE_RE.search(i or "")]
            sub_only = [i for i in val_list if MISSING_SUBRECIPE_ISSUE_RE.search(i or "")]
            if non_sub or qual_list or meas_list or not sub_only:
                return False
            for si in sub_only:
                m = re.search(r"'([^']+)'", si)
                if m and norm_title(m.group(1)) in published_titles_norm:
                    return False
            return True

        if is_only_unpublished_subrecipe(v_issues, q_warn, m_warn):
            skipped_waiting_sub += 1
            continue

        prev_ext = cached_by_job.get(jid)
        if not prev_ext and draft.get("ingredients") and draft.get("steps"):
            prev_ext = draft_to_external_extraction(draft)
        if not prev_ext:
            prev_ext = {
                "title": det.get("displayTitle") or "Untitled Recipe",
                "description": None,
                "servings": None,
                "yield": None,
                "sections": [],
                "timing": None,
                "tags": [],
            }

        if not args.skip_precheck and prev_ext.get("sections"):
            try:
                res = http_json(
                    "POST",
                    f"/bulk-import/jobs/{jid}/submit-extraction",
                    token=token,
                    body={
                        "extraction": normalize_extraction(prev_ext),
                        "extractedModel": det.get("extractedModel") or "agy-recheck",
                        "autoPublish": True,
                        "allowOverwritePublished": False,
                        "computeEmbeddings": not bool(args.no_embeddings),
                        "repairCount": int(draft.get("pipelineRepairCount") or 0),
                    },
                    timeout=120,
                )
                if res.get("status") == "Published":
                    auto_published_precheck += 1
                    pub_title = (res.get("draft") or {}).get("title") or det.get("displayTitle") or ""
                    if pub_title:
                        published_titles_norm.add(norm_title(pub_title))
                    continue

                v_issues = res.get("validationIssues") or []
                q_warn = res.get("qualityWarnings") or []
                m_warn = res.get("measurementWarnings") or []
                if is_only_unpublished_subrecipe(v_issues, q_warn, m_warn):
                    skipped_waiting_sub += 1
                    continue
            except Exception:
                pass

        repair_items.append(
            {
                "jobId": jid,
                "title": draft.get("title") or det.get("displayTitle") or jid,
                "sourceText": source_text,
                "sourceBookTitle": det.get("sourceBookTitle"),
                "sourceBookPage": det.get("sourceBookPage"),
                "extraction": prev_ext,
                "validationIssues": v_issues,
                "qualityWarnings": q_warn,
                "measurementWarnings": m_warn,
                "baseRepairCount": int(draft.get("pipelineRepairCount") or 0),
            }
        )

    summary = {
        "totalReadyForReview": len(review_jobs),
        "inspectedCount": min(len(review_jobs), limit),
        "autoPublishedOnPrecheck": auto_published_precheck,
        "skippedWaitingOnSubRecipe": skipped_waiting_sub,
        "queuedForRepair": len(repair_items),
    }

    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps({"summary": summary, "items": repair_items}, indent=2), encoding="utf-8")
    print(json.dumps(summary))
    return 0


def cmd_enqueue_epub(args: argparse.Namespace) -> int:
    epub_script = REPO_ROOT / "scripts" / "import_atk_epub.py"
    if not epub_script.exists():
        raise FileNotFoundError(f"Could not find EPUB importer script at {epub_script}")

    cmd = [sys.executable, str(epub_script), "--import-api", "--url", _CONN["api_base"]]
    env = dict(os.environ)
    mcp_cfg = _CONN.get("mcp_config")
    if mcp_cfg:
        cmd.extend(["--mcp-config", str(mcp_cfg)])
    else:
        token = get_token()
        env["LOCALROAST_TOKEN"] = token

    if getattr(args, "epub", None):
        cmd.extend(["--epub", args.epub])
    if getattr(args, "collection", None):
        cmd.extend(["--collection", args.collection])
    if getattr(args, "chapter", None):
        cmd.extend(["--chapter", args.chapter])
    if getattr(args, "limit", None) is not None:
        cmd.extend(["--limit", str(args.limit)])
    if getattr(args, "offset", 0):
        cmd.extend(["--offset", str(args.offset)])
    if getattr(args, "no_images", False):
        cmd.append("--no-images")
    if getattr(args, "no_skip_existing", False):
        cmd.append("--no-skip-existing")

    res = subprocess.run(cmd, env=env, text=True)
    return int(res.returncode)


def build_parser() -> argparse.ArgumentParser:
    conn_parent = argparse.ArgumentParser(add_help=False)
    conn_parent.add_argument(
        "--mcp-config",
        default=argparse.SUPPRESS,
        help="Path to LocalRoast MCP config JSON containing server URL and Bearer lr_pat_... token",
    )
    conn_parent.add_argument(
        "--url",
        default=argparse.SUPPRESS,
        help="LocalRoast base URL (overrides MCP config URL; default: http://localhost:5041)",
    )
    conn_parent.add_argument(
        "--token",
        default=argparse.SUPPRESS,
        help="Explicit JWT or lr_pat_... Bearer token (overrides MCP config token)",
    )

    p = argparse.ArgumentParser(
        description="LocalRoast bulk recipe import helper",
        parents=[conn_parent],
    )
    sub = p.add_subparsers(dest="cmd", required=True)

    pw = sub.add_parser(
        "plan-wave",
        parents=[conn_parent],
        help="Select and topologically order a wave of queued jobs",
    )
    pw.add_argument("--size", type=int, default=30)
    pw.add_argument("--batch-size", type=int, default=5)
    pw.add_argument("--out", required=True)
    pw.add_argument("--pilot-benchmark", action="store_true")
    pw.set_defaults(func=cmd_plan_wave)

    sb = sub.add_parser(
        "submit-batch",
        parents=[conn_parent],
        help="Submit extracted batch JSON sequentially",
    )
    sb.add_argument("--in", dest="in_file", required=True)
    sb.add_argument("--wave", required=False)
    sb.add_argument("--out", required=True)
    sb.add_argument("--extracted-model", default="gemini-hydrogen (agy-workflow)")
    sb.add_argument("--repair-count", type=int, default=0)
    sb.add_argument("--allow-overwrite-published", action="store_true")
    sb.add_argument("--no-embeddings", action="store_true")
    sb.set_defaults(func=cmd_submit_batch)

    so = sub.add_parser(
        "submit-one",
        parents=[conn_parent],
        help="Submit single repaired recipe extraction",
    )
    so.add_argument("--job-id", required=True)
    so.add_argument("--in", dest="in_file", required=True)
    so.add_argument("--wave", required=False)
    so.add_argument("--out", required=True)
    so.add_argument("--extracted-model", default="gemini-pro (agy-workflow)")
    so.add_argument("--repair-count", type=int, default=1)
    so.add_argument("--allow-overwrite-published", action="store_true")
    so.add_argument("--no-embeddings", action="store_true")
    so.set_defaults(func=cmd_submit_one)

    au = sub.add_parser(
        "audit",
        parents=[conn_parent],
        help="Audit all jobs and published recipes in a wave",
    )
    au.add_argument("--wave", required=True)
    au.add_argument("--out", required=True)
    au.set_defaults(func=cmd_audit)

    eq = sub.add_parser(
        "enqueue-epub",
        parents=[conn_parent],
        help="Idempotently enqueue ATK EPUB recipes and dish photos via /api/bulk-import/*",
    )
    eq.add_argument("--epub", default=None)
    eq.add_argument("--collection", default=None)
    eq.add_argument("--chapter", default=None)
    eq.add_argument("--limit", type=int, default=None)
    eq.add_argument("--offset", type=int, default=0)
    eq.add_argument("--no-images", action="store_true")
    eq.add_argument("--no-skip-existing", action="store_true")
    eq.set_defaults(func=cmd_enqueue_epub)

    pr = sub.add_parser(
        "plan-repairs",
        parents=[conn_parent],
        help="Sweep ReadyForReview jobs, run 0-token C# pre-check, and queue repairable items",
    )
    pr.add_argument("--out", required=True)
    pr.add_argument("--limit", type=int, default=200)
    pr.add_argument("--skip-precheck", action="store_true")
    pr.add_argument("--no-embeddings", action="store_true")
    pr.set_defaults(func=cmd_plan_repairs)

    return p


def main() -> int:
    parser = build_parser()
    args = parser.parse_args()
    configure_connection(args)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
