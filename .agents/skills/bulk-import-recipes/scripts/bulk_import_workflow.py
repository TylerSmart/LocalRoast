"""Antigravity `run_workflow` script for high-quality batched recipe ingestion.

Invoke via Antigravity's `run_workflow` tool:
  ScriptPath: "d:/Personal/LocalRoast/.agents/skills/bulk-import-recipes/scripts/bulk_import_workflow.py"
  Args: {
    "waveSize": 30,
    "batchSize": 5,
    "maxParallel": 4,
    "pilotBenchmark": true,
    "computeEmbeddings": true
  }
"""

from __future__ import annotations

from datetime import datetime
import json
from pathlib import Path
import subprocess
import sys

SKILL_DIR = Path("d:/Personal/LocalRoast/.agents/skills/bulk-import-recipes")
HELPER_SCRIPT = SKILL_DIR / "scripts" / "bulk_import_helper.py"
PROMPT_GUIDE_PATH = SKILL_DIR / "resources" / "extraction-prompt.md"

FOOD_CATEGORIES = [
    "Produce",
    "MeatAndSeafood",
    "DairyAndEggs",
    "Refrigerated",
    "Bakery",
    "PantryDryGoods",
    "CannedAndJarred",
    "CondimentsAndSauces",
    "SpicesAndSeasonings",
    "Baking",
    "Snacks",
    "Beverages",
    "Frozen",
    "Other",
]

TOKEN_OBJECT_SCHEMA = {
    "type": "object",
    "properties": {
        "type": {
            "type": "string",
            "enum": [
                "text",
                "ingredient",
                "mixture",
                "timer",
                "temperature",
                "dimension",
                "fixed",
            ],
        },
        "text": {
            "type": "string",
            "description": "Required when type='text'. Verbatim prose/spaces/punctuation segment. Omit on non-text tokens.",
        },
        "ingredientIndex": {
            "type": "integer",
            "description": "Flat 0-based ingredient index across all sections (ingredient token only). Omit when incidental step-only water/ice.",
        },
        "name": {
            "type": "string",
            "description": "Ingredient/mixture/vessel name for ingredient, mixture, or fixed tokens. Omit on check-yield fixed, text, timer, temperature, dimension.",
        },
        "quantity": {
            "type": "number",
            "description": "Positive numeric quantity for ingredient/mixture tokens.",
        },
        "maxQuantity": {
            "type": "number",
            "description": "Upper range bound ONLY when a genuine numeric range is printed (ingredient/mixture).",
        },
        "unit": {
            "type": "string",
            "description": "Lowercase singular unit (omit when count-only on ingredient). For timer: second|minute|hour|day. For temperature: fahrenheit|celsius. For dimension: inch|foot|yard|millimeter|centimeter|meter.",
        },
        "fixed": {
            "type": "boolean",
            "description": "True ONLY for fixed per-item portions on ingredient/mixture tokens.",
        },
        "amount": {
            "type": "number",
            "description": "Positive numeric amount for timer, temperature, dimension, or fixed tokens.",
        },
        "maxAmount": {
            "type": "number",
            "description": "Upper range bound ONLY when printed on timer, temperature, dimension, or fixed tokens.",
        },
        "label": {
            "type": "string",
            "description": "Short action label on timer tokens (e.g. 'Simmer sauce').",
        },
        "hyphenated": {
            "type": "boolean",
            "description": "True when hyphenated in source (e.g. '12-inch', '12-cup') on dimension/fixed tokens.",
        },
    },
    "required": ["type"],
}

REPLACEMENT_SCHEMA = {
    "type": "object",
    "properties": {
        "name": {"type": "string"},
        "quantity": {"type": "number"},
        "maxQuantity": {"type": "number"},
        "unit": {"type": "string"},
        "prepNote": {"type": "string"},
        "canonicalFoodName": {"type": "string"},
        "category": {"type": "string", "enum": FOOD_CATEGORIES},
        "isPantryStaple": {"type": "boolean"},
    },
    "required": ["name"],
}

ALT_MEASUREMENT_SCHEMA = {
    "type": "object",
    "properties": {
        "quantity": {"type": "number"},
        "unit": {"type": "string"},
    },
    "required": ["quantity"],
}

INGREDIENT_SCHEMA = {
    "type": "object",
    "properties": {
        "name": {"type": "string"},
        "quantity": {"type": "number"},
        "maxQuantity": {"type": "number"},
        "unit": {"type": "string"},
        "prepNote": {"type": "string"},
        "isOptional": {"type": "boolean"},
        "canonicalFoodName": {"type": "string"},
        "category": {"type": "string", "enum": FOOD_CATEGORIES},
        "isPantryStaple": {"type": "boolean"},
        "isRecipeReference": {"type": "boolean"},
        "referencedRecipeTitle": {"type": "string"},
        "replacements": {"type": "array", "items": REPLACEMENT_SCHEMA},
        "alternateMeasurements": {"type": "array", "items": ALT_MEASUREMENT_SCHEMA},
    },
    "required": ["name"],
}

STEP_SCHEMA = {
    "type": "object",
    "properties": {
        "instruction": {"type": "string"},
        "tokens": {"type": "array", "items": TOKEN_OBJECT_SCHEMA},
    },
    "required": ["instruction", "tokens"],
}

SECTION_SCHEMA = {
    "type": "object",
    "properties": {
        "name": {"type": "string"},
        "ingredients": {"type": "array", "items": INGREDIENT_SCHEMA},
        "steps": {"type": "array", "items": STEP_SCHEMA},
    },
    "required": ["ingredients", "steps"],
}

TIMING_SCHEMA = {
    "type": "object",
    "properties": {
        "prep": {"type": "integer"},
        "cook": {"type": "integer"},
        "total": {"type": "integer"},
    },
}

RECIPE_EXTRACTION_SCHEMA = {
    "type": "object",
    "properties": {
        "title": {"type": "string"},
        "description": {"type": "string"},
        "servings": {"type": "number"},
        "yield": {"type": "string"},
        "sections": {"type": "array", "items": SECTION_SCHEMA},
        "timing": TIMING_SCHEMA,
        "tags": {"type": "array", "items": {"type": "string"}},
    },
    "required": ["title", "sections"],
}

RECIPE_ENTRY_SCHEMA = {
    "type": "object",
    "properties": {
        "jobId": {"type": "string"},
        "extraction": RECIPE_EXTRACTION_SCHEMA,
    },
    "required": ["jobId", "extraction"],
}

BATCH_EXTRACTION_SCHEMA = {
    "type": "object",
    "properties": {
        "recipes": {"type": "array", "items": RECIPE_ENTRY_SCHEMA},
    },
    "required": ["recipes"],
}


def run_helper(cmd_args: list[str]) -> str:
    res = subprocess.run(
        [sys.executable, str(HELPER_SCRIPT), *cmd_args],
        capture_output=True,
        text=True,
    )
    if res.returncode != 0:
        raise RuntimeError(
            f"Helper command failed ({' '.join(cmd_args)}):\nSTDOUT:\n{res.stdout}\nSTDERR:\n{res.stderr}"
        )
    return res.stdout.strip()


def build_batch_prompt(guide: str, jobs: list[dict]) -> str:
    jobs_block = []
    for idx, j in enumerate(jobs):
        meta_parts = [f"jobId={j['id']}", f"titleHint={j.get('displayTitle', '')}"]
        if j.get("sourceBookTitle"):
            meta_parts.append(f"book={j['sourceBookTitle']}")
        if j.get("sourceBookPage"):
            meta_parts.append(f"page={j['sourceBookPage']}")
        jobs_block.append(
            f"=== RECIPE {idx + 1} ({' | '.join(meta_parts)}) ===\n{j['sourceText']}"
        )

    joined_recipes = "\n\n".join(jobs_block)
    return (
        f"Extract & tokenize all {len(jobs)} recipes in this batch cleanly.\n\n"
        f"{guide}\n\n"
        "## Output Instructions\n"
        "- Return `{ \"recipes\": [ { \"jobId\": \"...\", \"extraction\": { ... } }, ... ] }` with ONE entry per input `jobId` in order.\n"
        "- Omit optional properties (`description`, `unit`, `prepNote`, `maxQuantity`, `maxAmount`, `canonicalFoodName`, `referencedRecipeTitle`, `replacements`, `alternateMeasurements`, `text`, etc.) whenever absent/not applicable rather than emitting JSON null.\n"
        "- NEVER call `read_file`, `run_command`, `search_web`, or any tools — call `finish` directly.\n\n"
        f"## Input Recipes ({len(jobs)})\n\n{joined_recipes}"
    )


def build_repair_prompt(guide: str, item: dict, source_text: str) -> str:
    diagnostics = {
        "validationIssues": item.get("validationIssues") or [],
        "qualityWarnings": item.get("qualityWarnings") or [],
        "measurementWarnings": item.get("measurementWarnings") or [],
        "submissionError": item.get("error"),
    }
    prev_json = json.dumps(item.get("extraction") or {}, indent=2)
    diag_json = json.dumps(diagnostics, indent=2)
    return (
        f"Repair recipe extraction: {item.get('title') or item['jobId']}\n\n"
        f"{guide}\n\n"
        "## Targeted Repair Instructions\n"
        "The LocalRoast C# validator flagged the diagnostics below on your previous extraction for this job.\n"
        "Fix EVERY flagged validation issue, quality warning, or measurement warning faithfully against the source text, "
        "preserving all valid ingredients, SKU names, sections, and tokens.\n"
        "- Return `{ \"jobId\": \"" + item["jobId"] + "\", \"extraction\": { ... } }` via `finish`.\n"
        "- Omit optional fields when unused rather than emitting null.\n"
        "- Do NOT call `read_file`, `run_command`, or any workspace tools.\n\n"
        f"## C# Validator Diagnostics\n```json\n{diag_json}\n```\n\n"
        f"## Previous Extraction JSON\n```json\n{prev_json}\n```\n\n"
        f"## Source Text\n{source_text}"
    )


async def extract_batch_safe(batch: dict, guide: str) -> dict:
    bidx = batch["batchIndex"]
    jobs = batch["jobs"]
    prompt = build_batch_prompt(guide, jobs)
    try:
        res = await agent(
            prompt=prompt,
            schema=BATCH_EXTRACTION_SCHEMA,
            role=f"Extract batch {bidx} ({len(jobs)} recipes)",
            model="flash",
        )
        return {"ok": True, "batchIndex": bidx, "data": res}
    except Exception as exc:
        return {"ok": False, "batchIndex": bidx, "error": str(exc)}


async def repair_one_safe(item: dict, source_text: str, guide: str) -> dict:
    jid = item["jobId"]
    title = item.get("title") or jid
    prompt = build_repair_prompt(guide, item, source_text)
    try:
        res = await agent(
            prompt=prompt,
            schema=RECIPE_ENTRY_SCHEMA,
            role=f"Repair: {title[:45]}",
            model="pro",
        )
        return {"ok": True, "jobId": jid, "data": res}
    except Exception as exc:
        return {"ok": False, "jobId": jid, "error": str(exc)}


async def main() -> dict:
    wave_size = int(args.get("waveSize", 30))
    batch_size = int(args.get("batchSize", 5))
    max_parallel = max(1, int(args.get("maxParallel", 4)))
    pilot_benchmark = bool(args.get("pilotBenchmark", False))
    extracted_model = str(args.get("extractedModel", "gemini-flash (agy-workflow)"))
    repaired_model = str(args.get("repairedModel", "gemini-pro (agy-workflow)"))
    compute_embeddings = bool(args.get("computeEmbeddings", True))
    allow_overwrite = bool(args.get("allowOverwritePublished", False))

    run_stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    work_dir = Path(
        args.get("workDir") or str(SKILL_DIR / ".state" / f"wave-{run_stamp}")
    )
    work_dir.mkdir(parents=True, exist_ok=True)
    wave_file = work_dir / "wave.json"

    guide = PROMPT_GUIDE_PATH.read_text(encoding="utf-8")

    # ============================================================
    # PHASE 1: Topological Wave Planning
    # ============================================================
    phase("Plan Wave")
    plan_cmd = [
        "plan-wave",
        "--size",
        str(wave_size),
        "--batch-size",
        str(batch_size),
        "--out",
        str(wave_file),
    ]
    if pilot_benchmark:
        plan_cmd.append("--pilot-benchmark")

    plan_stdout = run_helper(plan_cmd)
    plan_summary = json.loads(plan_stdout)
    log(f"Planned wave in {work_dir}: {json.dumps(plan_summary)}")

    wave_data = json.loads(wave_file.read_text(encoding="utf-8"))
    batches = wave_data.get("batches") or []
    if not batches:
        log("No queued recipes selected for this wave.")
        return {"planned": plan_summary, "submitted": 0, "repaired": 0}

    source_by_job_id = {
        j["id"]: j["sourceText"]
        for b in batches
        for j in b.get("jobs") or []
    }
    title_by_job_id = {
        j["id"]: j["displayTitle"]
        for b in batches
        for j in b.get("jobs") or []
    }

    # ============================================================
    # PHASE 2: Batched Parallel Extraction (Flash) + Sequential Submit
    # ============================================================
    phase(f"Extract Batches - Flash ({len(batches)} batches, maxParallel={max_parallel})")
    all_submission_outcomes: dict[str, dict] = {}
    failed_or_missing_for_pro: list[dict] = []

    for chunk_start in range(0, len(batches), max_parallel):
        chunk_batches = batches[chunk_start : chunk_start + max_parallel]
        batch_results = await parallel(
            chunk_batches,
            lambda b: extract_batch_safe(b, guide),
        )

        # Submit sequentially in deterministic batch order so prerequisites & FoodItems persist in order
        for batch, bres in zip(chunk_batches, batch_results):
            bidx = batch["batchIndex"]
            expected_ids = [j["id"] for j in batch["jobs"]]

            if not bres.get("ok"):
                log(f"Batch {bidx} Flash agent error: {bres.get('error')}")
                for jid in expected_ids:
                    failed_or_missing_for_pro.append(
                        {
                            "jobId": jid,
                            "title": title_by_job_id.get(jid, jid),
                            "error": bres.get("error"),
                            "validationIssues": [],
                            "qualityWarnings": [],
                            "measurementWarnings": [],
                            "extraction": {},
                        }
                    )
                continue

            recipes_list = (bres.get("data") or {}).get("recipes") or []
            extracted_in_path = work_dir / f"batch_{bidx:03d}_extracted.json"
            submitted_out_path = work_dir / f"batch_{bidx:03d}_submitted.json"
            extracted_in_path.write_text(
                json.dumps({"recipes": recipes_list}, indent=2),
                encoding="utf-8",
            )

            submit_cmd = [
                "submit-batch",
                "--in",
                str(extracted_in_path),
                "--wave",
                str(wave_file),
                "--out",
                str(submitted_out_path),
                "--extracted-model",
                extracted_model,
            ]
            if allow_overwrite:
                submit_cmd.append("--allow-overwrite-published")
            if not compute_embeddings:
                submit_cmd.append("--no-embeddings")

            submit_stdout = run_helper(submit_cmd)
            log(f"Batch {bidx} submit summary: {submit_stdout}")

            submitted_rows = json.loads(submitted_out_path.read_text(encoding="utf-8"))
            seen_ids = set()
            for row in submitted_rows:
                jid = row["jobId"]
                seen_ids.add(jid)
                all_submission_outcomes[jid] = row
                if row.get("needsRepair"):
                    failed_or_missing_for_pro.append(row)

            for jid in expected_ids:
                if jid not in seen_ids:
                    log(f"Batch {bidx} omitted job {jid} ({title_by_job_id.get(jid)}); queuing for Phase 3.")
                    failed_or_missing_for_pro.append(
                        {
                            "jobId": jid,
                            "title": title_by_job_id.get(jid, jid),
                            "error": "Omitted from Flash batch response.",
                            "validationIssues": [],
                            "qualityWarnings": [],
                            "measurementWarnings": [],
                            "extraction": {},
                        }
                    )

    # ============================================================
    # PHASE 3: Targeted Repair (Pro)
    # ============================================================
    phase(f"Targeted Repair - Pro ({len(failed_or_missing_for_pro)} job(s))")
    repaired_count = 0
    if failed_or_missing_for_pro:
        for r_start in range(0, len(failed_or_missing_for_pro), max_parallel):
            r_chunk = failed_or_missing_for_pro[r_start : r_start + max_parallel]
            repair_results = await parallel(
                r_chunk,
                lambda item: repair_one_safe(
                    item,
                    source_by_job_id.get(item["jobId"], ""),
                    guide,
                ),
            )

            for item, rres in zip(r_chunk, repair_results):
                jid = item["jobId"]
                if not rres.get("ok"):
                    log(f"Repair failed on Pro for {item.get('title')}: {rres.get('error')}")
                    continue

                rep_in_path = work_dir / f"repair_{jid}.json"
                rep_out_path = work_dir / f"repair_{jid}_submitted.json"
                rep_in_path.write_text(
                    json.dumps(rres["data"], indent=2),
                    encoding="utf-8",
                )

                so_cmd = [
                    "submit-one",
                    "--job-id",
                    jid,
                    "--in",
                    str(rep_in_path),
                    "--wave",
                    str(wave_file),
                    "--out",
                    str(rep_out_path),
                    "--extracted-model",
                    repaired_model,
                    "--repair-count",
                    "1",
                ]
                if allow_overwrite:
                    so_cmd.append("--allow-overwrite-published")
                if not compute_embeddings:
                    so_cmd.append("--no-embeddings")

                so_stdout = run_helper(so_cmd)
                log(f"Repaired {item.get('title')}: {so_stdout}")
                repaired_row = json.loads(rep_out_path.read_text(encoding="utf-8"))
                all_submission_outcomes[jid] = repaired_row
                repaired_count += 1
    else:
        log("Zero recipes needed Phase 3 Pro repair!")

    # ============================================================
    # PHASE 4: Deep Audit & Summary
    # ============================================================
    phase("Wave Audit")
    audit_path = work_dir / "audit.json"
    audit_stdout = run_helper(["audit", "--wave", str(wave_file), "--out", str(audit_path)])
    for line in audit_stdout.splitlines():
        log(line)

    audit_data = json.loads(audit_path.read_text(encoding="utf-8"))
    return {
        "workDir": str(work_dir),
        "totals": audit_data.get("totals"),
        "categoryCounts": audit_data.get("categoryCounts"),
        "confidenceStats": {
            k: v
            for k, v in (audit_data.get("confidenceStats") or {}).items()
            if k != "scoresByRecipe"
        },
        "foodCatalog": {
            "totalFoodItems": (audit_data.get("foodCatalog") or {}).get("totalFoodItems")
        },
        "repairedAttempted": repaired_count,
        "nonPublishedCount": len(audit_data.get("nonPublished") or []),
    }


result = await main()
print(json.dumps(result, indent=2))
