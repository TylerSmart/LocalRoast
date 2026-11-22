---
name: bulk-import-recipes
description: >-
  Bulk-import queued cookbook recipes into LocalRoast using Antigravity's
  `run_workflow` orchestrator (`flash` batched single-pass extraction + `pro`
  targeted warning repair) backed by LocalRoast's deterministic C# compilation,
  catalog SKU guardrails, and sub-recipe promotion pipeline. Use when importing
  large queued recipe waves without running slow local Ollama text stages.
---

# Bulk Import Recipes (`bulk-import-recipes`)

Imports queued `IngestionJob`s into LocalRoast using parallel Antigravity workflow agents while keeping **100% of domain rules, SKU matching guardrails, step token healing/validation, measurement enrichment, sub-recipe auto-promotion, and confidence scoring** inside C# (`POST /api/ingestion/jobs/{id}/submit-extraction`).

## Prerequisites

1. **Local SQL Server & API running** on `http://localhost:5041`:
   ```powershell
   .\scripts\ensure-local-infra.ps1
   dotnet run --project API/App.API.csproj -c Release --launch-profile http
   ```
2. **Pause local Ollama background worker**: `plan-wave` automatically sets `enableIngestion = false` so local GPU Ollama jobs do not race with workflow submissions.

## Workflow Files

- [`resources/extraction-prompt.md`](resources/extraction-prompt.md): Unified single-pass extraction + step-tokenization prompt guide.
- [`scripts/bulk_import_helper.py`](scripts/bulk_import_helper.py): CLI providing `plan-wave`, `submit-batch`, `submit-one`, and `audit`.
- [`scripts/bulk_import_workflow.py`](scripts/bulk_import_workflow.py): Antigravity `run_workflow` script (`Plan Wave` -> `Extract Batches (Flash)` -> `Targeted Repair (Pro)` -> `Wave Audit`).

## Running a Wave via `run_workflow`

### 1. 30-Recipe Pilot / Benchmark Wave
```json
{
  "ScriptPath": "d:/Personal/LocalRoast/.agents/skills/bulk-import-recipes/scripts/bulk_import_workflow.py",
  "Description": "Pilot bulk import of 30 ATK recipes",
  "Args": {
    "waveSize": 30,
    "batchSize": 5,
    "maxParallel": 4,
    "pilotBenchmark": true,
    "computeEmbeddings": true
  }
}
```

### 2. Full-Catalog Production Waves (e.g., 150–200 recipes per wave)
Because every job transitions in SQL Server immediately upon submission, waves are **fully resumable**—simply repeat the call below until remaining `Queued` jobs reach `0`:
```json
{
  "ScriptPath": "d:/Personal/LocalRoast/.agents/skills/bulk-import-recipes/scripts/bulk_import_workflow.py",
  "Description": "Bulk import next 150 queued recipes",
  "Args": {
    "waveSize": 150,
    "batchSize": 5,
    "maxParallel": 4,
    "pilotBenchmark": false,
    "computeEmbeddings": true
  }
}
```

## Standalone CLI Helper Usage

```powershell
# Inspect & plan a wave manually
python .agents/skills/bulk-import-recipes/scripts/bulk_import_helper.py plan-wave --size 30 --batch-size 5 --pilot-benchmark --out .agents/skills/bulk-import-recipes/.state/manual/wave.json

# Run deep culinary quality audit on any wave.json
python .agents/skills/bulk-import-recipes/scripts/bulk_import_helper.py audit --wave .agents/skills/bulk-import-recipes/.state/manual/wave.json --out .agents/skills/bulk-import-recipes/.state/manual/audit.json
```

## Runtime State & Missing Sub-Recipes

- Intermediate wave plans, extracted batches, repair payloads, and `audit.json` are saved under `.agents/skills/bulk-import-recipes/.state/` (gitignored).
- If a recipe references a sub-recipe that has not been imported yet, LocalRoast saves it as `ReadyForReview` with `UnresolvedRecipeTitle` and **automatically promotes it to `Published`** as soon as its prerequisite sub-recipe is published in a later batch or wave.
