# Phase 08: Smart Flat Shopping Lists, Unit Consolidation & Pantry Staples

> **Branch:** `feature/phase-08-smart-shopping-lists`
> **Status:** `[~]` In Progress (`[ ]` Not Started | `[~]` In Progress | `[x]` Completed)
> **PRD Reference:** `docs/prd.md` — Sections 1, 2, 3, 4.5 (4.5.1–4.5.3), 5, and 6 (Milestone 3, Slice 2 of 3)
> **Target Execution:** Run `/execute-phase` to execute this plan step-by-step.

---

## 1. Scope & Objectives

This phase delivers the second vertical slice of **Milestone 3**: **Multiple Named Flat Shopping Lists**, **Smart Culinary Unit Consolidation** from both **Meal Plan Date Ranges** (including optional **On-Deck** meals and Sub-Recipes while strictly excluding `Leftovers` and `FreeformNote` entries) and **Single Recipes**, **Recipe Provenance Badges**, **Idempotent Re-Generation Detection**, and the **Lightweight Pantry Staples Verification Tray**:

1. **Multiple Named Flat Household Shopping Lists (`ShoppingList`, `ShoppingListItem`, `ShoppingListItemSource`, `IShoppingListService`, `/api/shopping-lists`):**
   - Seed a starter `"Weekly Groceries"` shopping list (`IsPinned = true`) in `HouseholdCatalogSeeder` when no shopping lists exist.
   - Support unlimited named household `ShoppingList` records (`Weekly Groceries`, `Costco`, `Trader Joe's`, `Thanksgiving Prep`) with pinned list ordering, clean flat checklist rendering (explicitly omitting store-aisle grouping and custom Store Layouts while keeping existing `StoreAisle` categories strictly inside Food Catalog), ad-hoc item creation (auto-linking matching canonical `FoodItem` / `FoodItemAlias` names when omitted), item editing, drag-or-order sorting (`SortOrder`), optimistic check/uncheck (`CheckedAt`, `CheckedByUserId`, `CheckedByDisplayName`), collapsible **Checked Off** management (`DELETE /shopping-lists/{id}/items/checked`), and 1-tap **Pantry Staple Promotion**.

2. **Smart Consolidation Engine, Provenance & Pantry Staples (`ShoppingConsolidator` in `App.Core.Shopping`):**
   - **Scaling & Sub-Recipe Flattening:** Multiplies recipe ingredients (and recursively traverses linked Sub-Recipes via `LinkedRecipeId` when `IncludeSubRecipes = true`) by `TargetServings / BaseServings`.
   - **Strict Meal Plan Exclusions:** Ignores any `MealPlanEntry` where `IsLeftovers == true`, `EntryType == MealPlanEntryType.Leftovers`, or `EntryType == MealPlanEntryType.FreeformNote`.
   - **Compatible Unit Math:** Sums compatible **Volume** units (`2 tbsp + 1/4 cup = 3/8 cup` or `6 tbsp`; `250 ml + 750 ml = 1 l`), compatible **Weight** units (`8 oz + 1 lb = 1 1/2 lb`; `250 g + 500 g = 750 g`), and identical **Discrete / Count** units (`2 cloves + 3 cloves = 5 cloves`).
   - **Incompatible Dimension Separation:** Preserves distinct measurement dimension families side-by-side (`Yellow Onion: 3` whole + `Yellow Onion: 1 cup`) without guessing density conversions.
   - **Recipe Provenance Badges:** Persists `ShoppingListItemSource` chips (`"Tue: Chicken Tacos"`, `"Thu: Enchiladas"`, `"On-Deck: Salsa Verde"`, or `"Fish Tacos"`) with optional `RecipeId` and `MealPlanEntryId`.
   - **Idempotent Re-Generation & Sync Warning:** Preview (`POST /shopping-lists/{id}/generate/meal-plan/preview`) and Execute (`POST /shopping-lists/{id}/generate/meal-plan`) detect already-imported `MealPlanEntryId` slices on the target list—skipping duplicates by default (`DuplicateMealPolicy.SkipAlreadyImported`) or cleanly replacing prior contributions when requested (`DuplicateMealPolicy.ReplaceAlreadyImported`).
   - **Pantry Staples Separation:** Routes ingredients whose canonical `FoodItem.IsPantryStaple == true` (*Water*, *Kosher Salt*, *Black Pepper*, *Extra-Virgin Olive Oil*, etc.) into `IsPantryStapleExcluded = true` so the active shopping list stays clutter-free while allowing 1-tap promotion (`POST /shopping-lists/{id}/items/{itemId}/promote-staple`) onto the active checklist.

3. **Responsive Angular Shopping Workspace & Cross-Feature Entry Points (`Client/`):**
   - Add **`Shop`** (`/shopping`) to both `.desktop-nav` and `360px` `.mobile-bottom-nav` in `AppShellComponent` (preserving all 5 existing mobile tabs with tuned flex spacing so all 6 fit at `360px` without horizontal overflow).
   - Build dedicated per-component directories under `Client/src/app/features/shopping/`:
     - `shopping-workspace/` (`ShoppingWorkspaceComponent`)
     - `generate-shopping-list-dialog/` (`GenerateShoppingListDialogComponent`)
     - `add-recipe-to-list-dialog/` (`AddRecipeToListDialogComponent`)
     - `shopping-item-dialog/` (`ShoppingItemDialogComponent`)
   - Add **"Shop Week"** action on `MealPlanCalendarComponent` (launching `GenerateShoppingListDialogComponent` pre-filled with the active week's Monday–Sunday range) and **"Add to List"** action on `RecipeDetailComponent` (launching `AddRecipeToListDialogComponent` pre-filled with the current recipe and `targetServings()`).

### Explicitly Out of Scope (Deferred to Phase 09 & Milestone 4)
- Store aisle grouping / custom Store Layouts on shopping lists — explicitly skipped per product decision (`docs/prd.md` Section 4.5.1).
- `SignalR` real-time multi-phone broadcast hub (`/api/hubs/shopping`) and Milestone 3 MCP tools (`get_meal_plan`, `plan_meals`, `remove_meal_plan_entry`, `get_shopping_lists`, `generate_shopping_list_from_meal_plan`, `upsert_shopping_list_items`, `set_shopping_list_item_checked`) — delivered in **Phase 09**.
- Hands-Free `Cook Mode`, `CookLog` ratings/history, `FoodItem` duplicate-merge management UI, and 1-click local LLM helpers — delivered in **Milestone 4**.

### 1.1 File Inventory (Modify & Create)

#### Existing Backend & Test Files to Modify
- `Data/Entities/FoodItem.cs` — Add `ShoppingListItems` navigation collection.
- `Data/AppDbContext.cs` — Add `DbSet<ShoppingList>`, `DbSet<ShoppingListItem>`, and `DbSet<ShoppingListItemSource>`.
- `Data/DataServiceCollectionExtensions.cs` — Register `IShoppingListService`.
- `Data/Seeding/HouseholdCatalogSeeder.cs` — Seed starter `"Weekly Groceries"` `ShoppingList` (`IsPinned = true`) when none exist.
- `Data/Services/FoodCatalogService.cs` — In `DeleteFoodItemAsync`, detach any `ShoppingListItems` (`FoodItemId = null`) referencing an unreferenced-by-recipes `FoodItem` prior to deletion so `DeleteBehavior.Restrict` never fails on historical shopping rows.
- `Tests/API.IntegrationTests/OpenApiIntegrationTests.cs` — Assert `/shopping-lists` is documented in `/api/openapi/v1.json`.

#### New Backend & Backend Test Files to Create
- `Core/Shopping/ShoppingEnums.cs`
- `Core/Shopping/ShoppingConsolidator.cs`
- `Core/Shopping/ShoppingListModels.cs`
- `Core/Shopping/IShoppingListService.cs`
- `Data/Entities/ShoppingList.cs`
- `Data/Entities/ShoppingListItem.cs`
- `Data/Entities/ShoppingListItemSource.cs`
- `Data/Configurations/ShoppingListConfiguration.cs`
- `Data/Configurations/ShoppingListItemConfiguration.cs`
- `Data/Configurations/ShoppingListItemSourceConfiguration.cs`
- `Data/Services/ShoppingListService.cs`
- `API/Controllers/ShoppingListsController.cs`
- `Tests/Core.UnitTests/ShoppingConsolidatorTests.cs`
- `Tests/Core.UnitTests/ShoppingListServiceTests.cs`
- `Tests/API.IntegrationTests/ShoppingListsIntegrationTests.cs`

#### Existing Frontend Files to Modify (`Client/`)
- `Client/src/app/app.routes.ts` — Register lazy `/shopping` route under `AppShellComponent`.
- `Client/src/app/features/shell/app-shell/app-shell.component.html` (+ `.scss`, `.spec.ts`) — Add `Shop` (`<mat-icon>shopping_cart</mat-icon>`, `routerLink="/shopping"`) to `.desktop-nav` and `.mobile-bottom-nav` (tuning `.mobile-tab` flex/padding so all 6 tabs fit cleanly at `360px` width without horizontal scroll).
- `Client/src/app/features/meal-plans/meal-plan-calendar/meal-plan-calendar.component.ts` (+ `.html`, `.spec.ts`) — Add **"Shop Week"** button in `.calendar-top-actions` opening `GenerateShoppingListDialogComponent` pre-filled with the active week's Monday–Sunday range.
- `Client/src/app/features/recipes/recipe-detail/recipe-detail.component.ts` (+ `.html`, `.spec.ts`) — Add **"Add to List"** button in `.detail-top-actions` opening `AddRecipeToListDialogComponent` pre-filled with `recipe()!` and `targetServings()`.

#### New Frontend & E2E Files to Create
- `Client/src/app/core/models/shopping.models.ts`
- `Client/src/app/core/services/shopping/shopping.service.ts` (+ `.spec.ts`)
- `Client/src/app/features/shopping/shopping-workspace/shopping-workspace.component.ts` (+ `.html`, `.scss`, `.spec.ts`)
- `Client/src/app/features/shopping/generate-shopping-list-dialog/generate-shopping-list-dialog.component.ts` (+ `.html`, `.scss`, `.spec.ts`)
- `Client/src/app/features/shopping/add-recipe-to-list-dialog/add-recipe-to-list-dialog.component.ts` (+ `.html`, `.scss`, `.spec.ts`)
- `Client/src/app/features/shopping/shopping-item-dialog/shopping-item-dialog.component.ts` (+ `.html`, `.scss`, `.spec.ts`)
- `Tests/Client.E2ETests/ShoppingListsE2ETests.cs`

---

## 2. Domain & Database Design (`Core/` & `Data/`)

### 2.1 Enums & Pure Consolidation Helper (`Core/Shopping/`)

1. **`Core/Shopping/ShoppingEnums.cs`** (`namespace App.Core.Shopping`):
   ```csharp
   using System.Text.Json.Serialization;

   namespace App.Core.Shopping;

   [JsonConverter(typeof(JsonStringEnumConverter))]
   public enum MeasurementDimension
   {
       Volume = 0,
       Weight = 1,
       Discrete = 2,
       Unspecified = 3,
   }

   [JsonConverter(typeof(JsonStringEnumConverter))]
   public enum ShoppingListSourceKind
   {
       Manual = 0,
       MealPlanEntry = 1,
       SingleRecipe = 2,
   }

   [JsonConverter(typeof(JsonStringEnumConverter))]
   public enum DuplicateMealPolicy
   {
       SkipAlreadyImported = 0,
       ReplaceAlreadyImported = 1,
   }
   ```

2. **`Core/Shopping/ShoppingConsolidator.cs`** (`namespace App.Core.Shopping`):
   - Pure static culinary math & consolidation engine (zero EF Core dependencies):
   - Input & output records:
     ```csharp
     public record ConsolidatableIngredientInput(
         Guid? FoodItemId,
         string CanonicalOrDisplayName,
         bool IsPantryStaple,
         decimal? Quantity,
         decimal? MaxQuantity,
         MeasurementUnit? Unit,
         string? CustomUnitName,
         string? PreparationNote,
         ShoppingListSourceKind SourceKind,
         Guid? MealPlanEntryId,
         Guid? RecipeId,
         string SourceLabel,
         DateOnly? ScheduledDate
     );

     public record ConsolidatedSourceContribution(
         ShoppingListSourceKind SourceKind,
         Guid? MealPlanEntryId,
         Guid? RecipeId,
         string SourceLabel,
         DateOnly? ScheduledDate,
         decimal? QuantityInConsolidatedUnit,
         decimal? MaxQuantityInConsolidatedUnit
     );

     public record ConsolidatedShoppingBucket(
         string ConsolidationKey,
         Guid? FoodItemId,
         string DisplayName,
         bool IsPantryStaple,
         MeasurementDimension Dimension,
         decimal? Quantity,
         decimal? MaxQuantity,
         MeasurementUnit? Unit,
         string? CustomUnitName,
         string? MergedNotes,
         IReadOnlyList<ConsolidatedSourceContribution> Sources
     );
     ```
   - **Exact Consolidation Rules:**
     - `GetDimension(MeasurementUnit? unit, decimal? quantity)`:
       - If `!quantity.HasValue || quantity.Value <= 0m` $\rightarrow$ `MeasurementDimension.Unspecified`.
       - `Tsp | Tbsp | FlOz | Cup | Pt | Qt | Gal | Ml | L` $\rightarrow$ `MeasurementDimension.Volume`.
       - `Oz | Lb | G | Kg` $\rightarrow$ `MeasurementDimension.Weight`.
       - `Pinch | Dash | Clove | Can | Bunch | Slice | Whole | Custom | null` (when `quantity > 0` and `unit == null`, treat as `MeasurementUnit.Whole`) $\rightarrow$ `MeasurementDimension.Discrete`.
     - `BuildConsolidationKey(Guid? foodItemId, string name, MeasurementDimension dim, MeasurementUnit? unit, string? customUnitName)`:
       - Identity prefix: `foodItemId.HasValue ? $"fi:{foodItemId.Value:N}" : $"name:{name.Trim().ToLowerInvariant()}"`.
       - Dimension suffix:
         - `Volume` $\rightarrow$ `"vol"` (all volumes consolidate together!).
         - `Weight` $\rightarrow$ `"wt"` (all weights consolidate together!).
         - `Discrete` $\rightarrow$ `unit == MeasurementUnit.Custom ? $"custom:{(customUnitName ?? string.Empty).Trim().ToLowerInvariant()}" : $"disc:{unit ?? MeasurementUnit.Whole}"` (identical discrete units sum together; `Whole` vs `Cup` stay separate!).
         - `Unspecified` $\rightarrow$ `"unspec"`.
     - **Lossless Compatible Unit Summation (`Consolidate(IEnumerable<ConsolidatableIngredientInput> inputs)`):**
       - **`Volume` buckets:**
         - If all items in the bucket use US Customary volumes (`Tsp`, `Tbsp`, `FlOz`, `Cup`, `Pt`, `Qt`, `Gal`), convert each to exact `Tsp` (`Tsp = 1m`, `Tbsp = 3m`, `FlOz = 6m`, `Cup = 48m`, `Pt = 96m`, `Qt = 192m`, `Gal = 768m`), sum `totalTsp` (and `totalMaxTsp`—when mixing ranged + fixed items, treat fixed item's `max = min` if at least one contributor has `MaxQuantity > Quantity`), and select the cleanest kitchen unit:
           - `< 3m` (`< 1 tbsp`) $\rightarrow$ `MeasurementUnit.Tsp` (`/ 1m`).
           - `>= 3m && < 12m` (`1 tbsp .. < 1/4 cup`) $\rightarrow$ `MeasurementUnit.Tbsp` (`/ 3m`).
           - `>= 12m && < 192m` (`1/4 cup .. < 1 qt`): if `totalTsp < 48m` and `!IsCleanKitchenFraction(totalTsp / 48m)` and `IsCleanKitchenFraction(totalTsp / 3m)`, use `MeasurementUnit.Tbsp` (`/ 3m`); otherwise `MeasurementUnit.Cup` (`/ 48m`). (Note: `2 tbsp + 1/4 cup` = `18 tsp` = `0.375 cup` = `3/8 cup` cleanly via `KnownFractions`!).
           - `>= 192m && < 768m` $\rightarrow$ `MeasurementUnit.Qt` (`/ 192m`).
           - `>= 768m` $\rightarrow$ `MeasurementUnit.Gal` (`/ 768m`).
         - If all items in the bucket use Metric volumes (`Ml`, `L`), sum in exact `Ml` (`Ml = 1m`, `L = 1000m`): `< 1000m` $\rightarrow$ `MeasurementUnit.Ml`, `>= 1000m` $\rightarrow$ `MeasurementUnit.L` (`/ 1000m`).
         - If mixed US + Metric volumes, sum in `Ml` (`Tsp = 4.92892m`, `Tbsp = 14.7868m`, `FlOz = 29.5735m`, `Cup = 236.588m`, `Pt = 473.176m`, `Qt = 946.353m`, `Gal = 3785.41m`, `Ml = 1m`, `L = 1000m`) and format into Metric (`Ml`/`L`) if the first contributor was Metric, else US Customary (`Tsp` via `/ 4.92892m` then apply US threshold rules).
       - **`Weight` buckets:**
         - If all items use US Customary weights (`Oz`, `Lb`), sum in exact `Oz` (`Oz = 1m`, `Lb = 16m`): `< 16m` $\rightarrow$ `MeasurementUnit.Oz`, `>= 16m` $\rightarrow$ `MeasurementUnit.Lb` (`/ 16m`).
         - If all items use Metric weights (`G`, `Kg`), sum in exact `G` (`G = 1m`, `Kg = 1000m`): `< 1000m` $\rightarrow$ `MeasurementUnit.G`, `>= 1000m` $\rightarrow$ `MeasurementUnit.Kg` (`/ 1000m`).
         - If mixed US + Metric weights, sum in `G` (`Oz = 28.3495m`, `Lb = 453.592m`, `G = 1m`, `Kg = 1000m`) and convert to Metric (`G`/`Kg`) if the first contributor was Metric, else US Customary (`Oz`/`Lb`).
       - **`Discrete` buckets:** Sum `Quantity` directly in the shared `MeasurementUnit` (`Whole` when `Unit == null`).
       - **`Unspecified` buckets:** `Quantity = null`, `MaxQuantity = null`, retaining `Unit` if consistent across contributors.
       - **Source Deduplication within a Bucket:** If the exact same `(SourceKind, MealPlanEntryId, RecipeId, SourceLabel)` contributes twice inside the same recipe (e.g., `"1 tbsp olive oil"` in Marinade section + `"2 tbsp olive oil"` in Sear section of Tuesday's *Chicken Tacos*), merge those two contributions into a single `ConsolidatedSourceContribution` (`3 tbsp`) so the UI displays one clean `"Tue: Chicken Tacos"` provenance chip instead of duplicate chips!
     - Provide `FormatMealPlanSourceLabel(DateOnly? scheduledDate, bool isOnDeck, string title)` helper:
       - If `isOnDeck || !scheduledDate.HasValue`: `$"On-Deck: {title}"`.
       - Else: `$"{scheduledDate.Value.DayOfWeek.ToString()[..3]}: {title}"` (e.g., `"Tue: Chicken Tacos"`).

### 2.2 Entities (`Data/Entities/`)

All 3 new entities reside in `namespace App.Data.Entities` and implement `IAuditable`:

1. **`Data/Entities/ShoppingList.cs`**:
   ```csharp
   namespace App.Data.Entities;

   public sealed class ShoppingList : IAuditable
   {
       public Guid Id { get; set; } = Guid.NewGuid();
       public string Name { get; set; } = string.Empty;
       public string NormalizedName { get; set; } = string.Empty;
       public bool IsPinned { get; set; }
       public DateTimeOffset CreatedAt { get; set; }
       public string CreatedBy { get; set; } = "System";
       public DateTimeOffset ModifiedAt { get; set; }
       public string ModifiedBy { get; set; } = "System";

       public ICollection<ShoppingListItem> Items { get; set; } = new List<ShoppingListItem>();
   }
   ```

2. **`Data/Entities/ShoppingListItem.cs`**:
   ```csharp
   using App.Core.Recipes;
   using App.Core.Shopping;

   namespace App.Data.Entities;

   public sealed class ShoppingListItem : IAuditable
   {
       public Guid Id { get; set; } = Guid.NewGuid();
       public Guid ShoppingListId { get; set; }
       public Guid? FoodItemId { get; set; }
       public string DisplayName { get; set; } = string.Empty;
       public decimal? Quantity { get; set; }
       public decimal? MaxQuantity { get; set; }
       public MeasurementUnit? Unit { get; set; }
       public string? CustomUnitName { get; set; }
       public MeasurementDimension Dimension { get; set; } = MeasurementDimension.Unspecified;
       public string? Notes { get; set; }
       public bool IsPantryStapleExcluded { get; set; }
       public bool IsChecked { get; set; }
       public DateTimeOffset? CheckedAt { get; set; }
       public Guid? CheckedByUserId { get; set; }
       public string? CheckedByDisplayName { get; set; }
       public int SortOrder { get; set; }
       public DateTimeOffset CreatedAt { get; set; }
       public string CreatedBy { get; set; } = "System";
       public DateTimeOffset ModifiedAt { get; set; }
       public string ModifiedBy { get; set; } = "System";

       public ShoppingList ShoppingList { get; set; } = null!;
       public FoodItem? FoodItem { get; set; }
       public ICollection<ShoppingListItemSource> Sources { get; set; } =
           new List<ShoppingListItemSource>();
   }
   ```

3. **`Data/Entities/ShoppingListItemSource.cs`**:
   ```csharp
   using App.Core.Shopping;

   namespace App.Data.Entities;

   public sealed class ShoppingListItemSource : IAuditable
   {
       public Guid Id { get; set; } = Guid.NewGuid();
       public Guid ShoppingListItemId { get; set; }
       public ShoppingListSourceKind SourceKind { get; set; } = ShoppingListSourceKind.Manual;
       public Guid? MealPlanEntryId { get; set; }
       public Guid? RecipeId { get; set; }
       public string SourceLabel { get; set; } = string.Empty;
       public DateOnly? ScheduledDate { get; set; }
       public decimal? ContributedQuantity { get; set; }
       public decimal? ContributedMaxQuantity { get; set; }
       public DateTimeOffset CreatedAt { get; set; }
       public string CreatedBy { get; set; } = "System";
       public DateTimeOffset ModifiedAt { get; set; }
       public string ModifiedBy { get; set; } = "System";

       public ShoppingListItem ShoppingListItem { get; set; } = null!;
   }
   ```

4. **Navigation Addition to Existing Entity:**
   - `Data/Entities/FoodItem.cs`:
     - `public ICollection<ShoppingListItem> ShoppingListItems { get; set; } = new List<ShoppingListItem>();`

### 2.3 EF Core Fluent Configurations (`Data/Configurations/`)

> **SQL Server Cascade Safety:** Parent-to-owned-child aggregations (`ShoppingList` $\rightarrow$ `ShoppingListItem` $\rightarrow$ `ShoppingListItemSource`) use `DeleteBehavior.Cascade`. The `FoodItem` reference on `ShoppingListItem` uses `DeleteBehavior.Restrict` (paired with explicit detachment in `FoodCatalogService.DeleteFoodItemAsync`) so SQL Server never encounters multiple cascade paths and EF Core InMemory behaves identically to SQL Server.

1. **`Data/Configurations/ShoppingListConfiguration.cs`**:
   - Table `"ShoppingLists"`, PK `Id`.
   - `Name`: required, `MaxLength(150)`.
   - `NormalizedName`: required, `MaxLength(150)`, **unique index** `HasIndex(x => x.NormalizedName).IsUnique()`.
   - Index on `(IsPinned, ModifiedAt)`.
   - `CreatedBy` / `ModifiedBy`: required, `MaxLength(256)`.

2. **`Data/Configurations/ShoppingListItemConfiguration.cs`**:
   - Table `"ShoppingListItems"`, PK `Id`.
   - `DisplayName`: required, `MaxLength(200)`.
   - `Quantity` & `MaxQuantity`: `HasPrecision(10, 4)`.
   - `Unit`: `HasConversion<string>().HasMaxLength(32)`.
   - `CustomUnitName`: `MaxLength(60)`.
   - `Dimension`: `HasConversion<string>().HasMaxLength(32)`.
   - `Notes`: `MaxLength(300)`.
   - `CheckedByDisplayName`: `MaxLength(150)`.
   - `CreatedBy` / `ModifiedBy`: required, `MaxLength(256)`.
   - Indexes: `(ShoppingListId, IsPantryStapleExcluded, IsChecked, SortOrder)` and `FoodItemId`.
   - `HasOne(x => x.ShoppingList).WithMany(l => l.Items).HasForeignKey(x => x.ShoppingListId).OnDelete(DeleteBehavior.Cascade)`.
   - `HasOne(x => x.FoodItem).WithMany(f => f.ShoppingListItems).HasForeignKey(x => x.FoodItemId).OnDelete(DeleteBehavior.Restrict)`.

3. **`Data/Configurations/ShoppingListItemSourceConfiguration.cs`**:
   - Table `"ShoppingListItemSources"`, PK `Id`.
   - `SourceKind`: `HasConversion<string>().HasMaxLength(32)`.
   - `SourceLabel`: required, `MaxLength(200)`.
   - `ContributedQuantity` & `ContributedMaxQuantity`: `HasPrecision(10, 4)`.
   - `CreatedBy` / `ModifiedBy`: required, `MaxLength(256)`.
   - Indexes: `ShoppingListItemId`, `MealPlanEntryId`, `RecipeId`.
   - `HasOne(x => x.ShoppingListItem).WithMany(i => i.Sources).HasForeignKey(x => x.ShoppingListItemId).OnDelete(DeleteBehavior.Cascade)`.

### 2.4 DTOs, Requests & Service Contracts (`Core/Shopping/`)

1. **`Core/Shopping/ShoppingListModels.cs`**:
   ```csharp
   using System.ComponentModel.DataAnnotations;
   using App.Core.Recipes;

   namespace App.Core.Shopping;

   public record ShoppingListSummaryDto(
       Guid Id,
       string Name,
       bool IsPinned,
       int ActiveUncheckedCount,
       int CheckedCount,
       int ExcludedPantryCount,
       DateTimeOffset CreatedAt,
       DateTimeOffset ModifiedAt
   );

   public record ShoppingListItemSourceDto(
       Guid Id,
       ShoppingListSourceKind SourceKind,
       Guid? MealPlanEntryId,
       Guid? RecipeId,
       string SourceLabel,
       DateOnly? ScheduledDate,
       decimal? ContributedQuantity,
       decimal? ContributedMaxQuantity
   );

   public record ShoppingListItemDto(
       Guid Id,
       Guid ShoppingListId,
       Guid? FoodItemId,
       string? FoodItemName,
       string DisplayName,
       decimal? Quantity,
       decimal? MaxQuantity,
       MeasurementUnit? Unit,
       string? CustomUnitName,
       string UnitLabel,
       string FormattedAmount,
       MeasurementDimension Dimension,
       string? Notes,
       bool IsPantryStapleExcluded,
       bool IsChecked,
       DateTimeOffset? CheckedAt,
       Guid? CheckedByUserId,
       string? CheckedByDisplayName,
       int SortOrder,
       IReadOnlyList<ShoppingListItemSourceDto> Sources,
       DateTimeOffset CreatedAt,
       DateTimeOffset ModifiedAt
   );

   public record ShoppingListDetailDto(
       Guid Id,
       string Name,
       bool IsPinned,
       IReadOnlyList<ShoppingListItemDto> ActiveItems,
       IReadOnlyList<ShoppingListItemDto> CheckedItems,
       IReadOnlyList<ShoppingListItemDto> ExcludedPantryItems,
       DateTimeOffset CreatedAt,
       DateTimeOffset ModifiedAt
   );

   public record CreateShoppingListRequest(
       [Required, MaxLength(150)] string Name,
       bool IsPinned = false
   );

   public record UpdateShoppingListRequest(
       [Required, MaxLength(150)] string Name,
       bool IsPinned = false
   );

   public record CreateShoppingListItemRequest(
       [Required, MaxLength(200)] string DisplayName,
       decimal? Quantity = null,
       decimal? MaxQuantity = null,
       MeasurementUnit? Unit = null,
       [MaxLength(60)] string? CustomUnitName = null,
       Guid? FoodItemId = null,
       [MaxLength(300)] string? Notes = null,
       bool ConsolidateIfCompatible = true
   );

   public record UpdateShoppingListItemRequest(
       [Required, MaxLength(200)] string DisplayName,
       decimal? Quantity = null,
       decimal? MaxQuantity = null,
       MeasurementUnit? Unit = null,
       [MaxLength(60)] string? CustomUnitName = null,
       [MaxLength(300)] string? Notes = null,
       bool IsPantryStapleExcluded = false
   );

   public record SetShoppingListItemCheckedRequest(bool IsChecked);

   public record ClearCheckedItemsResultDto(int RemovedCount);

   public record AlreadyImportedMealDto(
       Guid MealPlanEntryId,
       string DisplayTitle,
       DateOnly? ScheduledDate,
       bool IsOnDeck
   );

   public record GenerateFromMealPlanRequest(
       DateOnly StartDate,
       DateOnly EndDate,
       bool IncludeOnDeck = true,
       bool IncludeSubRecipes = true,
       bool IncludeOptionalIngredients = true,
       DuplicateMealPolicy DuplicatePolicy = DuplicateMealPolicy.SkipAlreadyImported,
       IReadOnlyList<Guid>? PromotePantryFoodItemIds = null
   );

   public record ShoppingListCandidateItemDto(
       Guid? FoodItemId,
       string DisplayName,
       bool IsPantryStaple,
       decimal? Quantity,
       decimal? MaxQuantity,
       MeasurementUnit? Unit,
       string? CustomUnitName,
       string UnitLabel,
       string FormattedAmount,
       MeasurementDimension Dimension,
       string? Notes,
       IReadOnlyList<string> SourceLabels
   );

   public record ShoppingListGenerationPreviewDto(
       DateOnly StartDate,
       DateOnly EndDate,
       int EligibleMealsCount,
       int NewMealsToImportCount,
       int IgnoredLeftoversCount,
       int IgnoredNotesCount,
       IReadOnlyList<AlreadyImportedMealDto> AlreadyImportedMeals,
       IReadOnlyList<ShoppingListCandidateItemDto> ActiveCandidateItems,
       IReadOnlyList<ShoppingListCandidateItemDto> ExcludedPantryCandidateItems
   );

   public record AddRecipeToShoppingListRequest(
       Guid RecipeId,
       decimal? TargetServings = null,
       bool IncludeSubRecipes = true,
       bool IncludeOptionalIngredients = true,
       IReadOnlyList<Guid>? PromotePantryFoodItemIds = null
   );

   public record ShoppingListGenerationResultDto(
       ShoppingListDetailDto ShoppingList,
       int ImportedMealsCount,
       int SkippedDuplicateMealsCount,
       int IgnoredLeftoversCount,
       int IgnoredNotesCount,
       int ActiveItemsAffectedCount,
       int ExcludedPantryItemsCount
   );
   ```

2. **`Core/Shopping/IShoppingListService.cs`**:
   ```csharp
   namespace App.Core.Shopping;

   public interface IShoppingListService
   {
       Task<IReadOnlyList<ShoppingListSummaryDto>> GetShoppingListsAsync(
           CancellationToken cancellationToken = default
       );
       Task<ShoppingListDetailDto?> GetShoppingListByIdAsync(
           Guid id,
           CancellationToken cancellationToken = default
       );
       Task<ShoppingListDetailDto> CreateShoppingListAsync(
           CreateShoppingListRequest request,
           CancellationToken cancellationToken = default
       );
       Task<ShoppingListDetailDto?> UpdateShoppingListAsync(
           Guid id,
           UpdateShoppingListRequest request,
           CancellationToken cancellationToken = default
       );
       Task<bool> DeleteShoppingListAsync(
           Guid id,
           CancellationToken cancellationToken = default
       );
       Task<ShoppingListItemDto?> AddItemAsync(
           Guid shoppingListId,
           CreateShoppingListItemRequest request,
           CancellationToken cancellationToken = default
       );
       Task<ShoppingListItemDto?> UpdateItemAsync(
           Guid shoppingListId,
           Guid itemId,
           UpdateShoppingListItemRequest request,
           CancellationToken cancellationToken = default
       );
       Task<ShoppingListItemDto?> SetItemCheckedAsync(
           Guid shoppingListId,
           Guid itemId,
           SetShoppingListItemCheckedRequest request,
           CancellationToken cancellationToken = default
       );
       Task<ShoppingListItemDto?> PromotePantryStapleItemAsync(
           Guid shoppingListId,
           Guid itemId,
           CancellationToken cancellationToken = default
       );
       Task<bool> DeleteItemAsync(
           Guid shoppingListId,
           Guid itemId,
           CancellationToken cancellationToken = default
       );
       Task<ClearCheckedItemsResultDto?> ClearCheckedItemsAsync(
           Guid shoppingListId,
           CancellationToken cancellationToken = default
       );
       Task<ShoppingListGenerationPreviewDto?> PreviewGenerateFromMealPlanAsync(
           Guid shoppingListId,
           GenerateFromMealPlanRequest request,
           CancellationToken cancellationToken = default
       );
       Task<ShoppingListGenerationResultDto?> GenerateFromMealPlanAsync(
           Guid shoppingListId,
           GenerateFromMealPlanRequest request,
           CancellationToken cancellationToken = default
       );
       Task<ShoppingListGenerationResultDto?> AddRecipeIngredientsAsync(
           Guid shoppingListId,
           AddRecipeToShoppingListRequest request,
           CancellationToken cancellationToken = default
       );
   }
   ```

### 2.5 Service Business Logic & Invariants (`Data/Services/` & `Data/Seeding/`)

1. **`Data/Seeding/HouseholdCatalogSeeder.cs` update:**
   - If `!await db.ShoppingLists.AnyAsync(cancellationToken)`: create `"Weekly Groceries"` (`NormalizedName = "weekly groceries"`, `IsPinned = true`).

2. **`Data/Services/ShoppingListService.cs`** (`public sealed class ShoppingListService(AppDbContext dbContext, IUserContext? userContext = null, TimeProvider? timeProvider = null) : IShoppingListService`):
   - **List CRUD & Flat Partitioning:**
     - Validates `Name` (`1..150` chars trimmed) and `NormalizedName = trimmed.ToLowerInvariant()`; throws `ConflictException` on duplicate list name.
     - `GetShoppingListsAsync`: returns summaries ordered by `IsPinned` desc, `ModifiedAt` desc, `Name` asc.
     - `GetShoppingListByIdAsync`:
       - Partition items into three flat buckets:
         1. `ActiveItems`: `!IsPantryStapleExcluded && !IsChecked` (ordered by `DisplayName` asc, `Dimension` asc, `SortOrder` asc so incompatible dimension rows like `Yellow Onion (4)` and `Yellow Onion (1 cup)` always appear right next to each other!).
         2. `CheckedItems`: `!IsPantryStapleExcluded && IsChecked` (ordered by `CheckedAt` desc, `DisplayName` asc).
         3. `ExcludedPantryItems`: `IsPantryStapleExcluded == true` (ordered by `DisplayName` asc, `Dimension` asc).
       - Format every item's `UnitLabel` and `FormattedAmount` using `CulinaryMath.ScaleAndConvert(item.Quantity, item.MaxQuantity, item.Unit, item.CustomUnitName, 1m, UnitSystem.Original)` so fractional US amounts (`3/8`, `1 1/2`) and clean metric amounts (`750`) match Recipe views identically!
   - **Manual / Ad-Hoc Item Creation & Editing (`AddItemAsync`, `UpdateItemAsync`):**
     - Validates `DisplayName` (`1..200` chars), `Quantity > 0` when provided, and `MaxQuantity >= Quantity` when provided (`DomainValidationException` otherwise).
     - **Catalog Auto-Binding:** If `FoodItemId == null`, searches `FoodItems` (by `NormalizedName`) and `FoodItemAliases` (by `NormalizedAlias`) for an exact case-insensitive match to `DisplayName.Trim().ToLowerInvariant()`. If matched, binds `FoodItemId` (and uses `matchedFoodItem.Name` if `DisplayName` matched an alias or preserves `DisplayName.Trim()`). Manually added ad-hoc items ALWAYS enter active items (`IsPantryStapleExcluded = false`), even if the user manually types `"Kosher Salt"`, because explicitly typing an item means the shopper wants to buy it!
     - If `ConsolidateIfCompatible == true`, searches existing active unchecked items (`!IsPantryStapleExcluded && !IsChecked`) on the list with a matching consolidation key (`ShoppingConsolidator.BuildConsolidationKey`), merges the two via `ShoppingConsolidator.Consolidate`, and updates that item in place; otherwise appends a new `ShoppingListItem`.
   - **Check / Uncheck & Clear Checked (`SetItemCheckedAsync`, `ClearCheckedItemsAsync`):**
     - Resolves current actor `(Guid? userId, string? username)` safely via `IUserContext` + `dbContext.Users` when authenticated.
     - Setting `IsChecked = true` sets `CheckedAt = UtcNow`, `CheckedByUserId = userId`, `CheckedByDisplayName = username` (and ensures `IsPantryStapleExcluded = false`). Setting `IsChecked = false` clears `CheckedAt`, `CheckedByUserId`, and `CheckedByDisplayName`.
     - `ClearCheckedItemsAsync` removes all items on the list where `!IsPantryStapleExcluded && IsChecked`, returning `ClearCheckedItemsResultDto(removedCount)`.
   - **1-Tap Pantry Staple Promotion (`PromotePantryStapleItemAsync`):**
     - Finds the item (`404` if missing). If `IsPantryStapleExcluded == true`, checks if an active unchecked item (`!IsPantryStapleExcluded && !IsChecked`) on the same list already shares the same consolidation key:
       - If yes: merges quantities + sources into that active item via `ShoppingConsolidator.Consolidate` and deletes the excluded row, returning the merged active `ShoppingListItemDto`.
       - If no: sets `IsPantryStapleExcluded = false`, `IsChecked = false`, saves, and returns the promoted `ShoppingListItemDto`.
   - **Meal Plan Date-Range & Single-Recipe Generation (`PreviewGenerateFromMealPlanAsync`, `GenerateFromMealPlanAsync`, `AddRecipeIngredientsAsync`):**
     - Validates `EndDate >= StartDate` and `(EndDate.DayNumber - StartDate.DayNumber) <= 92`.
     - Queries all `MealPlanEntries` in `[StartDate, EndDate]` (plus `IsOnDeck` entries for weeks overlapping `[firstMonday..lastMonday]` when `IncludeOnDeck == true`).
     - Counts & filters out:
       - `IgnoredLeftoversCount`: entries where `IsLeftovers || EntryType == MealPlanEntryType.Leftovers`.
       - `IgnoredNotesCount`: entries where `!IsLeftovers && EntryType == MealPlanEntryType.FreeformNote` (or `RecipeId == null`).
     - Inspects all existing `ShoppingListItemSources` across the target `ShoppingList` (`Items.SelectMany(i => i.Sources)`) to identify `AlreadyImportedMeals` (`MealPlanEntryId`s already present on the list):
       - Under `DuplicateMealPolicy.SkipAlreadyImported`: excludes those `MealPlanEntry` rows from extraction (`SkippedDuplicateMealsCount = alreadyImported.Count`).
       - Under `DuplicateMealPolicy.ReplaceAlreadyImported` (during execute): removes existing `ShoppingListItemSource` rows matching those `MealPlanEntryId`s, subtracting `ContributedQuantity` from their parent `ShoppingListItem` (or deleting the `ShoppingListItem` if its remaining sources drop to `0` and its remaining quantity `<= 0`), before re-importing the fresh scaled ingredients!
     - Loads all referenced `Recipe` graphs (including `Ingredients.FoodItem` and recursively any `LinkedRecipeId` sub-recipes when `IncludeSubRecipes == true`), filters out `IsOptional` ingredients when `IncludeOptionalIngredients == false`, scales quantities by `targetServings / baseServings` (multiplying sub-recipe batch quantities recursively, guarded against cycles with a `HashSet<Guid>`), and runs `ShoppingConsolidator.Consolidate`.
     - Merges each `ConsolidatedShoppingBucket` into the target `ShoppingList`:
       - `shouldExcludeAsPantryStaple = bucket.IsPantryStaple && !(PromotePantryFoodItemIds?.Contains(bucket.FoodItemId ?? Guid.Empty) ?? false)`.
       - If a matching row (same consolidation key and matching `IsPantryStapleExcluded` state, and `!IsChecked`) already exists on the list, merges quantities and appends new `ShoppingListItemSource` rows; otherwise creates a new `ShoppingListItem` + `ShoppingListItemSource` rows.

3. **`Data/Services/FoodCatalogService.cs` (`DeleteFoodItemAsync` update):**
   - Before removing `foodItem` (once verified unreferenced by `RecipeIngredients`), query `dbContext.ShoppingListItems.Where(i => i.FoodItemId == id)` and set `item.FoodItemId = null` so `DeleteBehavior.Restrict` never blocks deleting an unused catalog item.

---

## 3. API Endpoints (`API/Controllers/ShoppingListsController.cs`)

`ShoppingListsController` uses `[ApiController]`, `[Authorize]`, and relative `[Route("shopping-lists")]` (mounted under `/api` by `Program.cs`) and remains a thin HTTP adapter translating `DomainValidationException` $\rightarrow$ `400`, `NotFound` / `null` $\rightarrow$ `404`, and `ConflictException` $\rightarrow$ `409`.

| Verb | Route (`/api/...`) | Request Body / Query | Response | Error Cases |
| :--- | :--- | :--- | :--- | :--- |
| `GET` | `/shopping-lists` | — | `200 OK` (`IReadOnlyList<ShoppingListSummaryDto>`) | `401` |
| `GET` | `/shopping-lists/{id:guid}` | — | `200 OK` (`ShoppingListDetailDto`) | `401`, `404` |
| `POST` | `/shopping-lists` | Body: `CreateShoppingListRequest` | `201 Created` (`ShoppingListDetailDto`) | `400`, `401`, `409` |
| `PUT` | `/shopping-lists/{id:guid}` | Body: `UpdateShoppingListRequest` | `200 OK` (`ShoppingListDetailDto`) | `400`, `401`, `404`, `409` |
| `DELETE` | `/shopping-lists/{id:guid}` | — | `204 No Content` | `401`, `404` |
| `POST` | `/shopping-lists/{id:guid}/items` | Body: `CreateShoppingListItemRequest` | `201 Created` (`ShoppingListItemDto`) | `400`, `401`, `404` |
| `PUT` | `/shopping-lists/{id:guid}/items/{itemId:guid}` | Body: `UpdateShoppingListItemRequest` | `200 OK` (`ShoppingListItemDto`) | `400`, `401`, `404` |
| `PATCH` | `/shopping-lists/{id:guid}/items/{itemId:guid}/checked` | Body: `SetShoppingListItemCheckedRequest` | `200 OK` (`ShoppingListItemDto`) | `401`, `404` |
| `POST` | `/shopping-lists/{id:guid}/items/{itemId:guid}/promote-staple` | — | `200 OK` (`ShoppingListItemDto`) | `401`, `404` |
| `DELETE` | `/shopping-lists/{id:guid}/items/{itemId:guid}` | — | `204 No Content` | `401`, `404` |
| `DELETE` | `/shopping-lists/{id:guid}/items/checked` | — | `200 OK` (`ClearCheckedItemsResultDto`) | `401`, `404` |
| `POST` | `/shopping-lists/{id:guid}/generate/meal-plan/preview` | Body: `GenerateFromMealPlanRequest` | `200 OK` (`ShoppingListGenerationPreviewDto`) | `400`, `401`, `404` |
| `POST` | `/shopping-lists/{id:guid}/generate/meal-plan` | Body: `GenerateFromMealPlanRequest` | `200 OK` (`ShoppingListGenerationResultDto`) | `400`, `401`, `404` |
| `POST` | `/shopping-lists/{id:guid}/generate/recipe` | Body: `AddRecipeToShoppingListRequest` | `200 OK` (`ShoppingListGenerationResultDto`) | `400`, `401`, `404` |

---

## 4. Angular Frontend Design (`Client/`)

### 4.1 Models & Services (`src/app/core/`)

1. **`src/app/core/models/shopping.models.ts`**:
   - Define `MeasurementDimension = 'Volume' | 'Weight' | 'Discrete' | 'Unspecified'`, `ShoppingListSourceKind = 'Manual' | 'MealPlanEntry' | 'SingleRecipe'`, `DuplicateMealPolicy = 'SkipAlreadyImported' | 'ReplaceAlreadyImported'`, and TypeScript interfaces matching all Shopping List DTOs and Request payloads.

2. **`src/app/core/services/shopping/shopping.service.ts` (+ `shopping.service.spec.ts`)**:
   - Decorated with `@Service()` from `@angular/core` using `inject(HttpClient)` and `apiUrl('/shopping-lists...')`:
     - `getShoppingLists(): Observable<ShoppingListSummaryDto[]>`
     - `getShoppingListById(id: string): Observable<ShoppingListDetailDto>`
     - `createShoppingList(request: CreateShoppingListRequest): Observable<ShoppingListDetailDto>`
     - `updateShoppingList(id: string, request: UpdateShoppingListRequest): Observable<ShoppingListDetailDto>`
     - `deleteShoppingList(id: string): Observable<void>`
     - `addItem(listId: string, request: CreateShoppingListItemRequest): Observable<ShoppingListItemDto>`
     - `updateItem(listId: string, itemId: string, request: UpdateShoppingListItemRequest): Observable<ShoppingListItemDto>`
     - `setItemChecked(listId: string, itemId: string, isChecked: boolean): Observable<ShoppingListItemDto>`
     - `promotePantryStaple(listId: string, itemId: string): Observable<ShoppingListItemDto>`
     - `deleteItem(listId: string, itemId: string): Observable<void>`
     - `clearCheckedItems(listId: string): Observable<ClearCheckedItemsResultDto>`
     - `previewGenerateFromMealPlan(listId: string, request: GenerateFromMealPlanRequest): Observable<ShoppingListGenerationPreviewDto>`
     - `generateFromMealPlan(listId: string, request: GenerateFromMealPlanRequest): Observable<ShoppingListGenerationResultDto>`
     - `addRecipeToList(listId: string, request: AddRecipeToShoppingListRequest): Observable<ShoppingListGenerationResultDto>`

### 4.2 Feature Components & Routes (`src/app/features/shopping/`)

Every component strictly uses its own dedicated kebab-case directory with external `.ts`, `.html`, `.scss`, and `.spec.ts` files, OnPush signals (`signal()`, `computed()`), native `@if` / `@for`, Material Symbols Outlined icons, and Material 3 CSS variables (`--mat-sys-*`).

1. **`features/shopping/shopping-workspace/` (`ShoppingWorkspaceComponent`, route `/shopping`)**:
   - **Signals & State:**
     - `lists = signal<ShoppingListSummaryDto[]>([])`
     - `activeList = signal<ShoppingListDetailDto | null>(null)`
     - `isLoading = signal<boolean>(true)`
     - `pantryDrawerExpanded = signal<boolean>(false)`
     - `checkedSectionExpanded = signal<boolean>(true)`
     - Supports `?listId=<guid>` query parameter selection (defaulting to the first pinned/available list, e.g., seeded `"Weekly Groceries"`).
   - **Header & List Switcher:**
     - `.page-title` (`"Shopping Lists"`), horizontal list selector pills on desktop / `<mat-select>` switcher on mobile (`<= 768px`), **"+ New List"** inline prompt/dialog trigger, list `more_vert` menu (**Rename / Toggle Pin**, **Delete List** via `ConfirmDialogComponent`).
     - Primary **"Generate from Meal Plan"** button (`<mat-icon>calendar_add_on</mat-icon>`) opening `GenerateShoppingListDialogComponent`.
   - **Quick Item Adder Bar (`.quick-add-bar`):**
     - Fast single-row input (`placeholder="Add item (e.g., Paper Towels, Avocados)..."`, `aria-label="Quick add shopping item"`) + optional detailed item dialog button (`openItemDialog()`) + **"Add"** button (`Enter` submits immediately and clears input).
   - **Clean Flat Active Checklist (`.active-items-card`):**
     - Thumb-friendly `.shopping-item-row`:
       - `<mat-checkbox>` with **optimistic signal update** (immediately moves item between `activeItems` and `checkedItems` locally before `PATCH .../checked` resolves, reverting + showing snackbar on network error).
       - Quantity + unit badge (`.item-qty-badge`, e.g., `3/8 cup`, `1 1/2 lb`, `3`) + `.item-display-name` + optional `.item-notes`.
       - **Recipe Provenance Badges (`.provenance-chip`):** Renders `item.sources` labels (`"Tue: Chicken Tacos"`, `"On-Deck: Enchiladas"`), linking to `['/recipes', source.recipeId]` when `recipeId` is present.
       - Edit icon button (`openItemDialog(item)`) & Delete icon button.
   - **Collapsed Pantry Staples Tray (`.pantry-staples-drawer`):**
     - Shown whenever `activeList()!.excludedPantryItems.length > 0`: header button `"Verify Pantry Staples (Excluded) (N)"` (`[attr.aria-expanded]="pantryDrawerExpanded()"`).
     - Expanding lists each excluded staple (*Kosher Salt*, *Extra-Virgin Olive Oil*, *Water*, etc.) with its consolidated recipe amount, provenance chips, and a 1-tap **"Add to List"** button (`button.promote-staple-btn`, `<mat-icon>add_shopping_cart</mat-icon>`) calling `promotePantryStaple(...)`.
   - **Collapsible Checked Off Section (`.checked-items-section`):**
     - Shown whenever `activeList()!.checkedItems.length > 0`: toggle header `"Checked Off (N)"` + **"Clear Checked Items"** button (`button.clear-checked-btn`) calling `clearCheckedItems(...)`.

2. **`features/shopping/generate-shopping-list-dialog/` (`GenerateShoppingListDialogComponent`)**:
   - Accepts optional `{ targetShoppingListId?: string; startDate?: string; endDate?: string }` via `MAT_DIALOG_DATA`.
   - Controls: Target Shopping List `<mat-select>`, `Start Date` & `End Date` inputs (defaulting to current Monday–Sunday week via `normalizeToMondayIso` / `addDaysIso`), **`Include On-Deck Meals`** checkbox (default `true`), `Include Sub-Recipes` checkbox (default `true`), `Include Optional Ingredients` checkbox (default `true`).
   - Automatically fetches `previewGenerateFromMealPlan(...)` whenever inputs change:
     - Displays summary metrics (`N meals`, `M active items`, `P pantry staples excluded`, plus note when `Leftovers` or `FreeformNotes` were automatically ignored).
     - **Idempotent Sync Warning Banner (`.duplicate-meals-banner`):** Displayed when `preview.alreadyImportedMeals.length > 0`, offering a radio/toggle between `SkipAlreadyImported` (*"Skip already-imported meals so ingredients aren't doubled"* — default) and `ReplaceAlreadyImported` (*"Re-sync & replace quantities for overlapping meals"*).
     - Expandable preview of excluded pantry staples with checkboxes to pre-promote specific staples right during generation if desired.
   - Confirm button **"Generate Items"** executes `generateFromMealPlan(...)` and closes with the `ShoppingListGenerationResultDto`.

3. **`features/shopping/add-recipe-to-list-dialog/` (`AddRecipeToListDialogComponent`)**:
   - Accepts `{ recipeId: string; recipeTitle: string; targetServings: number; hasSubRecipes: boolean }` via `MAT_DIALOG_DATA`.
   - Lets the user select an existing Shopping List (or type a quick new list name inline), adjust `targetServings`, toggle `includeSubRecipes` / `includeOptionalIngredients`, and click **"Add Ingredients"** (`shoppingService.addRecipeToList(...)`).

4. **`features/shopping/shopping-item-dialog/` (`ShoppingItemDialogComponent`)**:
   - Dialog for creating a detailed item or editing an existing `ShoppingListItemDto` (`displayName`, `quantity`, `maxQuantity`, `unit` from `MEASUREMENT_UNITS`, `customUnitName`, and `notes`).

5. **Cross-Feature Shell, Meal Plan & Recipe Detail Touchpoints:**
   - `AppShellComponent`: Add `Shop` link (`/shopping`, icon `shopping_cart`) right after `Meal Plan` in `.desktop-nav` and `.mobile-bottom-nav`. In `app-shell.component.scss`, update `@media (max-width: 768px) .mobile-bottom-nav .mobile-tab` to `flex: 1 1 0; min-width: 0; padding: 0.25rem 0.1rem; font-size: 0.6rem; white-space: nowrap;` so all 6 tabs (`Cookbook`, `Meal Plan`, `Shop`, `Ingest`, `Matcher`, `Catalog`) render crisply inside `360px` with zero horizontal overflow.
   - `MealPlanCalendarComponent`: Add `<button type="button" mat-stroked-button (click)="openGenerateShoppingListDialog()" class="shop-week-btn" aria-label="Generate shopping list for this week"><mat-icon>shopping_cart_checkout</mat-icon><span class="btn-text">Shop Week</span></button>` in `.calendar-top-actions` (when `viewMode() === 'week'`), opening `GenerateShoppingListDialogComponent` pre-filled with `activeWeekStart()` to `addDaysIso(activeWeekStart(), 6)` and showing a snackbar with `"Open List"` navigation to `/shopping?listId=<id>`.
   - `RecipeDetailComponent`: Add `<button mat-stroked-button (click)="openAddToShoppingListDialog()" aria-label="Add ingredients to shopping list"><mat-icon>add_shopping_cart</mat-icon><span>Add to List</span></button>` in `.detail-top-actions` right after `Plan Meal`, opening `AddRecipeToListDialogComponent` with `recipe()!.id`, `recipe()!.title`, and `targetServings()`, showing a snackbar with `"View List"` navigation to `/shopping?listId=<id>`.

---

## 5. Playwright E2E Test Scenarios (`Tests/Client.E2ETests/ShoppingListsE2ETests.cs`)

Create `Tests/Client.E2ETests/ShoppingListsE2ETests.cs` inheriting from `E2ETestBase`:

- `[TestMethod] ShoppingList_GenerateFromMealPlan_ConsolidatesUnits_ExcludesLeftoversAndPantryStaples_AndPreventsDuplicates()`:
  - Registers/logs in, creates two recipes sharing compatible volume ingredients (`2 tbsp Extra-Virgin Olive Oil` + `1/4 cup Extra-Virgin Olive Oil` — pantry staple; `2 cloves Garlic` + `3 cloves Garlic`) AND incompatible dimension ingredients (`2 Whole Yellow Onion` in Recipe A + `1 cup Yellow Onion` diced in Recipe B), schedules Recipe A on Monday (`8` target servings vs `4` base = `2x` scale), puts Recipe B in **On-Deck**, schedules a `Leftovers` entry on Wednesday and a `FreeformNote` (`"Friday Pizza"`) on Friday.
  - Clicks **"Shop Week"** on `/meal-plan`, verifies the preview counts (`2` eligible meals, `1` Leftovers ignored, `1` Note ignored), generates into `"Weekly Groceries"`, and navigates to `/shopping`.
  - Verifies scaled & consolidated `Garlic` (`7 cloves` = `2*2 + 3`) with provenance chips for both Monday's meal and On-Deck's meal, verifies `Yellow Onion` renders two distinct dimension rows (`4` whole and `1 cup`), verifies neither `Leftovers` nor `"Friday Pizza"` contributed items, verifies `Extra-Virgin Olive Oil` (`1/2 cup` = `2*2 tbsp + 1/4 cup`) is routed into the collapsed **"Verify Pantry Staples (Excluded)"** drawer, taps **"Add to List"** on `Extra-Virgin Olive Oil` to promote it onto the active checklist, and re-opens **Generate from Meal Plan** for the same week to verify the **Already Imported** warning prevents doubling quantities.
- `[TestMethod] ShoppingList_MultipleNamedLists_QuickAdd_Edit_CheckOffAndClear()`:
  - Creates a new shopping list `"Costco Run"`, quick-adds `"Paper Towels"` and `"Rotisserie Chicken"`, edits `"Paper Towels"` quantity/notes via `ShoppingItemDialogComponent`, checks off `"Paper Towels"`, verifies it moves into **"Checked Off (1)"**, clicks **"Clear Checked Items"**, and verifies `"Paper Towels"` is removed while `"Rotisserie Chicken"` remains active (and `"Weekly Groceries"` stays independent).
- `[TestMethod] ShoppingList_AddFromRecipeDetail_AndMobileViewport_360px_RendersCleanly()`:
  - Opens a recipe on `/recipes/:id`, scales servings, clicks **"Add to List"**, adds ingredients to `"Weekly Groceries"`, then resizes viewport to `360x640`, navigates via the `Shop` tab in `nav.mobile-bottom-nav`, verifies `Shop` has `.active-tab`, asserts zero horizontal overflow (`documentElement.scrollWidth <= window.innerWidth`) across all 6 mobile bottom tabs and the shopping list workspace, quick-adds `"Limes"` at `360px`, and checks it off cleanly.

---

## 6. Step-by-Step Execution Checklist (For Implementation Agent)

- [x] **Task 1: Backend Core & Data Layer**
  - Create `Core/Shopping/` (`ShoppingEnums.cs`, `ShoppingConsolidator.cs`, `ShoppingListModels.cs`, `IShoppingListService.cs`).
  - Create `Data/Entities/` (`ShoppingList.cs`, `ShoppingListItem.cs`, `ShoppingListItemSource.cs`) and EF configurations in `Data/Configurations/`; update `FoodItem.cs`, `AppDbContext.cs`, `FoodCatalogService.DeleteFoodItemAsync`, and `HouseholdCatalogSeeder.SeedAsync`.
  - Implement `Data/Services/ShoppingListService.cs` and register `IShoppingListService` in `DataServiceCollectionExtensions.cs`.
  - Ensure local SQL Server is running, then generate, apply, and format the EF Core migration:
    ```powershell
    .\scripts\ensure-local-infra.ps1
    dotnet ef migrations add AddSmartShoppingLists --project Data/App.Data.csproj
    dotnet ef database update --project Data/App.Data.csproj
    dotnet csharpier format Data/Migrations
    ```
  - Write & run unit tests in `Tests/Core.UnitTests` (`ShoppingConsolidatorTests.cs`, `ShoppingListServiceTests.cs`):
    ```powershell
    dotnet test --project Tests/Core.UnitTests/App.Core.UnitTests.csproj
    ```

- [x] **Task 2: API Controllers & Integration Tests**
  - Create thin HTTP controller `API/Controllers/ShoppingListsController.cs`.
  - Update `Tests/API.IntegrationTests/OpenApiIntegrationTests.cs` and create `Tests/API.IntegrationTests/ShoppingListsIntegrationTests.cs` (covering seeded `"Weekly Groceries"` list, shopping list CRUD, ad-hoc item auto-catalog binding & compatible consolidation, check/uncheck attribution & clear-checked, meal-plan preview & generation with scaling + sub-recipes + `Leftovers`/`FreeformNote` exclusion + pantry staple separation & 1-tap promotion + idempotent `SkipAlreadyImported` vs `ReplaceAlreadyImported`, single-recipe generation, validation `400`/`409`s, and unauthenticated `401`s).
  - Run integration tests until 100% green:
    ```powershell
    dotnet test --project Tests/API.IntegrationTests/App.API.IntegrationTests.csproj
    ```

- [x] **Task 3: Angular Client & Unit Tests**
  - Create `shopping.models.ts` and `@Service()` `ShoppingService` (+ `shopping.service.spec.ts`).
  - Create dedicated per-component directories `shopping-workspace/`, `generate-shopping-list-dialog/`, `add-recipe-to-list-dialog/`, and `shopping-item-dialog/` under `Client/src/app/features/shopping/` (each with separate `.ts`, `.html`, `.scss`, and `.spec.ts` files).
  - Wire `/shopping` route in `app.routes.ts`, add `Shop` navigation item in `AppShellComponent` (+ `360px` 6-tab CSS & `.spec.ts`), add **"Shop Week"** button in `MealPlanCalendarComponent` (+ `.spec.ts`), and add **"Add to List"** button in `RecipeDetailComponent` (+ `.spec.ts`).
  - Run frontend lint, production build, and unit tests in `Client/` until 100% green:
    ```powershell
    # In Client/:
    npm run lint
    npm run build:production
    npm test -- --watch=false
    ```

- [x] **Task 4: Playwright E2E Verification & Final Formatting**
  - Implement `Tests/Client.E2ETests/ShoppingListsE2ETests.cs` covering all scenarios in Section 5 (and verify existing E2E suites including `NavigationAndThemeE2ETests` and `MealPlanE2ETests` still pass at `360px` width).
  - Build production Angular bundle (`npm run build:production` in `Client/`) and run Playwright E2E tests:
    ```powershell
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
