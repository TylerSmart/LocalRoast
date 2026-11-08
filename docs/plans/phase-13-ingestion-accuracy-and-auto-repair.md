# Phase 13: Ingestion Pipeline Accuracy, Diagnostics & Automatic Repair/Triage Loop

> **Branch:** `feature/phase-13-ingestion-accuracy-and-auto-repair`
> **Status:** `[x]` Completed
> **PRD Reference:** `docs/prd.md` — Sections 4.2.1, 4.2.3, 4.2.4; `docs/todo.md` — Ingestion items 1, 6
> **Target Execution:** Run `/execute-phase` to execute this plan step-by-step.
> **Live Environment Note:**
> - The local app (`http://localhost:4200`) and SQL Server (`localhost,14333` / container `local-roast-sqlserver`, DB `LocalRoast`) already contain the 1,562 imported recipes (`761` Published, `801` ReadyForReview, including *Ultracreamy Hummus*, *Antipasto Pasta Salad*, and *Pasta alla Trapanese*). You can query `LocalRoast` directly to verify diagnostics against all 801 review jobs; **never wipe or drop the database**.
> - The Ollama server runs at `http://ThinkStationP3:11434`. **Hardware Constraint:** Only ever send **1 LLM request at a time** (no parallel requests, and ensure the app's background ingestion queue is paused before running any live LLM check).

---

## 1. Scope & Objectives

This phase targets the ~50/50 split between auto-published (`761`) and `ReadyForReview` (`801`) recipes in local cookbook ingestion and eliminates the artificial `59%` / `70%` confidence clustering:

1. **Eliminate false-positive `MeasurementWarnings` (`247` jobs stuck at `70%` confidence):**
   - Extend `SourceMeasurementParser` to recognize `stick`/`sticks` (`1 stick = 4 oz / 8 tbsp`) and parenthetical equivalents anywhere in a source ingredient line (e.g., `"4 boneless chicken breasts (about 1 1/2 pounds)"`, `"4 ounces aged provolone cheese, grated (1 cup)"`), not only immediately after the primary unit.
   - Update `RecipeDraftNormalizer.Normalize` so model-extracted `AlternativeMeasurement` values that match any span in `SourceMeasurementParser.FindSpans(rawIng.OriginalRawText)` (or a `stick` conversion) do not emit `"verify the model-proposed alternative measurement against the source"`.
2. **Deterministic first-pass normalization (`289` jobs stuck at `59%` confidence):**
   - **Alternative & preparation text in food names (`206` jobs, e.g. *Pasta alla Trapanese*):** In `InstructionAmountTagger.RepairIngredientRows`, deterministically split `"X or Y"` and `"X and/or Y"` in `DisplayName` / `CanonicalFoodName` (e.g. `"linguine or spaghetti"` $\rightarrow$ `DisplayName = "linguine"`, `PreparationNote = "or spaghetti"`; `"cherry or grape tomatoes"` $\rightarrow$ `DisplayName = "cherry tomatoes"`, `PreparationNote = "or grape tomatoes"`; `"instant or rapid-rise yeast"` $\rightarrow$ `DisplayName = "instant yeast"`, `PreparationNote = "or rapid-rise yeast"`), and move trailing preparation phrases matched by `PreparationInName` into `PreparationNote`.
   - **Unmeasured `", plus <food/more/extra> for ..."` splitting (`83` jobs, e.g. *Antipasto Pasta Salad*):** When `OriginalRawText` ends with `,\s*plus\s+(?:more\s+|extra\s+)?[a-z\s]*?\bfor\s+(?<purpose>[^,()]+)$` (such as `"1/2 teaspoon table salt, plus salt for cooking pasta"`) and only one row was extracted for that `OriginalRawText`, `RepairIngredientRows` automatically appends an unmeasured sibling row (`Quantity = null, Unit = null, PreparationNote = "for " + purpose`) and strips the `plus ...` text from the primary row's `PreparationNote`.
3. **Smarter step unit comparison & mention diagnostics (`97` jobs, e.g. *Ultracreamy Hummus* & *Antipasto Pasta Salad*):**
   - In `InstructionAmountTagger.TryToRowUnit`: support parenthetical container sizes in `OriginalRawText` (e.g., `2 (15-ounce) cans` $\rightarrow$ `30 oz`) and culinary count-to-volume equivalents (`1 garlic clove` $\approx$ `1 tsp` / `1/3 tbsp` minced; `1 whole lemon` $\approx$ `3 tbsp` juice; `1 (15-ounce) can` $\approx$ `1.75 cups` / `28 tbsp`).
   - When a step measures a small sub-portion (e.g., `"Set aside 2 tablespoons whole chickpeas for garnish"` or `"Measure out 1 tablespoon garlic and set aside; discard remaining garlic"`) and the ingredient row is also linked to a step that uses the bulk/remainder without an explicit conflicting amount, treat the bulk step as consuming the row rather than failing with `"step amounts use units that cannot be compared"`.
   - When units genuinely cannot be compared, include the **step number and exact span text** in the diagnostic: `"Step 2 (\"1 tablespoon\"): garlic uses unit 'tbsp' which cannot be compared with the ingredient list (4 clove)."`.
   - In `RecipeExtractionDiagnostics.GetQualityWarnings`: do **not** emit `"Step {n}: {ingredient} is mentioned but not linked"` when that ingredient was already linked to an earlier step (`stepIndex > firstLinkedStepIndex`) and its row total is reconciled (e.g., Step 5 `"Add meat, provolone, peppers, basil, and pasta to mushrooms"` after mushrooms were cooked in Step 4).
4. **Automatic Iterative AI Repair & Triage Pass:**
   - When `extractionIssues.Count > 0` after the initial extraction in `IngestionProcessor`, run one automatic repair & triage pass (`ILlmExtractionClient.TriageAndRepairRecipeAsync`).
   - The LLM can fix ingredient rows / step tags AND return a per-issue triage status (`Resolved`, `IgnoreAsFalsePositive` with reason, or `NeedsHumanReview`). Issues triaged as `IgnoreAsFalsePositive` (for loosely written source recipes) do not block auto-publish.
5. **Graduated Confidence Score:**
   - Replace the hardcoded `Math.Min(confidence, 0.70m)` and `Math.Min(draft.ConfidenceScore, 0.59m)` clamps with a diagnostic-weighted calculation starting from a base completeness score (`0.95m`–`1.00m`), deducting `0.08m` per unresolved validation issue and `0.03m` per warning (clamped to `[0.15m, 1.00m]`), while gating `canAutoPublish` on `unresolvedValidationIssues.Count == 0`.

---

## 2. Domain & Database Design (`Core/`, `Data/` & `Infrastructure/`)

### 2.1 Enums & Models (`Core/Ingestion/IngestionModels.cs` & `ILlmExtractionClient.cs`)

- **New enum `IngestionIssueTriageResolution` (`Core/Ingestion/IngestionEnums.cs`):**
  ```csharp
  namespace App.Core.Ingestion;

  public enum IngestionIssueTriageResolution
  {
      NeedsHumanReview = 0,
      Resolved = 1,
      IgnoreAsFalsePositive = 2,
  }
  ```
- **New records in `Core/Ingestion/ILlmExtractionClient.cs`:**
  ```csharp
  public record LlmIssueTriageItem(
      string Issue,
      IngestionIssueTriageResolution Resolution,
      string? Reason = null
  );

  public record LlmAutoRepairAndTriageContext(
      string SourceContent,
      LlmExtractedRecipePayload Draft,
      IReadOnlyList<string> ValidationIssues,
      IReadOnlyList<string> QualityWarnings,
      IReadOnlyList<string>? AvailableTagNames = null,
      IReadOnlyList<string>? AvailableFoodNames = null
  );

  public record LlmAutoRepairAndTriageResult(
      LlmExtractedRecipePayload Draft,
      IReadOnlyList<LlmIssueTriageItem> TriageDecisions
  );
  ```
- **Add method to `ILlmExtractionClient`:**
  ```csharp
  Task<LlmAutoRepairAndTriageResult> TriageAndRepairRecipeAsync(
      EffectiveLlmConnectionConfig config,
      LlmAutoRepairAndTriageContext context,
      CancellationToken cancellationToken = default
  );
  ```

### 2.2 Service Business Logic

1. **`Core/Ingestion/SourceMeasurementParser.cs`:**
   - Add `sticks?` to `Unit` regex (`"stick" or "sticks"` in `RecipeDraftNormalizer.NormalizeUnit` or handled in `SourceMeasurementParser.ParseMatch`: `1 stick` = `4 oz` or `8 tbsp`).
   - Update `FindAlternative(string? source)`:
     - If no immediate post-unit alternative is found, scan parenthetical expressions in `trimmed` (e.g., `\((?:about\s+|approx\.?\s+)?(?<alt>[^()]+)\)`) after the primary match; if `Measurement.Match` matches the entire parenthetical body (after optional `about`/`approximately`) and its unit differs from `primary.Unit` (and is not a per-item multiplier like `2 (15-ounce) cans` between the quantity and unit), return `(primary, alternative)`.
   - Add helper `public static RecipeMeasurement? FindContainerUnitSize(string? source)` that extracts `15 oz` from `"2 (15-ounce) cans chickpeas"`.

2. **`Core/Ingestion/InstructionAmountTagger.cs`:**
   - In `RepairIngredientRows`:
     - Call `SanitizeFoodNamesAndAlternatives(row)`:
       - If `DisplayName` or `CanonicalFoodName` matches `^(?<first>.+?)\s+(?:and/or|or)\s+(?<second>.+)$`:
         - If `first` is a single modifier word and `second` ends with a multi-word noun phrase (e.g., `"cherry"` + `"grape tomatoes"` or `"instant"` + `"rapid-rise yeast"`), reconstruct `firstChoice = $"{first} {lastNoun}"` (`"cherry tomatoes"`, `"instant yeast"`).
         - Set `DisplayName` / `CanonicalFoodName` to `firstChoice` and append `"or {second}"` to `PreparationNote` (if not already present).
       - If `DisplayName` or `CanonicalFoodName` contains a trailing preparation phrase matching `, (?:(?:finely|coarsely|roughly|thinly)\s+)?(?:chopped|minced|diced|sliced|grated|peeled|softened|melted|drained|toasted|cut)\b.*$`, strip it from the name and merge it into `PreparationNote`.
     - Call `TrySplitPlusForRow(row, rows)`:
       - Match `OriginalRawText` against `,\s*plus\s+(?:more\s+|extra\s+)?(?<extraFood>[a-z\s]*?)\bfor\s+(?<purpose>[^,.;()]+)\s*$` (case-insensitive).
       - When matched and `rows.Count(r => string.Equals(SourceLine(r), SourceLine(row), StringComparison.OrdinalIgnoreCase)) == 1`, clean `"(plus ...)"` / `"plus ..."` from the primary row's `PreparationNote` and emit a second row with `Quantity = null, MaxQuantity = null, Unit = null, IsOptional = false, PreparationNote = $"for {purpose.Trim()}"`.
   - In `TryToRowUnit` & `Reconcile`:
     - If direct conversion and `AlternativeMeasurement` conversion fail:
       - Check `SourceMeasurementParser.FindContainerUnitSize(ingredient.OriginalRawText)` (e.g., `2 (15-ounce) cans` = `2 * 15 oz`): if the step amount converts to the container size unit (or volume equivalent `1 fl oz = 2 tbsp`), compute the fraction of cans.
       - Support standard culinary count conversions when `rowUnit` is `Clove` (`1 clove` = `1 tsp` = `1/3 tbsp`) or `Whole` citrus (`lemon`/`lime`: `1 whole` = `2 tbsp`–`3 tbsp`; `orange`: `1 whole` = `1/3 cup`).
     - In `Reconcile`:
       - When `TrySumInRowUnit` fails for ingredient `i`:
         - Check if ingredient `i` is linked to at least one step with **no** incompatible inline amount (an unstated bulk addition step like Step 1 `"Combine chickpeas..."`) AND the incompatible step amounts are small sub-portions ($\le 4\text{ tbsp}$). If so, do not emit an incompatible-unit validation error.
         - Otherwise, find the first step and inline amount causing the unit mismatch and emit:
           `"Step {stepNum} (\"{amountText}\"): {name} step amount uses a unit ({stepUnit}) that cannot be compared with the ingredient list ({FormatNumber(rowLow)} {ingredient.Unit})."`.

3. **`Core/Ingestion/RecipeExtractionDiagnostics.cs`:**
   - In `GetQualityWarnings`:
     - Before adding `"Step {stepIndex + 1}: {DescribeIngredient(ingredientIndex, ingredient)} is mentioned but not linked; verify whether this step uses it."`, check whether any earlier step (`0 <= prev < stepIndex`) already links `ingredientIndex` (or `SameFood`). If an earlier step already linked that food, skip the warning.

4. **`Core/Ingestion/RecipeDraftNormalizer.cs`:**
   - In `Normalize` (lines ~824–851):
     - Before adding `"{displayName}: verify the model-proposed alternative measurement against the source."`, check if `SourceMeasurementParser.FindSpans(rawIng.OriginalRawText)` contains a span with matching `Unit` and `Quantity` (within `0.01m`), or if `rawIng.OriginalRawText` contains `stick` matching `8 tbsp` / `4 oz` / `1/2 cup` per stick. If matched in the source line, accept `AlternativeMeasurement` with **no** warning.
   - Replace the `0.70m` confidence cap (`if (measurementWarnings.Count > 0) confidence = Math.Min(confidence, 0.70m);`) with `confidence = CalculateConfidenceScore(raw.ConfidenceScore, normalizedIngredients.Count, normalizedSteps.Count, validationIssueCount: 0, measurementWarnings.Count, qualityWarningCount: 0)`.
   - Add `public static decimal CalculateConfidenceScore(decimal? rawConfidence, int ingredientCount, int stepCount, int validationIssueCount, int measurementWarningCount, int qualityWarningCount)`:
     - If `ingredientCount == 0 || stepCount == 0`, return `0.25m`.
     - Base score = `rawConfidence is >= 0.50m and <= 1.0m ? Math.Max(rawConfidence.Value, 0.92m) : 0.95m`.
     - Deduct `0.08m * validationIssueCount + 0.03m * (measurementWarningCount + qualityWarningCount)`.
     - Return `Math.Clamp(Math.Round(score, 2), 0.15m, 1.00m)`.

5. **`Infrastructure/Ingestion/LlmExtractionClient.cs` & `Data/Services/IngestionProcessor.cs`:**
   - Implement `TriageAndRepairRecipeAsync`:
     - Calls `ExtractRecipeFromTextAsync` with `ReviewerHint = "Fix these validation issues if caused by extraction/tagging mistakes:\n" + string.Join("\n", context.ValidationIssues)` and `CurrentIngredients = context.Draft.Ingredients`.
     - Re-runs `RecipeExtractionDiagnostics.GetReviewIssues(repaired, context.SourceContent)`.
     - If any issues remain on `repaired`, sends a lightweight structured JSON chat (`recipe_issue_triage`) asking the model to classify each remaining issue as `Resolved`, `IgnoreAsFalsePositive` (only when the source recipe text itself has an unmeasured/inconsistent culinary quirk), or `NeedsHumanReview`.
   - In `IngestionProcessor.ProcessJobByIdAsync`:
     - When initial `extractionIssues.Count > 0`, call `llmClient.TriageAndRepairRecipeAsync(...)` once.
     - Keep the repaired draft if its unresolved issue count (`NeedsHumanReview`) is strictly less than the initial `extractionIssues.Count`.
     - Compute `draft = draft with { ConfidenceScore = RecipeDraftNormalizer.CalculateConfidenceScore(...) }`.
     - Set `canAutoPublish` using `unresolvedIssues.Count == 0` (and no unconfirmed measurement warnings).

---

## 3. API Endpoints

No new HTTP routes are required in Phase 13; existing ingestion queue and review endpoints automatically benefit from improved normalization, diagnostics, and auto-repair.

---

## 4. Angular Frontend Design

No frontend structural changes are required in Phase 13; existing review components display the graduated confidence percentage (`88%`, `92%`, etc.) and clearer step-specific unit comparison messages automatically.

---

## 5. Playwright E2E Test Scenarios

Existing `Tests/Client.E2ETests/LlmIngestionE2ETests.cs` continues to verify end-to-end ingestion and review flows.

---

## 6. Step-by-Step Execution Checklist (For Implementation Agent)

- [x] **Task 1: Core Diagnostics, Parser & Tagger Normalizations**
  - Update `Core/Ingestion/SourceMeasurementParser.cs` (`FindAlternative` anywhere in line, `FindContainerUnitSize`, `stick` support).
  - Update `Core/Ingestion/InstructionAmountTagger.cs` (`RepairIngredientRows` for `"X or Y"` food names and `", plus <food> for ..."` sibling splitting; `TryToRowUnit` and `Reconcile` for container/clove conversions, bulk-step sub-portions, and step-numbered error messages).
  - Update `Core/Ingestion/RecipeExtractionDiagnostics.cs` (skip `"mentioned but not linked"` warning when ingredient is already linked in an earlier step).
  - Update `Core/Ingestion/RecipeDraftNormalizer.cs` (confirm parenthetical `AlternativeMeasurement` against `FindSpans(OriginalRawText)` without false `MeasurementWarnings`; add `CalculateConfidenceScore`).
  - Add unit tests in `Tests/Core.UnitTests/InstructionAmountTaggerTests.cs`, `RecipeExtractionDiagnosticsTests.cs`, and `RecipeDraftNormalizerTests.cs` covering:
    - *Pasta alla Trapanese* (`"1 pound linguine or spaghetti"`, `"12 ounces cherry or grape tomatoes"` $\rightarrow$ zero issues).
    - *Antipasto Pasta Salad* (`"1/2 teaspoon table salt, plus salt for cooking pasta"` + Step 3 `"1 tablespoon salt"` $\rightarrow$ split sibling row, zero issues; Step 5 `"to mushrooms"` $\rightarrow$ no quality warning).
    - *Ultracreamy Hummus* (`"2 (15-ounce) cans chickpeas"` + `"2 tablespoons whole chickpeas"`, `"4 garlic cloves"` + `"1 tablespoon garlic"` $\rightarrow$ zero unit-comparison errors).
    - End-of-line parenthetical equivalents (`"4 chicken breasts (about 1 1/2 pounds)"`) $\rightarrow$ zero `MeasurementWarnings` and confidence $\ge 0.92$.
  - Run Core unit tests:
    ```powershell
    dotnet test --project Tests/Core.UnitTests/App.Core.UnitTests.csproj
    ```

- [x] **Task 2: Automatic AI Repair & Triage Pass in Infrastructure & Data**
  - Add `IngestionIssueTriageResolution`, `LlmAutoRepairAndTriageContext`, `LlmAutoRepairAndTriageResult`, and `TriageAndRepairRecipeAsync` to `Core/Ingestion/IngestionEnums.cs`, `Core/Ingestion/ILlmExtractionClient.cs`, and `Infrastructure/Ingestion/LlmExtractionClient.cs`.
  - Wire automatic repair & triage and graduated confidence calculation into `Data/Services/IngestionProcessor.cs`.
  - Add unit and integration tests in `Tests/Infrastructure.UnitTests/LlmExtractionClientTests.cs` and `Tests/API.IntegrationTests/IngestionIntegrationTests.cs`.
  - Run tests:
    ```powershell
    dotnet test --project Tests/Infrastructure.UnitTests/App.Infrastructure.UnitTests.csproj
    dotnet test --project Tests/API.IntegrationTests/App.API.IntegrationTests.csproj
    ```

- [x] **Task 3: Final Formatting & Full Test Suite**
  - Format and run all tests:
    ```powershell
    dotnet csharpier format .
    dotnet test App.slnx
    ```
  - Mark this plan's status as `[x] Completed`.
