---
name: bulk-import-recipes
description: >-
  Bulk-import queued cookbook recipes into LocalRoast using Antigravity's
  `run_workflow` orchestrator (single-pass parallel extraction using `model="inherit"`
  from your selected chat model, e.g. Hydrogen Low/Medium) backed by LocalRoast's
  deterministic C# compilation, catalog SKU guardrails, and sub-recipe promotion
  pipeline. Leaves flagged edge cases in `ReadyForReview` for the paired
  `repair-review-recipes` skill (switch model to Neon).
---

# Bulk Import Recipes (`bulk-import-recipes`)

Imports queued `IngestionJob`s into LocalRoast (either **local dev** or a remote **production deployment** over HTTPS — zero direct SQL access required) using parallel Antigravity workflow agents while keeping **100% of domain rules, SKU matching guardrails, step token healing/validation, measurement enrichment, sub-recipe auto-promotion, and confidence scoring** inside C# (`POST /api/bulk-import/jobs/{id}/submit-extraction`).

## Two-Skill / Two-Model Workflow (Recommended)

`run_workflow`'s `agent()` primitive selects specific custom models (like **Hydrogen** or **Neon**) via `model="inherit"` from the active Antigravity chat conversation:

1. **Pass 1 — Bulk Extraction (`bulk-import-recipes`, Hydrogen Low/Medium):**
   - Switch your Antigravity chat model to **Hydrogen Low** or **Hydrogen Medium**.
   - Run `bulk_import_workflow.py` (`agentModel: "inherit"`, `runRepairInWave: false` by default). Clean recipes publish straight to `Published`; edge cases requiring repair or missing sub-recipes land in `ReadyForReview` without spending premium repair quota during the bulk pass.
2. **Pass 2 — Multi-Round Targeted Repair ([`repair-review-recipes`](../repair-review-recipes/SKILL.md), Neon):**
   - After your bulk wave(s) complete, switch your Antigravity chat model to **Neon**.
   - Run `repair_review_workflow.py` via [`repair-review-recipes`](../repair-review-recipes/SKILL.md). It first runs a 0-token C# re-check (auto-publishing anything cleared by updated validator rules or newly published sub-recipes) and then drives up to **3 stateful back-and-forth repair rounds** per remaining recipe against live C# diagnostics.

*(Optional: pass `"runRepairInWave": true` to `bulk_import_workflow.py` if you ever want single-round inline repair in the same wave.)*

## Targets & Authentication

1. **Local Development (`http://localhost:5041`, default)**:
   - Start SQL Server & the Release API:
     ```powershell
     .\scripts\ensure-local-infra.ps1
     dotnet run --project API/App.API.csproj -c Release --launch-profile http
     ```
   - No config needed; scripts auto-authenticate against `localhost:5041` with the local dev runner account and call `/api/bulk-import/*`.
2. **Production / Remote Deployment (via MCP Config)**:
   - Use any standard LocalRoast MCP JSON snippet containing your server `url` and `Authorization: Bearer lr_pat_...` Personal Access Token (pass either a file path — e.g. `localroast.mcp.local.json`, gitignored — or inline JSON via `mcpConfig`):
     ```json
     {
       "mcpServers": {
         "localroast": {
           "url": "https://your-production-host/api/mcp",
           "headers": {
             "Authorization": "Bearer lr_pat_..."
           }
         }
       }
     }
     ```
   - All requests hit the scoped `/api/bulk-import/*` endpoints (accepted by both `lr_pat_...` PATs and standard JWTs without exposing `/api/account/*`).
3. **Background Ollama Worker Protection**: `plan-wave` and `enqueue-epub` automatically set `enableIngestion = false` via `PUT /api/bulk-import/worker` so any server background worker does not race with workflow submissions.

## Workflow Files

- [`resources/extraction-prompt.md`](resources/extraction-prompt.md): Unified single-pass extraction + step-tokenization prompt guide (also shared with `repair-review-recipes`).
- [`scripts/bulk_import_helper.py`](scripts/bulk_import_helper.py): CLI providing `enqueue-epub`, `plan-wave`, `plan-repairs`, `submit-batch`, `submit-one`, and `audit` (all supporting `--mcp-config`, `--url`, `--token`).
- [`scripts/bulk_import_workflow.py`](scripts/bulk_import_workflow.py): Antigravity `run_workflow` script (`Optional Enqueue EPUB` -> `Plan Wave` -> `Extract Batches (agentModel="inherit")` -> `Wave Audit`, plus optional `runRepairInWave`).

## Running Waves via `run_workflow`

### 1. Local Pilot / Production Waves
```json
{
  "ScriptPath": "d:/Personal/LocalRoast/.agents/skills/bulk-import-recipes/scripts/bulk_import_workflow.py",
  "Description": "Bulk import next 150 queued recipes",
  "Args": {
    "waveSize": 150,
    "batchSize": 5,
    "maxParallel": 4,
    "pilotBenchmark": false,
    "agentModel": "inherit",
    "computeEmbeddings": true
  }
}
```
Set `"pilotBenchmark": true` and `"waveSize": 30` for an initial 30-recipe benchmark.

### 2. Production Deployment: Upload ATK EPUB + Run Waves via MCP Config
To push the ATK EPUB (dish photos + queued jobs, idempotently skipping any recipes already queued or published on the target server) and immediately run the first 150-recipe wave against production:
```json
{
  "ScriptPath": "d:/Personal/LocalRoast/.agents/skills/bulk-import-recipes/scripts/bulk_import_workflow.py",
  "Description": "Enqueue ATK EPUB onto production and import first 150 recipes",
  "Args": {
    "mcpConfig": {
      "mcpServers": {
        "localroast": {
          "url": "https://your-production-host/api/mcp",
          "headers": {
            "Authorization": "Bearer lr_pat_..."
          }
        }
      }
    },
    "enqueueEpub": true,
    "waveSize": 150,
    "batchSize": 5,
    "maxParallel": 4,
    "computeEmbeddings": true
  }
}
```
*(After all EPUB jobs are uploaded once, omit `"enqueueEpub"` — or leave it `false` — on subsequent 150-recipe waves until remaining `Queued` jobs reach `0`.)*

## Standalone CLI Helper Usage

```powershell
# Idempotently upload ATK EPUB + cover photos onto production (skips already-present titles)
python .agents/skills/bulk-import-recipes/scripts/bulk_import_helper.py enqueue-epub --mcp-config localroast.mcp.local.json

# Plan & audit manually against local or production
python .agents/skills/bulk-import-recipes/scripts/bulk_import_helper.py plan-wave --mcp-config localroast.mcp.local.json --size 30 --batch-size 5 --pilot-benchmark --out .agents/skills/bulk-import-recipes/.state/manual/wave.json
python .agents/skills/bulk-import-recipes/scripts/bulk_import_helper.py audit --mcp-config localroast.mcp.local.json --wave .agents/skills/bulk-import-recipes/.state/manual/wave.json --out .agents/skills/bulk-import-recipes/.state/manual/audit.json
```

## Runtime State & Missing Sub-Recipes

- Intermediate wave plans, extracted batches, repair payloads, and `audit.json` are saved under `.agents/skills/bulk-import-recipes/.state/` (gitignored).
- If a recipe references a sub-recipe that has not been imported yet, LocalRoast saves it as `ReadyForReview` with `UnresolvedRecipeTitle` and **automatically promotes it to `Published`** as soon as its prerequisite sub-recipe is published in a later batch or wave.
