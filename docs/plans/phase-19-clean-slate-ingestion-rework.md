# Phase 19: Clean-Slate Recipe Ingestion Pipeline Rework

> **Goal:** Rip out the legacy heuristic-heavy ingestion workflow (~10,000+ lines of regex normalizers, 2-pass method taggers, line-number source sectioners, issue triage, and background refinement) and implement the clean, deterministic 5-stage pipeline described in [`docs/llm-rework/ingestion.md`](../llm-rework/ingestion.md).

## Mandatory Progress Tracking Rule
- Keep these checkboxes updated continuously as each item is completed so work can resume seamlessly across sessions if needed.

---

## Step 0: Record Plan in Repository
- [x] Create `docs/plans/phase-19-clean-slate-ingestion-rework.md` containing this complete plan and all checkboxes.

---

## Part 1: Rip Out Legacy Ingestion Workflow

### Step 1.1: Extract Small Shared Recipe Helpers (`Core/Recipes/RecipeUnitAndSectionHelpers.cs`)
Before deleting `RecipeDraftNormalizer.cs` and `StepTimerExtractor.cs`, extract the small non-ingestion helpers used by `RecipeService.cs`, `IngestionCatalogMaterializer.cs`, `IngestionService.cs`, `McpCookbookService.cs`, `McpMealAndShoppingService.cs`, and `StepInstructionParser.cs`:
- [x] Create `Core/Recipes/RecipeUnitAndSectionHelpers.cs` (`namespace App.Core.Recipes;`) containing:
  - `NormalizeUnit(string? unitString)` -> `(MeasurementUnit? Unit, string? CustomUnitName)`
  - `NormalizeQuantities(decimal? quantity, decimal? maxQuantity)` -> `(decimal? Quantity, decimal? MaxQuantity)`
  - `IsDisallowedUnitPlaceholder(string? customUnitName)` -> `bool`
  - `IsGenericTopLevelSection(string? sectionName)` -> `bool`
  - `Singularize(string word)` -> `string`
  - `ToTagDisplayCase(string raw)` -> `string`
  - `InferTagCategory(string tagName)` -> `TagCategory`
  - `NormalizeLookupKey(string? value)` -> `string`
  - `NormalizeRecipeLookupKey(string? value)` -> `string`
  - `CleanRecipeReferenceTitle(string? rawTitle)` -> `string`
  - `StripLeadingBulletOrIndex(string? text)` -> `string`
- [x] Update `Core/Recipes/StepInstructionParser.cs`:
  - Replace `RecipeDraftNormalizer.NormalizeUnit` calls with `RecipeUnitAndSectionHelpers.NormalizeUnit`.
  - Inline `IsDurationLabel(string? text)` directly into `StepInstructionParser.cs` (removing the `StepTimerExtractor.IsDurationLabel` dependency).
  - Add `FindBestMatchingIngredientIndex(StepIngredientToken token, string? stepSection, IReadOnlyList<IngredientCandidateInfo> candidates, IReadOnlyList<int> existingIndices, string? instruction = null)` to `StepInstructionParser.cs` (for `RecipeService.cs`).
- [x] Update `Core/Ingestion/SourceMeasurementParser.cs`:
  - Replace `RecipeDraftNormalizer.NormalizeUnit` call with `RecipeUnitAndSectionHelpers.NormalizeUnit`.
- [x] Update `Data/Services/RecipeService.cs`, `Data/Services/IngestionService.cs`, `Data/Services/IngestionCatalogMaterializer.cs`, `Data/Services/McpCookbookService.cs`, and `Data/Services/McpMealAndShoppingService.cs` to use `RecipeUnitAndSectionHelpers` instead of `RecipeDraftNormalizer` (and in `McpCookbookService.cs`, extract inline timer tokens from `StepInstructionParser.ExtractTokens(instruction).OfType<StepTimerToken>()` instead of `StepTimerExtractor.DetectTimers`).
- [x] Update `Data/Services/IngestionCatalogMaterializer.cs` (`PromoteWaitingRecipeReferenceJobsAsync`) so that when a waiting composite recipe's sub-recipe is resolved, it checks `(updatedDraft.QualityWarnings?.Count ?? 0) == 0` instead of calling `RecipeExtractionDiagnostics.GetBlockingIssues` / `GetQualityWarnings`.

### Step 1.2: Delete Legacy Ingestion Files (`[DELETE]`)
- [x] Delete legacy `Core/Ingestion/` files:
  - `Core/Ingestion/RecipeDraftNormalizer.cs`
  - `Core/Ingestion/RecipeExtractionDiagnostics.cs`
  - `Core/Ingestion/TaggedStepDiagnostics.cs`
  - `Core/Ingestion/RecipeSourceSectioner.cs`
  - `Core/Ingestion/StepTimerExtractor.cs`
  - `Core/Ingestion/IngestionDraftRepairMapper.cs`
  - `Core/Ingestion/IIngestionReviewService.cs`
- [x] Delete legacy `Data/` files:
  - `Data/Services/IngestionReviewService.cs`
  - `Data/Entities/IngestionDraftRefinement.cs`
  - `Data/Configurations/IngestionDraftRefinementConfiguration.cs`
- [x] Delete legacy `Client/` repair dialog files:
  - `Client/src/app/features/ingestion/ingestion-repair-dialog/ingestion-repair-dialog.component.ts`
  - `Client/src/app/features/ingestion/ingestion-repair-dialog/ingestion-repair-dialog.component.html`
  - `Client/src/app/features/ingestion/ingestion-repair-dialog/ingestion-repair-dialog.component.scss`
- [x] Delete legacy test files:
  - `Tests/Core.UnitTests/RecipeDraftNormalizerTests.cs`
  - `Tests/Core.UnitTests/RecipeExtractionDiagnosticsTests.cs`
  - `Tests/Core.UnitTests/TaggedStepDiagnosticsTests.cs`
  - `Tests/Core.UnitTests/RecipeSourceSectionerTests.cs`
  - `Tests/Core.UnitTests/StepTimerExtractorTests.cs`
  - `Tests/Core.UnitTests/IngestionReviewTests.cs`
  - `Tests/API.IntegrationTests/IngestionRefinementIntegrationTests.cs`

### Step 1.3: Remove Legacy Database & API Plumbing (`[MODIFY]`)
- [x] Update `Data/AppDbContext.cs`, `Data/Entities/IngestionJob.cs`, and `Data/Configurations/IngestionJobConfiguration.cs`:
  - Remove `DbSet<IngestionDraftRefinement> IngestionDraftRefinements` from `AppDbContext.cs`.
  - Remove `StructuredSourceText` from `IngestionJob.cs` and `IngestionJobConfiguration.cs`.
- [x] Update `Data/DataServiceCollectionExtensions.cs` and `Data/Services/AppDataMaintenanceService.cs`:
  - Remove `services.AddScoped<IIngestionReviewService, IngestionReviewService>()` from `DataServiceCollectionExtensions.cs`.
  - Remove `IngestionDraftRefinements` cleanup from `AppDataMaintenanceService.cs`.
- [x] Update `Infrastructure/Ingestion/IngestionBackgroundWorker.cs`:
  - Remove Step 4 (`RefineNextPendingDraftAsync` / `IIngestionReviewService`) so the loop only runs `AutoResumeIfQuotaResetDueAsync`, `ProcessNextQueuedJobAsync`, and `ProbeAndPromoteWaitingJobsAsync`.
- [x] Update `API/Controllers/IngestionJobsController.cs` and `Core/Ingestion/IIngestionService.cs` / `Data/Services/IngestionService.cs`:
  - Remove `IIngestionReviewService` parameter from `IngestionJobsController`.
  - Delete endpoints: `POST {id:guid}/repair-preview`, `GET {id:guid}/refinement`, `POST {id:guid}/refinement`, `DELETE {id:guid}/refinement`, `POST {id:guid}/refinement/preview`.
  - Add `Task<IngestionReviewDiagnosticsDto?> GetReviewDiagnosticsAsync(Guid id, IngestionExtractedDraftDto draft, CancellationToken cancellationToken = default)` to `IIngestionService` and `IngestionService` (performing fast deterministic checks on the draft: missing title/ingredients/steps, unresolved sub-recipe references, unreferenced ingredients, and surfacing `draft.QualityWarnings`), and wire `POST {id:guid}/review-diagnostics` in `IngestionJobsController` to it.
- [x] Generate EF Core Migration:
  - Run `dotnet ef migrations add RemoveLegacyIngestionRefinement --project Data/App.Data.csproj` (after Part 2 compiles).

---

## Part 2: Implement New Multi-Stage Deterministic Pipeline (`docs/llm-rework/ingestion.md`)

### Step 2.1: Define Stage 2 & Stage 3 Contracts (`Core/Ingestion/ExtractedRecipeModels.cs`)
- [x] Create `Core/Ingestion/ExtractedRecipeModels.cs` containing `ExtractedRecipeDto`, `ExtractedIngredientDto`, `ExtractedStepDto`, `StepIngredientTokenDto`, `StepTimerTokenDto`, `StepTemperatureTokenDto`, `RecipeQualityAuditDto`, `RecipeValidationResult`, and entity disambiguation contracts (`EntityCandidateOption`, `AmbiguousIngredientCandidate`, `AmbiguousTagCandidate`, `LlmEntityDisambiguationContext`, `LlmIngredientDisambiguationDecision`, `LlmTagDisambiguationDecision`, `LlmEntityDisambiguationResult`).
- [x] Update `Core/Ingestion/IngestionModels.cs`:
  - Add `IReadOnlyList<string>? QualityWarnings = null` and `IReadOnlyList<string>? ValidationIssues = null` to `IngestionExtractedDraftDto`.
  - Add `IngestionDraftDiagnosticsRequest`, `IngestionReviewDiagnosticsDto`, and `IngestionDiagnosticTargetDto`.
- [x] Update `Core/Ingestion/ILlmExtractionClient.cs`:
  - Clean up `ILlmExtractionClient` to expose `ExtractRecipeFromTextAsync` (returning `ExtractedRecipeDto`) and `DisambiguateEntitiesAsync` (returning `LlmEntityDisambiguationResult`) alongside `ProbeAndListModelsAsync`, `TranscribeRecipeImagesAsync`, `SuggestIngredientSubstitutionsAsync`, `AnswerKitchenQuestionAsync`, and `DraftWeeklyMealPlanAsync`.
  - Simplify `LlmExtractionContext` so it does NOT require passing catalog food items or tag lists into the extraction prompt.

### Step 2.2: Implement Deterministic Validator (`Core/Ingestion/RecipeValidator.cs`)
- [x] Create `Core/Ingestion/RecipeValidator.cs` validating completeness, ingredient integrity, step `{{token}}` / array parity, broken `IngredientRefId` references, token value integrity, divided `StepQuantity` sum checks, and ingredient coverage.

### Step 2.3: Implement Stage 3 Candidate Matcher (`Core/Ingestion/EntityCandidateMatcher.cs`)
- [x] Create `Core/Ingestion/EntityCandidateMatcher.cs` with 3-tier ingredient matching (exact/alias -> fuzzy composite score $\in [0.55, 0.92)$ top 5 -> clearly new) + tag matching + `ApplyDisambiguation(...)`.

### Step 2.4: Implement Stage 4 Token Compiler & Draft Assembler (`Core/Ingestion/RecipeDraftCompiler.cs`)
- [x] Create `Core/Ingestion/RecipeDraftCompiler.cs` compiling `ExtractedRecipeDto`, resolved entity matches, and validation results into `IngestionExtractedDraftDto` with compiled `||{...}||` step tokens, `IngredientIndices`, `IngredientAmounts`, `Timers`, `QualityWarnings`, `ValidationIssues`, and `ConfidenceScore`.

### Step 2.5: Implement Multi-Turn Self-Correction & Micro-Disambiguation in `Infrastructure/Ingestion/LlmExtractionClient.cs`
- [x] Rewrite the recipe extraction section of `Infrastructure/Ingestion/LlmExtractionClient.cs` to run multi-turn `ExtractRecipeFromTextAsync` (up to 3 attempts / 2 validation self-correction retries) and `DisambiguateEntitiesAsync`, keeping `ProbeAndListModelsAsync`, `TranscribeRecipeImagesAsync`, `SuggestIngredientSubstitutionsAsync`, `AnswerKitchenQuestionAsync`, and `DraftWeeklyMealPlanAsync` intact.

### Step 2.6: Update `Data/Services/IngestionProcessor.cs` to Orchestrate the 5 Stages
- [x] Update `Data/Services/IngestionProcessor.cs` to orchestrate Stage 1 (Preprocess) -> Stage 2 (Extract + Self-Correct Validate) -> Stage 3 (Candidate Match + Micro-Disambiguate) -> Stage 4 (Compile Draft & Resolve Sub-Recipes) -> Stage 5 (Auto-Publish vs ReadyForReview).

### Step 2.7: Clean Up Angular Ingestion Review UI (`Client/src/app/`)
- [x] Update `Client/src/app/core/models/ingestion.models.ts`: add `qualityWarnings?: string[] | null;` and `validationIssues?: string[] | null;` to `IngestionExtractedDraftDto`; remove `IngestionRepairPreviewDto`, `IngestionRefinementStatus`, and `IngestionRefinementDto`.
- [x] Update `Client/src/app/core/services/ingestion/ingestion.service.ts`: remove `previewRepair`, `getRefinement`, `requestRefinement`, `dismissRefinement`, and `previewRefinement`.
- [x] Update `Client/src/app/features/ingestion/ingestion-review/ingestion-review.component.ts` and `.html`: remove repair dialog and background refinement UI while keeping `diagnostics()`, `recheckReviewIssues()`, and `"New item"` badges.

---

## Part 3: Tests, Formatting & E2E Verification

### Step 3.1: Unit & Integration Tests
- [x] Create `Tests/Core.UnitTests/RecipeValidatorTests.cs`
- [x] Create `Tests/Core.UnitTests/EntityCandidateMatcherTests.cs`
- [x] Create `Tests/Core.UnitTests/RecipeDraftCompilerTests.cs`
- [x] Update `Tests/Core.UnitTests/FoodCategoryTests.cs` and `Tests/Core.UnitTests/RecipeMeasurementTests.cs`
- [x] Update `Tests/Infrastructure.UnitTests/LlmExtractionClientTests.cs` and `Tests/Infrastructure.UnitTests/GemmaExtractionTests.cs`
- [x] Update `Tests/API.IntegrationTests/IngestionIntegrationTests.cs` and `Tests/API.IntegrationTests/VisionAndPdfIngestionIntegrationTests.cs`
- [x] Update `Tests/Client.E2ETests/LlmProfilesAndRefinementE2ETests.cs`

### Step 3.2: Run Full Verification Suite
- [x] Run `dotnet build App.slnx --configuration Release`
- [x] Run `dotnet run --project Tests/Core.UnitTests --configuration Release`
- [x] Run `dotnet run --project Tests/Infrastructure.UnitTests --configuration Release`
- [x] Run `dotnet run --project Tests/API.IntegrationTests --configuration Release`
- [x] Run `npm --prefix Client test -- --watch=false`
- [x] Run `dotnet csharpier format .` and `npm --prefix Client run lint` and `npm --prefix Client run format`
- [x] Run `dotnet run --project Tests/Client.E2ETests --configuration Release`
