"""Antigravity `run_workflow` script for multi-round recipe repair (ReadyForReview -> Published).

Run this after switching the Antigravity chat model to Neon (defaults to `agentModel="inherit"`).

Invoke via Antigravity's `run_workflow` tool:
  ScriptPath: "d:/Personal/LocalRoast/.agents/skills/repair-review-recipes/scripts/repair_review_workflow.py"
  Args: {
    "limit": 200,
    "maxParallel": 4,
    "maxRounds": 3,
    "computeEmbeddings": true
  }
"""

from __future__ import annotations

import asyncio
from datetime import datetime
import json
from pathlib import Path
import subprocess
import sys

SKILL_DIR = Path("d:/Personal/LocalRoast/.agents/skills/repair-review-recipes")
BULK_SKILL_DIR = SKILL_DIR.parent / "bulk-import-recipes"
HELPER_SCRIPT = BULK_SKILL_DIR / "scripts" / "bulk_import_helper.py"
PROMPT_GUIDE_PATH = BULK_SKILL_DIR / "resources" / "extraction-prompt.md"

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
    "required": ["title", "description", "sections"],
}

RECIPE_ENTRY_SCHEMA = {
    "type": "object",
    "properties": {
        "jobId": {"type": "string"},
        "extraction": RECIPE_EXTRACTION_SCHEMA,
    },
    "required": ["jobId", "extraction"],
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


def _format_diagnostics(item: dict) -> dict:
    return {
        "status": item.get("status"),
        "confidenceScore": item.get("confidenceScore"),
        "validationIssues": item.get("validationIssues") or [],
        "qualityWarnings": item.get("qualityWarnings") or [],
        "measurementWarnings": item.get("measurementWarnings") or [],
        "submissionError": item.get("error"),
    }


def build_initial_repair_prompt(guide: str, item: dict) -> str:
    source_text = item.get("sourceText") or ""
    meta_parts = [f"jobId={item['jobId']}"]
    if item.get("sourceBookTitle"):
        meta_parts.append(f"book={item['sourceBookTitle']}")
    if item.get("sourceBookPage"):
        meta_parts.append(f"page={item['sourceBookPage']}")

    diag_json = json.dumps(_format_diagnostics(item), indent=2)
    prev_json = json.dumps(item.get("extraction") or {}, indent=2)
    return (
        f"Repair recipe extraction: {item.get('title') or item['jobId']} ({' | '.join(meta_parts)})\n\n"
        f"{guide}\n\n"
        "## Targeted Repair Instructions (Round 1)\n"
        "The LocalRoast C# validator flagged the diagnostics below on this ReadyForReview recipe.\n"
        "Fix EVERY flagged validation issue, quality warning, or measurement warning faithfully against the source text, "
        "preserving all valid ingredients, canonicalFoodName values, sections, and tokens.\n"
        f'- Return `{{ "jobId": "{item["jobId"]}", "extraction": {{ ... }} }}` via `finish`.\n'
        "- Omit optional fields when unused rather than emitting null.\n"
        "- NEVER call `read_file`, `run_command`, `search_web`, or any workspace tools.\n\n"
        f"## C# Validator Diagnostics\n```json\n{diag_json}\n```\n\n"
        f"## Previous Extraction JSON\n```json\n{prev_json}\n```\n\n"
        f"## Source Text\n{source_text}"
    )


def build_followup_repair_prompt(item: dict, round_num: int, max_rounds: int) -> str:
    diag_json = json.dumps(_format_diagnostics(item), indent=2)
    return (
        f"Repair Follow-Up (Round {round_num} of {max_rounds}): {item.get('title') or item['jobId']}\n\n"
        "After compiling your previous extraction against LocalRoast's C# pipeline, "
        "these remaining diagnostics were still reported (or newly surfaced):\n\n"
        f"```json\n{diag_json}\n```\n\n"
        "Fix EVERY remaining diagnostic carefully against the Source Text from earlier in this conversation:\n"
        "- Preserve all fixes from the previous round that succeeded; do NOT reintroduce previously resolved errors.\n"
        "- Pay close attention to flat 0-based `ingredientIndex`, token-to-`instruction` spacing/text consistency, "
        "exact quantity totals across divided ingredient allocations, and unlinked vs intentionally water/ice tokens.\n"
        f'- Return the COMPLETE updated `{{ "jobId": "{item["jobId"]}", "extraction": {{ ... }} }}` via `finish`.\n'
        "- Omit optional fields when unused rather than emitting null.\n"
        "- Do NOT call any tools."
    )


async def repair_item_multi_round(
    item: dict,
    guide: str,
    agent_model: str,
    repaired_model: str,
    max_rounds: int,
    conn_args: list[str],
    work_dir: Path,
    allow_overwrite: bool,
    compute_embeddings: bool,
    submit_lock: asyncio.Lock,
) -> dict:
    jid = item["jobId"]
    title = item.get("title") or jid
    base_repair_count = int(item.get("baseRepairCount") or 0)
    cur_item = dict(item)
    rounds_run = 0
    last_outcome: dict | None = None
    promoted_ids: list[str] = []

    rep_agent = agent(role=f"Repair: {title[:45]}", model=agent_model)
    try:
        for round_num in range(1, max_rounds + 1):
            rounds_run = round_num
            turn_prompt = (
                build_initial_repair_prompt(guide, cur_item)
                if round_num == 1
                else build_followup_repair_prompt(cur_item, round_num, max_rounds)
            )
            try:
                res = await rep_agent(turn_prompt, RECIPE_ENTRY_SCHEMA)
            except Exception as exc:
                return {
                    "jobId": jid,
                    "title": title,
                    "roundsRun": rounds_run,
                    "status": (last_outcome or {}).get("status") or "AgentError",
                    "published": False,
                    "onlyExternalMissingSubRecipe": False,
                    "error": f"Round {round_num} agent error: {exc}",
                    "promotedWaitingRecipeIds": promoted_ids,
                    "outcome": last_outcome,
                }

            extraction = (res or {}).get("extraction") or res or {}
            rep_in_path = work_dir / f"repair_{jid}_r{round_num}.json"
            rep_out_path = work_dir / f"repair_{jid}_r{round_num}_submitted.json"
            rep_in_path.write_text(
                json.dumps({"jobId": jid, "extraction": extraction}, indent=2),
                encoding="utf-8",
            )

            so_cmd = [
                "submit-one",
                *conn_args,
                "--job-id",
                jid,
                "--in",
                str(rep_in_path),
                "--out",
                str(rep_out_path),
                "--extracted-model",
                repaired_model,
                "--repair-count",
                str(base_repair_count + round_num),
            ]
            if allow_overwrite:
                so_cmd.append("--allow-overwrite-published")
            if not compute_embeddings:
                so_cmd.append("--no-embeddings")

            try:
                async with submit_lock:
                    so_stdout = await asyncio.to_thread(run_helper, so_cmd)
                log(f"[{title}] Round {round_num}/{max_rounds}: {so_stdout}")
                outcome = json.loads(rep_out_path.read_text(encoding="utf-8"))
            except Exception as exc:
                cur_item = {
                    **cur_item,
                    "error": f"Round {round_num} submit-one failed: {exc}",
                    "extraction": extraction,
                }
                if round_num == max_rounds:
                    return {
                        "jobId": jid,
                        "title": title,
                        "roundsRun": rounds_run,
                        "status": (last_outcome or {}).get("status") or "SubmitError",
                        "published": False,
                        "onlyExternalMissingSubRecipe": False,
                        "error": str(exc),
                        "promotedWaitingRecipeIds": promoted_ids,
                        "outcome": last_outcome,
                    }
                continue

            last_outcome = outcome
            for pid in outcome.get("promotedWaitingRecipeIds") or []:
                if pid not in promoted_ids:
                    promoted_ids.append(pid)

            status = outcome.get("status")
            needs_repair = bool(outcome.get("needsRepair"))
            only_ext_sub = bool(outcome.get("onlyExternalMissingSubRecipe"))

            if status == "Published" or (not needs_repair and only_ext_sub) or not needs_repair:
                return {
                    "jobId": jid,
                    "title": outcome.get("title") or title,
                    "roundsRun": rounds_run,
                    "status": status,
                    "confidenceScore": outcome.get("confidenceScore"),
                    "published": status == "Published",
                    "onlyExternalMissingSubRecipe": only_ext_sub,
                    "remainingValidationIssues": outcome.get("validationIssues") or [],
                    "remainingQualityWarnings": outcome.get("qualityWarnings") or [],
                    "remainingMeasurementWarnings": outcome.get("measurementWarnings") or [],
                    "promotedWaitingRecipeIds": promoted_ids,
                    "outcome": outcome,
                }

            cur_item = {
                **cur_item,
                "status": status,
                "confidenceScore": outcome.get("confidenceScore"),
                "validationIssues": outcome.get("validationIssues") or [],
                "qualityWarnings": outcome.get("qualityWarnings") or [],
                "measurementWarnings": outcome.get("measurementWarnings") or [],
                "error": outcome.get("error"),
                "extraction": extraction,
            }

        final_out = last_outcome or {}
        return {
            "jobId": jid,
            "title": final_out.get("title") or title,
            "roundsRun": rounds_run,
            "status": final_out.get("status") or "ReadyForReview",
            "confidenceScore": final_out.get("confidenceScore"),
            "published": final_out.get("status") == "Published",
            "onlyExternalMissingSubRecipe": bool(final_out.get("onlyExternalMissingSubRecipe")),
            "remainingValidationIssues": final_out.get("validationIssues") or [],
            "remainingQualityWarnings": final_out.get("qualityWarnings") or [],
            "remainingMeasurementWarnings": final_out.get("measurementWarnings") or [],
            "promotedWaitingRecipeIds": promoted_ids,
            "outcome": final_out,
        }
    finally:
        del rep_agent


async def main() -> dict:
    limit = int(args.get("limit", 200))
    max_parallel = max(1, int(args.get("maxParallel", 4)))
    max_rounds = max(1, int(args.get("maxRounds", 3)))
    agent_model = str(args.get("agentModel", "inherit"))
    repaired_model = str(args.get("repairedModel", "agy-repair-inherit"))
    compute_embeddings = bool(args.get("computeEmbeddings", True))
    skip_precheck = bool(args.get("skipPrecheck", False))
    allow_overwrite = bool(args.get("allowOverwritePublished", False))

    run_stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    work_dir = Path(
        args.get("workDir") or str(SKILL_DIR / ".state" / f"repair-{run_stamp}")
    )
    work_dir.mkdir(parents=True, exist_ok=True)
    plan_file = work_dir / "repairs_plan.json"

    conn_args: list[str] = []
    temp_mcp_file: Path | None = None
    raw_mcp = args.get("mcpConfig")
    if isinstance(raw_mcp, dict):
        temp_mcp_file = work_dir / ".mcp_config.json"
        temp_mcp_file.write_text(json.dumps(raw_mcp, indent=2), encoding="utf-8")
        conn_args.extend(["--mcp-config", str(temp_mcp_file)])
    elif isinstance(raw_mcp, str) and raw_mcp.strip():
        stripped_mcp = raw_mcp.strip()
        if stripped_mcp.startswith("{"):
            parsed_mcp = json.loads(stripped_mcp)
            temp_mcp_file = work_dir / ".mcp_config.json"
            temp_mcp_file.write_text(json.dumps(parsed_mcp, indent=2), encoding="utf-8")
            conn_args.extend(["--mcp-config", str(temp_mcp_file)])
        else:
            conn_args.extend(["--mcp-config", stripped_mcp])

    raw_api_url = args.get("apiUrl")
    if isinstance(raw_api_url, str) and raw_api_url.strip():
        conn_args.extend(["--url", raw_api_url.strip()])

    guide = PROMPT_GUIDE_PATH.read_text(encoding="utf-8")

    try:
        # ============================================================
        # PHASE 1: Sweep ReadyForReview + 0-Token C# Pre-Check
        # ============================================================
        phase("Sweep ReadyForReview & C# Pre-Check")
        plan_cmd = [
            "plan-repairs",
            *conn_args,
            "--out",
            str(plan_file),
            "--limit",
            str(limit),
        ]
        if skip_precheck:
            plan_cmd.append("--skip-precheck")
        if not compute_embeddings:
            plan_cmd.append("--no-embeddings")

        plan_stdout = run_helper(plan_cmd)
        plan_summary = json.loads(plan_stdout)
        log(f"Repair sweep plan in {work_dir}: {json.dumps(plan_summary)}")

        plan_data = json.loads(plan_file.read_text(encoding="utf-8"))
        items = plan_data.get("items") or []
        if not items:
            log("Zero ReadyForReview recipes require LLM repair after C# pre-check.")
            return {
                "workDir": str(work_dir),
                "planSummary": plan_summary,
                "attempted": 0,
                "published": 0,
                "resolvedMissingSubRecipeOnly": 0,
                "stillNeedsRepair": 0,
                "errors": 0,
                "promotedWaitingRecipes": 0,
                "roundsHistogram": {},
                "unresolved": [],
            }

        # ============================================================
        # PHASE 2: Multi-Round Stateful Repair (Neon via agentModel='inherit')
        # ============================================================
        phase(
            f"Stateful Repair - {agent_model} ({len(items)} recipe(s), "
            f"maxRounds={max_rounds}, maxParallel={max_parallel})"
        )
        submit_lock = asyncio.Lock()
        all_results: list[dict] = []

        for chunk_start in range(0, len(items), max_parallel):
            chunk = items[chunk_start : chunk_start + max_parallel]
            chunk_results = await parallel(
                chunk,
                lambda it: repair_item_multi_round(
                    item=it,
                    guide=guide,
                    agent_model=agent_model,
                    repaired_model=repaired_model,
                    max_rounds=max_rounds,
                    conn_args=conn_args,
                    work_dir=work_dir,
                    allow_overwrite=allow_overwrite,
                    compute_embeddings=compute_embeddings,
                    submit_lock=submit_lock,
                ),
            )
            all_results.extend(chunk_results)

        published_rows = [r for r in all_results if r.get("published")]
        subrecipe_only_rows = [
            r
            for r in all_results
            if not r.get("published") and r.get("onlyExternalMissingSubRecipe")
        ]
        error_rows = [r for r in all_results if r.get("error")]
        still_needs_repair_rows = [
            r
            for r in all_results
            if not r.get("published")
            and not r.get("onlyExternalMissingSubRecipe")
            and not r.get("error")
        ]

        promoted_set = {
            pid
            for r in all_results
            for pid in (r.get("promotedWaitingRecipeIds") or [])
        }
        rounds_hist: dict[str, int] = {}
        for r in published_rows:
            rk = str(r.get("roundsRun", 1))
            rounds_hist[rk] = rounds_hist.get(rk, 0) + 1

        report = {
            "workDir": str(work_dir),
            "planSummary": plan_summary,
            "attempted": len(all_results),
            "published": len(published_rows),
            "resolvedMissingSubRecipeOnly": len(subrecipe_only_rows),
            "stillNeedsRepair": len(still_needs_repair_rows),
            "errors": len(error_rows),
            "promotedWaitingRecipes": len(promoted_set),
            "roundsHistogramForPublished": rounds_hist,
            "unresolved": [
                {
                    "jobId": r["jobId"],
                    "title": r["title"],
                    "status": r.get("status"),
                    "roundsRun": r.get("roundsRun"),
                    "error": r.get("error"),
                    "validationIssues": r.get("remainingValidationIssues") or [],
                    "qualityWarnings": r.get("remainingQualityWarnings") or [],
                    "measurementWarnings": r.get("remainingMeasurementWarnings") or [],
                }
                for r in [*still_needs_repair_rows, *error_rows]
            ],
        }
        (work_dir / "repair_summary.json").write_text(
            json.dumps(report, indent=2),
            encoding="utf-8",
        )
        return report
    finally:
        if temp_mcp_file is not None and temp_mcp_file.exists():
            try:
                temp_mcp_file.unlink()
            except OSError:
                pass


result = await main()
print(json.dumps(result, indent=2))
