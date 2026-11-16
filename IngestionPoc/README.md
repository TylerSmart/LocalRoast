# LocalRoast Ingestion Proof-of-Concept (`IngestionPoc`)

Standalone experimentation harness for the multi-stage LLM recipe ingestion pipeline. Every system prompt lives in its own Markdown file under `IngestionPoc/prompts/`, every stage saves raw HTTP requests, raw responses (including thinking tokens), DB search candidates, parsed outputs, and scaled/metric step previews under `IngestionPoc/runs/` (git-ignored).

## Pipeline Stages

1. **Stage 1 (`prompts/01-extract-recipe.md`)**: Transforms normalized raw recipe text (ATK JSON, website scrapes, digital PDFs, or vision-transcribed scanned PDFs) into a uniform JSON recipe structure with standardized singular units and curated LocalRoast tags.
2. **Stage 2 (`prompts/02-shorten-description.md`)**: If `description.Length > 2000` (matching `Recipes.Description` varchar bounds), condenses the description iteratively until it fits cleanly.
3. **Stage 3 (`--stage3-mode vector-batched|vector-single|llm-terms`)**:
   - **`vector-batched` (default when `--embed-model` is set, default `embeddinggemma-2:270m`)**: Embeds all recipe ingredients in a single batched `POST /v1/embeddings` call, ranks `FoodItems` (including aliases) and `Recipes` by hybrid cosine + lexical score, and resolves all ingredients in one batched Stage 3b call (`prompts/03c-ingredient-match-batch.md`).
   - **`vector-single`**: Uses the same single batched embedding lookup, then runs per-ingredient Stage 3b matching (`prompts/03b-ingredient-match.md`).
   - **`llm-terms`**: Classic per-ingredient Stage 3a search-term generation (`prompts/03a-ingredient-search-terms.md`) + Stage 3b matching.
4. **Stage 4 (`prompts/04-tokenize-step.md` & `prompts/04b-tokenize-steps-batch.md`)**: Transforms instruction steps (`--stage4-mode single|batched`) into prose strings and typed tokens (`ingredient`, `mixture`, `timer`, `temperature`, `dimension`, `fixed`) strictly for explicit quantitative data (batched mode auto-falls back per step on validation warnings), and previews `1x`, `2x`, and `1x Metric` rendering.
5. **Stage 5 (`prompts/05a-polish-full.md` & `prompts/05b-polish-targeted.md`, optional via `--polish`)**: End-to-end QA pass comparing `currentRecipe` against `originalSourceText` (`none`, `targeted`, `full`, or `both` for side-by-side comparison).
6. **Objective Quality Scorer & Prod Comparison (`--from-db-jobs`, `--compare-prod`)**: Scores both PoC outputs and stored production `IngestionJobs` on a shared `0–100` rubric (`summary.md` & `prod-vs-poc-comparison.md`).

## Quick Start

> **Important (`ThinkStationP3` Hardware Limit):** Requests run strictly serially (`concurrency = 1`). When targeting `http://ThinkStationP3:11434`, ensure the main LocalRoast background ingestion worker is paused or idle so requests do not overlap.

```powershell
# 1. Run the curated 6-recipe ATK preset using your saved LocalRoast DB LLM config:
dotnet run --project IngestionPoc/IngestionPoc.csproj

# 2. Run a single ATK recipe (fast test with thinking disabled on Stage 2/3):
dotnet run --project IngestionPoc/IngestionPoc.csproj -- --titles "Classic Guacamole" --aux-thinking false

# 3. Compare Stage 5 targeted vs full end-to-end polish on specific recipes:
dotnet run --project IngestionPoc/IngestionPoc.csproj -- --titles "Ultracreamy Hummus,Lemon Ricotta Pancakes" --aux-thinking false --polish both

# 4. Test standard OpenAI-compatible /v1/chat/completions endpoint against Ollama:
dotnet run --project IngestionPoc/IngestionPoc.csproj -- --titles "Classic Guacamole" --aux-thinking false --endpoint-style openai

# 5. Run a subset of ATK recipes by index or slice:
dotnet run --project IngestionPoc/IngestionPoc.csproj -- --indices "0,3,6,11"
dotnet run --project IngestionPoc/IngestionPoc.csproj -- --offset 10 --limit 5

# 6. Compare another model saved in LocalRoast LLM Settings (e.g. gemma4:12b or Gemini Flash):
dotnet run --project IngestionPoc/IngestionPoc.csproj -- --model "gemma4:12b" --titles "Ultracreamy Hummus"
dotnet run --project IngestionPoc/IngestionPoc.csproj -- --model "models/gemini-3.8-flash" --titles "Classic Guacamole"

# 7. Test live Website URL scraping or PDF input or plain text:
dotnet run --project IngestionPoc/IngestionPoc.csproj -- --url "https://example.com/recipe"
dotnet run --project IngestionPoc/IngestionPoc.csproj -- --pdf "C:\path\to\recipe.pdf"
dotnet run --project IngestionPoc/IngestionPoc.csproj -- --text "C:\path\to\recipe.txt"

# 8. Force-test Stage 2 (description shortening) on an ATK recipe:
dotnet run --project IngestionPoc/IngestionPoc.csproj -- --titles "Chunky Guacamole" --max-description-length 450 --skip-ingredients --skip-steps

# 9. Clean-DB head-to-head Production vs. IngestionPoc benchmark against saved DB jobs:
dotnet run --project IngestionPoc/IngestionPoc.csproj -- --from-db-jobs all --limit 8 --empty-catalog --session-catalog --compare-prod --aux-thinking false --polish targeted
```

## Overnight Benchmark Findings (8-Recipe Clean-DB Run on `gemma4:e4b` + `embeddinggemma-2:270m`)

Both pipelines ran serially against `http://ThinkStationP3:11434` starting from an empty database (`0 FoodItems`, `0 Recipes`) across the same 8 ATK `IngestionJobs` (`Classic Guacamole`, `Ultracreamy Hummus`, `Easy-Peel Hard-Cooked Eggs`, `Curry Deviled Eggs with Easy-Peel Hard-Cooked Eggs`, `Lemon Ricotta Pancakes`, `Chicken Piccata`, `Beef Wellington`, `Lemon Bundt Cake`):

- **Current Production (`App.API`):** `6/8 Published`, **`2/8 ReadyForReview` failures** (`Chicken Piccata` failed 3/3 retries on duplicate `{{timer_4_A}}` / `{{timer_4_B}}` keys; `Beef Wellington` failed 3/3 retries on divided-ingredient `cremini mushroom` quantity sum `4 > 2` + missing `{{timer_7}}`). Objective rubric average: **`85.2 / 100`** in **~19m 00s** wall-clock (`08:26:06Z` → `08:45:06Z`), auto-publishing flaws such as `water`/`ice cube` pulled from `Easy-Peel Hard-Cooked Eggs` prose, dropped parentheticals in `Ultracreamy Hummus` (`66/100`), duplicate prep strings (`finely chopped onion`), and unscaled prose measurements.
- **`IngestionPoc` (commit `7efbae8`, Stages 1–5 Targeted + Session Catalog):** **`8/8` succeeded with `0` step validation warnings (`0w`) across all recipes**, averaging **`98.8 / 100`** (`+13.5` points over Production; `6/8` scored `100/100`, `Lemon Bundt Cake` `97/100`, `Chicken Piccata` `93/100`) in **~16m 04s** total including Stage 5 Targeted Polish (~`11m 23s` for Stages 1–4 baseline alone). Stage 3 embedding lookups (`embeddinggemma-2:270m`) completed in **`14–158 ms`** per recipe.

## Saved Run Layout (`IngestionPoc/runs/<timestamp>_<model>/`)

- `run-config.json`: Model settings, endpoint style, polish mode, prompt SHA-256 hashes, input metadata
- `summary.md` & `summary.json`: High-level metrics and warnings across every recipe
- `<NN>_<recipe-slug>/`:
  - `00-normalized-input.txt`: Exact text sent to Stage 1
  - `01-extract-recipe/`: `request.json`, `response.json`, `output.json`
  - `02-shorten-description/`: `status.json` or `request*.json`, `response*.json`, `output.json`
  - `03-ingredients/`: per-ingredient `03a-search-*`, `db-candidates.json`, `03b-match-*`, plus `summary.json`
  - `04-steps/`: per-step `request.json`, `response.json`, `output.json` (with `1x`, `2x`, `Metric` renders), plus `summary.json`
  - `final-recipe.json`: Combined Stages 1–4 baseline result
  - `05-polish-targeted/`: `request.json`, `response.json`, `output.json` (patches), `final-recipe.json`
  - `05-polish-full/`: `request.json`, `response.json`, `output.json` (polished recipe), `final-recipe.json`
  - `polish-comparison.md`: Side-by-side comparison report when `--polish both` is used
