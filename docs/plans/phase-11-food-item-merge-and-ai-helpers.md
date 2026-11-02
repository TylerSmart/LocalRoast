# Phase 11: Food Catalog Duplicate-Merge & 1-Click Local AI Kitchen Helpers

> **Branch:** `feature/phase-11-food-item-merge-and-ai-helpers`
> **Status:** Planned (`[ ]` Not Started | `[~]` In Progress | `[x]` Completed)
> **PRD Reference:** `docs/prd.md` — Sections 1, 2, 4.1.3, 4.2.4, 4.4, 4.6.1, 5, and 6 (Milestone 4, Slice 2 of 2)
> **Target Execution:** Run `/execute-phase` to execute this plan step-by-step.

---

## 1. Scope & Objectives

This phase completes **Milestone 4** (and the full four-milestone roadmap in `docs/prd.md`) by delivering **Canonical `FoodItem` Duplicate-Merge Management Tools** alongside **Targeted 1-Click Local LLM Kitchen Helpers** (*Suggest Ingredient Substitution* and *Draft Weekly Meal Plan*) with strict online/offline host resilience:

1. **Canonical `FoodItem` Duplicate-Merge Management (`MergeFoodItemsAsync`, `POST /api/food-items/merge`, `MergeFoodItemsDialogComponent`):**
   - Extend `FoodItemDto` with trailing optional `ReferencedShoppingListItemCount` so household users see both recipe and grocery-list usage counts in the Food Catalog and merge preview.
   - Implement atomic `IFoodCatalogService.MergeFoodItemsAsync` (`Data/Services/FoodCatalogService.cs`):
     - Merges `1..N` duplicate `SourceFoodItemIds` into a surviving `TargetFoodItemId` in a single atomic transaction (`BeginTransactionAsync` when relational + single `SaveChangesAsync`).
     - Re-points all `RecipeIngredient.FoodItemId` rows referencing any source item to `TargetFoodItemId` while preserving recipe-specific `DisplayName`, `PreparationNote`, and `OriginalRawText`.
     - Re-points all `ShoppingListItem.FoodItemId` rows referencing any source item to `TargetFoodItemId`, automatically consolidating compatible rows within the same `(ShoppingListId, IsChecked, IsPantryStapleExcluded)` bucket via `ShoppingConsolidator` (summing quantities and deduplicating `ShoppingListItemSource` provenance chips when measurement dimensions match, while keeping distinct dimensions like `whole` vs `cup` separate) and broadcasting live SignalR `ShoppingRealtimeEventType.ListUpdated` events to connected co-shoppers.
     - Updates any pending `ReadyForReview` `IngestionJob.ExtractedDraftJson` ingredient bindings referencing merged `SourceFoodItemIds` so Review Inbox drafts never hold stale `MatchedFoodItemId` values.
     - Preserves merged source item names and synonyms as `FoodItemAlias` rows on the target `FoodItem` when `KeepSourceNamesAsAliases == true` (re-parenting existing `FoodItemAlias` entities via FK update to avoid SQL Server unique-index `DELETE`+`INSERT` collisions, and inheriting `DefaultStoreAisleId` when the target item had none).
   - Deliver Angular `MergeFoodItemsDialogComponent` (`features/food-catalog/merge-food-items-dialog/`) with both a top-bar **`Merge Duplicates`** action and per-card **`Merge`** quick-action buttons in `FoodCatalogComponent`.

2. **Targeted 1-Click Local LLM Kitchen Helpers (`IAiKitchenHelperService`, `AiKitchenHelpersController`, Online/Offline Gating):**
   - Create `Core/AiHelpers/AiKitchenHelperModels.cs` and `Core/AiHelpers/IAiKitchenHelperService.cs` (zero HTTP/EF dependencies), extend `ILlmExtractionClient` / `LlmExtractionClient` (`SuggestIngredientSubstitutionsAsync` & `DraftWeeklyMealPlanAsync` supporting both **Ollama `/api/chat`** structured JSON schemas and **OpenAI-compatible `/v1/chat/completions`** `json_schema` responses), and implement `Data/Services/AiKitchenHelperService.cs`.
   - **Non-Blocking Online/Offline Status (`GET /api/ai-helpers/status` & `AiKitchenHelperService`):**
     - Returns `AiHelperStatusDto` immediately (`< 1ms`, zero network socket call) with `IsAvailable = false` when no `TextModel` is configured in effective `HouseholdLlmSettings`, or probes `ProbeAndListModelsAsync` (`timeoutSeconds: 4`) when a `TextModel` is configured.
     - Client `AiKitchenHelperService` (`core/services/ai-helpers/ai-kitchen-helper.service.ts`) caches availability in a reactive `signal` (`30s` TTL, refreshed automatically when `LlmSettingsService` saves or tests LLM settings) and hides 1-click AI helper triggers whenever the LAN LLM host is offline—ensuring core Cookbook, Cook Mode, and Meal Plan views remain 100% instant and clutter-free when the GPU workstation is powered off.
   - **1-Click Helper #1 — Suggest Ingredient Substitution (`POST /api/ai-helpers/ingredient-substitutions`, `IngredientSubstitutionDialogComponent`):**
     - Loads the `Recipe`, target `RecipeIngredient`, scaled/converted amount (`CulinaryMath.FormatIngredientDisplay` for `TargetServings` & `TargetSystem`), and steps referencing that ingredient, then requests `2–4` dish-tailored substitution options (`Label`, `RatioAndAmount`, `CulinaryNotes`, `FlavorOrTextureImpact`).
     - Exposes 1-tap swap buttons on non-sub-recipe ingredients in `RecipeDetailComponent` (`.substitute-ing-btn`, `.no-print`) and `CookModeComponent` (`.cook-substitute-btn` in the desktop ingredients pane, mobile slide-up ingredients drawer, and step ingredient callout chips, stopping click propagation so tapping Swap does not toggle ingredient completion), opening `IngredientSubstitutionDialogComponent` (`features/recipes/ingredient-substitution-dialog/`).
   - **1-Click Helper #2 — Draft Weekly Meal Plan (`POST /api/ai-helpers/draft-meal-plan`, `POST /api/meal-plans/week/apply-draft`, `DraftMealPlanDialogComponent`):**
     - Queries the household's saved `Published` recipes (including tags, total time, difficulty, canonical `FoodItem` ingredients, `AverageRating`, `TimesCooked`, and `DaysSinceLastCooked`) plus the target week's existing `MealPlanEntries`.
     - Prompts the configured `TextModel` using integer `CatalogIndex` (`1..N`) anchors and **strictly validates/resolves every candidate against real published household recipes** (discarding any hallucinated or out-of-catalog suggestions).
     - Adds batch `IMealPlanService.ApplyDraftEntriesAsync` (`POST /api/meal-plans/week/apply-draft`) and `DraftMealPlanDialogComponent` (`features/meal-plans/draft-meal-plan-dialog/`) launched from `MealPlanCalendarComponent` (`.draft-week-ai-btn`) with quick-constraint preset chips, editable day/slot/`On-Deck` & serving selectors, candidate checkboxes, and 1-click **`Add Selected Meals`**.

### Database Migration Note
- **No new EF Core migration is required in Phase 11.** All persistent tables (`FoodItems`, `FoodItemAliases`, `Recipes`, `RecipeIngredients`, `ShoppingLists`, `ShoppingListItems`, `ShoppingListItemSources`, `MealPlanEntries`, `HouseholdLlmSettings`, `IngestionJobs`) already exist and are migrated on `main`.

### Explicitly Out of Scope
- Open-ended embedded AI chat drawers (explicitly omitted per `docs/prd.md` Section 4.2.4; open-ended chat is delegated to `/api/mcp` clients).
- Nutritional calorie/macro calculations (explicitly deferred per `docs/prd.md` Section 2).

### 1.1 File Inventory (Modify & Create)

#### Existing Backend & Test Files to Modify
- `Core/FoodCatalog/FoodCatalogModels.cs` — Append trailing optional `ReferencedShoppingListItemCount = 0` to `FoodItemDto`; add `MergeFoodItemsRequest` and `MergeFoodItemsResultDto`.
- `Core/FoodCatalog/IFoodCatalogService.cs` — Add `MergeFoodItemsAsync`.
- `Core/Ingestion/ILlmExtractionClient.cs` — Add LLM prompt/response records and `SuggestIngredientSubstitutionsAsync` + `DraftWeeklyMealPlanAsync` methods.
- `Core/MealPlans/MealPlanModels.cs` — Add `ApplyDraftMealPlanRequest`.
- `Core/MealPlans/IMealPlanService.cs` — Add `ApplyDraftEntriesAsync`.
- `Data/Services/FoodCatalogService.cs` — Populate `ReferencedShoppingListItemCount` in `GetFoodItemsAsync`/`GetFoodItemByIdAsync`; implement atomic `MergeFoodItemsAsync` with `RecipeIngredient` re-pointing, `ShoppingConsolidator` bucket consolidation, SignalR notification, `IngestionJob` draft JSON refresh, and alias re-parenting.
- `Data/Services/MealPlanService.cs` — Implement `ApplyDraftEntriesAsync`.
- `Data/DataServiceCollectionExtensions.cs` — Register `IAiKitchenHelperService` $\rightarrow$ `AiKitchenHelperService`.
- `Infrastructure/Ingestion/LlmExtractionClient.cs` — Implement `SuggestIngredientSubstitutionsAsync` and `DraftWeeklyMealPlanAsync` for both `Ollama` and `OpenAiCompatible` providers.
- `API/Controllers/FoodItemsController.cs` — Add `POST /api/food-items/merge`.
- `API/Controllers/MealPlansController.cs` — Add `POST /api/meal-plans/week/apply-draft`.
- `Tests/API.IntegrationTests/FoodCatalogIntegrationTests.cs` — Add `MergeFoodItems` API integration tests (recipes, shopping list consolidation, aliases, validation/404 errors).
- `Tests/API.IntegrationTests/OpenApiIntegrationTests.cs` — Assert `/ai-helpers` and `/food-items/merge` appear in `/api/openapi/v1.json`.
- `Tests/Infrastructure.UnitTests/LlmExtractionClientTests.cs` — Add unit tests for `SuggestIngredientSubstitutionsAsync` and `DraftWeeklyMealPlanAsync` across Ollama, OpenAI-compatible `/v1`, markdown-fenced JSON, and offline/timeout `LlmHostUnavailableException`.

#### New Backend & Backend Test Files to Create
- `Core/AiHelpers/AiKitchenHelperModels.cs`
- `Core/AiHelpers/IAiKitchenHelperService.cs`
- `Data/Services/AiKitchenHelperService.cs`
- `API/Controllers/AiKitchenHelpersController.cs`
- `Tests/Core.UnitTests/FoodCatalogMergeServiceTests.cs`
- `Tests/Core.UnitTests/AiKitchenHelperServiceTests.cs`
- `Tests/API.IntegrationTests/AiKitchenHelpersIntegrationTests.cs`

#### Existing Frontend Files to Modify (`Client/`)
- `Client/src/app/core/models/food-catalog.models.ts` — Extend `FoodItemDto` with `referencedShoppingListItemCount?: number`; add `MergeFoodItemsRequest` and `MergeFoodItemsResultDto`.
- `Client/src/app/core/models/meal-plan.models.ts` — Add `ApplyDraftMealPlanRequest`.
- `Client/src/app/core/services/food-catalog/food-catalog.service.ts` (+ `.spec.ts`) — Add `mergeFoodItems(request: MergeFoodItemsRequest)`.
- `Client/src/app/core/services/meal-plan/meal-plan.service.ts` (+ `.spec.ts`) — Add `applyDraftEntries(request: ApplyDraftMealPlanRequest)`.
- `Client/src/app/core/services/llm-settings/llm-settings.service.ts` (+ `.spec.ts`) — Trigger `aiKitchenHelperService.refreshStatus(true)` via `tap(...)` when `updateSettings` or `testConnection` completes so AI helper availability updates immediately in-app.
- `Client/src/app/features/food-catalog/food-catalog/food-catalog.component.ts` (+ `.html`, `.scss`, `.spec.ts`) — Add header **`Merge Duplicates`** button (`.merge-duplicates-btn`), per-card **`Merge`** icon button (`.merge-item-btn`), shopping-list reference badge when `> 0`, and `openMergeDialog(preselectedSource?: FoodItemDto)` workflow.
- `Client/src/app/features/recipes/recipe-detail/recipe-detail.component.ts` (+ `.html`, `.scss`, `.spec.ts`) — Inject `AiKitchenHelperService`, refresh status on init, and render `.substitute-ing-btn` (`no-print`) on non-sub-recipe ingredient rows when `aiHelperService.isAvailable()` is `true`.
- `Client/src/app/features/recipes/cook-mode/cook-mode.component.ts` (+ `.html`, `.scss`, `.spec.ts`) — Inject `AiKitchenHelperService`, refresh status on init, and render `.cook-substitute-btn` on non-sub-recipe ingredients in desktop pane, mobile drawer, and step ingredient callout chips when `aiHelperService.isAvailable()` is `true`.
- `Client/src/app/features/meal-plans/meal-plan-calendar/meal-plan-calendar.component.ts` (+ `.html`, `.scss`, `.spec.ts`) — Inject `AiKitchenHelperService`, refresh status on init, and render `.draft-week-ai-btn` (`Draft Week`) in Weekly Board top actions when `aiHelperService.isAvailable()` is `true`.

#### New Frontend & E2E Files to Create
- `Client/src/app/core/models/ai-helper.models.ts`
- `Client/src/app/core/services/ai-helpers/ai-kitchen-helper.service.ts` (+ `.spec.ts`)
- `Client/src/app/features/food-catalog/merge-food-items-dialog/merge-food-items-dialog.component.ts` (+ `.html`, `.scss`, `.spec.ts`)
- `Client/src/app/features/recipes/ingredient-substitution-dialog/ingredient-substitution-dialog.component.ts` (+ `.html`, `.scss`, `.spec.ts`)
- `Client/src/app/features/meal-plans/draft-meal-plan-dialog/draft-meal-plan-dialog.component.ts` (+ `.html`, `.scss`, `.spec.ts`)
- `Tests/Client.E2ETests/FoodItemMergeAndAiHelpersE2ETests.cs`

---

## 2. Domain, Service & Infrastructure Design (`Core/`, `Data/`, & `Infrastructure/`)

### 2.1 Food Catalog Merge Contracts (`Core/FoodCatalog/`)

1. **Update `Core/FoodCatalog/FoodCatalogModels.cs`** (`namespace App.Core.FoodCatalog`):
   - Append trailing optional parameter to `FoodItemDto` (preserving backward compatibility with all existing positional callers):
     ```csharp
     public record FoodItemDto(
         Guid Id,
         string Name,
         Guid? DefaultStoreAisleId,
         string? DefaultStoreAisleName,
         int? DefaultStoreAisleSortOrder,
         bool IsPantryStaple,
         IReadOnlyList<string> Aliases,
         int ReferencedRecipeCount,
         DateTimeOffset CreatedAt,
         DateTimeOffset ModifiedAt,
         int ReferencedShoppingListItemCount = 0
     );
     ```
   - Add merge request & result records:
     ```csharp
     public record MergeFoodItemsRequest(
         Guid TargetFoodItemId,
         [Required, MinLength(1)] IReadOnlyList<Guid> SourceFoodItemIds,
         bool KeepSourceNamesAsAliases = true
     );

     public record MergeFoodItemsResultDto(
         FoodItemDto TargetFoodItem,
         int MergedSourceCount,
         int RepointedRecipeIngredientCount,
         int RepointedShoppingListItemCount,
         int ConsolidatedShoppingListItemCount,
         IReadOnlyList<string> AddedAliases
     );
     ```

2. **Update `Core/FoodCatalog/IFoodCatalogService.cs`**:
   - Add:
     ```csharp
     Task<MergeFoodItemsResultDto> MergeFoodItemsAsync(
         MergeFoodItemsRequest request,
         CancellationToken cancellationToken = default
     );
     ```

### 2.2 AI Kitchen Helpers & Meal Plan Batch Contracts (`Core/AiHelpers/`, `Core/Ingestion/`, `Core/MealPlans/`)

1. **Create `Core/AiHelpers/AiKitchenHelperModels.cs`** (`namespace App.Core.AiHelpers`):
   ```csharp
   using System.ComponentModel.DataAnnotations;
   using App.Core.Ingestion;
   using App.Core.MealPlans;
   using App.Core.Recipes;

   namespace App.Core.AiHelpers;

   public record AiHelperStatusDto(
       bool IsAvailable,
       LlmProvider Provider,
       string? TextModel,
       int LatencyMs,
       string? UnavailableReason
   );

   public record SuggestIngredientSubstitutionsRequest(
       Guid RecipeId,
       Guid RecipeIngredientId,
       [Range(typeof(decimal), "0.25", "999")] decimal? TargetServings = null,
       MeasurementSystem TargetSystem = MeasurementSystem.Original,
       [MaxLength(300)] string? DietaryOrPantryNote = null
   );

   public record IngredientSubstitutionOptionDto(
       string Label,
       string RatioAndAmount,
       string CulinaryNotes,
       string FlavorOrTextureImpact
   );

   public record IngredientSubstitutionResponseDto(
       Guid RecipeId,
       string RecipeTitle,
       Guid RecipeIngredientId,
       string IngredientDisplayName,
       string ScaledAmountDisplay,
       string ModelUsed,
       IReadOnlyList<IngredientSubstitutionOptionDto> Substitutions
   );

   public record DraftWeeklyMealPlanRequest(
       DateOnly? WeekStartDate = null,
       [Required, MaxLength(600)] string Prompt = "",
       [Range(1, 14)] int MealCount = 4,
       MealSlot DefaultSlot = MealSlot.Dinner,
       bool PlaceOnDeck = false
   );

   public record DraftMealPlanCandidateDto(
       Guid RecipeId,
       MealPlanRecipeBadgeDto Recipe,
       bool IsOnDeck,
       int? DayOffset,
       DateOnly? ScheduledDate,
       MealSlot? Slot,
       decimal TargetServings,
       string Rationale
   );

   public record DraftMealPlanResponseDto(
       DateOnly WeekStartDate,
       DateOnly WeekEndDate,
       string ModelUsed,
       int ConsideredCookbookRecipeCount,
       IReadOnlyList<DraftMealPlanCandidateDto> Candidates
   );
   ```

2. **Create `Core/AiHelpers/IAiKitchenHelperService.cs`** (`namespace App.Core.AiHelpers`):
   ```csharp
   namespace App.Core.AiHelpers;

   public interface IAiKitchenHelperService
   {
       Task<AiHelperStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);

       Task<IngredientSubstitutionResponseDto> SuggestIngredientSubstitutionsAsync(
           SuggestIngredientSubstitutionsRequest request,
           CancellationToken cancellationToken = default
       );

       Task<DraftMealPlanResponseDto> DraftWeeklyMealPlanAsync(
           DraftWeeklyMealPlanRequest request,
           CancellationToken cancellationToken = default
       );
   }
   ```

3. **Update `Core/Ingestion/ILlmExtractionClient.cs`** (`namespace App.Core.Ingestion`):
   - Add LLM-facing prompt/payload records and methods:
     ```csharp
     public record LlmSubstitutionContext(
         string RecipeTitle,
         string? RecipeDescription,
         decimal TargetServings,
         string TargetIngredientName,
         string ScaledAmountDisplay,
         string? PreparationNote,
         IReadOnlyList<string> OtherRecipeIngredients,
         IReadOnlyList<string> RelevantStepInstructions,
         string? DietaryOrPantryNote
     );

     public record LlmSubstitutionOptionItem(
         string Label,
         string RatioAndAmount,
         string CulinaryNotes,
         string FlavorOrTextureImpact
     );

     public record LlmSubstitutionPayload(IReadOnlyList<LlmSubstitutionOptionItem>? Substitutions);

     public record LlmCookbookRecipeCandidate(
         int CatalogIndex,
         Guid RecipeId,
         string Title,
         decimal BaseServings,
         int? TotalTimeMinutes,
         string? Difficulty,
         IReadOnlyList<string> Tags,
         IReadOnlyList<string> KeyIngredients,
         decimal? AverageRating,
         int TimesCooked,
         int? DaysSinceLastCooked
     );

     public record LlmDraftMealPlanContext(
         DateOnly WeekStartDate,
         string UserPrompt,
         int RequestedMealCount,
         string DefaultSlotName,
         bool PlaceOnDeck,
         IReadOnlyList<string> ExistingWeekScheduleLines,
         IReadOnlyList<LlmCookbookRecipeCandidate> CookbookCatalog
     );

     public record LlmDraftMealProposalItem(
         int CatalogIndex,
         string? RecipeTitle,
         int? DayOffset,
         string? Slot,
         bool? IsOnDeck,
         decimal? TargetServings,
         string? Rationale
     );

     public record LlmDraftMealPlanPayload(IReadOnlyList<LlmDraftMealProposalItem>? Proposals);
     ```
   - Add methods on `ILlmExtractionClient`:
     ```csharp
     Task<LlmSubstitutionPayload> SuggestIngredientSubstitutionsAsync(
         EffectiveLlmConnectionConfig config,
         LlmSubstitutionContext context,
         CancellationToken cancellationToken = default
     );

     Task<LlmDraftMealPlanPayload> DraftWeeklyMealPlanAsync(
         EffectiveLlmConnectionConfig config,
         LlmDraftMealPlanContext context,
         CancellationToken cancellationToken = default
     );
     ```

4. **Update `Core/MealPlans/MealPlanModels.cs` & `Core/MealPlans/IMealPlanService.cs`**:
   - Add to `MealPlanModels.cs`:
     ```csharp
     public sealed record ApplyDraftMealPlanRequest(
         DateOnly WeekStartDate,
         [Required, MinLength(1)] IReadOnlyList<CreateMealPlanEntryRequest> Entries
     );
     ```
   - Add to `IMealPlanService.cs`:
     ```csharp
     Task<MealPlanWeekDto> ApplyDraftEntriesAsync(
         ApplyDraftMealPlanRequest request,
         CancellationToken cancellationToken = default
     );
     ```

### 2.3 Service & Infrastructure Business Logic (`Data/Services/` & `Infrastructure/Ingestion/`)

1. **`Data/Services/FoodCatalogService.cs` (`MergeFoodItemsAsync`)**:
   - Update constructor to `public sealed class FoodCatalogService(AppDbContext dbContext, IShoppingListService? shoppingListService = null, IShoppingRealtimeNotifier? shoppingRealtimeNotifier = null, TimeProvider? timeProvider = null) : IFoodCatalogService`.
   - Populate `f.ShoppingListItems.Count()` into `ReferencedShoppingListItemCount` on `GetFoodItemsAsync` and `GetFoodItemByIdAsync`.
   - **`MergeFoodItemsAsync` Invariants**:
     - Validate `request.TargetFoodItemId != Guid.Empty` and `distinctSourceIds = (request.SourceFoodItemIds ?? []).Where(id => id != Guid.Empty).Distinct().ToList()` is non-empty (`DomainValidationException` otherwise).
     - Reject self-merge (`distinctSourceIds.Contains(request.TargetFoodItemId)` $\rightarrow$ `DomainValidationException("Cannot merge a food item into itself.")`).
     - Load tracked `target` (`Include(f => f.Aliases)`) and `sources` (`Include(f => f.Aliases)`): throw `NotFoundException` if `target` is missing or `sources.Count != distinctSourceIds.Count`.
     - If `dbContext.Database.IsRelational()`, open `await using var tx = await dbContext.Database.BeginTransactionAsync(cancellationToken);`.
     - **Aisle Inheritance**: If `target.DefaultStoreAisleId is null`, inherit the first non-null `DefaultStoreAisleId` from `sources` (ordered by `distinctSourceIds`).
     - **Recipe Ingredients**: Load all `RecipeIngredients` where `FoodItemId` is in `distinctSourceIds` and set `ri.FoodItemId = target.Id` (`repointedRecipeIngredientCount`).
     - **Shopping List Items & Bucket Consolidation**:
       - Find distinct `ShoppingListId`s containing any `ShoppingListItem` with `FoodItemId` in `distinctSourceIds`.
       - Load tracked `ShoppingListItems` (`Include(i => i.Sources)`) on those lists whose `FoodItemId` equals `target.Id` or is in `distinctSourceIds`.
       - Record `repointedShoppingListItemCount` = count of items originally pointing to `distinctSourceIds`.
       - For each source-linked item: set `item.FoodItemId = target.Id`, and if `item.DisplayName` matched any merged source `Name` case-insensitively, update `item.DisplayName = target.Name`.
       - Group those items by `(item.ShoppingListId, item.IsChecked, item.IsPantryStapleExcluded, ShoppingConsolidator.BuildConsolidationKey(target.Id, target.Name, ShoppingConsolidator.GetDimension(item.Unit, item.Quantity), item.Unit, item.CustomUnitName))`:
         - For any group with `Count > 1`: convert the group's items into `ConsolidatableIngredientInput` entries (preserving existing `ShoppingListItemSource` entries, or synthesizing a `Manual` contribution when an item had zero sources), run `ShoppingConsolidator.Consolidate(...)` to compute merged `(Quantity, MaxQuantity, Unit, CustomUnitName, Dimension, MergedNotes, Sources)`, update the surviving `ShoppingListItem` (preferring the earliest/existing target row), replace its `Sources` with the deduplicated `ShoppingListItemSource` rows, and `dbContext.ShoppingListItems.Remove` the redundant rows (incrementing `consolidatedShoppingListItemCount`).
     - **Review Inbox Draft Hygiene**:
       - Load `IngestionJobs` where `Status == IngestionJobStatus.ReadyForReview && ExtractedDraftJson != null`. If any `IngestionDraftIngredientDto.MatchedFoodItemId` is in `distinctSourceIds`, rewrite `MatchedFoodItemId = target.Id` and `MatchedFoodItemName = target.Name` in `ExtractedDraftJson`.
     - **Aliases & Source Removal (SQL Server Unique-Index Safe)**:
       - Build `knownNormKeys` (`HashSet<string>(StringComparer.OrdinalIgnoreCase)`) seeded with `target.NormalizedName` and `target.Aliases.Select(a => a.NormalizedAlias)`.
       - When `request.KeepSourceNamesAsAliases == true`:
         - First, for each `source` in `sources`, iterate `source.Aliases.ToList()`: if `knownNormKeys.Add(alias.NormalizedAlias)`, re-parent `alias.FoodItemId = target.Id` (`UPDATE` in place—never `DELETE`+`INSERT`) and add `alias.Alias` to `addedAliases`; otherwise `dbContext.FoodItemAliases.Remove(alias)`.
         - Second, for each `source` in `sources`: if `knownNormKeys.Add(source.NormalizedName)`, add `new FoodItemAlias { FoodItemId = target.Id, Alias = source.Name, NormalizedAlias = source.NormalizedName }` to `dbContext.FoodItemAliases` and add `source.Name` to `addedAliases`.
       - Remove `sources` via `dbContext.FoodItems.RemoveRange(sources)`.
     - Call `await dbContext.SaveChangesAsync(cancellationToken)` (and `await tx.CommitAsync(cancellationToken)` when relational).
     - If `affectedListIds.Count > 0 && shoppingListService is not null && shoppingRealtimeNotifier is not null`, broadcast `ShoppingRealtimeEventType.ListUpdated` for each affected `ShoppingListId` so active SignalR co-shopping sessions update immediately.
     - Return `MergeFoodItemsResultDto` with refreshed `GetFoodItemByIdAsync(target.Id, cancellationToken)`.

2. **`Infrastructure/Ingestion/LlmExtractionClient.cs` (`SuggestIngredientSubstitutionsAsync` & `DraftWeeklyMealPlanAsync`)**:
   - Extract a shared private helper `SendStructuredTextChatAsync(EffectiveLlmConnectionConfig config, string schemaName, object jsonSchema, string systemPrompt, string userPrompt, double temperature, CancellationToken cancellationToken)` inside `LlmExtractionClient` so `ExtractRecipeFromTextAsync`, `SuggestIngredientSubstitutionsAsync`, and `DraftWeeklyMealPlanAsync` share identical `Ollama` (`/api/chat` + `format`) and `OpenAiCompatible` (`/v1/chat/completions` + `response_format.json_schema`) transport, `Bearer` auth header, `OllamaKeepAlive`, timeout, markdown fence cleaning, and `LlmHostUnavailableException` mapping!
   - **`SuggestIngredientSubstitutionsAsync`**:
     - Throws `LlmHostUnavailableException` if `string.IsNullOrWhiteSpace(config.TextModel)`.
     - Uses `temperature = 0.2` and JSON schema `{ type = "object", properties = new { substitutions = new { type = "array", items = new { type = "object", properties = new { label = ..., ratioAndAmount = ..., culinaryNotes = ..., flavorOrTextureImpact = ... }, required = ["label", "ratioAndAmount", "culinaryNotes", "flavorOrTextureImpact"] } } }, required = ["substitutions"] }`.
     - System prompt instructs the culinary model to provide `2 to 3` practical kitchen substitutions specifically scaled to `context.ScaledAmountDisplay` of `context.TargetIngredientName` in `context.RecipeTitle`, accounting for how the ingredient functions in `context.RelevantStepInstructions` and honoring `context.DietaryOrPantryNote` when provided.
     - Deserializes `LlmSubstitutionPayload` and throws `DomainValidationException` if `Substitutions` is null/empty.
   - **`DraftWeeklyMealPlanAsync`**:
     - Throws `LlmHostUnavailableException` if `string.IsNullOrWhiteSpace(config.TextModel)`.
     - Uses `temperature = 0.2` and JSON schema `{ type = "object", properties = new { proposals = new { type = "array", items = new { type = "object", properties = new { catalogIndex = new { type = "integer" }, recipeTitle = new { type = "string" }, dayOffset = new { type = "integer" }, slot = new { type = "string" }, isOnDeck = new { type = "boolean" }, targetServings = new { type = "number" }, rationale = new { type = "string" } }, required = ["catalogIndex", "rationale"] } } }, required = ["proposals"] }`.
     - System prompt strictly instructs the model: *"You MUST ONLY select recipes from the provided Household Cookbook Catalog using their exact integer `catalogIndex`. Never invent recipes outside the catalog."*

3. **`Data/Services/AiKitchenHelperService.cs`**:
   - Primary constructor: `public sealed class AiKitchenHelperService(AppDbContext dbContext, ILlmSettingsService llmSettingsService, ILlmExtractionClient llmClient, TimeProvider? timeProvider = null) : IAiKitchenHelperService`.
   - Register `services.AddScoped<IAiKitchenHelperService, AiKitchenHelperService>();` in `Data/DataServiceCollectionExtensions.cs`.
   - **`GetStatusAsync`**:
     - Resolves `config = await llmSettingsService.GetEffectiveConfigAsync(cancellationToken)`.
     - If `string.IsNullOrWhiteSpace(config.TextModel)`, returns immediately (`LatencyMs: 0`, zero HTTP call):
       `new AiHelperStatusDto(false, config.Provider, null, 0, "No Text LLM model is configured in LLM Settings.")`.
     - Calls `probe = await llmClient.ProbeAndListModelsAsync(config, timeoutSeconds: 4, cancellationToken)` and returns `new AiHelperStatusDto(probe.IsReachable, config.Provider, config.TextModel, probe.LatencyMs, probe.IsReachable ? null : (probe.ErrorMessage ?? "LLM host is unreachable."))`.
   - **`SuggestIngredientSubstitutionsAsync`**:
     - Validates `request.RecipeId != Guid.Empty` and `request.RecipeIngredientId != Guid.Empty` (`DomainValidationException`).
     - Loads `Recipe` (`AsNoTracking()`, `Include(r => r.Ingredients).ThenInclude(i => i.FoodItem)`, `Include(r => r.Steps).ThenInclude(s => s.StepIngredients)`) or throws `NotFoundException("Recipe not found.")`.
     - Finds `ingredient = recipe.Ingredients.FirstOrDefault(i => i.Id == request.RecipeIngredientId)` or throws `NotFoundException("Recipe ingredient not found.")`.
     - Rejects sub-recipe link rows (`ingredient.LinkedRecipeId.HasValue` $\rightarrow$ `DomainValidationException("Ingredient substitutions are only supported for food ingredients, not linked sub-recipes.")`).
     - Computes `effectiveServings = request.TargetServings ?? recipe.BaseServings` (`0.25m..999m`), `scaleFactor = recipe.BaseServings > 0 ? effectiveServings / recipe.BaseServings : 1m`, and formatted amount string using `CulinaryMath.ScaleAndConvert(ingredient.Quantity, ingredient.MaxQuantity, ingredient.Unit, ingredient.CustomUnitName, scaleFactor, request.TargetSystem)`.
     - Collects other ingredient display names and the step instructions referencing `ingredient.Id` (falling back to all steps if no explicit `StepIngredients` link exists).
     - Calls `llmClient.SuggestIngredientSubstitutionsAsync(config, context, cancellationToken)` and normalizes/trims up to `4` non-empty `IngredientSubstitutionOptionDto` options (throwing `DomainValidationException` if none remain).
   - **`DraftWeeklyMealPlanAsync`**:
     - Validates `Prompt` is non-whitespace and `<= 600` chars, and `1 <= MealCount <= 14` (`DomainValidationException`).
     - Normalizes `weekMonday = MealPlanDateHelper.NormalizeToMonday(request.WeekStartDate ?? DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime))` and `weekSunday = weekMonday.AddDays(6)`.
     - Loads all `RecipeStatus.Published` recipes (`AsNoTracking()`, including `RecipeTags.Tag` and `Ingredients.FoodItem`). If `0` published recipes exist $\rightarrow$ throws `DomainValidationException("Your household cookbook has no published recipes yet. Add or publish recipes before drafting a meal plan.")`.
     - Ranks up to `150` candidate recipes (scoring prompt keyword matches + variety/rating if `> 150` exist, otherwise including the entire cookbook) and assigns sequential `CatalogIndex` (`1..N`), calculating `DaysSinceLastCooked` from `_timeProvider.GetUtcNow()`.
     - Loads existing `MealPlanEntries` (`AsNoTracking()`, `Include(e => e.Recipe)`) for `weekMonday` so existing scheduled slots are passed as context and open `Dinner`/`DefaultSlot` days (`0..6`) are tracked for fallback day assignment.
     - Calls `llmClient.DraftWeeklyMealPlanAsync(config, context, cancellationToken)`.
     - **Strict Grounding Filter**: Resolves each returned `LlmDraftMealProposalItem` against the in-memory `CatalogIndex` map (falling back to exact case-insensitive `RecipeTitle` match against the published cookbook map), **discarding any item that does not match a real published household recipe** and deduplicating repeated `RecipeId`s unless `publishedRecipes.Count < request.MealCount`.
     - Normalizes placement:
       - When `request.PlaceOnDeck == true` or `item.IsOnDeck == true`: sets `IsOnDeck = true, DayOffset = null, ScheduledDate = null, Slot = null`.
       - Otherwise clamps/assigns `DayOffset` in `0..6` (auto-distributing across open days of the week if omitted or out of `0..6`), sets `ScheduledDate = weekMonday.AddDays(dayOffset)`, parses `Slot` (defaulting to `request.DefaultSlot`), and defaults `TargetServings` to `recipe.BaseServings` when omitted or `<= 0`.
     - Caps returned `Candidates` to `request.MealCount` and throws `DomainValidationException("The AI model did not return any valid recipes from your household cookbook. Try refining your prompt.")` if `0` grounded candidates matched.

4. **`Data/Services/MealPlanService.cs` (`ApplyDraftEntriesAsync`)**:
   - Validates `request.Entries` has between `1` and `28` items (`DomainValidationException` otherwise).
   - Normalizes `weekMonday = MealPlanDateHelper.NormalizeToMonday(request.WeekStartDate)`.
   - Iterates through `request.Entries`, defaulting `WeekStartDate = weekMonday` when `entry.IsOnDeck` and `entry.WeekStartDate is null`, invoking `CreateEntryAsync` (or validated creation within a single unit of work) so all existing `MealPlanService` placement, recipe-existence, and `SortOrder` invariants apply identically, then returns `await GetWeekAsync(weekMonday, cancellationToken)`.

---

## 3. API Endpoints (`API/Controllers/`)

| Verb | Route (`/api/...`) | Request Body / Query | Response (`200`) | Error Cases (`400`/`401`/`404`/`503`) |
| :--- | :--- | :--- | :--- | :--- |
| `POST` | `/food-items/merge` | `MergeFoodItemsRequest` (JSON) | `200 OK` (`MergeFoodItemsResultDto`) | `400 Bad Request`, `401`, `404 Not Found` |
| `GET` | `/ai-helpers/status` | — | `200 OK` (`AiHelperStatusDto`) | `401 Unauthorized` |
| `POST` | `/ai-helpers/ingredient-substitutions` | `SuggestIngredientSubstitutionsRequest` (JSON) | `200 OK` (`IngredientSubstitutionResponseDto`) | `400 Bad Request`, `401`, `404 Not Found`, `503 Service Unavailable` (`LlmHostUnavailableException`) |
| `POST` | `/ai-helpers/draft-meal-plan` | `DraftWeeklyMealPlanRequest` (JSON) | `200 OK` (`DraftMealPlanResponseDto`) | `400 Bad Request`, `401`, `503 Service Unavailable` (`LlmHostUnavailableException`) |
| `POST` | `/meal-plans/week/apply-draft` | `ApplyDraftMealPlanRequest` (JSON) | `200 OK` (`MealPlanWeekDto`) | `400 Bad Request`, `401`, `404 Not Found` |

- Keep `FoodItemsController`, `AiKitchenHelpersController` (`[ApiController]`, `[Route("ai-helpers")]`, `[Authorize]`), and `MealPlansController` strictly thin HTTP adapters delegating all validation, database queries, and LLM orchestration to `IFoodCatalogService`, `IAiKitchenHelperService`, and `IMealPlanService`.
- Map `LlmHostUnavailableException` to `StatusCodes.Status503ServiceUnavailable` with RFC 7807 `ProblemDetails` (`Title = "LLM Host Unavailable"`, `Detail = ex.Message`)—never `401 Unauthorized`—so Angular's `authInterceptor` preserves the user's session if the GPU server goes to sleep mid-session.

---

## 4. Angular Frontend Design (`Client/`)

### 4.1 Models & Core Services (`src/app/core/`)

1. **`src/app/core/models/food-catalog.models.ts`**:
   - Add `referencedShoppingListItemCount?: number` on `FoodItemDto`.
   - Add `MergeFoodItemsRequest` (`targetFoodItemId: string`, `sourceFoodItemIds: string[]`, `keepSourceNamesAsAliases?: boolean`) and `MergeFoodItemsResultDto`.

2. **`src/app/core/models/ai-helper.models.ts`**:
   - Define `AiHelperStatusDto`, `SuggestIngredientSubstitutionsRequest`, `IngredientSubstitutionOptionDto`, `IngredientSubstitutionResponseDto`, `DraftWeeklyMealPlanRequest`, `DraftMealPlanCandidateDto`, and `DraftMealPlanResponseDto`.

3. **`src/app/core/models/meal-plan.models.ts`**:
   - Add `ApplyDraftMealPlanRequest` (`weekStartDate: string`, `entries: CreateMealPlanEntryRequest[]`).

4. **`AiKitchenHelperService` (`src/app/core/services/ai-helpers/ai-kitchen-helper.service.ts`)**:
   - Decorated with `@Service()`.
   - Exposes `readonly status = signal<AiHelperStatusDto | null>(null)`, `readonly isCheckingStatus = signal<boolean>(false)`, and `readonly isAvailable = computed(() => this.status()?.isAvailable === true)`.
   - `refreshStatus(force = false)`: skips HTTP call if cached within the last `30_000ms` unless `force === true`; fetches `GET /api/ai-helpers/status` asynchronously, swallowing unexpected network errors into `{ isAvailable: false, ... }` so callers never fail.
   - `suggestSubstitutions(request: SuggestIngredientSubstitutionsRequest): Observable<IngredientSubstitutionResponseDto>`: posts to `apiUrl('/ai-helpers/ingredient-substitutions')`; on HTTP `503`, updates `status` signal to `isAvailable: false` before rethrowing.
   - `draftWeeklyMealPlan(request: DraftWeeklyMealPlanRequest): Observable<DraftMealPlanResponseDto>`: posts to `apiUrl('/ai-helpers/draft-meal-plan')`; on HTTP `503`, updates `status` signal to `isAvailable: false` before rethrowing.
   - Update `LlmSettingsService` (`llm-settings.service.ts`) to inject `AiKitchenHelperService` and call `this.aiHelperService.refreshStatus(true)` inside `tap(...)` on `updateSettings` and `testConnection` so enabling or changing the LLM host immediately lights up 1-click helpers across the SPA.

5. **`FoodCatalogService` & `MealPlanService` Updates**:
   - `FoodCatalogService`: add `mergeFoodItems(request: MergeFoodItemsRequest): Observable<MergeFoodItemsResultDto>` posting to `apiUrl('/food-items/merge')`.
   - `MealPlanService`: add `applyDraftEntries(request: ApplyDraftMealPlanRequest): Observable<MealPlanWeekDto>` posting to `apiUrl('/meal-plans/week/apply-draft')`.

### 4.2 Feature Components & Dialogs (`src/app/features/`)

Every new component strictly uses its own dedicated kebab-case folder with external `.ts`, `.html`, `.scss`, and `.spec.ts` files, `inject()`, Signals, Material Symbols Outlined icons, and Material 3 CSS variables (`--mat-sys-*`, `--app-success*`, `--app-warning*`):

1. **`MergeFoodItemsDialogComponent` (`src/app/features/food-catalog/merge-food-items-dialog/`)**:
   - Receives `{ items: FoodItemDto[]; preselectedSource?: FoodItemDto }` via `MAT_DIALOG_DATA`.
   - **Target Selector**: Search/filter input + `<mat-select>` (`targetFoodItemId` signal) selecting the canonical `FoodItem` to keep (displaying name, aisle, pantry star, and `Used in N recipes`). If `preselectedSource` is provided and another item shares a similar name or exists, leaves `preselectedSource` checked in `selectedSourceIds` and filters `preselectedSource` out of target options until swapped.
   - **Duplicate Source Checklist**: Searchable checklist of remaining `FoodItemDto`s (`selectedSourceIds` signal) with a quick **`Swap Target & Source`** button when exactly 1 source is selected.
   - **Alias Preservation Toggle**: `<mat-slide-toggle>` (`keepSourceNamesAsAliases`, default `true`) — *"Save merged item names & synonyms as aliases on the target item"*.
   - **Impact Preview Banner (`.merge-impact-summary`)**: Computed summary showing how many duplicate items will be removed and the combined recipe + shopping-list references that will be consolidated into the target item.
   - **Submit (`Merge Items`)**: Calls `foodCatalogService.mergeFoodItems(...)` and closes the dialog returning `MergeFoodItemsResultDto`.

2. **`FoodCatalogComponent` Updates (`src/app/features/food-catalog/food-catalog/`)**:
   - Add **`Merge Duplicates`** button (`.merge-duplicates-btn`, `<mat-icon>merge</mat-icon>`, disabled when `items().length < 2`) next to `Add Food Item` in `.header-actions`.
   - Add per-card **`Merge`** icon button (`.merge-item-btn`, `<mat-icon>merge</mat-icon>`, `aria-label="Merge into another food item"`, `matTooltip="Merge Duplicate..."`) inside `.item-actions` on each `.food-item-card`, plus optional shopping-list usage count indicator when `(item.referencedShoppingListItemCount ?? 0) > 0`.
   - After `MergeFoodItemsDialogComponent` closes with a `MergeFoodItemsResultDto`, reloads catalog items (`loadItems()`) and shows a snackbar summary (`Merged N item(s) into "${result.targetFoodItem.name}"`).

3. **`IngredientSubstitutionDialogComponent` (`src/app/features/recipes/ingredient-substitution-dialog/`)**:
   - Receives `{ recipeId: string; recipeTitle: string; ingredientId: string; ingredientDisplayName: string; targetServings: number; targetSystem: MeasurementSystem }` via `MAT_DIALOG_DATA`.
   - Automatically triggers `aiHelperService.suggestSubstitutions(...)` on `ngOnInit` (true 1-click workflow).
   - Displays header context (`Out of ${ingredientDisplayName}?` + recipe title & scaled amount chip), accessible loading spinner while querying the LAN model, error/offline alert with retry if unreachable, and `2–4` structured `.substitution-option-card` cards showing:
     - **Option Label** + prominent **`RatioAndAmount`** pill + 1-tap `Copy` icon button (`navigator.clipboard?.writeText`).
     - **How to Prepare / Culinary Notes** (`culinaryNotes`).
     - **Flavor & Texture Impact** (`flavorOrTextureImpact`).
   - Includes a compact optional constraint bar (`dietaryOrPantryNote` input, e.g., *"dairy-free"*, *"only have Greek yogurt"*, + **`Refresh`** button) to re-query tailored to what's in the fridge.

4. **`RecipeDetailComponent` & `CookModeComponent` Updates**:
   - `RecipeDetailComponent`:
     - Inject `AiKitchenHelperService` and call `this.aiHelperService.refreshStatus()` in `ngOnInit()`.
     - On each non-sub-recipe `.ingredient-row`, when `aiHelperService.isAvailable()` is `true`, render a compact `.substitute-ing-btn` (`no-print`, `<mat-icon>swap_horiz</mat-icon>`, `matTooltip="Suggest substitution (Local AI)"`, `[attr.aria-label]="'Suggest substitution for ' + item.ingredient.displayName"`) opening `IngredientSubstitutionDialogComponent` with current `targetServings()` and `targetSystem()`.
   - `CookModeComponent`:
     - Inject `AiKitchenHelperService` and call `this.aiHelperService.refreshStatus()` in `ngOnInit()`.
     - When `aiHelperService.isAvailable()` is `true`, render `.cook-substitute-btn` (`<mat-icon>swap_horiz</mat-icon>`, `(click)="$event.stopPropagation(); openSubstitutionDialog(item.ingredient)"`) on non-sub-recipe rows in `.cook-ingredients-pane`, `.mobile-ingredients-drawer`, and inline `.cook-step-ingredient-chip` callouts (resolving the matching `RecipeIngredientDto` by `callout.id`).

5. **`DraftMealPlanDialogComponent` (`src/app/features/meal-plans/draft-meal-plan-dialog/`) & `MealPlanCalendarComponent` Updates**:
   - In `MealPlanCalendarComponent`:
     - Inject `AiKitchenHelperService` and call `this.aiHelperService.refreshStatus()` in `ngOnInit()`.
     - In `.calendar-top-actions` (when `viewMode() === 'week' && aiHelperService.isAvailable()`), render **`.draft-week-ai-btn`** (`mat-stroked-button`, `<mat-icon>auto_awesome</mat-icon> Draft Week`) opening `DraftMealPlanDialogComponent` with `{ weekStartDate: activeWeekStart(), periodTitle: periodTitle() }`.
   - In `DraftMealPlanDialogComponent`:
     - **Constraint Form**:
       - Quick-fill preset chips (`"4 weeknight dinners under 35 mins"`, `"High-rated favorites we haven't cooked recently"`, `"Easy vegetarian week"`, `"Weekend prep + 3 quick dinners"`).
       - `Prompt` `<textarea>` (`maxlength="600"`), `Meals to Draft` (`1–14`, default `4`), `Default Slot` (`Dinner`, `Lunch`, `Breakfast`, `Snack / Prep`), and `Schedule Placement` toggle (`Calendar Days` vs `On-Deck This Week` tray), plus **`Generate Draft`** button.
     - **Candidate Review List (`.draft-candidate-card`)**:
       - Renders each grounded household cookbook proposal with selection `<mat-checkbox>` (checked by default), recipe thumbnail/title/time badge, AI `rationale` note, editable placement selector (`Mon`–`Sun` dates of the target week or `On-Deck`), `Slot` selector (when not `On-Deck`), and `Servings` numeric input.
     - **Apply Footer**:
       - **`Add N Selected Meals`** primary button calling `mealPlanService.applyDraftEntries(...)` and closing the dialog with the updated `MealPlanWeekDto`, which `MealPlanCalendarComponent` applies directly to `weekData` with a confirmation snackbar!

---

## 5. Playwright E2E Test Scenarios (`Tests/Client.E2ETests/FoodItemMergeAndAiHelpersE2ETests.cs`)

Create `Tests/Client.E2ETests/FoodItemMergeAndAiHelpersE2ETests.cs` extending `E2ETestBase` with `[TestClass]` and an embedded loopback `MockKitchenAiOllamaServer` (`IAsyncDisposable`, supporting `/api/tags` and `/api/chat` inspection + `SimulateOffline` toggle):

1. **`[TestMethod] FoodCatalog_MergeDuplicateFoodItems_RepointsRecipes_ConsolidatesShoppingList_AndPreservesAliases()`**:
   - Registers & logs in a household user, creates canonical `"Sweet Green Onion"` (`Produce` aisle) and duplicate `"Spring Scallion"` (alias `"Salad Onion"`), creates two published recipes (*"Ginger Scallion Noodles"* using `3 Whole Spring Scallion` and *"Crispy Scallion Pancakes"* using `2 Whole Sweet Green Onion`), and adds both recipes to a `"Weekly Groceries"` shopping list (verifying 2 separate unchecked rows initially).
   - Navigates to `/food-catalog`, clicks `.merge-item-btn` on `"Spring Scallion"`, selects `"Sweet Green Onion"` as the canonical target in `MergeFoodItemsDialogComponent`, verifies `.merge-impact-summary`, and confirms **`Merge Items`**.
   - Verifies `"Spring Scallion"` disappears from the catalog while `"Sweet Green Onion"` displays **`Used in 2 recipes`** and alias pills for **`Spring Scallion`** and **`Salad Onion`**, and searching `"Spring Scallion"` in `/food-catalog` resolves `"Sweet Green Onion"`.
   - Navigates to `/shopping`, opens `"Weekly Groceries"`, and verifies the two rows automatically consolidated into a single **`5` (`whole`) `Sweet Green Onion`** row preserving both recipe provenance chips!

2. **`[TestMethod] AiHelpers_OfflineHostHidesTriggers_OnlineHostSuggestsScaledSubstitutions_InRecipeAndCookMode()`**:
   - Starts `MockKitchenAiOllamaServer` initially with `SimulateOffline = true`, configures LLM Settings pointing to its `BaseUrl` with `TextModel = "qwen2.5:14b"`, creates *"Fluffy Buttermilk Pancakes"* (`4` base servings, `1 Cup Buttermilk`), and verifies that while the LLM host is offline:
     - `RecipeDetailComponent` renders cleanly and instantaneously with **zero** `.substitute-ing-btn` buttons.
     - `CookModeComponent` (`/recipes/{id}/cook`) renders cleanly with **zero** `.cook-substitute-btn` buttons.
     - `MealPlanCalendarComponent` (`/meal-plan`) renders cleanly with **zero** `.draft-week-ai-btn` button.
   - Flips `SimulateOffline = false`, tests connection in LLM Settings (lighting up `AiKitchenHelperService`), navigates back to *"Fluffy Buttermilk Pancakes"*, clicks **`2x`** (`8` servings / `2 cups Buttermilk`), and clicks **`.substitute-ing-btn`** on `Buttermilk`.
   - Verifies `IngredientSubstitutionDialogComponent` immediately displays dish-tailored substitutions scaled to `2 cups` (*"2 tbsp lemon juice + whole milk to measure 2 cups"*), enters constraint `"dairy-free"` and clicks **`Refresh`** (verifying updated response), closes dialog, launches **Cook Mode**, clicks **`.cook-substitute-btn`** on `Buttermilk`, and verifies the substitution dialog opens **without** checking off the `Buttermilk` ingredient checkbox!

3. **`[TestMethod] AiHelpers_DraftWeeklyMealPlan_StrictlyUsesSavedCookbook_AppliesToCalendar_AndMobile360pxZeroOverflow()`**:
   - Starts `MockKitchenAiOllamaServer` online (configured to return 2 valid `catalogIndex` proposals plus 1 hallucinated `catalogIndex: 999` proposal), registers & logs in, configures LLM Settings, and creates 3 published household recipes (*"25-Min Honey Garlic Chicken Thighs"*, *"20-Min Crispy Sesame Tofu"*, *"Sunday Braised Short Ribs"*).
   - Navigates to `/meal-plan`, clicks **`.draft-week-ai-btn`** (`Draft Week`), clicks a quick-preset constraint chip / enters `"2 weeknight dinners under 30 minutes"`, and clicks **`Generate Draft`**.
   - Verifies `DraftMealPlanDialogComponent` renders **only the 2 real household recipes** (filtering out `catalogIndex: 999`), displays AI rationales, switches one candidate to **`On-Deck`**, clicks **`Add 2 Meals to Week`**, and verifies the calendar board shows 1 scheduled dinner card and `1` item in `On-Deck This Week`!
   - Resizes viewport to **`360x640`** (`360px` mobile width) and verifies `/food-catalog` + `MergeFoodItemsDialogComponent`, `CookModeComponent` + `IngredientSubstitutionDialogComponent`, and `/meal-plan` + `DraftMealPlanDialogComponent` render cleanly with zero horizontal overflow (`document.documentElement.scrollWidth <= document.documentElement.clientWidth`).

---

## 6. Step-by-Step Execution Checklist (For Implementation Agent)

- [ ] **Task 1: Backend Core, FoodItem Merge, AI Helper Services & LLM Client**
  - Update `Core/FoodCatalog/FoodCatalogModels.cs`, `Core/FoodCatalog/IFoodCatalogService.cs`, `Core/Ingestion/ILlmExtractionClient.cs`, `Core/MealPlans/MealPlanModels.cs`, and `Core/MealPlans/IMealPlanService.cs`.
  - Create `Core/AiHelpers/AiKitchenHelperModels.cs` and `Core/AiHelpers/IAiKitchenHelperService.cs`.
  - Implement `SuggestIngredientSubstitutionsAsync` and `DraftWeeklyMealPlanAsync` in `Infrastructure/Ingestion/LlmExtractionClient.cs`.
  - Update `Data/Services/FoodCatalogService.cs` (`MergeFoodItemsAsync` + `ReferencedShoppingListItemCount`) and `Data/Services/MealPlanService.cs` (`ApplyDraftEntriesAsync`).
  - Create `Data/Services/AiKitchenHelperService.cs` and register `IAiKitchenHelperService` in `Data/DataServiceCollectionExtensions.cs`.
  - Write unit tests in `Tests/Core.UnitTests/FoodCatalogMergeServiceTests.cs`, `Tests/Core.UnitTests/AiKitchenHelperServiceTests.cs`, and `Tests/Infrastructure.UnitTests/LlmExtractionClientTests.cs`, then run unit tests until 100% green:
    ```powershell
    dotnet test --project Tests/Core.UnitTests/App.Core.UnitTests.csproj
    dotnet test --project Tests/Infrastructure.UnitTests/App.Infrastructure.UnitTests.csproj
    ```

- [ ] **Task 2: API Controllers & Integration Tests**
  - Add `POST /api/food-items/merge` to `API/Controllers/FoodItemsController.cs` and `POST /api/meal-plans/week/apply-draft` to `API/Controllers/MealPlansController.cs`.
  - Create `API/Controllers/AiKitchenHelpersController.cs` (`GET /api/ai-helpers/status`, `POST /api/ai-helpers/ingredient-substitutions`, `POST /api/ai-helpers/draft-meal-plan`).
  - Update `Tests/API.IntegrationTests/FoodCatalogIntegrationTests.cs` and `Tests/API.IntegrationTests/OpenApiIntegrationTests.cs`, and create `Tests/API.IntegrationTests/AiKitchenHelpersIntegrationTests.cs` covering auth boundaries (`401`), offline/unconfigured status, `503` host-unavailable mapping, scaled substitution generation, strict cookbook grounding during meal-plan drafting, and batch draft application.
  - Run API integration tests until 100% green:
    ```powershell
    dotnet test --project Tests/API.IntegrationTests/App.API.IntegrationTests.csproj
    ```

- [ ] **Task 3: Angular Client Components, Dialogs & Unit Tests**
  - Update `food-catalog.models.ts`, `meal-plan.models.ts`, `food-catalog.service.ts`, `meal-plan.service.ts`, and `llm-settings.service.ts`; create `ai-helper.models.ts` and `AiKitchenHelperService` (`core/services/ai-helpers/`) with `.spec.ts` unit tests.
  - Create `MergeFoodItemsDialogComponent` (`features/food-catalog/merge-food-items-dialog/`), `IngredientSubstitutionDialogComponent` (`features/recipes/ingredient-substitution-dialog/`), and `DraftMealPlanDialogComponent` (`features/meal-plans/draft-meal-plan-dialog/`) with dedicated external `.ts`, `.html`, `.scss`, and `.spec.ts` files.
  - Update `FoodCatalogComponent`, `RecipeDetailComponent`, `CookModeComponent`, and `MealPlanCalendarComponent` (+ `.spec.ts` tests).
  - Run frontend lint, production build, and unit tests until 100% green:
    ```powershell
    # In Client/:
    npm run lint
    npm run build:production
    npm test -- --watch=false
    ```

- [ ] **Task 4: Playwright E2E Verification & Final Formatting**
  - Create `Tests/Client.E2ETests/FoodItemMergeAndAiHelpersE2ETests.cs` implementing all 3 scenarios from Section 5.
  - Build production Angular bundle + .NET solution and run Playwright E2E tests:
    ```powershell
    # In Client/:
    npm run build:production
    # From repo root:
    dotnet build App.slnx
    dotnet test --project Tests/Client.E2ETests/App.Client.E2ETests.csproj
    ```
  - Format all backend and frontend files:
    ```powershell
    dotnet csharpier format .
    # In Client/:
    npm run format
    npm run lint:fix
    ```
  - Run full solution test suite (`dotnet test App.slnx`).
  - Mark this plan's status as `[x] Completed`.
