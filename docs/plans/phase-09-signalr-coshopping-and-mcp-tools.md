# Phase 09: SignalR Real-Time Co-Shopping & Milestone 3 MCP Tools

> **Branch:** `feature/phase-09-signalr-coshopping-and-mcp-tools`
> **Status:** `[x]` Completed (`[ ]` Not Started | `[~]` In Progress | `[x]` Completed)
> **PRD Reference:** `docs/prd.md` — Sections 1, 2, 4.3 (Groups 3 & 4), 4.5.4, 5, and 6 (Milestone 3, Slice 3 of 3)
> **Target Execution:** Run `/execute-phase` to execute this plan step-by-step.

---

## 1. Scope & Objectives

This phase completes **Milestone 3** of **LocalRoast** by delivering **SignalR Real-Time Multi-Phone Co-Shopping** (`/api/hubs/shopping`) with optimistic UI reconciliation, live connection status indicators, and automatic grocery-store reconnect recovery, alongside expanding the embedded **Model Context Protocol (`/api/mcp`)** server with all 7 **Meal Planning & Shopping List MCP Tools**:

1. **Real-Time Multi-Phone Co-Shopping (`IShoppingRealtimeNotifier`, `ShoppingHub`, `/api/hubs/shopping`):**
   - Expose an authenticated SignalR hub at `/api/hubs/shopping` (`[Authorize]` via `DualBearerAuthenticationHandler`, which already supports both `Authorization: Bearer <jwt>` on `/negotiate` and `?access_token=<jwt>` on WebSocket/SSE upgrades while rejecting `lr_pat_...` tokens).
   - Introduce `IShoppingRealtimeNotifier` in `App.Core.Shopping` (zero HTTP/SignalR dependencies) and inject it optionally into `ShoppingListService` (`Data/Services/ShoppingListService.cs`) so **every** shopping list or item mutation—whether triggered from Phone A, Phone B, REST API, or an external MCP AI assistant—automatically broadcasts typed `ShoppingListUpdated` (list-scoped group `shopping-list:{id:D}`) and `ShoppingListsCatalogChanged` (workspace group `shopping-workspace`) events after `SaveChangesAsync`.
   - Build Angular `ShoppingRealtimeService` (`@microsoft/signalr`) with automatic backoff reconnection, group join/leave management on list switching, full state reconciliation on reconnect after cellular/Wi-Fi dead zones, flicker-free local optimistic check/uncheck preservation (`< 50ms`), and an accessible **Live Sync Indicator** pill (`Live Sync` / `Reconnecting…` / `Offline`) in `ShoppingWorkspaceComponent`.

2. **Milestone 3 Embedded MCP Tools (`IMcpMealAndShoppingService`, `MealPlanMcpTools`, `ShoppingMcpTools` on `/api/mcp`):**
   - Expand `/api/mcp` (authenticated via SHA-256 `PersonalAccessToken`s with `CreatedBy` / `ModifiedBy` / `CheckedByUserId` attributed to the token owner via `McpUserContextAccessor`) from 6 tools to **13 tools** by adding:
     - **Meal Planning Tools:**
       1. `get_meal_plan` (`ReadOnly = true`): Read scheduled calendar entries and weekly `On-Deck` queue for a date range (defaulting to the current Monday–Sunday week when dates are omitted).
       2. `plan_meals`: Batch-schedule one or more `Recipe` (by `recipeId`, `recipeSlug`, or `recipeTitle`), `Leftovers`, or `FreeformNote` entries onto dated `Breakfast | Lunch | Dinner | SnackPrep` slots or the weekly `On-Deck` tray.
       3. `remove_meal_plan_entry`: Delete/clear a planned entry or reschedule/move it to a new date/slot or `On-Deck` tray.
     - **Shopping List Tools:**
       4. `get_shopping_lists` (`ReadOnly = true`): Inspect household shopping lists alongside `ActiveItems`, `CheckedItems`, and `ExcludedPantryItems` (filtering optionally by `shoppingListId` or case-insensitive `listName`).
       5. `generate_shopping_list_from_meal_plan`: Consolidate scaled meal-plan ingredients (excluding `Leftovers` and `FreeformNote` entries and separating `IsPantryStaple` items) into a target shopping list (resolving or creating `listName` when needed).
       6. `upsert_shopping_list_items`: Batch-add or update shopping list items with natural unit string normalization (`"gallon"`, `"cups"`, `"lb"`, `"packs"` via `RecipeDraftNormalizer.NormalizeUnit`), automatic `FoodItem` matching, compatible unit consolidation, and auto-list creation (e.g., *"Add paper towels and 2 gallons of milk to Costco list"*).
       7. `set_shopping_list_item_checked`: Check or uncheck an item by `itemId` or case-insensitive `itemName` on a target list with actor attribution and live SignalR broadcast.
   - Update `PersonalAccessTokensPanelComponent` copy/tool badges so users minting PATs see all 4 MCP capability domains (*Cookbook*, *Ingestion*, *Meal Planning*, *Shopping Lists*).

### Database Migration Note
- **No new EF Core migration is required in Phase 09.** All persistent entities (`MealPlanEntry`, `MealPlanTemplate`, `ShoppingList`, `ShoppingListItem`, `ShoppingListItemSource`, `PersonalAccessToken`) were created and migrated in Phases 06–08.

### Explicitly Out of Scope (Deferred to Milestone 4)
- Hands-Free `Cook Mode` (`navigator.wakeLock`, inline step ingredient chips, floating multi-timer bar), `CookLog` ratings/tweak notes, *"Forgotten Favorites"* UI filter refinement, `FoodItem` duplicate-merge management UI, and 1-click local LLM kitchen helpers (*Suggest Ingredient Substitution* & *Draft Weekly Meal Plan*).

### 1.1 File Inventory (Modify & Create)

#### Existing Backend & Test Files to Modify
- `Core/Shopping/ShoppingEnums.cs` — Add `ShoppingRealtimeEventType` enum.
- `Core/Shopping/ShoppingListModels.cs` — Add `ShoppingListRealtimeEventDto` and `ShoppingListsCatalogChangedDto`.
- `Core/Mcp/McpModels.cs` — Add Milestone 3 Meal Plan & Shopping List MCP request/response records.
- `Data/Services/ShoppingListService.cs` — Inject optional `IShoppingRealtimeNotifier? realtimeNotifier = null` and broadcast list + catalog updates after every mutating persistence operation.
- `Data/DataServiceCollectionExtensions.cs` — Register `IMcpMealAndShoppingService`.
- `API/Mcp/McpServiceCollectionExtensions.cs` — Register `.WithTools<MealPlanMcpTools>()` and `.WithTools<ShoppingMcpTools>()`.
- `API/Program.cs` — Register `AddSignalR()` (with `JsonStringEnumConverter`), register `IShoppingRealtimeNotifier` $\rightarrow$ `SignalRShoppingRealtimeNotifier`, and map `app.MapHub<ShoppingHub>("/api/hubs/shopping").RequireAuthorization().ExcludeFromDescription()`.
- `Tests/API.IntegrationTests/App.API.IntegrationTests.csproj` — Add `<PackageReference Include="Microsoft.AspNetCore.SignalR.Client" Version="10.0.11" />`.
- `Tests/API.IntegrationTests/McpServerIntegrationTests.cs` — Extend tool inventory assertion to all 13 MCP tools and add end-to-end Milestone 3 Meal Plan + Shopping List MCP tool integration tests.

#### New Backend & Backend Test Files to Create
- `Core/Shopping/IShoppingRealtimeNotifier.cs`
- `Core/Mcp/IMcpMealAndShoppingService.cs`
- `Data/Services/McpMealAndShoppingService.cs`
- `API/Hubs/ShoppingHubGroups.cs`
- `API/Hubs/IShoppingClient.cs`
- `API/Hubs/ShoppingHub.cs`
- `API/Hubs/SignalRShoppingRealtimeNotifier.cs`
- `API/Mcp/MealPlanMcpTools.cs`
- `API/Mcp/ShoppingMcpTools.cs`
- `Tests/Core.UnitTests/McpMealAndShoppingServiceTests.cs`
- `Tests/Core.UnitTests/ShoppingRealtimeNotificationTests.cs`
- `Tests/API.IntegrationTests/ShoppingHubIntegrationTests.cs`

#### Existing Frontend Files to Modify (`Client/`)
- `Client/package.json` (+ `Client/package-lock.json`) — Add `"@microsoft/signalr": "^10.0.11"`.
- `Client/proxy.conf.json` — Add `"/api/hubs"` rule with `"ws": true` before `"/api"` for local dev WebSocket proxying.
- `Client/src/app/core/models/shopping.models.ts` — Add `ShoppingRealtimeEventType`, `RealtimeSyncState`, `ShoppingListRealtimeEventDto`, and `ShoppingListsCatalogChangedDto`.
- `Client/src/app/features/shopping/shopping-workspace/shopping-workspace.component.ts` (+ `.html`, `.scss`, `.spec.ts`) — Wire `ShoppingRealtimeService`, live sync status pill (`Live Sync` / `Reconnecting…` / `Offline`), list group switching, optimistic pending-item protection, and live catalog pill count sync.
- `Client/src/app/features/mcp-tokens/personal-access-tokens-panel/personal-access-tokens-panel.component.html` (+ `.scss`, `.spec.ts`) — Update intro description and display capability chips for Cookbook, Ingestion, Meal Planning, and Shopping Lists.

#### New Frontend & E2E Files to Create
- `Client/src/app/core/services/shopping/shopping-realtime.service.ts` (+ `.spec.ts`)
- `Tests/Client.E2ETests/SignalRCoShoppingAndMcpE2ETests.cs`

---

## 2. Domain & Service Design (`Core/` & `Data/`)

### 2.1 Shopping Realtime Contracts (`Core/Shopping/`)

1. **Append to `Core/Shopping/ShoppingEnums.cs`** (`namespace App.Core.Shopping`):
   ```csharp
   [JsonConverter(typeof(JsonStringEnumConverter))]
   public enum ShoppingRealtimeEventType
   {
       ListCreated = 0,
       ListUpdated = 1,
       ListDeleted = 2,
       ItemAdded = 3,
       ItemUpdated = 4,
       ItemCheckedChanged = 5,
       ItemPromoted = 6,
       ItemDeleted = 7,
       CheckedItemsCleared = 8,
       ItemsGenerated = 9,
   }
   ```

2. **Append to `Core/Shopping/ShoppingListModels.cs`** (`namespace App.Core.Shopping`):
   ```csharp
   public record ShoppingListRealtimeEventDto(
       ShoppingRealtimeEventType EventType,
       Guid ShoppingListId,
       ShoppingListDetailDto? ShoppingList,
       ShoppingListItemDto? ChangedItem,
       Guid? DeletedItemId,
       Guid? ActorUserId,
       string? ActorDisplayName,
       DateTimeOffset OccurredAt
   );

   public record ShoppingListsCatalogChangedDto(
       ShoppingRealtimeEventType EventType,
       Guid AffectedShoppingListId,
       IReadOnlyList<ShoppingListSummaryDto> Lists,
       DateTimeOffset OccurredAt
   );
   ```

3. **Create `Core/Shopping/IShoppingRealtimeNotifier.cs`** (`namespace App.Core.Shopping`):
   ```csharp
   namespace App.Core.Shopping;

   public interface IShoppingRealtimeNotifier
   {
       Task NotifyListChangedAsync(
           ShoppingListRealtimeEventDto listEvent,
           ShoppingListsCatalogChangedDto catalogEvent,
           CancellationToken cancellationToken = default
       );
   }
   ```

### 2.2 Milestone 3 MCP Contracts (`Core/Mcp/`)

1. **Append to `Core/Mcp/McpModels.cs`** (`namespace App.Core.Mcp` — add `using App.Core.MealPlans;` and `using App.Core.Shopping;`):
   ```csharp
   public sealed record McpGetMealPlanRequest(
       string? StartDate = null,
       string? EndDate = null,
       bool IncludeOnDeck = true
   );

   public sealed record McpPlanMealItemInput(
       string? EntryType = "Recipe",
       bool IsOnDeck = false,
       string? ScheduledDate = null,
       string? WeekStartDate = null,
       string? Slot = null,
       Guid? RecipeId = null,
       [MaxLength(220)] string? RecipeSlug = null,
       [MaxLength(200)] string? RecipeTitle = null,
       [Range(typeof(decimal), "0.25", "999")] decimal? TargetServings = null,
       Guid? SourceMealPlanEntryId = null,
       [MaxLength(200)] string? CustomTitle = null,
       [MaxLength(500)] string? Notes = null
   );

   public sealed record McpPlanMealsRequest(IReadOnlyList<McpPlanMealItemInput> Entries);

   public sealed record McpPlanMealsResultDto(
       int PlannedCount,
       IReadOnlyList<MealPlanEntryDto> Entries
   );

   public sealed record McpRemoveMealPlanEntryRequest(
       Guid EntryId,
       string? RescheduleToDate = null,
       string? RescheduleToSlot = null,
       bool MoveToOnDeck = false,
       string? WeekStartDate = null
   );

   public sealed record McpRemoveOrRescheduleMealResultDto(
       string Action,
       Guid EntryId,
       bool Removed,
       MealPlanEntryDto? RescheduledEntry
   );

   public sealed record McpGetShoppingListsRequest(
       Guid? ShoppingListId = null,
       [MaxLength(150)] string? ListName = null,
       bool IncludeItems = true
   );

   public sealed record McpShoppingListsResponseDto(
       IReadOnlyList<ShoppingListSummaryDto> Summaries,
       IReadOnlyList<ShoppingListDetailDto> Lists
   );

   public sealed record McpGenerateShoppingListRequest(
       [Required] string StartDate,
       [Required] string EndDate,
       Guid? ShoppingListId = null,
       [MaxLength(150)] string? ListName = null,
       bool CreateListIfMissing = true,
       bool IncludeOnDeck = true,
       bool IncludeSubRecipes = true,
       bool IncludeOptionalIngredients = true,
       string DuplicatePolicy = "SkipAlreadyImported",
       IReadOnlyList<Guid>? PromotePantryFoodItemIds = null
   );

   public sealed record McpShoppingListItemUpsertInput(
       [Required, MaxLength(200)] string DisplayName,
       Guid? ItemId = null,
       decimal? Quantity = null,
       decimal? MaxQuantity = null,
       [MaxLength(60)] string? Unit = null,
       Guid? FoodItemId = null,
       [MaxLength(300)] string? Notes = null,
       bool ConsolidateIfCompatible = true
   );

   public sealed record McpUpsertShoppingListItemsRequest(
       IReadOnlyList<McpShoppingListItemUpsertInput> Items,
       Guid? ShoppingListId = null,
       [MaxLength(150)] string? ListName = null,
       bool CreateListIfMissing = true
   );

   public sealed record McpUpsertShoppingItemsResultDto(
       ShoppingListDetailDto ShoppingList,
       IReadOnlyList<ShoppingListItemDto> UpsertedItems
   );

   public sealed record McpSetShoppingItemCheckedRequest(
       bool IsChecked = true,
       Guid? ShoppingListId = null,
       [MaxLength(150)] string? ListName = null,
       Guid? ItemId = null,
       [MaxLength(200)] string? ItemName = null
   );
   ```

2. **Create `Core/Mcp/IMcpMealAndShoppingService.cs`** (`namespace App.Core.Mcp`):
   ```csharp
   using App.Core.MealPlans;
   using App.Core.Shopping;

   namespace App.Core.Mcp;

   public interface IMcpMealAndShoppingService
   {
       Task<MealPlanRangeResponseDto> GetMealPlanAsync(
           McpGetMealPlanRequest request,
           CancellationToken cancellationToken = default
       );

       Task<McpPlanMealsResultDto> PlanMealsAsync(
           McpPlanMealsRequest request,
           CancellationToken cancellationToken = default
       );

       Task<McpRemoveOrRescheduleMealResultDto> RemoveOrRescheduleMealPlanEntryAsync(
           McpRemoveMealPlanEntryRequest request,
           CancellationToken cancellationToken = default
       );

       Task<McpShoppingListsResponseDto> GetShoppingListsAsync(
           McpGetShoppingListsRequest request,
           CancellationToken cancellationToken = default
       );

       Task<ShoppingListGenerationResultDto> GenerateShoppingListFromMealPlanAsync(
           McpGenerateShoppingListRequest request,
           CancellationToken cancellationToken = default
       );

       Task<McpUpsertShoppingItemsResultDto> UpsertShoppingListItemsAsync(
           McpUpsertShoppingListItemsRequest request,
           CancellationToken cancellationToken = default
       );

       Task<ShoppingListItemDto> SetShoppingListItemCheckedAsync(
           McpSetShoppingItemCheckedRequest request,
           CancellationToken cancellationToken = default
       );
   }
   ```

### 2.3 Service Business Logic & Invariants (`Data/Services/`)

1. **`Data/Services/ShoppingListService.cs` Realtime Broadcast Integration:**
   - Extend primary constructor:
     ```csharp
     public sealed class ShoppingListService(
         AppDbContext dbContext,
         IUserContext? userContext = null,
         TimeProvider? timeProvider = null,
         IShoppingRealtimeNotifier? realtimeNotifier = null
     ) : IShoppingListService
     ```
   - Add helper `BroadcastChangeAsync`:
     - If `realtimeNotifier is null`, return immediately (zero overhead in unit tests that do not supply a notifier).
     - Otherwise, using `CancellationToken.None` (so post-commit broadcasts always complete even if the caller's HTTP request closes):
       - Resolve `(actorUserId, actorDisplayName)` via `ResolveActorAsync(CancellationToken.None)`.
       - If `detail is null && eventType != ShoppingRealtimeEventType.ListDeleted`, load `detail = await GetShoppingListByIdAsync(shoppingListId, CancellationToken.None)`.
       - Load `lists = await GetShoppingListsAsync(CancellationToken.None)`.
       - Build `ShoppingListRealtimeEventDto` and `ShoppingListsCatalogChangedDto` with `OccurredAt = (timeProvider ?? TimeProvider.System).GetUtcNow()` and await `realtimeNotifier.NotifyListChangedAsync(listEvent, catalogEvent, CancellationToken.None)`.
   - Invoke `BroadcastChangeAsync` after `SaveChangesAsync` in:
     - `CreateShoppingListAsync` $\rightarrow$ `ListCreated` (reuse already-loaded `ShoppingListDetailDto`)
     - `UpdateShoppingListAsync` $\rightarrow$ `ListUpdated` (reuse already-loaded `ShoppingListDetailDto`)
     - `DeleteShoppingListAsync` $\rightarrow$ `ListDeleted` (`detail: null`)
     - `AddItemAsync` $\rightarrow$ `ItemAdded` (`changedItem: itemDto`)
     - `UpdateItemAsync` $\rightarrow$ `ItemUpdated` (`changedItem: itemDto`)
     - `SetItemCheckedAsync` $\rightarrow$ `ItemCheckedChanged` (`changedItem: itemDto`)
     - `PromotePantryStapleItemAsync` $\rightarrow$ `ItemPromoted` (`changedItem: itemDto`)
     - `DeleteItemAsync` $\rightarrow$ `ItemDeleted` (`deletedItemId: itemId`)
     - `ClearCheckedItemsAsync` $\rightarrow$ `CheckedItemsCleared` (only when `removedCount > 0`)
     - `GenerateFromMealPlanAsync` $\rightarrow$ `ItemsGenerated` (reuse `result.ShoppingList`)
     - `AddRecipeIngredientsAsync` $\rightarrow$ `ItemsGenerated` (reuse `result.ShoppingList`)

2. **`Data/Services/McpMealAndShoppingService.cs`** (`public sealed class McpMealAndShoppingService(AppDbContext dbContext, IMealPlanService mealPlanService, IShoppingListService shoppingListService, TimeProvider? timeProvider = null) : IMcpMealAndShoppingService`):
   - Register `services.AddScoped<IMcpMealAndShoppingService, McpMealAndShoppingService>();` in `Data/DataServiceCollectionExtensions.cs`.
   - **Strict ISO Date Parsing (`ParseDateOnly`):**
     - Uses `DateOnly.TryParseExact(raw.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)`; throws `DomainValidationException($"Invalid date '{raw}'. Expected YYYY-MM-DD.")` on failure.
   - **`GetMealPlanAsync`:**
     - When both `StartDate` and `EndDate` are null/whitespace: computes `today = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime)`, `monday = MealPlanDateHelper.NormalizeToMonday(today)`, and queries `[monday, monday.AddDays(6)]`.
     - When `StartDate` is provided and `EndDate` is omitted: defaults `endDate = startDate.AddDays(6)`.
     - When `EndDate` is provided without `StartDate`: throws `DomainValidationException("Provide startDate when endDate is specified.")`.
     - Delegates to `mealPlanService.GetRangeAsync(startDate, endDate, request.IncludeOnDeck, cancellationToken)`.
   - **`PlanMealsAsync`:**
     - Validates `request.Entries is { Count: >= 1 and <= 50 }` (`DomainValidationException` otherwise).
     - Parses `EntryType` (`Recipe` default, `Leftovers`, `FreeformNote` — case-insensitive; also maps `"Note"` / `"Freeform"` to `FreeformNote`).
     - Parses `Slot` (`Breakfast`, `Lunch`, `Dinner`, `SnackPrep` — case-insensitive; also maps `"Snack"`, `"Prep"`, `"Snack / Prep"` to `MealSlot.SnackPrep`; defaults to `MealSlot.Dinner` when `!item.IsOnDeck` and `Slot` is omitted).
     - When `item.IsOnDeck == true` and `WeekStartDate` is omitted: defaults `WeekStartDate` to `ScheduledDate` (if provided) or the current week's Monday.
     - Resolves `RecipeId`: if `item.RecipeId` is null and `RecipeSlug` or `RecipeTitle` is provided, looks up non-archived `Recipes.AsNoTracking()` by exact normalized slug first, then exact case-insensitive title (`Title.ToLower() == norm`); throws `NotFoundException` if a slug/title was explicitly provided and no recipe matched.
     - Invokes `mealPlanService.CreateEntryAsync` sequentially so `SortOrder` within multi-dish slots increments deterministically.
   - **`RemoveOrRescheduleMealPlanEntryAsync`:**
     - Validates `request.EntryId != Guid.Empty` (`DomainValidationException` otherwise).
     - If `request.MoveToOnDeck`: loads existing entry (`NotFoundException` if null), parses optional `WeekStartDate` (defaulting to `existing.WeekStartDate`), calls `mealPlanService.MoveEntryAsync(request.EntryId, new MoveMealPlanEntryRequest(IsOnDeck: true, WeekStartDate: targetWeek))`, and returns `Action = "RescheduledToOnDeck"`.
     - Else if `!string.IsNullOrWhiteSpace(request.RescheduleToDate)`: loads existing entry (`NotFoundException` if null), parses `targetDate`, parses optional `RescheduleToSlot` (defaulting to `existing.Slot ?? MealSlot.Dinner`), calls `mealPlanService.MoveEntryAsync(request.EntryId, new MoveMealPlanEntryRequest(IsOnDeck: false, ScheduledDate: targetDate, Slot: targetSlot))`, and returns `Action = "Rescheduled"`.
     - Else: calls `mealPlanService.DeleteEntryAsync(request.EntryId, cancellationToken)`, throws `NotFoundException("Meal plan entry not found.")` if `false`, and returns `Action = "Removed", Removed = true`.
   - **`ResolveTargetShoppingListIdAsync(Guid? shoppingListId, string? listName, bool createIfMissing, CancellationToken ct)`:**
     - If `shoppingListId.HasValue`: verifies existence in `dbContext.ShoppingLists.AsNoTracking()` (`NotFoundException` if missing) and returns `shoppingListId.Value`.
     - If `!string.IsNullOrWhiteSpace(listName)`: checks `NormalizedName == listName.Trim().ToLowerInvariant()`. If found, returns `Id`. If missing and `createIfMissing == true`, creates via `shoppingListService.CreateShoppingListAsync(new CreateShoppingListRequest(listName.Trim(), IsPinned: false), ct)` and returns `created.Id`; else throws `NotFoundException($"Shopping list '{listName.Trim()}' not found.")`.
     - If both omitted: returns first list from `shoppingListService.GetShoppingListsAsync(ct)`, or creates `"Weekly Groceries"` (`IsPinned: true`) if zero lists exist.
   - **`GetShoppingListsAsync`:**
     - Filters summaries by `ShoppingListId` or case-insensitive `ListName` (`NotFoundException` when an explicit filter matches nothing). Loads full `ShoppingListDetailDto` for each matched list when `IncludeItems == true`.
   - **`GenerateShoppingListFromMealPlanAsync`:**
     - Parses `StartDate`, `EndDate`, and `DuplicatePolicy` (`SkipAlreadyImported` vs `ReplaceAlreadyImported`, `DomainValidationException` on invalid policy), resolves target list ID, and delegates to `shoppingListService.GenerateFromMealPlanAsync`.
   - **`UpsertShoppingListItemsAsync`:**
     - Validates `request.Items is { Count: >= 1 and <= 100 }`.
     - Resolves target list ID, normalizes each `item.Unit` via `RecipeDraftNormalizer.NormalizeUnit(item.Unit)`, updates (`UpdateItemAsync`) when `item.ItemId.HasValue` or adds (`AddItemAsync`) otherwise, and returns the refreshed `ShoppingListDetailDto` + affected `ShoppingListItemDto`s.
   - **`SetShoppingListItemCheckedAsync`:**
     - Validates `request.ItemId.HasValue || !string.IsNullOrWhiteSpace(request.ItemName)` (`DomainValidationException` otherwise).
     - Resolves target list ID (`createIfMissing: false`). When `ItemId` is omitted, loads `ShoppingListDetailDto` and matches `ItemName.Trim()` case-insensitively against `DisplayName` or `FoodItemName` (prioritizing `ActiveItems` when `IsChecked == true` and `CheckedItems` when `IsChecked == false`, then exact match across remaining buckets, then single unambiguous substring match; throws `NotFoundException` if not found or `DomainValidationException` if ambiguous across multiple distinct items).
     - Delegates to `shoppingListService.SetItemCheckedAsync`.

---

## 3. SignalR Hub & MCP Endpoints (`API/`)

### 3.1 SignalR Co-Shopping Hub (`API/Hubs/`)

| Transport / Hub | Route | Auth Policy / Scheme | Client-to-Server Methods | Server-to-Client Events (`IShoppingClient`) |
| :--- | :--- | :--- | :--- | :--- |
| **SignalR Hub** (`WebSockets`, `ServerSentEvents`, `LongPolling`) | `/api/hubs/shopping` | `[Authorize]` (`LocalJwtScheme` via `DualBearerAuthenticationHandler`: accepts `Bearer <jwt>` header & `?access_token=<jwt>`, rejects `lr_pat_...` and anonymous `401`) | `JoinShoppingList(Guid shoppingListId)`<br>`LeaveShoppingList(Guid shoppingListId)`<br>*(Auto-joins `"shopping-workspace"` on connect)* | `ShoppingListUpdated(ShoppingListRealtimeEventDto)`<br>`ShoppingListsCatalogChanged(ShoppingListsCatalogChangedDto)` |

- **`API/Hubs/ShoppingHubGroups.cs`**: `WorkspaceGroup = "shopping-workspace"` and `ForList(Guid id) => $"shopping-list:{id:D}"`.
- **`API/Hubs/IShoppingClient.cs`**: Typed SignalR client contract (`ShoppingListUpdated`, `ShoppingListsCatalogChanged`).
- **`API/Hubs/ShoppingHub.cs`**: `[Authorize] public sealed class ShoppingHub : Hub<IShoppingClient>`.
- **`API/Hubs/SignalRShoppingRealtimeNotifier.cs`**: Implements `IShoppingRealtimeNotifier` via `IHubContext<ShoppingHub, IShoppingClient>`, catching and logging unexpected send errors so committed DB operations never fail on transient socket issues.
- **`API/Program.cs`**:
  - `builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));`
  - `builder.Services.AddScoped<IShoppingRealtimeNotifier, SignalRShoppingRealtimeNotifier>();`
  - `app.MapHub<ShoppingHub>("/api/hubs/shopping").RequireAuthorization().ExcludeFromDescription();`

### 3.2 Embedded MCP Tools (`API/Mcp/MealPlanMcpTools.cs` & `API/Mcp/ShoppingMcpTools.cs`)

All MCP tools are mounted on `/api/mcp` (`McpAuthorizationPolicy` / `PersonalAccessTokenAuthenticationHandler`), decorated with `[McpServerToolType]` and `[McpServerTool(Name = "...")]`, accept `ClaimsPrincipal? user = null, CancellationToken cancellationToken = default`, and execute inside `McpToolResultHelper.ExecuteAsync(user, ...)`:

| MCP Tool Name | `ReadOnly` | Key Parameters | Service Delegation |
| :--- | :---: | :--- | :--- |
| `get_meal_plan` | `true` | `startDate?`, `endDate?`, `includeOnDeck = true` | `IMcpMealAndShoppingService.GetMealPlanAsync` |
| `plan_meals` | `false` | `entries` (`McpPlanMealItemInput[]`) | `IMcpMealAndShoppingService.PlanMealsAsync` |
| `remove_meal_plan_entry` | `false` | `entryId`, `rescheduleToDate?`, `rescheduleToSlot?`, `moveToOnDeck = false`, `weekStartDate?` | `IMcpMealAndShoppingService.RemoveOrRescheduleMealPlanEntryAsync` |
| `get_shopping_lists` | `true` | `shoppingListId?`, `listName?`, `includeItems = true` | `IMcpMealAndShoppingService.GetShoppingListsAsync` |
| `generate_shopping_list_from_meal_plan` | `false` | `startDate`, `endDate`, `shoppingListId?`, `listName?`, `createListIfMissing = true`, `includeOnDeck = true`, `includeSubRecipes = true`, `includeOptionalIngredients = true`, `duplicatePolicy = "SkipAlreadyImported"` | `IMcpMealAndShoppingService.GenerateShoppingListFromMealPlanAsync` |
| `upsert_shopping_list_items` | `false` | `items` (`McpShoppingListItemUpsertInput[]`), `shoppingListId?`, `listName?`, `createListIfMissing = true` | `IMcpMealAndShoppingService.UpsertShoppingListItemsAsync` |
| `set_shopping_list_item_checked` | `false` | `isChecked = true`, `shoppingListId?`, `listName?`, `itemId?`, `itemName?` | `IMcpMealAndShoppingService.SetShoppingListItemCheckedAsync` |

---

## 4. Angular Frontend Design (`Client/`)

### 4.1 Dependencies, Proxy & Models (`Client/`)

1. **`Client/package.json`**:
   - Install `@microsoft/signalr` (`^10.0.11`).
   - **Bundle Budget Note:** `ShoppingRealtimeService` is only injected inside lazy-loaded `ShoppingWorkspaceComponent` (`/shopping`), keeping `@microsoft/signalr` inside the lazy shopping chunk so the initial app bundle stays well below the `500kB` warning budget.

2. **`Client/proxy.conf.json`**:
   - Add WebSocket proxy rule for SignalR hubs above `"/api"`:
     ```json
     {
       "/api/hubs": {
         "target": "http://localhost:5041",
         "secure": false,
         "changeOrigin": true,
         "ws": true,
         "logLevel": "silent"
       },
       "/api": {
         "target": "http://localhost:5041",
         "secure": false,
         "changeOrigin": true,
         "logLevel": "silent"
       },
       "/HealthCheck.svc": {
         "target": "http://localhost:5041",
         "secure": false,
         "changeOrigin": true,
         "logLevel": "silent"
       }
     }
     ```

3. **`Client/src/app/core/models/shopping.models.ts`**:
   - Add:
     ```ts
     export type RealtimeSyncState = 'disconnected' | 'connecting' | 'live' | 'reconnecting';

     export type ShoppingRealtimeEventType =
       | 'ListCreated'
       | 'ListUpdated'
       | 'ListDeleted'
       | 'ItemAdded'
       | 'ItemUpdated'
       | 'ItemCheckedChanged'
       | 'ItemPromoted'
       | 'ItemDeleted'
       | 'CheckedItemsCleared'
       | 'ItemsGenerated';

     export interface ShoppingListRealtimeEventDto {
       eventType: ShoppingRealtimeEventType;
       shoppingListId: string;
       shoppingList: ShoppingListDetailDto | null;
       changedItem: ShoppingListItemDto | null;
       deletedItemId: string | null;
       actorUserId: string | null;
       actorDisplayName: string | null;
       occurredAt: string;
     }

     export interface ShoppingListsCatalogChangedDto {
       eventType: ShoppingRealtimeEventType;
       affectedShoppingListId: string;
       lists: ShoppingListSummaryDto[];
       occurredAt: string;
     }
     ```

### 4.2 `ShoppingRealtimeService` (`src/app/core/services/shopping/shopping-realtime.service.ts`)

- Decorated with `@Service()` from `@angular/core`, injecting `AuthService` via `inject(AuthService)`.
- Signals & RxJS streams:
  - `readonly syncState = signal<RealtimeSyncState>('disconnected');`
  - `readonly listUpdated$ = new Subject<ShoppingListRealtimeEventDto>();`
  - `readonly catalogChanged$ = new Subject<ShoppingListsCatalogChangedDto>();`
  - `readonly reconnected$ = new Subject<void>();`
- Connection & Group Management:
  - `async connect(): Promise<void>`:
    - No-op if `!this.authService.token()` or connection is already `Connected` / `Connecting` / `Reconnecting`.
    - Builds `HubConnection` via `new HubConnectionBuilder().withUrl(apiUrl('/hubs/shopping'), { accessTokenFactory: () => this.authService.token() ?? '' }).withAutomaticReconnect([0, 1000, 2000, 5000, 10000, 15000]).configureLogging(LogLevel.None).build()`.
    - Registers `"ShoppingListUpdated"` $\rightarrow$ `this.listUpdated$.next(payload)` and `"ShoppingListsCatalogChanged"` $\rightarrow$ `this.catalogChanged$.next(payload)`.
    - Registers `onreconnecting` $\rightarrow$ `syncState.set('reconnecting')`, `onreconnected` $\rightarrow$ `syncState.set('live')`, re-joins `currentListId` if set, and emits `reconnected$.next()`, `onclose` $\rightarrow$ `syncState.set('disconnected')`.
    - Starts connection inside `try / catch`: on success sets `syncState.set('live')` and joins `currentListId` if already selected; on failure sets `syncState.set('disconnected')` cleanly without throwing unhandled rejections.
  - `async watchList(listId: string | null): Promise<void>`:
    - Tracks `previousListId` and updates `currentListId = listId`.
    - If `connection?.state === HubConnectionState.Connected`: invokes `LeaveShoppingList(previousListId)` (if previous != next) and `JoinShoppingList(listId)` (if `listId` is non-null).
  - `async disconnect(): Promise<void>`:
    - Clears `currentListId`, stops and disposes `connection`, sets `syncState.set('disconnected')`.

### 4.3 Feature Component Updates (`src/app/features/`)

1. **`features/shopping/shopping-workspace/` (`ShoppingWorkspaceComponent` — `.ts`, `.html`, `.scss`, `.spec.ts`)**:
   - Implements `OnInit, OnDestroy` (`DestroyRef` / subscription cleanup; calls `shoppingRealtime.disconnect()` on `ngOnDestroy`).
   - On `ngOnInit()`: calls `void this.shoppingRealtime.connect()` and subscribes to:
     - `shoppingRealtime.listUpdated$`:
       - When `event.shoppingListId === this.activeList()?.id`:
         - If `event.eventType === 'ListDeleted'`: handled via `catalogChanged$` fallback selection.
         - If `event.shoppingList`: reconciles `activeList` immediately while preserving any in-flight optimistic item toggle IDs (`pendingCheckItemIds: Map<string, boolean>`) so rapid local taps never flicker.
     - `shoppingRealtime.catalogChanged$`:
       - Updates `this.lists.set(event.lists)` immediately (keeping desktop pill counts `(N)` and mobile `<mat-select>` counts accurate across all mutations!).
       - If `this.activeList()` is null and `event.lists.length > 0`, selects `event.lists[0].id`.
       - If `this.activeList()` was deleted (`!event.lists.some(l => l.id === active.id)`), automatically switches to `event.lists[0]?.id` (or sets `activeList.set(null)` when empty).
     - `shoppingRealtime.reconnected$`:
       - Refreshes `shoppingService.getShoppingLists()` and `refreshActiveList()` so any changes made while the phone was offline in a store dead zone reconcile immediately upon reconnect.
   - Calls `void this.shoppingRealtime.watchList(id)` inside `selectList(id)` (and `watchList(null)` when no lists exist).
   - Updates local summary count helper on optimistic `toggleItemChecked` so even before the HTTP response returns (`< 50ms`), the active list pill badge `(N)` updates optimistically!
   - **Live SignalR Sync Indicator UI (`.sync-status-pill` in `.header-action-buttons`):**
     - Accessible status badge (`role="status"`, `aria-live="polite"`):
       - `syncState() === 'live'`: `<span class="sync-status-pill live"><span class="sync-dot"></span><span>Live Sync</span></span>`
       - `syncState() === 'connecting' || syncState() === 'reconnecting'`: `<span class="sync-status-pill reconnecting"><mat-icon class="spin-icon">sync</mat-icon><span>Syncing…</span></span>`
       - `syncState() === 'disconnected'`: `<button type="button" class="sync-status-pill offline" (click)="reconnectRealtime()" aria-label="Reconnect live sync"><mat-icon>cloud_off</mat-icon><span>Offline</span></button>`
     - Styled strictly with Material 3 CSS tokens (`--mat-sys-*`, `--app-success*`, `--app-warning*`) and flex-wrapped cleanly so `360px` mobile viewports have zero horizontal overflow.

2. **`features/mcp-tokens/personal-access-tokens-panel/` (`PersonalAccessTokensPanelComponent` — `.html`, `.scss`, `.spec.ts`)**:
   - Update `.panel-intro` copy to highlight recipes, meal plans, and live shopping lists, and render a compact `.mcp-capabilities-row` showing the 4 MCP tool groups (`Cookbook & Search`, `Recipe Ingestion`, `Meal Planning`, `Live Shopping Lists`) without breaking `360px` mobile layout or existing PAT tests.

---

## 5. Playwright E2E Test Scenarios (`Tests/Client.E2ETests/SignalRCoShoppingAndMcpE2ETests.cs`)

Create `Tests/Client.E2ETests/SignalRCoShoppingAndMcpE2ETests.cs` extending `E2ETestBase` with `[TestClass]`:

1. **`[TestMethod] SignalR_TwoHouseholdPhones_LiveCoShopping_SyncsAddCheckPromoteClearAndListCreation()`**:
   - Registers User A (`Page` — Phone 1) and opens a second isolated browser context via `await Browser.NewContextAsync()` to register User B (`pageB` — Phone 2).
   - Navigates both browsers to `/shopping` (`"Weekly Groceries"`), asserting both render `.sync-status-pill.live` (`"Live Sync"`).
   - **Live Add:** User A quick-adds `"Organic Blueberries"` and `"Sourdough Loaf"`; asserts User B's checklist renders both items automatically **without page reload** and updates list pill count `(2)`.
   - **Live Check with Attribution:** User B checks off `"Organic Blueberries"`; asserts User A's screen moves `"Organic Blueberries"` into `.checked-items-section` (`"Checked Off (1)"`) and displays `"Checked off by <userB>"` in real time.
   - **Live Clear Checked & List Creation:** User A clicks `"Clear Checked Items"` (asserting `.checked-items-section` disappears on User B's screen), then User A creates a new list `"Costco Run"` (asserting `"Costco Run"` pill appears automatically in User B's `.desktop-list-pills`).

2. **`[TestMethod] McpMilestone3Tools_PlanMeals_GenerateShoppingList_UpsertAndCheckItems_UpdatesBrowserLive()`**:
   - Logs in User A, mints a Personal Access Token (`lr_pat_...`) via Account Settings $\rightarrow$ `MCP & API Tokens`, then navigates the browser to `/shopping` (`"Weekly Groceries"`).
   - Connects a real `McpClient` (`HttpClientTransport` / `StreamableHttp`) to `${ServerUrl}/api/mcp` with `Authorization: Bearer <pat>`.
   - Calls `mcpClient.ListToolsAsync()` and asserts all **13** MCP tools are advertised (6 Milestone 2 + 7 Milestone 3 tools).
   - Calls `save_structured_recipe` (`publishImmediately = true`, base 4 servings, with `Garlic` 3 cloves + `Kosher Salt` 1 tsp pantry staple).
   - Calls `plan_meals` to schedule the recipe (scaled to `8` servings on Monday Dinner), a `Leftovers` entry on Tuesday Lunch, and a `FreeformNote` (`"Friday Takeout"`) on Friday Dinner; verifies via `get_meal_plan` and reschedules Friday's note via `remove_meal_plan_entry` (`rescheduleToDate`), then removes Tuesday's leftovers entry via `remove_meal_plan_entry`.
   - Calls `generate_shopping_list_from_meal_plan` for the week into `"Weekly Groceries"`; asserts the open browser `/shopping` page **live-updates via SignalR** to show `"Garlic"` (`6 cloves`), excludes `"Friday Takeout"`, and places `"Kosher Salt"` in `.pantry-staples-drawer` (`Verify Pantry Staples (Excluded) (1)`).
   - Calls `upsert_shopping_list_items` (`"Paper Towels"`, quantity `2`) and `set_shopping_list_item_checked` (`itemName: "Paper Towels"`, `isChecked: true`); asserts `"Paper Towels"` appears and moves into `"Checked Off (1)"` live in the open browser!

3. **`[TestMethod] SignalRCoShopping_MobileViewport_360px_RendersLiveSyncBadgeAndRecoversFromOffline()`**:
   - Resizes viewport to `360x640`, logs in, navigates via mobile bottom nav `Shop` tab to `/shopping`.
   - Asserts `.sync-status-pill.live` (`"Live Sync"`) is visible and `document.documentElement.scrollWidth <= window.innerWidth` (zero horizontal overflow at `360px`).
   - Toggles `await Context.SetOfflineAsync(true)` / `await Context.SetOfflineAsync(false)` (and clicks `.sync-status-pill.offline` if in disconnected state), verifying the client recovers back to `"Live Sync"` and still checks off items cleanly with zero horizontal overflow at `360px`.

---

## 6. Step-by-Step Execution Checklist (For Implementation Agent)

- [x] **Task 1: Backend Core, Realtime Notifier Contract & Milestone 3 MCP Service**
  - Update `Core/Shopping/ShoppingEnums.cs`, `Core/Shopping/ShoppingListModels.cs`, and create `Core/Shopping/IShoppingRealtimeNotifier.cs`.
  - Update `Data/Services/ShoppingListService.cs` to inject optional `IShoppingRealtimeNotifier? realtimeNotifier = null` and broadcast list + catalog updates after every mutating persistence call.
  - Update `Core/Mcp/McpModels.cs`, create `Core/Mcp/IMcpMealAndShoppingService.cs`, implement `Data/Services/McpMealAndShoppingService.cs`, and register `IMcpMealAndShoppingService` in `Data/DataServiceCollectionExtensions.cs`.
  - Write unit tests in `Tests/Core.UnitTests/ShoppingRealtimeNotificationTests.cs` and `Tests/Core.UnitTests/McpMealAndShoppingServiceTests.cs` (covering date-range defaults, recipe slug/title resolution, leftovers/pantry-staple shopping math, reschedule vs. remove, list auto-creation, unit normalization, item-name check matching, and realtime broadcast invocations).
  - Run unit tests until 100% green:
    ```powershell
    dotnet test --project Tests/Core.UnitTests/App.Core.UnitTests.csproj
    ```

- [x] **Task 2: SignalR Hub, MCP Tool Endpoints & API Integration Tests**
  - Create `API/Hubs/ShoppingHubGroups.cs`, `API/Hubs/IShoppingClient.cs`, `API/Hubs/ShoppingHub.cs`, and `API/Hubs/SignalRShoppingRealtimeNotifier.cs`.
  - Create `API/Mcp/MealPlanMcpTools.cs` and `API/Mcp/ShoppingMcpTools.cs`, and register them in `API/Mcp/McpServiceCollectionExtensions.cs`.
  - Update `API/Program.cs` to configure `AddSignalR()` (with `JsonStringEnumConverter`), register `IShoppingRealtimeNotifier`, and map `/api/hubs/shopping`.
  - Add `Microsoft.AspNetCore.SignalR.Client` (`10.0.11`) to `Tests/API.IntegrationTests/App.API.IntegrationTests.csproj`.
  - Create `Tests/API.IntegrationTests/ShoppingHubIntegrationTests.cs` (verifying `401` on anonymous/PAT connections, `?access_token=<jwt>` connection auth, group isolation across two `HubConnection` clients, and live broadcast on REST + MCP shopping mutations) and update `Tests/API.IntegrationTests/McpServerIntegrationTests.cs` (asserting all 13 MCP tools and end-to-end Milestone 3 tool execution + PAT `CreatedBy`/`CheckedBy` attribution).
  - Run integration tests until 100% green:
    ```powershell
    dotnet test --project Tests/API.IntegrationTests/App.API.IntegrationTests.csproj
    ```

- [x] **Task 3: Angular SignalR Co-Shopping Client, Live Sync UI & Unit Tests**
  - Install `@microsoft/signalr` (`^10.0.11`) in `Client/`, update `Client/proxy.conf.json` with `/api/hubs` (`ws: true`), and extend `Client/src/app/core/models/shopping.models.ts`.
  - Create `Client/src/app/core/services/shopping/shopping-realtime.service.ts` (+ `.spec.ts`).
  - Update `ShoppingWorkspaceComponent` (`.ts`, `.html`, `.scss`, `.spec.ts`) with live SignalR subscription, list group switching, optimistic pending-toggle protection, reconnect reconciliation, and the accessible `.sync-status-pill` indicator.
  - Update `PersonalAccessTokensPanelComponent` (`.html`, `.scss`, `.spec.ts`) to showcase all 4 MCP capability groups.
  - Run frontend lint, production build, and unit tests until 100% green:
    ```powershell
    # In Client/:
    npm run lint
    npm run build:production
    npm test -- --watch=false
    ```

- [x] **Task 4: Playwright E2E Verification & Final Formatting**
  - Create `Tests/Client.E2ETests/SignalRCoShoppingAndMcpE2ETests.cs` implementing all 3 scenarios from Section 5.
  - Build backend + production Angular bundle and run Playwright E2E tests:
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
