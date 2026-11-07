# Phase 12: Tagged Instruction Ingestion & Scalable Inline Amounts

> **Branch:** `feature/phase-12-tagged-instruction-ingestion`
> **Status:** `[x]` Completed
> **PRD Reference:** `docs/prd.md` — Sections 4.1.4, 4.1.5, 4.2.1, 4.2.3, 4.2.4
> **Target Execution:** Run `/execute-phase` to execute this plan step-by-step.

---

## 0. Background (read first)

LLMs (local Gemma 4 E4B and hosted Gemini Flash) cannot reliably *rewrite* step text to strip
ingredient amounts. This phase replaces that approach with **"tag, don't rewrite"**:

1. Step text is the **verbatim source paragraph**. No splitting and no rewriting.
2. **Code** finds every amount and duration in the method (`SourceMeasurementParser.FindSpans`,
   `InstructionAmountTagger.FindCandidates`).
3. The **LLM only classifies** them: which ingredient row each amount measures (or *mixture* / *other*),
   which rows each paragraph uses, and a label for each timer.
4. **Code assembles** the steps (`InstructionAmountTagger.Assemble`) and reconciles the step amounts
   against the shopping-list rows.
5. The UI renders each tagged amount as an **inline chip that scales** with the serving multiplier and
   unit system. It aggregates the uses per ingredient row into a "divided" breakdown.

A working prototype already exists **uncommitted on this branch** and must be kept and promoted:

| File | Prototype content |
| :--- | :--- |
| `Core/Ingestion/InstructionAmountTagger.cs` | `ParseMethod`, `FindCandidates` (duration filter), `GetIngredientLabels`, `BuildPrompt`, `Assemble` (sibling-extra, overflow repair, mention fallback, reconciliation), `RepairIngredientRows` |
| `Core/Ingestion/SourceMeasurementParser.cs` | `FindSpans` + `SourceMeasurementSpan` |
| `Core/Recipes/CulinaryMath.cs` | `TryConvertQuantity` |
| `Core/Ingestion/IngestionModels.cs` | `InlineStepAmount`, `InlineStepAmountKind`, `LlmExtractedStepItem.InlineAmounts` |
| `Infrastructure/Ingestion/LlmExtractionClient.cs` | `MethodTagsSystemPrompt`, `ExtractRecipeWithTaggedStepsAsync` (public, not on the interface) |
| `Core/Ingestion/RecipeDraftNormalizer.cs`, `RecipeExtractionDiagnostics.cs` | bypasses for steps with `InlineAmounts` |
| `scripts/IngestionEvaluator/Program.cs`, `scripts/evaluate_atk_ingestion.py` | `--pipeline staged\|tagged` |
| `Tests/Core.UnitTests/InstructionAmountTaggerTests.cs` | 10 tests |

Prototype ATK results (gemma4:e4b, 9 cases): the current pipeline had **10 issues in ~1,372 s**; the tagged
pipeline had **3 issues in ~550 s**. The 3 remaining issues are expectation-only.

Other uncommitted work on this branch, also to be committed with this phase:
- Startup legacy backfills were removed (`API/Program.cs`, `FoodCategoryBackfill.cs` deleted,
  `IngestionCatalogMaterializer` legacy methods).
- The `c012-ss286` Sourdough Starter ATK case was added.

**Data policy (user decision):** this is a testing period. Do **not** write data backfills or
retroactive fix-ups. Existing recipes simply have no inline amounts and render their plain text.
EF migrations are still required.

---

## 1. Scope & Objectives

**In scope**
1. **Persist inline amounts** per step (`RecipeStepInlineAmount`) and expose them in the DTOs, editor inputs and
   the ingestion draft.
2. **One text extraction pipeline:** `ILlmExtractionClient.ExtractRecipeFromTextAsync` *is* the tagged pipeline.
   - Delete the staged and single-pass rewriting paths, the automatic repair loop, the step JSON schema,
     and the settings `ExtractionStrategy` / `ExtractionRepairRounds`.
3. **Vision becomes transcription:** images and scanned PDFs are transcribed verbatim by the Vision model into
   canonical `Ingredients:` / `Instructions:` text (stored in `IngestionJob.StructuredSourceText`). That text
   then runs through the same tagged text pipeline with the Text model.
4. **Manual AI repair and background refine** re-run the tagged extraction with the reviewer hint and the current
   rows as context. Steps are always regenerated from the source paragraphs.
5. **Diagnostics:** a new `TaggedStepDiagnostics` reconciles any (edited) draft. Delete the obsolete step-prose checks
   from `RecipeExtractionDiagnostics`.
6. **Client:**
   - A shared `StepInstructionComponent` renders scalable chips in recipe detail, cook mode (including speech),
     the public share, print and the review preview.
   - A divided-uses breakdown appears under the ingredient rows.
   - A shared `InlineAmountEditorComponent` (detect / tag / relocate) is used in the recipe editor and the
     review editor.
7. Update the docs (`docs/prd.md`, `docs/llm-model-settings.md`, `docs/contributing.md` if it mentions step amounts)
   and the ATK evaluator. The evaluator keeps a single pipeline, so remove `--pipeline` and `--repair-rounds`.

**Out of scope**
- Re-extracting or migrating existing recipes.
- MCP tool output changes beyond what compiles. MCP keeps using the derived `IngredientAmounts`.
- Shopping-list math, which keeps using whole-recipe rows.

---

## 2. Domain & Database Design

### 2.1 Enums & Value Types

**Move** `InlineStepAmountKind` from `Core/Ingestion/IngestionModels.cs` to the new file
`Core/Recipes/InlineStepAmountKind.cs` (namespace `App.Core.Recipes`). Keep the XML docs and the values
`Ingredient`, `Mixture`, `Fixed`. Fix the usings.

**Delete** `LlmExtractionStrategy` from `Core/Ingestion/IngestionEnums.cs`.

### 2.2 Entities (`Data/Entities/`)

**New `RecipeStepInlineAmount.cs`** (`IAuditable`; mirror `RecipeStepIngredient`):
```csharp
public sealed class RecipeStepInlineAmount : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RecipeStepId { get; set; }
    public int SortOrder { get; set; }              // ascending by Start
    public int Start { get; set; }                  // char offset into RecipeStep.Instruction
    public int Length { get; set; }
    public InlineStepAmountKind Kind { get; set; }
    public Guid? RecipeIngredientId { get; set; }   // required iff Kind == Ingredient
    public decimal? Quantity { get; set; }
    public decimal? MaxQuantity { get; set; }
    public MeasurementUnit? Unit { get; set; }
    public string? CustomUnitName { get; set; }
    public decimal? AlternativeQuantity { get; set; }
    public decimal? AlternativeMaxQuantity { get; set; }
    public MeasurementUnit? AlternativeUnit { get; set; }
    public string? AlternativeCustomUnitName { get; set; }
    // audit fields + navigations: RecipeStep RecipeStep, RecipeIngredient? RecipeIngredient
}
```
- `RecipeStep`: add `ICollection<RecipeStepInlineAmount> InlineAmounts`.
- `RecipeIngredient`: add `ICollection<RecipeStepInlineAmount> InlineAmountReferences`.
- `HouseholdLlmSettings`: **remove** `ExtractionRepairRounds` and `ExtractionStrategy`.

### 2.3 EF Core Configurations

**New `Data/Configurations/RecipeStepInlineAmountConfiguration.cs`**
- Table `RecipeStepInlineAmounts`, key `Id`.
- Unique index `(RecipeStepId, SortOrder)`.
- Precision `(10,4)` on the four quantity columns.
- `Unit` / `AlternativeUnit`: `HasConversion<string>().HasMaxLength(32)`. `Kind`: `HasConversion<string>().HasMaxLength(16)`.
- Custom unit names: `HasMaxLength(50)`. `CreatedBy` / `ModifiedBy`: required, `HasMaxLength(256)`.
- `RecipeStep` → `InlineAmounts`: `DeleteBehavior.Cascade`.
- `RecipeIngredient` → `InlineAmountReferences`: **`DeleteBehavior.Restrict`**. A cascade here would create
  multiple cascade paths from `Recipe`. The services must delete inline amounts before ingredients, exactly as they
  do for `RecipeStepIngredient`; find every `StepReferences`/`IngredientReferences` removal and mirror it.

Remove the two settings column configurations from `HouseholdLlmSettingsConfiguration.cs`.

Register the new `DbSet<RecipeStepInlineAmount> RecipeStepInlineAmounts` in `AppDbContext`.

**Migration:** `dotnet ef migrations add TaggedInstructionAmounts --project Data/App.Data.csproj`
(creates the table and drops `ExtractionRepairRounds` and `ExtractionStrategy`; no data scripts).

### 2.4 DTOs, Requests & Contracts

`Core/Recipes/RecipeModels.cs`:
```csharp
public record RecipeStepInlineAmountInput(
    [Range(0, 3999)] int Start,
    [Range(1, 200)] int Length,
    InlineStepAmountKind Kind,
    int? IngredientIndex,
    [Required] RecipeMeasurement Measurement,
    RecipeMeasurement? AlternativeMeasurement = null
);
public record RecipeStepInlineAmountDto(
    int Start, int Length, InlineStepAmountKind Kind, Guid? IngredientId,
    RecipeMeasurement Measurement, RecipeMeasurement? AlternativeMeasurement = null
);
```
- `RecipeStepInput`: add `IReadOnlyList<RecipeStepInlineAmountInput>? InlineAmounts = null` (last parameter).
- `RecipeStepDto`: add `IReadOnlyList<RecipeStepInlineAmountDto>? InlineAmounts = null` (last parameter).
- `Core/Ingestion/IngestionModels.cs`, `IngestionDraftStepDto`: add
  `IReadOnlyList<RecipeStepInlineAmountInput>? InlineAmounts = null` (last parameter).
- New `public record DetectedStepAmountDto(int Start, int Length, string Text, RecipeMeasurement Measurement, RecipeMeasurement? AlternativeMeasurement);`
  and `public record DetectStepAmountsRequest([Required, MaxLength(4000)] string Instruction);` in `RecipeModels.cs`.
- `IRecipeService`: add `IReadOnlyList<DetectedStepAmountDto> DetectStepAmounts(string instruction);`
  It is synchronous and pure, wrapping `SourceMeasurementParser.FindSpans`.
  - Map `SourceMeasurementSpan.Measurement` / `.Alternative` to `RecipeMeasurement`.
  - Skip spans whose `Measurement.Quantity` is null.

`Core/Ingestion/ILlmExtractionClient.cs`:
- `LlmExtractionContext`: add `string? ReviewerHint = null` and
  `IReadOnlyList<LlmExtractedIngredientItem>? CurrentIngredients = null`.
- **Replace** `ExtractRecipeFromImagesAsync` with
  `Task<string?> TranscribeRecipeImagesAsync(EffectiveLlmConnectionConfig config, LlmVisionExtractionContext context, CancellationToken ct = default);`
  - It returns verbatim text containing `Ingredients:` with `- ` bullets and `Instructions:` with numbered paragraphs.
  - It returns null if no recipe is found.
- **Change** `RepairRecipeDraftAsync`: `LlmRecipeRepairContext.SourceContent` becomes **required** (`string`), and `Images` is removed.
  - Implementation: call `ExtractRecipeFromTextAsync` with `ReviewerHint = UserHint` and `CurrentIngredients = Draft.Ingredients`.
  - Return the new payload plus notices. The notices are the tagger issues, prefixed `"Still needs review: "`.
- `LlmExtractedRecipePayload`: add `IReadOnlyList<string>? TaggerIssues = null`. It is set by the pipeline and not part of any JSON schema.

`Core/Ingestion/LlmSettingsModels.cs` / `LlmModelProfiles.cs`:
- Remove every `ExtractionStrategy` / `ExtractionRepairRounds` / `UseStagedExtraction` member and parameter, plus `UsesStagedExtraction`.
- Remove the profile notes that say "Extracts ingredients and instructions in separate requests" or "Extracts each recipe in a single request".
- Remove the E2B note "Often leaves amounts in step text".

### 2.5 Service Business Logic

**`Core/Ingestion/InstructionAmountTagger.cs`**
- Promote it as-is.
- Extract the reconciliation in `Assemble` (the per-row totals check, the "not used in any step" check and overflow detection) into
  `public static IReadOnlyList<string> Reconcile(IReadOnlyList<LlmExtractedIngredientItem> ingredients, IReadOnlyList<LlmExtractedStepItem> steps)`.
  `Assemble` calls it, so the behavior stays identical.

**New `Core/Ingestion/TaggedStepDiagnostics.cs`**: `public static IReadOnlyList<string> GetIssues(LlmExtractedRecipePayload recipe)`. It returns:
- Structural issues per step:
  - Spans out of range, overlapping or unsorted.
  - `Ingredient` kind without a valid index; non-`Ingredient` kind with an index.
  - Text in the span that no longer parses (`SourceMeasurementParser.FindSpans` of the span text yields none).
  - Message format: `"Step {n}: inline amount \"{text}\" ..."`.
- `InstructionAmountTagger.Reconcile(...)` issues.

**`Core/Ingestion/RecipeExtractionDiagnostics.cs`** (tagged steps are verbatim and reconciled, so the prose checks are obsolete). **Delete** these checks and their helpers once they become unused:
- The source-method mixture issue (~line 124).
- The step >300 characters warning.
- The "multiple actions" warning.
- The fixed-ingredient-amounts issue.
- The divided-addition reuse issue.
- Source portion conservation (~line 449).
- Source timed phases (~line 495).
- "Measured ingredient is not linked to any step" (now `Reconcile`'s "not used in any step").

**Keep** the ingredient-name, preparation-note, non-positive, custom-unit, missing source line/verbatim `originalRawText`, alternatives, out-of-range index, vague timer label and mentioned-but-not-linked checks. Remove the prototype "tagged bypass" branches.

`GetReviewIssues` = the kept issues + `TaggedStepDiagnostics.GetIssues`.

Update `RecipeExtractionDiagnosticsTests` by deleting the tests for the removed checks.

**`Core/Ingestion/RecipeDraftNormalizer.cs`**
- Map `LlmExtractedStepItem.InlineAmounts` to `IngestionDraftStepDto.InlineAmounts`. `Text` is dropped.
- Remap `IngredientIndex` through the same old→new index map used for `IngredientIndices`. An `Ingredient` span whose row was dropped becomes `Fixed` with a null index.
- Do **not** strip or rewrite instruction text when `InlineAmounts` is non-empty; the instruction must be byte-identical so the offsets stay valid. If any trimming happens, shift `Start` accordingly.
- Remove the prototype bypass comments that become redundant.

**`Core/Ingestion/IngestionDraftRepairMapper.cs`**
- `ToPayload`: steps carry `InlineAmounts`. Build `InlineStepAmount` with `Text = instruction.Substring(Start, Length)`.
- `Merge`: the proposed steps (with inline amounts) replace the draft's steps wholesale. Index remapping follows the ingredient merge order.

**`Data/Services/RecipeService.cs`**
- *Validation* (beside the timer/IngredientAmounts validation, ~line 1283), for each step's `InlineAmounts`:
  - Sorted by `Start` and non-overlapping.
  - `Start + Length <= Instruction.Trim().Length`.
  - `Kind == Ingredient` ⇔ `IngredientIndex` in `[0, ingCount)`.
  - `RecipeMeasurementValidation.Validate` on both measurements.
  - Otherwise `DomainValidationException("Each inline amount must cover a distinct part of its step and reference a valid ingredient.")`.
  - Offsets are relative to the **trimmed** instruction: compute `lead = Instruction.Length - Instruction.TrimStart().Length` and subtract it from `Start` before validating.
- *Persistence* (~line 1444):
  - Create `RecipeStepInlineAmount` rows with `SortOrder` = position, linking `createdIngredients[idx]`.
  - The step's ingredient links are the union of `IngredientIndices`, the `IngredientAmounts` keys and the `Ingredient` span indices.
  - When a row has ≥1 `Ingredient` span in the step, its `RecipeStepIngredient` amount is **derived**: the sum of its spans converted into the first span's unit via `CulinaryMath.TryConvertQuantity`. If any span fails to convert, the amount is null. A client-sent `IngredientAmounts` entry for that row is ignored.
- *Reads* (~line 648): include `.ThenInclude(s => s.InlineAmounts)` in every query that includes `IngredientReferences` and map to `RecipeStepInlineAmountDto` ordered by `SortOrder`.
- *Deletes / updates:* delete the step inline amounts wherever step ingredient references are deleted (Restrict FK).
- `DetectStepAmounts`: described in 2.4.
- **`Data/Services/IngestionCatalogMaterializer.cs` / `IngestionService.cs`:** pass `InlineAmounts` from `IngestionDraftStepDto` to `RecipeStepInput` in both publish paths. Grep `IngredientAmounts =` in both files and add the sibling.

**`Infrastructure/Ingestion/LlmExtractionClient.cs`**
- `ExtractRecipeFromTextAsync` becomes the body of `ExtractRecipeWithTaggedStepsAsync`; delete the latter name.
- `ExtractRecipeFromTextAsync` sets `TaggerIssues` on the returned payload.
- When `context.ReviewerHint` or `CurrentIngredients` is set, append to the ingredient prompt (user message, after the source):
  `"Reviewer feedback (apply it): {hint}"` and `"Previous ingredient rows (fix rather than restart): {json}"`.
  The hint is also appended to the method-tags user prompt.
- **Delete:**
  - `ExtractRecipeInStagesAsync`.
  - The single-pass branch.
  - `RepairRecipeAsync`, `RepairSystemPrompt`, `GetManualRepairIssues`, `RecipeStepsPayload`.
  - `BuildExtractionSystemPrompt`, `BuildVisionExtractionSystemPrompt`.
  - From `GetRecipeExtractionJsonSchema`: the `steps` property, its `required` entry and the step sub-schemas, so the method no longer needs to remove them at runtime.
  - `MeasurementAndReferenceRules`, if it becomes unused.
- **New `TranscribeRecipeImagesAsync`:**
  - Uses `SendStructuredVisionChatAsync` with schema `{title, introduction, ingredientLines: string[], instructionParagraphs: string[], notes}`.
  - The system prompt says: copy text verbatim; one ingredient line per printed line, including section headers as lines ending in `:`; one entry per printed method paragraph; keep the original numbering out.
  - Code builds the canonical text:
    `"{title}\n\n{introduction}\n\nIngredients:\n- {line}…\n\nInstructions:\n1. {para}…"`.
  - It returns null when there are no ingredient lines or no instruction paragraphs.
- `RepairRecipeDraftAsync`: implemented as in 2.4.

**`Data/Services/IngestionProcessor.cs`**
- *Vision branch:*
  - Call `TranscribeRecipeImagesAsync`.
  - Set `job.StructuredSourceText` to the result; on null, throw `DomainValidationException("No recipe text could be read from the images.")`.
  - Set `diagnosticsSource` to it.
  - Then fall through to the **text** extraction with the Text model. Require `config.TextModel` and set `modelUsed = $"{VisionModel} → {TextModel}"`.
  - Restructure the method so the vision and text paths converge before `ExtractRecipeFromTextAsync`.
- Delete the empty-result retry block (~line 575–605); the tagged pipeline throws on empty results.
- `extractionIssues = RecipeExtractionDiagnostics.GetReviewIssues(rawExtracted, diagnosticsSource)`. This now includes the tagger issues, so ignore `rawExtracted.TaggerIssues` here to avoid duplicates.
- The refine condition loses `!requiresVision`, because vision jobs now have source text.

**`Data/Services/IngestionReviewService.cs`**
- `GetSource(job)` must return `StructuredSourceText ?? NormalizedSourceText ?? RawText`.
- Repair or refine with a null source throws `DomainValidationException("This job has no source text to re-extract from.")`.
- Remove image loading for repair.

**`Data/Services/LlmSettingsService.cs`**: remove the strategy / repair-round mapping and validation.

---

## 3. API Endpoints

| Verb | Route | Request | Response | Errors |
| :--- | :--- | :--- | :--- | :--- |
| `POST` | `/api/recipes/step-amounts/detect` | `DetectStepAmountsRequest` | `200 IReadOnlyList<DetectedStepAmountDto>` | `400` invalid body; `401` unauthenticated |

Add it to `API/Controllers/RecipesController.cs` with the controller's existing authorization; it is a thin call to
`recipeService.DetectStepAmounts`.

All existing recipe, share and ingestion endpoints carry the new `InlineAmounts` fields automatically. The
LLM settings endpoints lose the two removed fields.

---

## 4. Angular Frontend Design

### 4.1 Models & Utils

`core/models/recipe.models.ts`:
```ts
export type InlineStepAmountKind = 'Ingredient' | 'Mixture' | 'Fixed';
export interface RecipeStepInlineAmountDto { start: number; length: number; kind: InlineStepAmountKind; ingredientId: string | null; measurement: RecipeMeasurement; alternativeMeasurement?: RecipeMeasurement | null; }
export interface RecipeStepInlineAmountInput { start: number; length: number; kind: InlineStepAmountKind; ingredientIndex: number | null; measurement: RecipeMeasurement; alternativeMeasurement?: RecipeMeasurement | null; }
export interface DetectedStepAmountDto { start: number; length: number; text: string; measurement: RecipeMeasurement; alternativeMeasurement: RecipeMeasurement | null; }
```
- `RecipeStepDto.inlineAmounts?: RecipeStepInlineAmountDto[] | null`.
- `RecipeStepInput.inlineAmounts?` (`RecipeStepInlineAmountInput[]`), and the same on the ingestion draft step model.
- `core/models/ingestion.models.ts`: remove `extractionStrategy`, `extractionRepairRounds` and `useStagedExtraction`, along with their option lists.
- `RecipeService.detectStepAmounts(instruction: string): Observable<DetectedStepAmountDto[]>`.

**New `core/utils/step-instruction.ts`** (with `step-instruction.spec.ts`):
- `export type InstructionSegment = { text: string; amount?: { kind: InlineStepAmountKind; ingredientId: string | null; scaled: boolean } }`
- `buildInstructionSegments(step, ingredients, factor, system): InstructionSegment[]`. Plain text and amount segments alternate.
  - `Fixed`, or `factor === 1 && system === 'Original'`: the verbatim span text.
  - `Ingredient`: `scaleStepIngredient(ingredient, measurement, factor, system)` → `formattedAmount + ' ' + unitLabel`. If the span has an `alternativeMeasurement`, append ` (alt)` scaled with `scaleAndConvert`, mirroring the source style `2 cups (10 ounces)`.
  - `Mixture`: `scaleAndConvert(...)` on the measurement (+ alternative).
  - Ignore invalid spans (out of range or overlapping); render the text plainly.
- `renderScaledInstruction(...): string` joins the segment texts; used by cook-mode speech.
- `ingredientUses(steps, ingredientId, ingredients, factor, system): { stepNumber: number; label: string }[]`: one entry per `Ingredient` span of that row.
- `relocateInlineAmounts(oldText, newText, amounts): { amounts; dropped: number }`:
  - Each span keeps its text.
  - It is relocated to the occurrence of that text in `newText` closest to its old start that doesn't overlap an already placed span.
  - Unmatched spans are dropped.
- Spec cases: verbatim at 1x; 2x scaling of `1 1/2 cups` → `3 cups`; Fixed unchanged at 2x; metric conversion; alternative appended; invalid span ignored; relocation after inserted text; dropped span.

`core/utils/culinary-math.ts`: in `stepIngredientCallouts`, **skip** rows that have an `Ingredient` inline span in that step. The chip already shows the amount, and unmeasured or unspanned links still show as callouts.

### 4.2 Components

**New `shared/components/step-instruction/` (`step-instruction.component.{ts,html,scss,spec.ts}`)**
- Standalone `StepInstructionComponent`, `selector: 'app-step-instruction'`.
- Inputs (signals): `step`, `ingredients`, `factor`, `unitSystem`.
- `segments = computed(() => buildInstructionSegments(...))`.
- Renders inline `<span class="inline-amount" [class.is-scaled]="seg.amount.scaled" [attr.title]="ingredient display name">`. Fixed amounts get no chip styling.
- Chips use `--mat-sys-primary-container` / `--mat-sys-on-primary-container`, `border-radius: 6px`, `padding: 0 4px`, and `white-space: nowrap`.
- Print CSS: no background, bold text.

Use it in place of the raw `{{ ...instruction }}` in:
- `features/recipes/recipe-detail/recipe-detail.component.html` (line ~545).
- `features/recipes/cook-mode/cook-mode.component.html` (~461). Cook mode's speech uses `renderScaledInstruction`.
- `features/recipes/public-shared-recipe/public-shared-recipe.component.html`.
- `features/ingestion/recipe-detail-preview/recipe-detail-preview.component.html`.

Pass the existing scale factor and unit-system signals of each view.

**Divided breakdown:** in recipe detail, public share and review preview, under an ingredient row whose `ingredientUses(...)` returns ≥2 entries, or 1 entry whose label differs from the row's scaled total, render
`<div class="ingredient-uses">Divided: 1/2 cup (step 2) · 1/2 cup (step 4)</div>` (small, `--mat-sys-on-surface-variant`).

**New `shared/components/inline-amount-editor/` (`inline-amount-editor.component.{ts,html,scss,spec.ts}`)**
- Inputs: `instruction` (signal), `amounts` (`RecipeStepInlineAmountInput[]`), `ingredientOptions` (`{ index: number; label: string }[]`). Output: `amountsChange`.
- Behavior:
  - Shows the instruction with the tagged spans highlighted.
  - Lists the spans as rows: span text, a kind `mat-select` (Ingredient / Mixture / Other), and an ingredient select when the kind is Ingredient.
  - Each row has a remove button (`delete` icon).
  - The **"Detect amounts"** button (`auto_awesome` icon) calls `detectStepAmounts`. It adds spans that don't overlap existing spans as `Fixed`, with the detected measurements.
  - When `instruction` changes, it runs `relocateInlineAmounts`. If any span was dropped, it emits and shows `MatSnackBar` "N tagged amounts no longer match the step text and were removed."
- Layout: rows stack vertically at `360px`; on desktop the selects are inline.

Embed it under each step's instruction textarea in:
- `features/recipes/recipe-editor/recipe-editor.component.{ts,html}`, which maps to and from `inlineAmounts` in the form model and save payload.
- `features/ingestion/ingestion-review/ingestion-review.component.{ts,html}`.

When an ingredient row is deleted or reordered, remap or downgrade the indices exactly like the existing `ingredientIndices` / `ingredientAmounts` handling: deleted row → `Fixed`.

**`features/ingestion/llm-settings-dialog/`**: remove the Extraction strategy and repair rounds controls, form fields, profile application and spec expectations.

**`features/ingestion/ingestion-repair-dialog/`**: the copy changes to "Re-extract with AI". There is no image-only repair.

---

## 5. Playwright E2E Test Scenarios (`Tests/Client.E2ETests/TaggedInstructionAmountsE2ETests.cs`)

The class extends `E2ETestBase`. Seed via the API with `UpsertRecipeRequest`:
- Ingredient 0: `2 cups all-purpose flour`. Ingredient 1: `1 cup whole milk`.
- Step 1: `"Whisk 1 cup flour with the milk, then cook until reduced to 1 cup."`
  - Span "1 cup" at offset 6 is an Ingredient span for row 0.
  - The second "1 cup" is Fixed.
- Step 2: `"Stir in the remaining 1 cup flour."`, with an Ingredient span for row 0.

- `[TestMethod] InlineAmounts_RecipeDetail_ScalesChipsAndKeepsFixedText()`:
  - At 1x, the step text is verbatim.
  - After choosing `2x`, the first chip reads `2 cups`, the fixed `1 cup` is unchanged, and step 2 reads `2 cups`.
  - The flour row shows `Divided: 2 cups (step 1) · 2 cups (step 2)`.
- `[TestMethod] InlineAmounts_CookMode_ShowsScaledChips()`: cook mode at 2x shows a `2 cups` chip, and the flour callout is not duplicated under the step.
- `[TestMethod] InlineAmounts_Editor_DetectsTagsAndSaves()`:
  - In the editor, add a step `"Add 3 tablespoons milk."`, click **Detect amounts**, set the kind to Ingredient → whole milk, and save.
  - Detail at 2x shows `6 tbsp` (or the client's tbsp label).
- `[TestMethod] InlineAmounts_MobileViewport_360px_RendersCorrectly()`: at `360x640`, the chips wrap inside the step without horizontal overflow (`scrollWidth <= clientWidth`), and the editor rows stack.
- Update `LlmIngestionE2ETests.cs` to drop the removed settings fields.

---

## 6. Step-by-Step Execution Checklist

- [x] **Task 1: Core, Data & Infrastructure**
  1. Move `InlineStepAmountKind`; add the DTOs/records from 2.4; remove `LlmExtractionStrategy` and the settings members (2.1–2.4).
  2. `InstructionAmountTagger.Reconcile`, `TaggedStepDiagnostics`, the `RecipeExtractionDiagnostics` deletions, the normalizer mapping, and the repair mapper (2.5).
  3. Entity, configuration, `AppDbContext`, and `RecipeService` validation/persistence/reads/detect (2.2–2.5).
  4. The `LlmExtractionClient` rewrite, `IngestionProcessor`, `IngestionReviewService`, `LlmSettingsService`, and the materializer/publish paths (2.5).
  5. Migration:
     ```powershell
     dotnet ef migrations add TaggedInstructionAmounts --project Data/App.Data.csproj
     dotnet ef database update --project Data/App.Data.csproj
     dotnet csharpier format Data/Migrations
     ```
  6. Tests in `Tests/Core.UnitTests`:
     - `TaggedStepDiagnosticsTests`: overlap, out of range, kind/index mismatch, reconcile overflow, clean pass.
     - Normalizer inline mapping and index remap (dropped row → Fixed).
     - Repair mapper round trip.
     - `RecipeExtractionDiagnosticsTests` cleanup; `LlmModelProfilesTests` cleanup.
  7. In `Tests/Infrastructure.UnitTests`:
     - Rewrite the `LlmExtractionClientTests` / `GemmaExtractionTests` that exercised the staged, single-pass or repair-loop paths so they use the tagged pipeline with fake HTTP responses (ingredients JSON, then tags JSON).
     - Add a `TranscribeRecipeImagesAsync` canonical-text test.
     - Delete the tests of the removed behavior.
  8. Run:
     ```powershell
     dotnet test --project Tests/Core.UnitTests/App.Core.UnitTests.csproj
     dotnet test --project Tests/Infrastructure.UnitTests/App.Infrastructure.UnitTests.csproj
     ```

- [x] **Task 2: API & Integration Tests**
  1. `RecipesController` detect endpoint (Section 3).
  2. New `Tests/API.IntegrationTests/RecipeInlineAmountsIntegrationTests.cs`:
     - Create and get round-trip with ingredient ids.
     - Update with re-created steps.
     - `400` for overlap, out of range, Ingredient kind without an index, and Fixed with an index.
     - Derived step amount (two spans of 1/2 cup + 1/2 cup ⇒ step link amount 1 cup).
     - Deleting an ingredient row in an update succeeds (Restrict FK handled).
     - The detect endpoint returns `1 1/2 cups` at the right offsets.
     - The public share includes `inlineAmounts`.
  3. Update `IngestionIntegrationTests`, `IngestionRefinementIntegrationTests` and `LlmSettingsIntegrationTests` (fake LLM client signature changes, removed fields).
  4. Add one ingestion test: a draft with `InlineAmounts` publishes, and the recipe GET returns them with the remapped ingredient ids.
  5. Run:
     ```powershell
     dotnet test --project Tests/API.IntegrationTests/App.API.IntegrationTests.csproj
     ```

- [x] **Task 3: Angular Client & Unit Tests**
  1. Models, `RecipeService.detectStepAmounts`, `step-instruction.ts` utils + spec, and the callout change (4.1).
  2. `StepInstructionComponent`, the divided breakdown, and `InlineAmountEditorComponent`. Wire them into the 4 views + 2 editors. Settings dialog removals (4.2).
  3. Update the affected specs: recipe-detail, cook-mode, public-shared-recipe, recipe-detail-preview, recipe-editor, ingestion-review, llm-settings-dialog, ingestion-repair-dialog.
  4. Run (in `Client/`):
     ```powershell
     npm run lint
     npm run build:production
     npm test -- --watch=false
     ```

- [x] **Task 4: Docs, Evaluator, E2E & Final Verify**
  1. Docs:
     - `docs/prd.md` 4.1.4: steps are verbatim source paragraphs with tagged inline amounts that scale as chips. A row's uses aggregate into a divided breakdown. Multiple timers per step. Replace the "Explicit Step Portions" and "Flat Steps" bullets.
     - 4.2.1: vision transcribes, then the text pipeline runs.
     - 4.2.3: the quality gate lists reconciliation issues instead of the prose checks.
     - 4.2.4: remove the strategy and repair rounds.
     - `docs/llm-model-settings.md`: remove the strategy, repair round and staged notes; record the ATK comparison from Section 0.
     - `docs/contributing.md`: fix any step-amount guidance.
  2. Evaluator (`scripts/IngestionEvaluator/Program.cs`, `scripts/evaluate_atk_ingestion.py`, `scripts/test_evaluate_atk_ingestion.py`):
     - Remove `--pipeline` and `--repair-rounds`, and the staged scoring branches; the tagged scoring becomes the only scoring.
     - Run `cd scripts; python -m unittest test_evaluate_atk_ingestion`.
  3. Write `Tests/Client.E2ETests/TaggedInstructionAmountsE2ETests.cs` (Section 5) and run:
     ```powershell
     dotnet test --project Tests/Client.E2ETests/App.Client.E2ETests.csproj
     ```
  4. Format, then run the whole solution:
     ```powershell
     dotnet csharpier format .
     # In Client/ (CRLF checkouts: format only changed files)
     npx prettier --write <changed files>
     npm run lint:fix
     dotnet test App.slnx
     ```
  5. Optional live check (gemma4:e4b only):
     ```powershell
     cd scripts; python evaluate_atk_ingestion.py --models gemma4:e4b --base-url http://ThinkStationP3:11434 --thinking --context-window 32768 --timeout 600 --output <dir>
     ```
     Expect ≤3 issues.
  6. Mark this plan `[x] Completed`.
