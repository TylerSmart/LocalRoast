---
name: repair-review-recipes
description: >-
  Repair flagged ReadyForReview recipe ingestion jobs in LocalRoast using
  0-token C# re-validation followed by stateful multi-round repair agents
  (`model="inherit"`, e.g. Neon) conversing directly with live C# diagnostics
  until Published. Works against local dev or production via MCP config.
---

# Repair Review Recipes (`repair-review-recipes`)

Sweeps `ReadyForReview` `IngestionJob`s from LocalRoast (**local dev** or remote **production** via MCP config), filters out jobs blocked solely on unresolved external sub-recipes, runs a **0-token C# pre-check** to auto-publish any jobs cleared by updated validator rules or newly published sub-recipes, and drives **stateful multi-round repair agents** (up to **3 rounds** per recipe) on your selected chat model until `Published`.

## Prerequisites (Model Switch)

`run_workflow` selects custom models via `model="inherit"` from the active Antigravity chat conversation:

1. Finish your bulk extraction waves using [`bulk-import-recipes`](../bulk-import-recipes/SKILL.md) (on **Hydrogen Low/Medium**).
2. **Switch your Antigravity chat model to Neon** in the UI model picker.
3. Run `repair_review_workflow.py` (`agentModel: "inherit"`, `maxRounds: 3`).

## How It Works

1. **Phase 1 — Sweep & 0-Token C# Pre-Check (`plan-repairs`)**:
   - Pages `GET /api/bulk-import/jobs?status=ReadyForReview` and skips recipes whose only issue is `UnresolvedRecipeTitle` (`onlyExternalMissingSubRecipe=true` with no quality/validation warnings — those auto-promote automatically as sub-recipes publish).
   - Converts saved drafts via `draft_to_external_extraction` and re-submits them to C# `SubmitExternalExtractionAsync` (`submit-one`) with **0 LLM tokens** so updated validator rules immediately publish newly passing recipes.
2. **Phase 2 — Multi-Round Stateful Repair (`maxRounds=3`, `agentModel="inherit"`)**:
   - Launches one stateful `agent(...)` per remaining flagged recipe (`maxParallel=4`).
   - **Round 1**: Sends the C# diagnostics, `extractedDraftJson`, `sourceText`, and shared [`extraction-prompt.md`](../bulk-import-recipes/resources/extraction-prompt.md).
   - **Round 2–3 (only if still flagged)**: Submits via `submit-one`, captures the exact new C# `validationIssues` / `qualityWarnings` / `measurementWarnings` / `submissionError`, and replies on the **same agent conversation** until `Published` or resolved down to an external sub-recipe dependency.

## Shared Tooling & Targets

- Reuses [`bulk_import_helper.py`](../bulk-import-recipes/scripts/bulk_import_helper.py) (`plan-repairs` + `submit-one`) and [`extraction-prompt.md`](../bulk-import-recipes/resources/extraction-prompt.md).
- Supports identical connection options to `bulk-import-recipes`:
  - **Local Dev (`http://localhost:5041`)**: omit `mcpConfig` and `apiUrl`.
  - **Production / Remote**: pass `"mcpConfig": "localroast.mcp.local.json"` (or inline MCP JSON object/string containing your `lr_pat_...` Bearer token).

## Running via `run_workflow`

```json
{
  "ScriptPath": "d:/Personal/LocalRoast/.agents/skills/repair-review-recipes/scripts/repair_review_workflow.py",
  "Description": "Multi-round Neon repair sweep for ReadyForReview recipes",
  "Args": {
    "limit": 200,
    "maxParallel": 4,
    "maxRounds": 3,
    "agentModel": "inherit",
    "computeEmbeddings": true
  }
}
```

For production, add `"mcpConfig": { "mcpServers": { "localroast": { "url": "https://your-host/api/mcp", "headers": { "Authorization": "Bearer lr_pat_..." } } } }` (or `"mcpConfig": "localroast.mcp.local.json"`).

## Standalone 0-Token C# Pre-Check CLI

```powershell
# Re-validate ReadyForReview jobs locally at 0 LLM cost and dump remaining repair queue
python .agents/skills/bulk-import-recipes/scripts/bulk_import_helper.py plan-repairs --out .agents/skills/repair-review-recipes/.state/manual_plan.json --limit 200

# Production
python .agents/skills/bulk-import-recipes/scripts/bulk_import_helper.py plan-repairs --mcp-config localroast.mcp.local.json --out .agents/skills/repair-review-recipes/.state/prod_plan.json --limit 200
```

## Runtime State

Intermediate `repairs_plan.json`, per-round submissions (`repair_<jobId>_r<1..3>.json`), and `repair_summary.json` are stored under `.agents/skills/repair-review-recipes/.state/` (gitignored).
