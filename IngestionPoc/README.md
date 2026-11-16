# LocalRoast Ingestion Proof-of-Concept (`IngestionPoc`)

Standalone experimentation harness for the multi-stage LLM recipe ingestion pipeline. Every system prompt lives in its own Markdown file under `IngestionPoc/prompts/`, every stage saves raw HTTP requests, raw responses (including thinking tokens), DB search candidates, parsed outputs, and scaled/metric step previews under `IngestionPoc/runs/` (git-ignored).

## Pipeline Stages

1. **Stage 1 (`prompts/01-extract-recipe.md`)**: Transforms normalized raw recipe text (ATK JSON, website scrapes, digital PDFs, or vision-transcribed scanned PDFs) into a uniform JSON recipe structure with standardized singular units and curated LocalRoast tags.
2. **Stage 2 (`prompts/02-shorten-description.md`)**: If `description.Length > 2000` (matching `Recipes.Description` varchar bounds), condenses the description iteratively until it fits cleanly.
3. **Stage 3 (`prompts/03a-ingredient-search-terms.md` & `prompts/03b-ingredient-match.md`)**:
   - Generates candidate search terms per ingredient.
   - Queries the live `LocalRoast` SQL Server database (read-only) for matching `FoodItems` (and aliases) and `Recipes` (sub-recipes).
   - Matches each ingredient to an existing DB `FoodItem`, an existing DB `Recipe`, an unresolved sub-recipe, or proposes a new canonical `FoodItem` with a `FoodCategory`.
4. **Stage 4 (`prompts/04-tokenize-step.md`)**: Transforms each instruction step into an ordered array of prose strings and typed tokens (`ingredient`, `mixture`, `timer`, `temperature`, `dimension`, `fixed`) and previews `1x`, `2x`, and `1x Metric` rendering.

## Quick Start

> **Important (`ThinkStationP3` Hardware Limit):** Requests run strictly serially (`concurrency = 1`). When targeting `http://ThinkStationP3:11434`, ensure the main LocalRoast background ingestion worker is paused or idle so requests do not overlap.

```powershell
# 1. Run the curated 6-recipe ATK preset using your saved LocalRoast DB LLM config:
dotnet run --project IngestionPoc/IngestionPoc.csproj

# 2. Run a single ATK recipe (fast test with thinking disabled on Stage 2/3):
dotnet run --project IngestionPoc/IngestionPoc.csproj -- --titles "Classic Guacamole" --aux-thinking false

# 3. Run a subset of ATK recipes by index or slice:
dotnet run --project IngestionPoc/IngestionPoc.csproj -- --indices "0,3,6,11"
dotnet run --project IngestionPoc/IngestionPoc.csproj -- --offset 10 --limit 5

# 4. Compare another model saved in LocalRoast LLM Settings (e.g. gemma4:12b or Gemini Flash):
dotnet run --project IngestionPoc/IngestionPoc.csproj -- --model "gemma4:12b" --titles "Ultracreamy Hummus"
dotnet run --project IngestionPoc/IngestionPoc.csproj -- --model "models/gemini-3.8-flash" --titles "Classic Guacamole"

# 5. Test live Website URL scraping or PDF input or plain text:
dotnet run --project IngestionPoc/IngestionPoc.csproj -- --url "https://example.com/recipe"
dotnet run --project IngestionPoc/IngestionPoc.csproj -- --pdf "C:\path\to\recipe.pdf"
dotnet run --project IngestionPoc/IngestionPoc.csproj -- --text "C:\path\to\recipe.txt"

# 6. Force-test Stage 2 (description shortening) on an ATK recipe:
dotnet run --project IngestionPoc/IngestionPoc.csproj -- --titles "Chunky Guacamole" --max-description-length 450 --skip-ingredients --skip-steps
```

## Saved Run Layout (`IngestionPoc/runs/<timestamp>_<model>/`)

- `run-config.json`: Model settings, prompt SHA-256 hashes, input metadata
- `summary.md` & `summary.json`: High-level metrics and warnings across every recipe
- `<NN>_<recipe-slug>/`:
  - `00-normalized-input.txt`: Exact text sent to Stage 1
  - `01-extract-recipe/`: `request.json`, `response.json`, `output.json`
  - `02-shorten-description/`: `status.json` or `request*.json`, `response*.json`, `output.json`
  - `03-ingredients/`: per-ingredient `03a-search-*`, `db-candidates.json`, `03b-match-*`, plus `summary.json`
  - `04-steps/`: per-step `request.json`, `response.json`, `output.json` (with `1x`, `2x`, `Metric` renders), plus `summary.json`
  - `final-recipe.json`: Combined end-to-end result
