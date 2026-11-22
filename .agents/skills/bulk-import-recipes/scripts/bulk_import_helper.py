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
import sys
import unicodedata
import urllib.error
import urllib.parse
import urllib.request
from typing import Any

API_BASE = os.environ.get("LOCALROAST_API", "http://localhost:5041/api")
SKILL_DIR = Path(__file__).resolve().parent.parent
STATE_DIR = SKILL_DIR / ".state"
TOKEN_CACHE = STATE_DIR / ".runner_token.json"

DEFAULT_EMAIL = os.environ.get("LOCALROAST_RUNNER_EMAIL", "dev-runner@localroast.local")
DEFAULT_USER = os.environ.get("LOCALROAST_RUNNER_USER", "devrunner")
DEFAULT_PASS = os.environ.get("LOCALROAST_RUNNER_PASS", "LocalRoast_DevRunner_2026!")

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


def http_json(
    method: str,
    path: str,
    token: str | None = None,
    body: Any = None,
    timeout: int = 60,
) -> Any:
    url = f"{API_BASE}{path}"
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


def get_token(force_refresh: bool = False) -> str:
    STATE_DIR.mkdir(parents=True, exist_ok=True)
    if not force_refresh and TOKEN_CACHE.exists():
        try:
            cached = json.loads(TOKEN_CACHE.read_text(encoding="utf-8"))
            token = cached.get("token")
            if token:
                http_json("GET", "/account", token=token, timeout=10)
                return token
        except Exception:
            pass

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
    TOKEN_CACHE.write_text(json.dumps({"token": token}), encoding="utf-8")
    return token


def set_ingestion_enabled(token: str, enabled: bool) -> dict[str, Any]:
    settings = http_json("GET", "/llm-settings", token=token)
    payload = {
        "textModelProfileId": settings.get("textModelProfileId"),
        "visionModelProfileId": settings.get("visionModelProfileId"),
        "refineModelProfileId": settings.get("refineModelProfileId"),
        "embeddingModelProfileId": settings.get("embeddingModelProfileId"),
        "autoPublishHighConfidenceText": True,
        "maxIngestionRetries": settings.get("maxIngestionRetries", 2),
        "enableIngestion": enabled,
        "autoResumeQueue": settings.get("autoResumeQueue", False),
        "refineDuringIngestion": False,
        "refineDuringIdle": False,
    }
    return http_json("PUT", "/llm-settings/workflows", token=token, body=payload)


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
        "/ingestion/jobs/batch-source",
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
                f"/ingestion/jobs/{job_id}/submit-extraction",
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
        f"/ingestion/jobs/{args.job_id}/submit-extraction",
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

    llm_settings = http_json("GET", "/llm-settings", token=token)
    food_items_raw = http_json("GET", "/food-items", token=token)
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
        det = http_json("GET", f"/ingestion/jobs/{jid}", token=token)
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
        rec = http_json("GET", f"/recipes/{rid}", token=token)
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
            "enableIngestion": llm_settings.get("enableIngestion"),
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


def build_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(description="LocalRoast bulk recipe import helper")
    sub = p.add_subparsers(dest="cmd", required=True)

    pw = sub.add_parser("plan-wave", help="Select and topologically order a wave of queued jobs")
    pw.add_argument("--size", type=int, default=30)
    pw.add_argument("--batch-size", type=int, default=5)
    pw.add_argument("--out", required=True)
    pw.add_argument("--pilot-benchmark", action="store_true")
    pw.set_defaults(func=cmd_plan_wave)

    sb = sub.add_parser("submit-batch", help="Submit extracted batch JSON sequentially")
    sb.add_argument("--in", dest="in_file", required=True)
    sb.add_argument("--wave", required=False)
    sb.add_argument("--out", required=True)
    sb.add_argument("--extracted-model", default="gemini-flash (agy-workflow)")
    sb.add_argument("--repair-count", type=int, default=0)
    sb.add_argument("--allow-overwrite-published", action="store_true")
    sb.add_argument("--no-embeddings", action="store_true")
    sb.set_defaults(func=cmd_submit_batch)

    so = sub.add_parser("submit-one", help="Submit single repaired recipe extraction")
    so.add_argument("--job-id", required=True)
    so.add_argument("--in", dest="in_file", required=True)
    so.add_argument("--wave", required=False)
    so.add_argument("--out", required=True)
    so.add_argument("--extracted-model", default="gemini-pro (agy-workflow)")
    so.add_argument("--repair-count", type=int, default=1)
    so.add_argument("--allow-overwrite-published", action="store_true")
    so.add_argument("--no-embeddings", action="store_true")
    so.set_defaults(func=cmd_submit_one)

    au = sub.add_parser("audit", help="Audit all jobs and published recipes in a wave")
    au.add_argument("--wave", required=True)
    au.add_argument("--out", required=True)
    au.set_defaults(func=cmd_audit)

    return p


def main() -> int:
    parser = build_parser()
    args = parser.parse_args()
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
