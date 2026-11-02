# Phase 07: Meal Planning Calendar, "On-Deck" Weekly Tray & Reusable Week Templates

> **Branch:** `feature/phase-07-meal-plan-calendar-and-templates`
> **Status:** `[ ]` Not Started (`[ ]` Not Started | `[~]` In Progress | `[x]` Completed)
> **PRD Reference:** `docs/prd.md` — Sections 1, 2, 3, 4.4 (4.4.1–4.4.4), 5, and 6 (Milestone 3, Slice 1 of 3)
> **Target Execution:** Run `/execute-phase` to execute this plan step-by-step.

---

## 1. Scope & Objectives

This phase begins **Milestone 3** of **LocalRoast** by delivering the **Multi-Slot Weekly & Monthly Meal Planning Calendar**, real-world meal entry types (`Recipe` with custom serving overrides, `Leftovers` with zero-ingredient shopping semantics, and `FreeformNote`), the flexible **"On-Deck This Week"** tray with drag-and-drop scheduling and 1-tap **"Cook Tonight"**, **Reusable Week Templates**, and quick **"Plan Meal"** scheduling from Recipe Detail:

1. **Multi-Slot Household Meal Planning Domain (`MealPlanEntry`, `IMealPlanService`, `/api/meal-plans`):**
   - Model global household meal plan entries across Monday–Sunday weeks (`WeekStartDate` normalized to ISO Monday) supporting both dated slot entries (`IsOnDeck = false`, `ScheduledDate`, `Slot` in `Breakfast | Lunch | Dinner | SnackPrep`) and flexible weekly **On-Deck** tray entries (`IsOnDeck = true`, `ScheduledDate = null`, `Slot = null`).
   - Support **Multi-Dish Slots** via deterministic `SortOrder` sequencing inside every `(ScheduledDate, Slot)` bucket and inside each week's `On-Deck` tray.
   - Support all three real-world entry types from `docs/prd.md` Section 4.4.2:
     - `Recipe`: Links a saved `Recipe` (`RecipeId`) with customizable `TargetServings` (defaulting to `Recipe.BaseServings`).
     - `Leftovers`: Persists `IsLeftovers = true` (guaranteeing zero ingredient contribution when Milestone 3 Shopping List generation runs in Phase 08) and optionally links back to a prior `SourceMealPlanEntryId` or `RecipeId` (auto-labeling `"Leftovers: <Title>"` when `CustomTitle` is omitted) or stands alone.
     - `FreeformNote`: Quick text entry (`CustomTitle`, e.g., `"Date Night / Eating Out"`, `"Order Thai"`) with optional `Notes`.
   - Support moving/reordering entries across slots and between **On-Deck** and dated slots (`POST /api/meal-plans/entries/{id}/move`), 1-tap **"Cook Tonight"** promotion (`POST /api/meal-plans/entries/{id}/cook-tonight`), week clearing, and both **Weekly** (`GET /api/meal-plans/week`) and **Date-Range / Monthly** (`GET /api/meal-plans`) queries.

2. **Reusable Week Templates (`MealPlanTemplate`, `MealPlanTemplateItem`, `IMealPlanTemplateService`, `/api/meal-plan-templates`):**
   - Snapshot any planned week (including optional `On-Deck` items) into a named, reusable `MealPlanTemplate` (`DayOffset` `0..6` relative to Monday + `IsOnDeck` items), or create/update/delete templates directly.
   - Stamp any saved template onto any target calendar week via `POST /api/meal-plan-templates/{id}/apply` supporting both **`Merge`** mode (idempotently appending non-duplicate items without erasing existing meals) and **`Replace`** mode (clearing the target week before stamping).

3. **Responsive Angular Meal Plan Workspace, CDK Drag-and-Drop & Recipe Integration (`Client/`):**
   - Add **Meal Plan** (`/meal-plan`) to both desktop top navigation and `360px` mobile bottom navigation in `AppShellComponent`.
   - Build `MealPlanCalendarComponent` featuring:
     - **Weekly Board View (Desktop):** 7-day Monday–Sunday multi-slot board alongside a persistent **"On-Deck This Week"** sidebar using `@angular/cdk/drag-drop` (`cdkDropListGroup`) for smooth drag-and-drop between On-Deck and day slots, multi-dish reordering, 1-tap **"Cook Tonight"** on On-Deck cards, and accessible menu actions (**Edit**, **Schedule Leftovers (+1 Day)**, **Move to On-Deck**, **Remove**).
     - **Monthly Overview (`Month` toggle):** Full Monday–Sunday month grid displaying color-coded meal pills per day, overflow counts, On-Deck week badges, and 1-click drill-down into any week.
     - **Mobile (`360px`) Responsive Layout:** Vertical day-by-day agenda accordion (`Today` and populated days expanded) paired with a collapsible **"On-Deck This Week"** drawer and thumb-friendly 1-tap **"Cook Tonight"** / **"Add Meal"** actions with zero horizontal overflow.
   - Build `MealPlanEntryDialogComponent` (reusable from both `/meal-plan` and the new **"Plan Meal"** button on `RecipeDetailComponent`) and `MealPlanTemplatesDialogComponent`.

### Explicitly Out of Scope (Deferred to Phases 08, 09 & Milestone 4)
- Smart Shopping Lists, unit-consolidated Meal-Plan-to-Shopping-List generation, Pantry Staples tray, and Custom Store Aisle Layouts — delivered in **Phase 08**.
- `SignalR` real-time co-shopping hub (`/api/hubs/shopping`) and Milestone 3 MCP tools (`get_meal_plan`, `plan_meals`, `remove_meal_plan_entry`, and shopping list MCP tools) — delivered in **Phase 09**.
- Hands-Free `Cook Mode`, `CookLog` ratings/history, `FoodItem` merge, and 1-click local LLM *"Draft Weekly Meal Plan"* helper — delivered in **Milestone 4**.

### 1.1 File Inventory (Modify & Create)

#### Existing Backend & Test Files to Modify
- `Data/Entities/Recipe.cs` — Add `MealPlanEntries` and `MealPlanTemplateItems` navigation collections.
- `Data/AppDbContext.cs` — Add `DbSet<MealPlanEntry> MealPlanEntries`, `DbSet<MealPlanTemplate> MealPlanTemplates`, and `DbSet<MealPlanTemplateItem> MealPlanTemplateItems`.
- `Data/DataServiceCollectionExtensions.cs` — Register `IMealPlanService` and `IMealPlanTemplateService`.
- `Data/Services/RecipeService.cs` — In `DeleteRecipeAsync`, gracefully detach any `MealPlanEntries` and `MealPlanTemplateItems` referencing the deleted `RecipeId` (preserving their display titles as `FreeformNote` / `Leftovers` text so `DeleteBehavior.Restrict` never blocks recipe deletion or loses historical calendar notes).
- `Tests/API.IntegrationTests/OpenApiIntegrationTests.cs` — Assert `/meal-plans` and `/meal-plan-templates` are documented in `/api/openapi/v1.json`.

#### New Backend & Backend Test Files to Create
- `Core/MealPlans/MealPlanEnums.cs`
- `Core/MealPlans/MealPlanDateHelper.cs`
- `Core/MealPlans/MealPlanModels.cs`
- `Core/MealPlans/MealPlanTemplateModels.cs`
- `Core/MealPlans/IMealPlanService.cs`
- `Core/MealPlans/IMealPlanTemplateService.cs`
- `Data/Entities/MealPlanEntry.cs`
- `Data/Entities/MealPlanTemplate.cs`
- `Data/Entities/MealPlanTemplateItem.cs`
- `Data/Configurations/MealPlanEntryConfiguration.cs`
- `Data/Configurations/MealPlanTemplateConfiguration.cs`
- `Data/Configurations/MealPlanTemplateItemConfiguration.cs`
- `Data/Services/MealPlanService.cs`
- `Data/Services/MealPlanTemplateService.cs`
- `API/Controllers/MealPlansController.cs`
- `API/Controllers/MealPlanTemplatesController.cs`
- `Tests/Core.UnitTests/MealPlanDateHelperTests.cs`
- `Tests/Core.UnitTests/MealPlanServiceTests.cs`
- `Tests/Core.UnitTests/MealPlanTemplateServiceTests.cs`
- `Tests/API.IntegrationTests/MealPlansIntegrationTests.cs`

#### Existing Frontend Files to Modify (`Client/`)
- `Client/src/app/app.routes.ts` — Register lazy `/meal-plan` route under `AppShellComponent`.
- `Client/src/app/features/shell/app-shell/app-shell.component.html` (+ `.scss`, `.spec.ts`) — Add `Meal Plan` (`<mat-icon>calendar_month</mat-icon>`, `routerLink="/meal-plan"`) to desktop nav and `360px` mobile bottom nav, tuning `.mobile-tab` spacing so all 5 tabs fit cleanly at `360px` width without horizontal scroll.
- `Client/src/app/features/recipes/recipe-detail/recipe-detail.component.ts` (+ `.html`, `.spec.ts`) — Add **"Plan Meal"** button in `.detail-top-actions` opening `MealPlanEntryDialogComponent` pre-filled with the current recipe and `targetServings()`.

#### New Frontend & E2E Files to Create
- `Client/src/app/core/models/meal-plan.models.ts`
- `Client/src/app/core/utils/meal-plan-date.ts` (+ `.spec.ts`)
- `Client/src/app/core/services/meal-plan/meal-plan.service.ts` (+ `.spec.ts`)
- `Client/src/app/features/meal-plans/meal-plan-calendar/meal-plan-calendar.component.ts` (+ `.html`, `.scss`, `.spec.ts`)
- `Client/src/app/features/meal-plans/meal-plan-entry-dialog/meal-plan-entry-dialog.component.ts` (+ `.html`, `.scss`, `.spec.ts`)
- `Client/src/app/features/meal-plans/meal-plan-templates-dialog/meal-plan-templates-dialog.component.ts` (+ `.html`, `.scss`, `.spec.ts`)
- `Tests/Client.E2ETests/MealPlanE2ETests.cs`

---

## 2. Domain & Database Design (`Core/` & `Data/`)

### 2.1 Enums & Pure Date Helper (`Core/MealPlans/`)

1. **`Core/MealPlans/MealPlanEnums.cs`** (`namespace App.Core.MealPlans`):
   ```csharp
   using System.Text.Json.Serialization;

   namespace App.Core.MealPlans;

   [JsonConverter(typeof(JsonStringEnumConverter))]
   public enum MealSlot
   {
       Breakfast = 0,
       Lunch = 1,
       Dinner = 2,
       SnackPrep = 3,
   }

   [JsonConverter(typeof(JsonStringEnumConverter))]
   public enum MealPlanEntryType
   {
       Recipe = 0,
       Leftovers = 1,
       FreeformNote = 2,
   }

   [JsonConverter(typeof(JsonStringEnumConverter))]
   public enum ApplyTemplateMode
   {
       Merge = 0,
       Replace = 1,
   }
   ```

2. **`Core/MealPlans/MealPlanDateHelper.cs`** (`namespace App.Core.MealPlans`):
   - `public static DateOnly NormalizeToMonday(DateOnly date)`:
     - Computes `int diff = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;`
     - Returns `date.AddDays(-diff);` (ISO Monday-start week: `Mon = 0` .. `Sun = 6`).
   - `public static int GetDayOffsetFromMonday(DateOnly date)`:
     - Returns `((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;` (`0..6`).
   - `public static (DateOnly GridStartMonday, DateOnly GridEndSunday) GetMonthGridBounds(int year, int month)`:
     - Validates `year` in `2000..2100` and `month` in `1..12` (throws `DomainValidationException` if out of range).
     - `var firstOfMonth = new DateOnly(year, month, 1);`
     - `var lastOfMonth = new DateOnly(year, month, DateTime.DaysInMonth(year, month));`
     - Returns `(NormalizeToMonday(firstOfMonth), NormalizeToMonday(lastOfMonth).AddDays(6))`.

### 2.2 Entities (`Data/Entities/`)

1. **`Data/Entities/MealPlanEntry.cs`** (`namespace App.Data.Entities`):
   ```csharp
   using App.Core.MealPlans;

   namespace App.Data.Entities;

   public sealed class MealPlanEntry : IAuditable
   {
       public Guid Id { get; set; } = Guid.NewGuid();

       public DateOnly WeekStartDate { get; set; }

       public bool IsOnDeck { get; set; }

       public DateOnly? ScheduledDate { get; set; }

       public MealSlot? Slot { get; set; }

       public int SortOrder { get; set; }

       public MealPlanEntryType EntryType { get; set; } = MealPlanEntryType.Recipe;

       public Guid? RecipeId { get; set; }

       public decimal? TargetServings { get; set; }

       public bool IsLeftovers { get; set; }

       public Guid? SourceMealPlanEntryId { get; set; }

       public string? CustomTitle { get; set; }

       public string? Notes { get; set; }

       public DateTimeOffset CreatedAt { get; set; }

       public string CreatedBy { get; set; } = "System";

       public DateTimeOffset ModifiedAt { get; set; }

       public string ModifiedBy { get; set; } = "System";

       public Recipe? Recipe { get; set; }

       public MealPlanEntry? SourceMealPlanEntry { get; set; }

       public ICollection<MealPlanEntry> DerivedLeftoverEntries { get; set; } =
           new List<MealPlanEntry>();
   }
   ```

2. **`Data/Entities/MealPlanTemplate.cs`** (`namespace App.Data.Entities`):
   ```csharp
   namespace App.Data.Entities;

   public sealed class MealPlanTemplate : IAuditable
   {
       public Guid Id { get; set; } = Guid.NewGuid();

       public string Name { get; set; } = string.Empty;

       public string NormalizedName { get; set; } = string.Empty;

       public string? Description { get; set; }

       public DateTimeOffset CreatedAt { get; set; }

       public string CreatedBy { get; set; } = "System";

       public DateTimeOffset ModifiedAt { get; set; }

       public string ModifiedBy { get; set; } = "System";

       public ICollection<MealPlanTemplateItem> Items { get; set; } =
           new List<MealPlanTemplateItem>();
   }
   ```

3. **`Data/Entities/MealPlanTemplateItem.cs`** (`namespace App.Data.Entities`):
   ```csharp
   using App.Core.MealPlans;

   namespace App.Data.Entities;

   public sealed class MealPlanTemplateItem : IAuditable
   {
       public Guid Id { get; set; } = Guid.NewGuid();

       public Guid TemplateId { get; set; }

       public bool IsOnDeck { get; set; }

       public int? DayOffset { get; set; }

       public MealSlot? Slot { get; set; }

       public int SortOrder { get; set; }

       public MealPlanEntryType EntryType { get; set; } = MealPlanEntryType.Recipe;

       public Guid? RecipeId { get; set; }

       public decimal? TargetServings { get; set; }

       public bool IsLeftovers { get; set; }

       public string? CustomTitle { get; set; }

       public string? Notes { get; set; }

       public DateTimeOffset CreatedAt { get; set; }

       public string CreatedBy { get; set; } = "System";

       public DateTimeOffset ModifiedAt { get; set; }

       public string ModifiedBy { get; set; } = "System";

       public MealPlanTemplate Template { get; set; } = null!;

       public Recipe? Recipe { get; set; }
   }
   ```

4. **Update `Data/Entities/Recipe.cs`:**
   - Add:
     ```csharp
     public ICollection<MealPlanEntry> MealPlanEntries { get; set; } = new List<MealPlanEntry>();

     public ICollection<MealPlanTemplateItem> MealPlanTemplateItems { get; set; } =
         new List<MealPlanTemplateItem>();
     ```

### 2.3 EF Core Fluent Configurations (`Data/Configurations/`)

1. **`Data/Configurations/MealPlanEntryConfiguration.cs`**:
   - Table: `"MealPlanEntries"`, PK: `Id`.
   - `Slot`: `HasConversion<string>().HasMaxLength(32)`.
   - `EntryType`: `HasConversion<string>().HasMaxLength(32)`.
   - `TargetServings`: `HasPrecision(8, 2)`.
   - `CustomTitle`: `HasMaxLength(200)`.
   - `Notes`: `HasMaxLength(500)`.
   - `CreatedBy` & `ModifiedBy`: `IsRequired().HasMaxLength(256)`.
   - Indexes:
     - `builder.HasIndex(e => new { e.WeekStartDate, e.IsOnDeck, e.SortOrder });`
     - `builder.HasIndex(e => new { e.ScheduledDate, e.Slot, e.SortOrder });`
     - `builder.HasIndex(e => e.RecipeId);`
   - Foreign Keys (**SQL Server multiple-cascade-path safe**):
     - `builder.HasOne(e => e.Recipe).WithMany(r => r.MealPlanEntries).HasForeignKey(e => e.RecipeId).OnDelete(DeleteBehavior.Restrict);`
     - `builder.HasOne(e => e.SourceMealPlanEntry).WithMany(s => s.DerivedLeftoverEntries).HasForeignKey(e => e.SourceMealPlanEntryId).OnDelete(DeleteBehavior.Restrict);`
     - **SQL Server Note:** Self-referencing foreign keys (`SourceMealPlanEntryId -> MealPlanEntries.Id`) MUST use `DeleteBehavior.Restrict` because SQL Server forbids `Cascade` / `SetNull` on self-referencing tables. `RecipeId` also uses `DeleteBehavior.Restrict` and is gracefully detached in `RecipeService.DeleteRecipeAsync`, avoiding any cascade cycles now and when Phase 08 `ShoppingListItemSource` links to `MealPlanEntry`.

2. **`Data/Configurations/MealPlanTemplateConfiguration.cs`**:
   - Table: `"MealPlanTemplates"`, PK: `Id`.
   - `Name`: `IsRequired().HasMaxLength(120)`.
   - `NormalizedName`: `IsRequired().HasMaxLength(120)`.
   - `Description`: `HasMaxLength(500)`.
   - `CreatedBy` & `ModifiedBy`: `IsRequired().HasMaxLength(256)`.
   - Unique Index: `builder.HasIndex(t => t.NormalizedName).IsUnique();`

3. **`Data/Configurations/MealPlanTemplateItemConfiguration.cs`**:
   - Table: `"MealPlanTemplateItems"`, PK: `Id`.
   - `Slot`: `HasConversion<string>().HasMaxLength(32)`.
   - `EntryType`: `HasConversion<string>().HasMaxLength(32)`.
   - `TargetServings`: `HasPrecision(8, 2)`.
   - `CustomTitle`: `HasMaxLength(200)`.
   - `Notes`: `HasMaxLength(500)`.
   - `CreatedBy` & `ModifiedBy`: `IsRequired().HasMaxLength(256)`.
   - Index: `builder.HasIndex(i => new { i.TemplateId, i.IsOnDeck, i.DayOffset, i.Slot, i.SortOrder });`
   - Foreign Keys:
     - `builder.HasOne(i => i.Template).WithMany(t => t.Items).HasForeignKey(i => i.TemplateId).OnDelete(DeleteBehavior.Cascade);`
     - `builder.HasOne(i => i.Recipe).WithMany(r => r.MealPlanTemplateItems).HasForeignKey(i => i.RecipeId).OnDelete(DeleteBehavior.Restrict);`

4. **EF Core Migration Name:** `AddMealPlanCalendarAndTemplates`.

### 2.4 DTOs, Requests & Service Contracts (`Core/MealPlans/`)

1. **`Core/MealPlans/MealPlanModels.cs`** (`namespace App.Core.MealPlans`):
   ```csharp
   using System.ComponentModel.DataAnnotations;
   using App.Core.Recipes;

   namespace App.Core.MealPlans;

   public sealed record MealPlanRecipeBadgeDto(
       Guid Id,
       string Title,
       string Slug,
       decimal BaseServings,
       int? TotalTimeMinutes,
       RecipeDifficulty? Difficulty,
       string? ThumbnailImagePath
   );

   public sealed record MealPlanEntryDto(
       Guid Id,
       DateOnly WeekStartDate,
       bool IsOnDeck,
       DateOnly? ScheduledDate,
       MealSlot? Slot,
       int SortOrder,
       MealPlanEntryType EntryType,
       bool IsLeftovers,
       decimal? TargetServings,
       string DisplayTitle,
       string? CustomTitle,
       string? Notes,
       Guid? SourceMealPlanEntryId,
       MealPlanRecipeBadgeDto? Recipe,
       DateTimeOffset CreatedAt,
       string CreatedBy,
       DateTimeOffset ModifiedAt,
       string ModifiedBy
   );

   public sealed record MealPlanDayDto(
       DateOnly Date,
       int DayOffset,
       IReadOnlyList<MealPlanEntryDto> Entries
   );

   public sealed record MealPlanWeekDto(
       DateOnly WeekStartDate,
       DateOnly WeekEndDate,
       IReadOnlyList<MealPlanDayDto> Days,
       IReadOnlyList<MealPlanEntryDto> OnDeckEntries
   );

   public sealed record MealPlanRangeResponseDto(
       DateOnly StartDate,
       DateOnly EndDate,
       IReadOnlyList<MealPlanEntryDto> ScheduledEntries,
       IReadOnlyList<MealPlanEntryDto> OnDeckEntries
   );

   public sealed record CreateMealPlanEntryRequest(
       MealPlanEntryType EntryType = MealPlanEntryType.Recipe,
       bool IsOnDeck = false,
       DateOnly? WeekStartDate = null,
       DateOnly? ScheduledDate = null,
       MealSlot? Slot = null,
       Guid? RecipeId = null,
       [Range(typeof(decimal), "0.25", "999")] decimal? TargetServings = null,
       Guid? SourceMealPlanEntryId = null,
       [MaxLength(200)] string? CustomTitle = null,
       [MaxLength(500)] string? Notes = null
   );

   public sealed record UpdateMealPlanEntryRequest(
       MealPlanEntryType EntryType = MealPlanEntryType.Recipe,
       bool IsOnDeck = false,
       DateOnly? WeekStartDate = null,
       DateOnly? ScheduledDate = null,
       MealSlot? Slot = null,
       Guid? RecipeId = null,
       [Range(typeof(decimal), "0.25", "999")] decimal? TargetServings = null,
       Guid? SourceMealPlanEntryId = null,
       [MaxLength(200)] string? CustomTitle = null,
       [MaxLength(500)] string? Notes = null
   );

   public sealed record MoveMealPlanEntryRequest(
       bool IsOnDeck = false,
       DateOnly? WeekStartDate = null,
       DateOnly? ScheduledDate = null,
       MealSlot? Slot = null,
       [Range(0, 1000)] int? TargetSortOrder = null
   );

   public sealed record CookTonightRequest(
       DateOnly? TargetDate = null,
       MealSlot Slot = MealSlot.Dinner
   );
   ```

2. **`Core/MealPlans/MealPlanTemplateModels.cs`** (`namespace App.Core.MealPlans`):
   ```csharp
   using System.ComponentModel.DataAnnotations;

   namespace App.Core.MealPlans;

   public sealed record MealPlanTemplateItemDto(
       Guid Id,
       bool IsOnDeck,
       int? DayOffset,
       MealSlot? Slot,
       int SortOrder,
       MealPlanEntryType EntryType,
       bool IsLeftovers,
       decimal? TargetServings,
       string DisplayTitle,
       string? CustomTitle,
       string? Notes,
       MealPlanRecipeBadgeDto? Recipe
   );

   public sealed record MealPlanTemplateSummaryDto(
       Guid Id,
       string Name,
       string? Description,
       int ItemCount,
       int ScheduledItemCount,
       int OnDeckItemCount,
       DateTimeOffset CreatedAt,
       DateTimeOffset ModifiedAt
   );

   public sealed record MealPlanTemplateDetailDto(
       Guid Id,
       string Name,
       string? Description,
       IReadOnlyList<MealPlanTemplateItemDto> Items,
       DateTimeOffset CreatedAt,
       string CreatedBy,
       DateTimeOffset ModifiedAt,
       string ModifiedBy
   );

   public sealed record MealPlanTemplateItemInput(
       bool IsOnDeck = false,
       [Range(0, 6)] int? DayOffset = null,
       MealSlot? Slot = null,
       MealPlanEntryType EntryType = MealPlanEntryType.Recipe,
       Guid? RecipeId = null,
       [Range(typeof(decimal), "0.25", "999")] decimal? TargetServings = null,
       [MaxLength(200)] string? CustomTitle = null,
       [MaxLength(500)] string? Notes = null
   );

   public sealed record UpsertMealPlanTemplateRequest(
       [Required, MinLength(2), MaxLength(120)] string Name,
       [MaxLength(500)] string? Description = null,
       IReadOnlyList<MealPlanTemplateItemInput>? Items = null
   );

   public sealed record CreateTemplateFromWeekRequest(
       [Required] DateOnly WeekStartDate,
       [Required, MinLength(2), MaxLength(120)] string Name,
       [MaxLength(500)] string? Description = null,
       bool IncludeOnDeck = true
   );

   public sealed record ApplyMealPlanTemplateRequest(
       [Required] DateOnly TargetWeekStartDate,
       ApplyTemplateMode Mode = ApplyTemplateMode.Merge
   );
   ```

3. **`Core/MealPlans/IMealPlanService.cs`** (`namespace App.Core.MealPlans`):
   ```csharp
   namespace App.Core.MealPlans;

   public interface IMealPlanService
   {
       Task<MealPlanWeekDto> GetWeekAsync(
           DateOnly weekDate,
           CancellationToken cancellationToken = default
       );

       Task<MealPlanRangeResponseDto> GetRangeAsync(
           DateOnly startDate,
           DateOnly endDate,
           bool includeOnDeck = true,
           CancellationToken cancellationToken = default
       );

       Task<MealPlanEntryDto?> GetEntryByIdAsync(
           Guid id,
           CancellationToken cancellationToken = default
       );

       Task<MealPlanEntryDto> CreateEntryAsync(
           CreateMealPlanEntryRequest request,
           CancellationToken cancellationToken = default
       );

       Task<MealPlanEntryDto?> UpdateEntryAsync(
           Guid id,
           UpdateMealPlanEntryRequest request,
           CancellationToken cancellationToken = default
       );

       Task<MealPlanEntryDto?> MoveEntryAsync(
           Guid id,
           MoveMealPlanEntryRequest request,
           CancellationToken cancellationToken = default
       );

       Task<MealPlanEntryDto?> CookTonightAsync(
           Guid id,
           CookTonightRequest request,
           CancellationToken cancellationToken = default
       );

       Task<bool> DeleteEntryAsync(
           Guid id,
           CancellationToken cancellationToken = default
       );

       Task<int> ClearWeekAsync(
           DateOnly weekDate,
           bool includeOnDeck = false,
           CancellationToken cancellationToken = default
       );
   }
   ```

4. **`Core/MealPlans/IMealPlanTemplateService.cs`** (`namespace App.Core.MealPlans`):
   ```csharp
   namespace App.Core.MealPlans;

   public interface IMealPlanTemplateService
   {
       Task<IReadOnlyList<MealPlanTemplateSummaryDto>> GetTemplatesAsync(
           CancellationToken cancellationToken = default
       );

       Task<MealPlanTemplateDetailDto?> GetTemplateByIdAsync(
           Guid id,
           CancellationToken cancellationToken = default
       );

       Task<MealPlanTemplateDetailDto> CreateTemplateAsync(
           UpsertMealPlanTemplateRequest request,
           CancellationToken cancellationToken = default
       );

       Task<MealPlanTemplateDetailDto> CreateTemplateFromWeekAsync(
           CreateTemplateFromWeekRequest request,
           CancellationToken cancellationToken = default
       );

       Task<MealPlanTemplateDetailDto?> UpdateTemplateAsync(
           Guid id,
           UpsertMealPlanTemplateRequest request,
           CancellationToken cancellationToken = default
       );

       Task<bool> DeleteTemplateAsync(
           Guid id,
           CancellationToken cancellationToken = default
       );

       Task<MealPlanWeekDto?> ApplyTemplateToWeekAsync(
           Guid templateId,
           ApplyMealPlanTemplateRequest request,
           CancellationToken cancellationToken = default
       );
   }
   ```

### 2.5 Service Business Logic & Invariants (`Data/Services/`)

1. **`Data/Services/MealPlanService.cs`** (`public sealed class MealPlanService(AppDbContext dbContext, TimeProvider? timeProvider = null) : IMealPlanService`):
   - **Global Household Tenancy:** All authenticated household users view and collaborate on the single shared household meal calendar (`IAuditable` records `CreatedBy` / `ModifiedBy`).
   - **Read Queries (`.AsNoTracking()`):**
     - `GetWeekAsync(weekDate)`:
       - Snaps `weekMonday = MealPlanDateHelper.NormalizeToMonday(weekDate)` and `weekSunday = weekMonday.AddDays(6)`.
       - Queries `MealPlanEntries.AsNoTracking().Include(e => e.Recipe).Where(e => e.WeekStartDate == weekMonday)`.
       - Builds exactly 7 `MealPlanDayDto` records (`DayOffset` `0..6`, `Date = weekMonday.AddDays(offset)`), ordering entries within each day by `Slot`, `SortOrder`, `CreatedAt`, plus `OnDeckEntries` (`IsOnDeck == true`) ordered by `SortOrder`, `CreatedAt`.
     - `GetRangeAsync(startDate, endDate, includeOnDeck)`:
       - Validates `endDate >= startDate` and `(endDate.DayNumber - startDate.DayNumber) <= 92` (`DomainValidationException` otherwise).
       - Precomputes `firstMonday = MealPlanDateHelper.NormalizeToMonday(startDate)` and `lastMonday = MealPlanDateHelper.NormalizeToMonday(endDate)` in C# before LINQ execution so both SQL Server and EF InMemory translate pure column comparisons cleanly.
       - Queries scheduled entries (`!e.IsOnDeck && e.ScheduledDate >= startDate && e.ScheduledDate <= endDate`) ordered by `ScheduledDate`, `Slot`, `SortOrder`, `CreatedAt`, and (if `includeOnDeck`) On-Deck entries (`e.IsOnDeck && e.WeekStartDate >= firstMonday && e.WeekStartDate <= lastMonday`) ordered by `WeekStartDate`, `SortOrder`, `CreatedAt`.
   - **`DisplayTitle` Resolution:**
     - `Recipe`: `!string.IsNullOrWhiteSpace(CustomTitle) ? CustomTitle : (Recipe?.Title ?? "Scheduled Recipe")`.
     - `Leftovers`: `!string.IsNullOrWhiteSpace(CustomTitle) ? CustomTitle : (Recipe is not null ? $"Leftovers: {Recipe.Title}" : "Leftovers")`.
     - `FreeformNote`: `CustomTitle ?? "Note"`.
   - **Placement & EntryType Validation (`CreateEntryAsync` & `UpdateEntryAsync`):**
     - If `IsOnDeck == true`:
       - Resolves `weekMonday` from `WeekStartDate` (falling back to `ScheduledDate` if supplied); throws `DomainValidationException("WeekStartDate is required when placing an entry in the On-Deck tray.")` if both are null.
       - Forces `ScheduledDate = null` and `Slot = null`.
     - If `IsOnDeck == false`:
       - Requires `ScheduledDate.HasValue` and `Slot.HasValue` (`Enum.IsDefined(Slot.Value)`), throwing `DomainValidationException` otherwise.
       - Sets `WeekStartDate = MealPlanDateHelper.NormalizeToMonday(ScheduledDate.Value)`.
     - `EntryType == MealPlanEntryType.Recipe`:
       - Requires `RecipeId.HasValue`; loads `recipe` from `dbContext.Recipes`; throws `DomainValidationException` if missing or if creating a new entry for an `Archived` recipe (`Status == RecipeStatus.Archived`).
       - Validates `TargetServings` (`0.25m..999m`) if provided; defaults `TargetServings = recipe.BaseServings` when null.
       - Sets `IsLeftovers = false`, `SourceMealPlanEntryId = null`.
     - `EntryType == MealPlanEntryType.Leftovers`:
       - Sets `IsLeftovers = true` (enforcing the shopping-list zero-ingredient exclusion invariant).
       - If `SourceMealPlanEntryId.HasValue`: verifies `SourceMealPlanEntryId != currentId` and loads `sourceEntry` (with `Include(s => s.Recipe)`); throws `DomainValidationException` if not found; inherits `RecipeId ??= sourceEntry.RecipeId`; if `CustomTitle` is blank, sets `CustomTitle = Truncate($"Leftovers: {ResolveTitle(sourceEntry)}", 200)`.
       - Else if `RecipeId.HasValue`: verifies `recipe` exists (`DomainValidationException` if missing); if `CustomTitle` is blank, sets `CustomTitle = Truncate($"Leftovers: {recipe.Title}", 200)`.
       - Else: sets `CustomTitle = string.IsNullOrWhiteSpace(CustomTitle) ? "Leftovers" : CustomTitle.Trim()`.
     - `EntryType == MealPlanEntryType.FreeformNote`:
       - Requires non-whitespace `CustomTitle` (`1..200` chars after trim, `DomainValidationException` otherwise).
       - Forces `RecipeId = null`, `TargetServings = null`, `IsLeftovers = false`, `SourceMealPlanEntryId = null`.
   - **Ordering, Moving & Deleting:**
     - `CreateEntryAsync` appends at `max(SortOrder) + 1` (`0` when bucket is empty).
     - `MoveEntryAsync` validates target bucket (`IsOnDeck` vs `ScheduledDate + Slot`; when moving to `IsOnDeck` with `WeekStartDate == null`, defaults to `entry.WeekStartDate`), inserts at clamped `TargetSortOrder ?? targetPeers.Count`, and re-sequences `0..N-1` in the destination bucket (and compacts source bucket `0..M-1` when crossing buckets).
     - `CookTonightAsync` resolves `date = request.TargetDate ?? DateOnly.FromDateTime((timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime)` and delegates to `MoveEntryAsync` (`IsOnDeck = false`, `ScheduledDate = date`, `Slot = request.Slot`).
     - `DeleteEntryAsync` and `ClearWeekAsync`: before deleting target `MealPlanEntries`, find any dependent `MealPlanEntries` whose `SourceMealPlanEntryId` is in the deleted set, preserve their `CustomTitle` if blank, and set `SourceMealPlanEntryId = null` so `DeleteBehavior.Restrict` succeeds cleanly on SQL Server.

2. **`Data/Services/MealPlanTemplateService.cs`** (`public sealed class MealPlanTemplateService(AppDbContext dbContext, IMealPlanService mealPlanService) : IMealPlanTemplateService`):
   - Validates `Name` (`2..120` chars trimmed) and `NormalizedName = trimmedName.ToUpperInvariant()`, throwing `ConflictException` if another template already uses `NormalizedName`.
   - Validates each `MealPlanTemplateItemInput`:
     - If `IsOnDeck`: `DayOffset = null`, `Slot = null`.
     - If `!IsOnDeck`: requires `DayOffset` in `0..6` and valid `Slot` (`DomainValidationException` otherwise).
     - Validates `EntryType` (`Recipe` requires valid `RecipeId`, defaulting `TargetServings` to `Recipe.BaseServings`; `Leftovers` sets `IsLeftovers = true`; `FreeformNote` requires `CustomTitle`).
   - `CreateTemplateFromWeekAsync`:
     - Normalizes `weekMonday = MealPlanDateHelper.NormalizeToMonday(request.WeekStartDate)`.
     - Loads week entries (`!IncludeOnDeck` excludes `IsOnDeck` rows); throws `DomainValidationException` if the week has zero matching entries.
     - Creates `MealPlanTemplate` + ordered `MealPlanTemplateItem` rows (`DayOffset = e.IsOnDeck ? null : e.ScheduledDate!.Value.DayNumber - weekMonday.DayNumber`).
   - `ApplyTemplateToWeekAsync`:
     - Returns `null` if `templateId` does not exist.
     - Normalizes `targetMonday = MealPlanDateHelper.NormalizeToMonday(request.TargetWeekStartDate)`.
     - When `Mode == ApplyTemplateMode.Replace`: calls `await mealPlanService.ClearWeekAsync(targetMonday, includeOnDeck: true, cancellationToken)` first.
     - Iterates template items ordered by `IsOnDeck`, `DayOffset`, `Slot`, `SortOrder`:
       - Computes `scheduledDate = item.IsOnDeck ? (DateOnly?)null : targetMonday.AddDays(item.DayOffset!.Value)`.
       - In `Merge` mode, skips stamping if an entry already exists in the exact target bucket with matching `EntryType`, `RecipeId`, and case-insensitive `CustomTitle`.
       - Appends new `MealPlanEntry` rows with sequential `SortOrder` per bucket, saves changes, and returns `await mealPlanService.GetWeekAsync(targetMonday, cancellationToken)`.

3. **`Data/Services/RecipeService.cs` (`DeleteRecipeAsync` update):**
   - Before removing `recipe`, query `MealPlanEntries.Where(e => e.RecipeId == id)` and `MealPlanTemplateItems.Where(i => i.RecipeId == id)`:
     - Set `RecipeId = null`, preserve `CustomTitle` (`IsLeftovers ? $"Leftovers: {recipe.Title}" : recipe.Title` when `CustomTitle` is null/whitespace), and if `!IsLeftovers`, set `EntryType = MealPlanEntryType.FreeformNote`.

---

## 3. API Endpoints (`API/Controllers/`)

Both controllers use `[ApiController]`, `[Authorize]`, and relative routes (mounted under `/api` by `Program.cs`) and remain thin HTTP adapters translating `DomainValidationException` $\rightarrow$ `400`, `NotFound` / `null` $\rightarrow$ `404`, and `ConflictException` $\rightarrow$ `409`.

### 3.1 `API/Controllers/MealPlansController.cs` (`[Route("meal-plans")]`)

| Verb | Route (`/api/...`) | Request Body / Query | Response | Error Cases |
| :--- | :--- | :--- | :--- | :--- |
| `GET` | `/meal-plans/week` | Query: `DateOnly? weekStart` (defaults to UTC today) | `200 OK` (`MealPlanWeekDto`) | `401` |
| `GET` | `/meal-plans` | Query: `DateOnly startDate`, `DateOnly endDate`, `bool includeOnDeck = true` | `200 OK` (`MealPlanRangeResponseDto`) | `400` (invalid/>93d range), `401` |
| `GET` | `/meal-plans/entries/{id:guid}` | — | `200 OK` (`MealPlanEntryDto`) | `401`, `404` |
| `POST` | `/meal-plans/entries` | Body: `CreateMealPlanEntryRequest` | `201 Created` (`MealPlanEntryDto`) | `400`, `401` |
| `PUT` | `/meal-plans/entries/{id:guid}` | Body: `UpdateMealPlanEntryRequest` | `200 OK` (`MealPlanEntryDto`) | `400`, `401`, `404` |
| `POST` | `/meal-plans/entries/{id:guid}/move` | Body: `MoveMealPlanEntryRequest` | `200 OK` (`MealPlanEntryDto`) | `400`, `401`, `404` |
| `POST` | `/meal-plans/entries/{id:guid}/cook-tonight` | Body: `CookTonightRequest` | `200 OK` (`MealPlanEntryDto`) | `400`, `401`, `404` |
| `DELETE` | `/meal-plans/entries/{id:guid}` | — | `204 No Content` | `401`, `404` |
| `DELETE` | `/meal-plans/week` | Query: `DateOnly weekStart`, `bool includeOnDeck = false` | `204 No Content` | `401` |

### 3.2 `API/Controllers/MealPlanTemplatesController.cs` (`[Route("meal-plan-templates")]`)

| Verb | Route (`/api/...`) | Request Body / Query | Response | Error Cases |
| :--- | :--- | :--- | :--- | :--- |
| `GET` | `/meal-plan-templates` | — | `200 OK` (`IReadOnlyList<MealPlanTemplateSummaryDto>`) | `401` |
| `GET` | `/meal-plan-templates/{id:guid}` | — | `200 OK` (`MealPlanTemplateDetailDto`) | `401`, `404` |
| `POST` | `/meal-plan-templates` | Body: `UpsertMealPlanTemplateRequest` | `201 Created` (`MealPlanTemplateDetailDto`) | `400`, `401`, `409` |
| `POST` | `/meal-plan-templates/from-week` | Body: `CreateTemplateFromWeekRequest` | `201 Created` (`MealPlanTemplateDetailDto`) | `400` (empty week), `401`, `409` |
| `PUT` | `/meal-plan-templates/{id:guid}` | Body: `UpsertMealPlanTemplateRequest` | `200 OK` (`MealPlanTemplateDetailDto`) | `400`, `401`, `404`, `409` |
| `DELETE` | `/meal-plan-templates/{id:guid}` | — | `204 No Content` | `401`, `404` |
| `POST` | `/meal-plan-templates/{id:guid}/apply` | Body: `ApplyMealPlanTemplateRequest` | `200 OK` (`MealPlanWeekDto`) | `400`, `401`, `404` |

---

## 4. Angular Frontend Design (`Client/`)

### 4.1 Models, Date Utils & Services (`src/app/core/`)

1. **`src/app/core/models/meal-plan.models.ts`**:
   - Define `MealSlot = 'Breakfast' | 'Lunch' | 'Dinner' | 'SnackPrep'`, `MEAL_SLOTS` metadata constant (`value`, `label` including `'Snack / Prep'`, and Material Symbol `icon`), `MealPlanEntryType = 'Recipe' | 'Leftovers' | 'FreeformNote'`, `ApplyTemplateMode = 'Merge' | 'Replace'`, and TypeScript interfaces matching all backend DTOs and Request records.
2. **`src/app/core/utils/meal-plan-date.ts` (+ `meal-plan-date.spec.ts`)**:
   - Timezone-safe local calendar ISO date helpers (`parseIsoDateLocal`, `formatIsoDateLocal`, `getTodayIsoLocal`, `normalizeToMondayIso`, `addDaysIso`, `getMonthGridBoundsIso`, `formatWeekRangeLabel`, `formatMonthYearLabel`, `formatDayHeader`, `DAY_NAMES_SHORT`, `DAY_NAMES_LONG`).
3. **`src/app/core/services/meal-plan/meal-plan.service.ts` (+ `meal-plan.service.spec.ts`)**:
   - Decorated with `@Service()` from `@angular/core` and using `inject(HttpClient)` + `apiUrl('/meal-plans...')` / `apiUrl('/meal-plan-templates...')`:
     - `getWeek(weekStartIso?: string): Observable<MealPlanWeekDto>`
     - `getRange(startDateIso: string, endDateIso: string, includeOnDeck = true): Observable<MealPlanRangeResponseDto>`
     - `createEntry(request: CreateMealPlanEntryRequest): Observable<MealPlanEntryDto>`
     - `updateEntry(id: string, request: UpdateMealPlanEntryRequest): Observable<MealPlanEntryDto>`
     - `moveEntry(id: string, request: MoveMealPlanEntryRequest): Observable<MealPlanEntryDto>`
     - `cookTonight(id: string, request?: CookTonightRequest): Observable<MealPlanEntryDto>`
     - `deleteEntry(id: string): Observable<void>`
     - `clearWeek(weekStartIso: string, includeOnDeck = false): Observable<void>`
     - `getTemplates(): Observable<MealPlanTemplateSummaryDto[]>`
     - `getTemplateById(id: string): Observable<MealPlanTemplateDetailDto>`
     - `createTemplate(request: UpsertMealPlanTemplateRequest): Observable<MealPlanTemplateDetailDto>`
     - `createTemplateFromWeek(request: CreateTemplateFromWeekRequest): Observable<MealPlanTemplateDetailDto>`
     - `updateTemplate(id: string, request: UpsertMealPlanTemplateRequest): Observable<MealPlanTemplateDetailDto>`
     - `deleteTemplate(id: string): Observable<void>`
     - `applyTemplate(templateId: string, request: ApplyMealPlanTemplateRequest): Observable<MealPlanWeekDto>`

### 4.2 Feature Components & Routes (`src/app/features/meal-plans/`)

Every component strictly uses its own dedicated kebab-case folder with separate `.ts`, `.html`, `.scss`, and `.spec.ts` files, OnPush signals (`signal()`, `computed()`), native `@if` / `@for` / `@switch`, and Material 3 CSS variables (`--mat-sys-*`).

1. **`features/meal-plans/meal-plan-calendar/` (`MealPlanCalendarComponent`, route `/meal-plan`)**:
   - **Signals & State:** `viewMode = signal<'week' | 'month'>('week')`, `activeWeekStart = signal<string>(normalizeToMondayIso(getTodayIsoLocal()))`, `activeMonth = signal<{ year: number; monthIndex0: number }>(...)`, `weekData = signal<MealPlanWeekDto | null>(null)`, `monthRangeData = signal<MealPlanRangeResponseDto | null>(null)`, `isLoading = signal<boolean>(true)`, `mobileOnDeckOpen = signal<boolean>(false)`.
   - **Toolbar:**
     - `.page-title` (`"Meal Plan"`), `Week` / `Month` toggle (`mat-button-toggle-group`), Previous / `Today` / Next period navigation with formatted range title, **"Templates"** button (`openTemplatesDialog()`), **"Clear Week"** button (confirming via `ConfirmDialogComponent`), and primary **"+ Add Meal"** button (`openAddEntryDialog()`).
   - **Weekly Board View (`viewMode() === 'week'`):**
     - **Desktop Layout (`> 900px`):** `cdkDropListGroup` wrapping:
       - **7-Day Board Grid (`.week-days-grid`):** 7 day columns (`Mon`–`Sun`), highlighting Today (`[class.is-today]`). Each day renders the 4 ordered `MEAL_SLOTS`, each slot acting as a `cdkDropList` (`[cdkDropListData]="{ isOnDeck: false, date: day.date, slot: slot.value }"`) with a compact `+` icon button (`[attr.aria-label]="'Add meal to ' + day.date + ' ' + slot.label"`).
       - **Entry Cards (`.meal-entry-card`, `cdkDrag`):** Display thumbnail/type icon, clickable recipe link or custom title, serving pill (`6 srv` when `EntryType === 'Recipe'`), `Leftovers` badge (`0 shop` indicator) when `isLeftovers`, or `Note` badge when `FreeformNote`, optional `notes`, and an `aria-label`led `more_vert` action menu (**Edit**, **Open Recipe**, **Schedule Leftovers (+1 Day)**, **Move to On-Deck**, **Delete**).
       - **Persistent "On-Deck This Week" Sidebar (`.ondeck-sidebar`, `cdkDropList`):** Shows queued flexible meals for the week, **"+ Add to On-Deck"** button, drag-to-schedule support, and a prominent 1-tap **"Cook Tonight"** button (`cookTonight(entry)` sending `{ targetDate: getTodayIsoLocal(), slot: 'Dinner' }`) on every On-Deck item.
     - **Mobile Layout (`<= 900px` / `360px` viewport):**
       - Collapsible **"On-Deck This Week (N)"** drawer card at top/sticky trigger (`button.mobile-ondeck-toggle`) expanding On-Deck items with full-width **"Cook Tonight"** and **"+ Add to On-Deck"** buttons.
       - Vertical day-by-day `mat-accordion` agenda (`Today` and any day with entries expanded by default) with full-width slot lists, `+` add buttons, and 1-tap recipe navigation without horizontal overflow at `360px`.
   - **Monthly Overview (`viewMode() === 'month'`):**
     - Desktop 7-column (`Mon`–`Sun`) month grid (falling back to a clean vertical day-agenda list at `<= 768px` mobile widths) showing up to 3 color-coded meal pills per day + `"+N more"` indicator and an `"On-Deck (N)"` badge on week rows; clicking a day cell jumps straight to that week in `Week` view.

2. **`features/meal-plans/meal-plan-entry-dialog/` (`MealPlanEntryDialogComponent`)**:
   - Accepts `MAT_DIALOG_DATA` supporting:
     - Creating a new entry (optionally pre-selecting `scheduledDate`, `slot`, `isOnDeck`, `weekStartDate`, `entryType`, `preselectedRecipe`, `initialTargetServings`, `sourceMealPlanEntry`, and `weekScheduledEntries` for linking Leftovers!).
     - Editing an existing `MealPlanEntryDto`.
   - Interactive controls:
     - **Entry Type toggle:** `Recipe` | `Leftovers` | `FreeformNote`.
     - **Placement toggle:** `Calendar Day & Slot` (`date` input + `MealSlot` select) vs `On-Deck This Week` (snaps to week's Monday).
     - **`Recipe` mode:** Searchable recipe filter + `mat-select` / autocomplete list of published/draft recipes, auto-populating `targetServings` from `recipe.baseServings` on selection with numeric/stepper override (`0.5` increments) and optional `notes`.
     - **`Leftovers` mode:** Optional **"Link from Planned Meal / Recipe"** selector (`weekScheduledEntries` + cookbook recipes), editable `customTitle` (auto-filled as `"Leftovers: <Title>"`), optional `notes`, and informational banner explaining zero shopping-list ingredient impact.
     - **`FreeformNote` mode:** `customTitle` input with quick suggestion chips (`"Eating Out"`, `"Order Takeout"`, `"Leftover Buffet"`, `"Meal Prep"`) + optional `notes`.

3. **`features/meal-plans/meal-plan-templates-dialog/` (`MealPlanTemplatesDialogComponent`)**:
   - Accepts `{ currentWeekStartDate: string; currentWeekLabel: string; currentWeekEntryCount: number }` via `MAT_DIALOG_DATA`.
   - **Tab 1 — Saved Templates:** Lists saved `MealPlanTemplateSummaryDto` cards with item counts, expandable preview of template items (grouped by `Mon`–`Sun` + `On-Deck`), `ApplyTemplateMode` selector (`Merge (Keep existing)` vs `Replace week`), **"Apply to Week"** button returning the updated `MealPlanWeekDto`, and **"Delete"** action.
   - **Tab 2 — Save Current Week:** Form (`name`, `description`, `includeOnDeck` checkbox) calling `mealPlanService.createTemplateFromWeek(...)` (disabled with helpful message if `currentWeekEntryCount === 0`).

4. **Shell & Recipe Detail Updates:**
   - `AppShellComponent`: Add `Meal Plan` link (`/meal-plan`, icon `calendar_month`) to `.desktop-nav` and `.mobile-bottom-nav`.
   - `RecipeDetailComponent`: Add `openPlanMealDialog()` and `<button mat-stroked-button (click)="openPlanMealDialog()" aria-label="Plan meal"><mat-icon>calendar_month</mat-icon><span>Plan Meal</span></button>` in `.detail-top-actions`, pre-populating the current recipe and `targetServings()`, and offering a `"View Meal Plan"` snackbar action upon save.

---

## 5. Playwright E2E Test Scenarios (`Tests/Client.E2ETests/MealPlanE2ETests.cs`)

Create `Tests/Client.E2ETests/MealPlanE2ETests.cs` inheriting from `E2ETestBase`:

- `[TestMethod] MealPlan_ScheduleFromRecipeDetail_AndAddMultiDishLeftoversAndNotes()`:
  - Registers/logs in, creates two recipes (e.g., `"Sunday Chili"` base `4` servings and `" Jalapeño Cornbread"`), clicks **"Plan Meal"** directly on `"Sunday Chili"` Recipe Detail with `8` target servings onto the current week's **On-Deck** tray, navigates to `/meal-plan`, verifies `"Sunday Chili"` appears in **On-Deck This Week**, clicks **"Cook Tonight"** to promote it into Today's `Dinner` slot (`8 srv`), adds `"Jalapeño Cornbread"` as a second dish in the same `Dinner` slot (multi-dish verification), schedules a `Leftovers` entry (`"Leftovers: Sunday Chili"`) and a `FreeformNote` (`"Friday Takeout"`), and verifies all badges and reload persistence.
- `[TestMethod] MealPlan_WeekTemplates_SaveCurrentWeek_NavigateAndApplyMergeAndReplace()`:
  - Populates a week with scheduled meals + an On-Deck meal, opens **Templates**, saves the week as `"Busy Fall Week"`, navigates to **Next Week** (verifying empty calendar), opens **Templates** and stamps `"Busy Fall Week"` in `Merge` mode, verifies all meals + On-Deck items populate on Next Week, adds an extra ad-hoc note, re-applies in `Replace` mode, and verifies the extra note is replaced cleanly.
- `[TestMethod] MealPlan_MonthlyOverview_AndMobileViewport_360px_RendersCleanly()`:
  - Switches `/meal-plan` to `Month` view, verifies scheduled meal pills render in the monthly grid and clicking a day switches back to `Week` view; then resizes viewport to `360x640`, verifies mobile bottom navigation (`Meal Plan` tab active, zero horizontal scroll `scrollWidth <= clientWidth`), toggles the mobile **On-Deck This Week** drawer, adds an On-Deck item, taps **"Cook Tonight"**, and verifies Today's agenda panel updates cleanly at `360px`.

---

## 6. Step-by-Step Execution Checklist (For Implementation Agent)

- [ ] **Task 1: Backend Core & Data Layer**
  - Create `Core/MealPlans/` enums, `MealPlanDateHelper`, DTOs/requests, `IMealPlanService`, and `IMealPlanTemplateService`.
  - Create `Data/Entities/MealPlanEntry.cs`, `MealPlanTemplate.cs`, `MealPlanTemplateItem.cs`, EF configurations in `Data/Configurations/`, update `Recipe.cs`, `AppDbContext.cs`, `RecipeService.DeleteRecipeAsync`, and implement `MealPlanService` + `MealPlanTemplateService`.
  - Register `IMealPlanService` and `IMealPlanTemplateService` in `DataServiceCollectionExtensions.cs`.
  - Ensure local SQL Server is running, then generate, apply, and format the EF Core migration:
    ```powershell
    .\scripts\ensure-local-infra.ps1
    dotnet ef migrations add AddMealPlanCalendarAndTemplates --project Data/App.Data.csproj
    dotnet ef database update --project Data/App.Data.csproj
    dotnet csharpier format Data/Migrations
    ```
  - Write & run unit tests in `Tests/Core.UnitTests` (`MealPlanDateHelperTests.cs`, `MealPlanServiceTests.cs`, `MealPlanTemplateServiceTests.cs`):
    ```powershell
    dotnet test --project Tests/Core.UnitTests/App.Core.UnitTests.csproj
    ```

- [ ] **Task 2: API Controllers & Integration Tests**
  - Create thin HTTP controllers `API/Controllers/MealPlansController.cs` and `API/Controllers/MealPlanTemplatesController.cs`.
  - Update `Tests/API.IntegrationTests/OpenApiIntegrationTests.cs` and create `Tests/API.IntegrationTests/MealPlansIntegrationTests.cs` (covering week & range queries, On-Deck tray, multi-dish ordering, `move` & `cook-tonight`, `Leftovers` `IsLeftovers = true` & source-entry deletion safety, recipe deletion detachment safety, template creation/snapshot/duplicate `409`, `Merge` vs `Replace` stamping, validation `400`s, and unauthenticated `401`s).
  - Run integration tests until 100% green:
    ```powershell
    dotnet test --project Tests/API.IntegrationTests/App.API.IntegrationTests.csproj
    ```

- [ ] **Task 3: Angular Client & Unit Tests**
  - Create `meal-plan.models.ts`, `meal-plan-date.ts` (+ `.spec.ts`), and `@Service()` `MealPlanService` (+ `.spec.ts`).
  - Create dedicated per-component folders `meal-plan-calendar/`, `meal-plan-entry-dialog/`, and `meal-plan-templates-dialog/` under `Client/src/app/features/meal-plans/` (each with external `.ts`, `.html`, `.scss`, and `.spec.ts` files).
  - Wire `/meal-plan` route in `app.routes.ts`, add `Meal Plan` navigation item in `AppShellComponent` (+ `.spec.ts`), and add **"Plan Meal"** button in `RecipeDetailComponent` (+ `.spec.ts`).
  - Run frontend lint, production build, and unit tests in `Client/` until 100% green:
    ```powershell
    # In Client/:
    npm run lint
    npm run build:production
    npm test -- --watch=false
    ```

- [ ] **Task 4: Playwright E2E Verification & Final Formatting**
  - Implement `Tests/Client.E2ETests/MealPlanE2ETests.cs` covering all scenarios in Section 5 (and verify `NavigationAndThemeE2ETests` still passes at `360px` width).
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
