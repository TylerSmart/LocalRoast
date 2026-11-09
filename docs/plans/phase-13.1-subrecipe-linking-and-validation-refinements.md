# Phase 13.1: Sub-Recipe Linking, Measurement Confirmation & Deterministic Validation Refinements

> **Branch:** `feature/phase-13-ingestion-accuracy-and-auto-repair`
> **Status:** `[~]` In Progress
> **PRD Reference:** `docs/prd.md` — Sections 4.2.1, 4.2.3, 4.2.4, 4.3.2
> **Target Execution:** Run `/execute-phase 13.1` (or approve the plan) to execute this blueprint step-by-step.
> **Live Environment Note:**
> - Follow-up refinement to [Phase 13](./phase-13-ingestion-accuracy-and-auto-repair.md) based on live evaluation of the 801 requeued review jobs in `LocalRoast` (`localhost,14333`).
> - Preserves all existing data in `LocalRoast` and respects the single-request concurrency rule on `http://ThinkStationP3:11434`.

---

## 1. Scope & Objectives

Live testing of Phase 13 across 259 processed review jobs increased auto-published recipes from `761` to `923` (`+162`), while surfacing three distinct problem classes across the `97` recipes routed to `ReadyForReview` and published sub-recipe parents:

1. **Eliminate false-positive Review Inbox routing for high-confidence recipes with `0` validation issues (`66` of `97` jobs, e.g., *Chicken Provençal*, *Easy-Peel Jammy Eggs*):**
   - **Hyphenated & size-word source measurements (`59` jobs):** `SourceMeasurementParser` required whitespace before the unit (`\s*`) and lacked size-word counts (`small`, `medium`, `large`), failing to parse standard cookbook parentheticals like `(14.5-ounce)`, `(4-pound)`, `(5- to 6-ounce)`, `(2 medium)`, and dual parentheticals like `(2 1/2 cups or 5 ounces)`. This caused `RecipeDraftNormalizer.IsAlternativeConfirmedBySource` to emit false-positive `MeasurementWarnings` (`"verify the model-proposed alternative measurement against the source"`), which blocked `canAutoPublish` despite `0` validation issues and `91%`–`97%` confidence.
   - **False-positive split-line warning:** `RecipeDraftNormalizer.Normalize` emitted `"the source equivalent applies to a whole ingredient line, not each split portion"` whenever `sourcePair is not null && isSplitSourceLine`, even when the split ingredient row did not carry the full-line `AlternativeMeasurement`.
   - **Single-ingredient recipes (`7` jobs at `100%` confidence, e.g., *Easy-Peel Jammy Eggs*, *Frico*, *Oven-Fried Bacon*):** `IngestionProcessor` required `(draft.Ingredients?.Count ?? 0) >= 2` when `job.AutoPublish` was false, even when `config.AutoPublishHighConfidenceText` was enabled and `unresolvedIssues.Count == 0`.
2. **Fix Sub-Recipe Detection, Unit Normalization, Back-Linking & Review Preview (*Denver Omelets* and `26+` unlinked sub-recipe parents):**
   - **Invalid `"Custom"` unit on sub-recipe rows (*Denver Omelets*):** Ingredient 6 (`"1 recipe Filling for Denver Omelets (this page)"`) was extracted with `IsRecipeReference = true, Unit = "Custom", CustomUnitName = "custom"` because `"recipe"` / `"batch"` was not mapped in `RecipeDraftNormalizer.NormalizeUnit`, triggering `"Ingredient 6 'Filling for Denver Omelets': custom unit is missing its actual unit name."`
   - **Measured sub-recipes & title cleanup:** Sub-recipe lines starting with a volume/weight measurement and ending with `(recipe follows)` (e.g., `"1/2 cup Fried Shallots (recipe follows)"`, `"2 tablespoons Sichuan Chili Oil (recipe follows)"`) were missed by `RecipeReferenceRegex`, and `NormalizeRecipeReferenceTitle` did not strip `(recipe follows)`, `(optional; recipe follows)`, `(this page and this page)`, or trailing preparation clauses.
   - **Automatic back-linking when sub-recipes publish after parent recipes:** In cookbook order, parent recipes with `"(recipe follows)"` are ingested and published *before* the sub-recipe that follows them. When the sub-recipe later publishes, `26` existing `RecipeIngredients` remained stuck with `LinkedRecipeId = null` and `UnresolvedRecipeTitle != null`.
   - **Review Editor & Preview sub-recipe link resolution:** `IngestionReviewComponent` hardcoded `unresolvedRecipeTitle: ing.isRecipeReference ? ing.referencedRecipeTitle : null` and never resolved `ReferencedRecipeTitle` against existing cookbook recipes, causing `RecipeDetailPreviewComponent` to unconditionally display `"Recipe component not linked"` even when the target recipe (such as *Filling for Denver Omelets*) was already published.
3. **Eliminate Remaining Deterministic Validation Failures (`31` of `97` jobs):**
   - **Culinary weight-to-volume conversions (`8` jobs, including *Denver Omelets*):** Support `4 oz ≈ 1 cup` (`1 oz ≈ 1/4 cup = 4 tbsp`) for shredded/grated cheese and diced/chopped meat/pancetta in `InstructionAmountTagger.TryToRowUnit`.
   - **Sub-recipe step portions (`2` jobs: *Lighter Chicken Parmesan*, *Tamales*):** When `ingredient.IsRecipeReference == true`, allow steps to measure portions in volume/weight units (`2 tbsp`, `1/2 cup`) without failing unit comparison against `1 Whole` recipe.
   - **Batch repetition & same-unit bulk remainder steps (`13` jobs, including *Denver Omelets*, *French Omelets*, *Chicken Kyiv*, *Blueberry Pancakes*, *Corn Tortillas*):** Recognize batch-repetition steps (`"Repeat [the instructions] for the second / remaining..."`) and treat unmeasured bulk steps as consuming the remaining quantity when another step measures a small sub-portion in the same unit (`1 tbsp` of `1 cup` flour/blueberries/water).
   - **Boiling/cooking water & conditional `"if needed"` oil (`9` jobs, e.g., *Hong Kong–Style Wonton Noodle Soup*, *Poulet au Vinaigre*):** Reclassify water inline amounts that exceed the ingredient row total as `Fixed` cooking medium, and treat conditional oil/fat additions (`"if pan is dry"`, `"if needed"`) after full row consumption as `Fixed`.
   - **Auto-link clearly mentioned unlinked ingredients (`3` jobs: *Chicken Vesuvio*, *Baked Ziti*, *Best Beef Stew*):** When an ingredient row is linked to `0` steps, automatically link it to steps where `RecipeExtractionDiagnostics.MentionsIngredient` is true.

---

## 2. Domain & Service Design (`Core/` & `Data/`)

### 2.1 `Core/Ingestion/SourceMeasurementParser.cs`

1. **Hyphenated number-unit & range regexes:**
   - Update `Measurement` regex to allow optional hyphens between numbers and units (`14.5-ounce`, `4-pound`, `5- to 6-ounce`):
     ```csharp
     private static readonly string Measurement =
         $@"(?<![-−\d/])(?<quantity>{Number})(?:[-–—]?\s*(?:to|[-–—])\s*(?<maximum>{Number}))?(?:[-–—]+|\s*)(?<unit>{Unit})\b";
     ```
   - Add `small|medium|large|recipes?|batch(?:es)?` to `Unit` regex and map `small`/`medium`/`large`/`recipe`/`recipes`/`batch`/`batches` to `MeasurementUnit.Whole` in `NormalizeUnit`.
2. **Compound & per-item parenthetical extraction:**
   - In `FindAlternative(string? source)`:
     - When a parenthetical contains `" or "` (e.g., `(2 1/2 cups or 5 ounces)` or `(1 pound or 2 medium)`), split on `\bor\b` and test each segment for a valid measurement whose unit differs from `primary.Unit`.
   - Add `public static IReadOnlyList<RecipeMeasurement> FindAllConfirmableMeasurements(string? source)`:
     - Returns all spans from `FindSpans(source)`, plus any measurements inside parenthetical segments split on `\bor\b`, plus container/piece multiplied totals (e.g., `8 (5- to 6-ounce) chicken thighs` $\rightarrow$ `40–48 oz` / `2.5–3 lb`; `1 (14.5-ounce) can` $\rightarrow$ `14.5 oz`).

### 2.2 `Core/Ingestion/RecipeDraftNormalizer.cs`

1. **Unit & sub-recipe normalization:**
   - In `NormalizeUnit`: map `"recipe" or "recipes" or "batch" or "batches"` to `MeasurementUnit.Whole`.
   - When `isRecipeReference` is true and `unit == MeasurementUnit.Custom` with empty or placeholder `CustomUnitName` (`"custom"`, `"recipe"`, `"batch"`), normalize `unit = MeasurementUnit.Whole` and `customUnitName = null`.
   - Extend `RecipeReferenceRegex` and add `TrailingRecipeFollowsRegex`:
     - Detect ingredients whose `OriginalRawText` or `DisplayName` contains `(?:optional\s*;\s*)?recipes?\s+follows?` or `see\s+this\s+page` even when preceded by a volume/weight measurement (e.g., `"1/2 cup Fried Shallots (recipe follows)"`, `"2 tablespoons Sichuan Chili Oil (recipe follows)"`).
   - Update `NormalizeRecipeReferenceTitle(string? value)`:
     - Strip parenthetical page/recipe markers: `\((?:optional\s*;\s*)?(?:see\s+)?(?:recipes?\s+follows?|this\s+page(?:\s+and\s+this\s+page)?|page\s+\d+)\)`
     - Strip trailing preparation suffixes after a comma (e.g., `", warm"`, `", room temperature"`, `", chilled"`, `", divided"`, `", optional"`, `" for serving"`).
2. **Accurate `MeasurementWarnings` confirmation:**
   - Update `IsAlternativeConfirmedBySource(RecipeMeasurement alternative, string? source)` to check against `SourceMeasurementParser.FindAllConfirmableMeasurements(source)` (including ounce/pound conversions: `16 oz = 1 lb`).
   - In `Normalize`: only emit `"the source equivalent applies to a whole ingredient line, not each split portion"` when `isSplitSourceLine && rawIng.AlternativeMeasurement is not null && EquivalentMeasurements(NormalizedMeasurement(rawIng.AlternativeMeasurement), sourcePair.Value.Alternative)`.

### 2.3 `Core/Ingestion/InstructionAmountTagger.cs`

1. **`RepairIngredientRows`:**
   - If `row.IsRecipeReference` (or `RecipeDraftNormalizer.TryExtractRecipeReference` matches `row.OriginalRawText` / `row.DisplayName`) and `NormalizeUnit(row.Unit)` is `Custom` with null/placeholder `CustomUnitName` (`"custom"`, `"recipe"`, `"batch"`), set `row.Unit = MeasurementUnit.Whole.ToString()` and `row.CustomUnitName = null`.
   - Auto-link unlinked ingredient rows: if an ingredient index `i` is not in any step's `IngredientIndices`, scan all steps with `RecipeExtractionDiagnostics.MentionsIngredient(row, step.Instruction)` and add `i` to matching steps' `IngredientIndices` before running `TagSteps` / `Reconcile`.
2. **`TryToRowUnit` & `Reconcile`:**
   - **Sub-recipe exemption:** When `ingredient.IsRecipeReference == true` and `rowUnit == MeasurementUnit.Whole`, treat step inline measurements for that ingredient as compatible sub-recipe portions (do not emit `"uses unit '...' which cannot be compared with the ingredient list (1 Whole)"`).
   - **Cheese & diced meat weight-to-volume conversions:** When `IsShreddedOrGratedCheese(ingredient)` or `IsDicedMeatOrPancetta(ingredient)` is true, convert between weight (`Oz`, `Lb`, `G`) and volume (`Cup`, `Tbsp`, `Tsp`) using `4 oz = 1 cup = 16 tbsp` (`113.4 g = 1 cup`).
   - **Batch repetition multiplier:** When a step instruction matches `\brepeat\b.*\bfor\s+(?:the\s+)?(?:second|other|remaining|next|each)\b` (e.g., *Denver Omelets* `"Repeat the instructions for the second omelet"`, *French Omelets*, *Chicken Kyiv*), treat single-batch step consumption that equals an exact fraction (`1/2`, `1/3`, `1/4`) of the row quantity as reconciled across the repeated batches.
   - **Same-unit small sub-portion + unmeasured bulk step:** When an ingredient has both (a) one or more measured step amounts in a compatible unit totaling less than the row quantity (e.g., `1 tbsp` of `1 cup` flour/blueberries/water) and (b) at least one other linked step that mentions the ingredient in bulk without an inline amount for that ingredient, treat the unmeasured bulk step as consuming the remainder.
   - **Boiling/cooking water & conditional oil:**
     - When `IsWaterIngredient(ingredient)` is true and a step span's converted amount exceeds the ingredient row's total quantity (e.g., `"4 quarts water"` for boiling wontons when the filling row has `"2 tablespoons water"`), reclassify that span as `Fixed` (`IngredientIndex = null`) so it does not overflow the recipe ingredient row.
     - When an oil/butter/fat ingredient row is already 100% consumed by earlier spans and a later span appears in a conditional clause (`\bif\s+(?:the\s+)?(?:pan|skillet|pot|mixture)\s+is\s+dry\b|\bif\s+needed\b|\bas\s+needed\b`), reclassify that conditional span as `Fixed`.

### 2.4 `Data/Services/IngestionCatalogMaterializer.cs`, `IngestionProcessor.cs`, `IngestionService.cs` & `RecipeService.cs`

1. **Sub-Recipe Back-Linking (`IngestionCatalogMaterializer`):**
   - Enhance `ResolveRecipeReferenceAsync` so it strips trailing parenthetical markers (`NormalizeRecipeReferenceTitle`) and matches case-insensitively against `Recipes.Title`.
   - Add `BacklinkUnresolvedRecipeReferencesAsync(AppDbContext dbContext, Guid recipeId, string recipeTitle, CancellationToken cancellationToken)`:
     - Finds all `RecipeIngredients` where `LinkedRecipeId == null && UnresolvedRecipeTitle != null && RecipeId != recipeId` whose normalized `UnresolvedRecipeTitle` matches `NormalizeRecipeReferenceTitle(recipeTitle)` (case-insensitive).
     - Sets `LinkedRecipeId = recipeId` and `UnresolvedRecipeTitle = null`.
   - Add `RepairAllUnresolvedRecipeReferencesAsync(AppDbContext dbContext, CancellationToken cancellationToken)` to back-link all existing published recipes in one batch query.
   - Call `BacklinkUnresolvedRecipeReferencesAsync` inside:
     - `IngestionProcessor.PublishDraftInternalAsync`
     - `IngestionService.ApproveJobAsync`
     - `RecipeService.CreateRecipeAsync` and `RecipeService.UpdateRecipeAsync`
2. **Auto-Publish Gating (`IngestionProcessor.cs`):**
   - Allow 1-ingredient recipes (`(draft.Ingredients?.Count ?? 0) >= 1`) to auto-publish when `job.AutoPublish || config.AutoPublishHighConfidenceText` is true and `unresolvedIssues.Count == 0`.
   - Do not block `canAutoPublish` on advisory `MeasurementWarnings` when `unresolvedIssues.Count == 0` and `draft.ConfidenceScore >= AutoPublishConfidenceThreshold`.
3. **Draft Sub-Recipe Resolution (`Core/Ingestion/IngestionModels.cs`, `IngestionProcessor.cs`, `IngestionService.cs`):**
   - Add optional fields to `IngestionDraftIngredientDto`:
     ```csharp
     Guid? MatchedRecipeId = null,
     string? MatchedRecipeTitle = null,
     string? MatchedRecipeSlug = null
     ```
   - Populate `MatchedRecipeId`, `MatchedRecipeTitle`, and `MatchedRecipeSlug` when matching draft catalog items (`IngestionProcessor.MatchFoodCatalogAsync` and `IngestionService.GetJobByIdAsync`) so the Review UI knows immediately whether a referenced recipe is already in the cookbook.

---

## 3. Angular Client Changes (`Client/`)

### 3.1 Models (`Client/src/app/core/models/ingestion.models.ts` & `recipe.models.ts`)

- In `IngestionDraftIngredientDto` (`ingestion.models.ts`), add:
  ```ts
  matchedRecipeId?: string | null;
  matchedRecipeTitle?: string | null;
  matchedRecipeSlug?: string | null;
  ```
- In `RecipePreviewIngredient` (`recipe.models.ts`), add:
  ```ts
  linkedRecipeId?: string | null;
  linkedRecipeTitle?: string | null;
  linkedRecipeSlug?: string | null;
  ```

### 3.2 Review Editor & Preview (`Client/src/app/features/ingestion/`)

- **`ingestion-review.component.ts` & `.html`:**
  - Include `matchedRecipeId`, `matchedRecipeTitle`, and `matchedRecipeSlug` in the ingredient form group.
  - In the ingredient binding strip (`ingestion-review.component.html`), when `isRecipeReference` is true:
    - If `matchedRecipeId` is present, show a linked recipe pill (`Linked Recipe: <matchedRecipeTitle || referencedRecipeTitle>`).
    - Otherwise, show `Recipe component (will link when published)`.
  - In `buildPreviewData()` (`ingestion-review.component.ts` line 778), set:
    ```ts
    linkedRecipeId: ing.isRecipeReference ? (ing.matchedRecipeId ?? null) : null,
    linkedRecipeTitle: ing.isRecipeReference && ing.matchedRecipeId
      ? (ing.matchedRecipeTitle || ing.referencedRecipeTitle || null)
      : null,
    unresolvedRecipeTitle: ing.isRecipeReference && !ing.matchedRecipeId
      ? (ing.referencedRecipeTitle || null)
      : null,
    ```
- **`recipe-detail-preview.component.html`:**
  - When `item.ingredient.linkedRecipeTitle` is truthy, render a linked recipe badge (`Linked recipe`) instead of `"Recipe component not linked"`.

---

## 4. Verification & Live Requeue Plan

1. **Unit & Integration Tests:**
   - Add unit tests in `Tests/Core.UnitTests/Ingestion/SourceMeasurementParserTests.cs`, `RecipeDraftNormalizerTests.cs`, and `InstructionAmountTaggerTests.cs` covering:
     - Hyphenated parentheticals (`(14.5-ounce)`, `(4-pound)`, `(5- to 6-ounce)`), size words (`(2 medium)`), and `"or"` parentheticals (`(2 1/2 cups or 5 ounces)`).
     - *Chicken Provençal* and *Easy-Peel Jammy Eggs* producing `0` measurement warnings and qualifying for auto-publish.
     - *Denver Omelets* (`1 recipe Filling for Denver Omelets (this page)`, `2 tbsp` butter, `4 oz` shredded Monterey Jack cheese (`1/2 cup`), and Step 5 `"Repeat the instructions for the second omelet"`) producing `0` validation issues.
     - Measured sub-recipe references (`"1/2 cup Fried Shallots (recipe follows)"`) and title normalization (`NormalizeRecipeReferenceTitle`).
   - Add integration tests in `Tests/API.IntegrationTests/IngestionControllerTests.cs` verifying that publishing a sub-recipe after its parent recipe automatically back-links the parent's `RecipeIngredient.LinkedRecipeId` and clears `UnresolvedRecipeTitle`.
2. **Angular Unit Tests & Playwright E2E Tests:**
   - Update `ingestion-review.component.spec.ts` to verify that a draft with a matched sub-recipe (`matchedRecipeId` set) renders the linked recipe state in both the editor and `RecipeDetailPreviewComponent` (without showing `"Recipe component not linked"`).
   - Run full Playwright E2E verification (`Tests/Client.E2ETests`).
3. **Live Database Repair & Requeue:**
   - Run the sub-recipe back-linking repair across existing published recipes in `LocalRoast` and requeue the remaining `ReadyForReview` jobs to verify live auto-publish and sub-recipe linking (*Denver Omelets*, *Chicken Provençal*, etc.).

---

## 5. Ordered Execution Checklist

- [x] **Task 1: `SourceMeasurementParser` & `RecipeDraftNormalizer` Refinements (`Core/Ingestion/`)**
  - Update `SourceMeasurementParser.cs` and `RecipeDraftNormalizer.cs`.
  - Add unit tests in `Tests/Core.UnitTests/Ingestion/`.
  - Verify: `dotnet test --project Tests/Core.UnitTests/App.Core.UnitTests.csproj`
- [x] **Task 2: `InstructionAmountTagger` Culinary Conversions, Batch Repetition & Auto-Linking (`Core/Ingestion/`)**
  - Update `InstructionAmountTagger.cs`.
  - Add unit tests in `Tests/Core.UnitTests/Ingestion/InstructionAmountTaggerTests.cs`.
  - Verify: `dotnet test --project Tests/Core.UnitTests/App.Core.UnitTests.csproj`
- [x] **Task 3: Sub-Recipe Back-Linking, Draft Sub-Recipe Resolution & Auto-Publish Rules (`Data/Services/`)**
  - Update `IngestionModels.cs`, `IngestionCatalogMaterializer.cs`, `IngestionProcessor.cs`, `IngestionService.cs`, and `RecipeService.cs`.
  - Add integration test in `Tests/API.IntegrationTests/`.
  - Verify: `dotnet test --project Tests/API.IntegrationTests/App.API.IntegrationTests.csproj`
- [x] **Task 4: Angular Review Editor & Preview Sub-Recipe Link Display (`Client/`)**
  - Update `ingestion.models.ts`, `recipe.models.ts`, `ingestion-review.component.ts/.html`, `recipe-detail-preview.component.html`, and `ingestion-review.component.spec.ts`.
  - Verify: `npm --prefix Client test -- --watch=false`
- [x] **Task 5: Formatting, E2E Verification & Live Requeue**
  - Run `dotnet csharpier check .` and `npm --prefix Client run lint`.
  - Run `dotnet test --project Tests/Client.E2ETests/App.Client.E2ETests.csproj`.
  - Restart local API daemon and requeue remaining `ReadyForReview` jobs to confirm *Chicken Provençal*, *Denver Omelets*, and sub-recipe back-links.
