# Phase 10: Hands-Free Cook Mode, Household Cook Logs & Forgotten Favorites

> **Branch:** `feature/phase-10-cook-mode-and-cook-logs`
> **Status:** `[ ]` Not Started (`[ ]` Not Started | `[~]` In Progress | `[x]` Completed)
> **PRD Reference:** `docs/prd.md` — Sections 1, 2, 3, 4.3, 4.6.1, 4.6.2, 4.7.1, 4.7.3, 5, and 6 (Milestone 4, Slice 1 of 2)
> **Target Execution:** Run `/execute-phase` to execute this plan step-by-step.

---

## 1. Scope & Objectives

This phase initiates **Milestone 4** of **LocalRoast** by delivering the **Hands-Free Kitchen "Cook Mode"** experience alongside **Household Cook Logs ("I Made This!")**, **Dish Photo Uploads**, **Searchable Tweak Notes**, **Public Share Link Cook-Note Opt-In**, **MCP Cook History Enrichment**, and the **"Forgotten Favorites"** discovery filter:

1. **CookLog Domain, Dish Photos & Denormalized Recipe Stats (`CookLog`, `ICookLogService`, `AddCookLogs` Migration):**
   - Create the `CookLog` entity (`Data/Entities/CookLog.cs`) implementing `IAuditable`, tracking `RecipeId`, `CookedByUserId` + `CookedByDisplayName` (attributed to the signed-in household user), `CookedAt`, optional `ServingsMade`, optional `Rating` (`1–5` stars), optional `Notes` (kitchen tweak notes), and optional dish photo paths (`PhotoImagePath`, `PhotoThumbnailPath`).
   - Add `RatingCount` (`int`) to `Recipe` and atomically recompute `Recipe.TimesCooked`, `Recipe.LastCookedAt`, `Recipe.RatingCount`, and `Recipe.AverageRating` whenever a `CookLog` is created, updated, or deleted.
   - Extend `IMediaStorageService` / `LocalFileMediaStorageService` and `MediaController` (`POST /api/media/cook-log-photo`) to store EXIF-normalized responsive WebP dish photos + thumbnails under `/api/uploads/cook-logs/`, with automatic file cleanup on cook-log photo replacement, cook-log deletion, or recipe deletion.

2. **Discovery, Public Share & MCP Cook History Integration:**
   - Extend `RecipeQueryParameters` and `RecipeService.GetRecipesAsync` with `MinRating`, `ForgottenFavoritesOnly` (`(AverageRating >= 4.0 || TimesCooked >= 2) && (LastCookedAt == null || LastCookedAt <= UtcNow - 60 days)`), and full-text search across `CookLog.Notes` (powering both the Cookbook filter bar and `Ctrl+K` Global Instant Search).
   - Wire `McpCookbookService.GetRecipeAsync` (`get_recipe`) to populate `CookHistoryNotes` from `CookLogs` and `McpCookbookService.SearchRecipesAsync` (`search_recipes`) to match `CookLog.Notes`.
   - Wire `ShareLinkService.GetPublicSharedRecipeByTokenAsync` and `PublicSharedRecipeComponent` (`/share/:token`) to expose sanitized read-only `PublicCookLogDto` entries **only** when `ShareLink.IncludeCookLogs == true`, omitting all household user IDs, usernames, and emails.

3. **Hands-Free "Cook Mode" (`/recipes/:id/cook`) & Multi-Timer Engine:**
   - Mount `CookModeComponent` as a top-level authenticated route (`recipes/:id/cook`) outside `AppShellComponent` chrome so phones and tablets get a 100%-viewport distraction-free kitchen counter workspace.
   - Build `WakeLockService` (`navigator.wakeLock.request('screen')` with automatic `visibilitychange` re-acquisition and graceful fallback indicator when unsupported) and `KitchenTimerService` (wall-clock `endsAtMs` multi-timer engine supporting simultaneous named countdowns, `+1m` bump, pause/resume/reset/dismiss, custom ad-hoc timers, synthesized Web Audio API chime, and optional browser Notification alert).
   - Deliver responsive `CookModeComponent` layout: two-column counter split on desktop/tablet (`> 900px` pinned scaled ingredients + expandable sub-recipes on left, oversized active step + inline scaled step-ingredient chips + 1-tap step timer launchers on right) and full-screen step focus + slide-up ingredients sheet + sticky floating multi-timer bar on `360px` mobile viewports, plus `ArrowLeft` / `ArrowRight` keyboard navigation and live serving/unit conversion.
   - Launch Cook Mode and `CookLogDialogComponent` (*"I Made This!"*) seamlessly from `CookModeComponent`, `RecipeDetailComponent` (with a prominent **Latest Kitchen Note** banner at the top of the recipe and a **Cook History** timeline), and `MealPlanCalendarComponent` (1-tap **Start Cooking** button on Today's scheduled recipe cards carrying `targetServings` into Cook Mode).

### Explicitly Out of Scope (Deferred to Phase 11 — Milestone 4, Slice 2 of 2)
- `FoodItem` duplicate-merge management tools (`MergeFoodItems`) in the Food Catalog.
- Targeted 1-click local LLM kitchen helpers (*Suggest Ingredient Substitution* & *Draft Weekly Meal Plan* with live LLM host online/offline gating).

### 1.1 File Inventory (Modify & Create)

#### Existing Backend & Test Files to Modify
- `Core/Media/MediaModels.cs` — Add `UploadedCookLogPhotoDto`.
- `Core/Media/IMediaStorageService.cs` — Add `SaveCookLogPhotoAsync`.
- `Core/Recipes/RecipeModels.cs` — Add trailing optional `LastCookedAt`, `RatingCount`, `LatestCookNote` to `RecipeSummaryDto`; trailing optional `RatingCount`, `CookLogs` to `RecipeDetailDto`; trailing optional `MinRating`, `ForgottenFavoritesOnly`, `ForgottenDaysThreshold` to `RecipeQueryParameters`.
- `Core/Recipes/ShareLinkModels.cs` — Add `PublicCookLogDto` and trailing optional `TimesCooked`, `AverageRating`, `RatingCount`, `CookLogs` on `PublicSharedRecipeDto`.
- `Data/Entities/Recipe.cs` — Add `RatingCount` (`int`) and `ICollection<CookLog> CookLogs`.
- `Data/Entities/User.cs` — Add `ICollection<CookLog> CookLogs`.
- `Data/Configurations/RecipeConfiguration.cs` — Add composite index on `(TimesCooked, AverageRating, LastCookedAt)`.
- `Data/AppDbContext.cs` — Add `DbSet<CookLog> CookLogs`.
- `Data/DataServiceCollectionExtensions.cs` — Register `ICookLogService` $\rightarrow$ `CookLogService`.
- `Data/Services/RecipeService.cs` — Inject optional `TimeProvider? timeProvider = null`; add `MinRating`, `ForgottenFavoritesOnly`, and `CookLogs.Notes` search to `GetRecipesAsync`; include `CookLogs` and `RatingCount` in `GetRecipeByIdAsync`; delete cook-log photo files in `DeleteRecipeAsync`.
- `Data/Services/ShareLinkService.cs` — Populate sanitized `PublicCookLogDto` list and rating summary when `shareLink.IncludeCookLogs == true`.
- `Data/Services/McpCookbookService.cs` — Populate `CookHistoryNotes` in `GetRecipeAsync` and include `CookLogs.Notes` in `SearchRecipesAsync` keyword predicate.
- `Infrastructure/Media/LocalFileMediaStorageService.cs` — Implement `SaveCookLogPhotoAsync` (`/api/uploads/cook-logs/{token}-photo.webp` & `{token}-thumb.webp`).
- `API/Controllers/MediaController.cs` — Add `POST /api/media/cook-log-photo`.
- `Tests/Core.UnitTests/McpCookbookServiceTests.cs` — Implement `SaveCookLogPhotoAsync` on `DummyMediaStorageService` and add cook-history MCP assertion.
- `Tests/Infrastructure.UnitTests/WebRecipeScraperTests.cs` — Implement `SaveCookLogPhotoAsync` on `MockMediaStorageService`.

#### New Backend & Backend Test Files to Create
- `Core/Recipes/CookLogModels.cs`
- `Core/Recipes/ICookLogService.cs`
- `Data/Entities/CookLog.cs`
- `Data/Configurations/CookLogConfiguration.cs`
- `Data/Services/CookLogService.cs`
- `Data/Migrations/<Timestamp>_AddCookLogs.cs` (+ `.Designer.cs` & `AppDbContextModelSnapshot.cs` update)
- `API/Controllers/RecipeCookLogsController.cs`
- `Tests/Core.UnitTests/CookLogServiceTests.cs`
- `Tests/API.IntegrationTests/CookLogsAndDiscoveryIntegrationTests.cs`

#### Existing Frontend Files to Modify (`Client/`)
- `Client/src/app/app.routes.ts` — Register top-level `recipes/:id/cook` route (`canActivate: [authGuard]`) lazy-loading `CookModeComponent`.
- `Client/src/app/core/models/media.models.ts` — Add `UploadedCookLogPhotoDto`.
- `Client/src/app/core/models/recipe.models.ts` — Add `CookLogDto`, `RecipeCookStatsSummaryDto`, `CookLogMutationResultDto`, `UpsertCookLogRequest`, and extend `RecipeSummaryDto`, `RecipeDetailDto`, `RecipeQueryParameters`.
- `Client/src/app/core/models/share-link.models.ts` — Add `PublicCookLogDto` and extend `PublicSharedRecipeDto`.
- `Client/src/app/core/services/media/media.service.ts` (+ `.spec.ts`) — Add `uploadCookLogPhoto(file: File)`.
- `Client/src/app/core/services/recipe/recipe.service.ts` (+ `.spec.ts`) — Pass `minRating` and `forgottenFavoritesOnly` query params in `getRecipes`; add `getCookLogs`, `createCookLog`, `updateCookLog`, `deleteCookLog`.
- `Client/src/app/core/utils/culinary-math.ts` (+ `.spec.ts`) — Add `formatCountdown(totalSeconds: number)` helper (`MM:SS` / `H:MM:SS`).
- `Client/src/app/features/shell/app-shell/app-shell.component.ts` (+ `.html`, `.scss`) — Show compact non-intrusive active kitchen timer pill in top bar when `kitchenTimerService.timers().length > 0` so cooks can jump straight back to Cook Mode.
- `Client/src/app/features/recipes/recipe-detail/recipe-detail.component.ts` (+ `.html`, `.scss`, `.spec.ts`) — Add primary **Start Cooking** CTA, **I Made This!** button, hero rating/cook stats, prominent **Latest Kitchen Note** banner, and **Household Cook History** timeline with edit/delete and photo lightbox.
- `Client/src/app/features/recipes/recipe-list/recipe-list.component.ts` (+ `.html`, `.scss`, `.spec.ts`) — Add **Forgotten Favorites** filter pill, **Min Rating** selector, debounced backend search (matching ingredients & cook log notes), and star-rating badges on recipe cards.
- `Client/src/app/features/meal-plans/meal-plan-calendar/meal-plan-calendar.component.ts` (+ `.html`, `.scss`, `.spec.ts`) — Add 1-tap **Start Cooking** button on Today's scheduled recipe cards plus `Start Cooking` and `Log a Cook` menu actions on recipe meal cards.
- `Client/src/app/features/recipes/public-shared-recipe/public-shared-recipe.component.html` (+ `.scss`, `.spec.ts`) — Render read-only **Household Cook Notes & Ratings** section when `includeCookLogs === true && cookLogs.length > 0`.

#### New Frontend & E2E Files to Create
- `Client/src/app/core/services/wake-lock/wake-lock.service.ts` (+ `.spec.ts`)
- `Client/src/app/core/services/kitchen-timer/kitchen-timer.service.ts` (+ `.spec.ts`)
- `Client/src/app/features/recipes/cook-mode/cook-mode.component.ts` (+ `.html`, `.scss`, `.spec.ts`)
- `Client/src/app/features/recipes/cook-log-dialog/cook-log-dialog.component.ts` (+ `.html`, `.scss`, `.spec.ts`)
- `Tests/Client.E2ETests/CookModeAndCookLogsE2ETests.cs`

---

## 2. Domain & Database Design (`Core/`, `Data/`, & `Infrastructure/`)

### 2.1 Entities (`Data/Entities/`)

1. **Create `Data/Entities/CookLog.cs`** (`namespace App.Data.Entities`):
   ```csharp
   namespace App.Data.Entities;

   public sealed class CookLog : IAuditable
   {
       public Guid Id { get; set; } = Guid.NewGuid();

       public Guid RecipeId { get; set; }

       public Guid? CookedByUserId { get; set; }

       public string CookedByDisplayName { get; set; } = "Household Member";

       public DateTimeOffset CookedAt { get; set; }

       public decimal? ServingsMade { get; set; }

       public int? Rating { get; set; }

       public string? Notes { get; set; }

       public string? PhotoImagePath { get; set; }

       public string? PhotoThumbnailPath { get; set; }

       public DateTimeOffset CreatedAt { get; set; }

       public string CreatedBy { get; set; } = "System";

       public DateTimeOffset ModifiedAt { get; set; }

       public string ModifiedBy { get; set; } = "System";

       public Recipe Recipe { get; set; } = null!;

       public User? CookedByUser { get; set; }
   }
   ```

2. **Update `Data/Entities/Recipe.cs`**:
   - Add `public int RatingCount { get; set; }` below `AverageRating`.
   - Add navigation `public ICollection<CookLog> CookLogs { get; set; } = new List<CookLog>();`.

3. **Update `Data/Entities/User.cs`**:
   - Add navigation `public ICollection<CookLog> CookLogs { get; set; } = new List<CookLog>();`.

4. **Update `Data/AppDbContext.cs`**:
   - Add `public DbSet<CookLog> CookLogs => Set<CookLog>();`.

### 2.2 EF Core Fluent Configurations (`Data/Configurations/`)

1. **Create `Data/Configurations/CookLogConfiguration.cs`** (`namespace App.Data.Configurations`):
   - Table: `"CookLogs"`, primary key `Id`.
   - `CookedByDisplayName`: `IsRequired().HasMaxLength(150)`.
   - `ServingsMade`: `HasPrecision(8, 2)`.
   - `Notes`: `HasMaxLength(2000)`.
   - `PhotoImagePath`: `HasMaxLength(500)`.
   - `PhotoThumbnailPath`: `HasMaxLength(500)`.
   - `CreatedBy` & `ModifiedBy`: `IsRequired().HasMaxLength(256)`.
   - Indexes:
     - `builder.HasIndex(c => new { c.RecipeId, c.CookedAt });`
     - `builder.HasIndex(c => c.CookedByUserId);`
   - Foreign Keys (**SQL Server Cascade Safety**):
     - `Recipe`: `builder.HasOne(c => c.Recipe).WithMany(r => r.CookLogs).HasForeignKey(c => c.RecipeId).OnDelete(DeleteBehavior.Cascade);`
     - `CookedByUser`: `builder.HasOne(c => c.CookedByUser).WithMany(u => u.CookLogs).HasForeignKey(c => c.CookedByUserId).OnDelete(DeleteBehavior.SetNull);` (safe on SQL Server because `Recipes` has no FK to `Users`, so no multiple cascade paths exist).

2. **Update `Data/Configurations/RecipeConfiguration.cs`**:
   - Add index `builder.HasIndex(r => new { r.TimesCooked, r.AverageRating, r.LastCookedAt });`.

3. **EF Core Migration**:
   ```powershell
   .\scripts\ensure-local-infra.ps1
   dotnet ef migrations add AddCookLogs --project Data/App.Data.csproj
   dotnet ef database update --project Data/App.Data.csproj
   dotnet csharpier format Data/Migrations
   ```

### 2.3 DTOs, Requests & Service Contracts (`Core/`)

1. **Create `Core/Recipes/CookLogModels.cs`** (`namespace App.Core.Recipes`):
   ```csharp
   using System.ComponentModel.DataAnnotations;

   namespace App.Core.Recipes;

   public record CookLogDto(
       Guid Id,
       Guid RecipeId,
       Guid? CookedByUserId,
       string CookedByDisplayName,
       DateTimeOffset CookedAt,
       decimal? ServingsMade,
       int? Rating,
       string? Notes,
       string? PhotoImagePath,
       string? PhotoThumbnailPath,
       DateTimeOffset CreatedAt,
       DateTimeOffset ModifiedAt
   );

   public record RecipeCookStatsSummaryDto(
       Guid RecipeId,
       int TimesCooked,
       DateTimeOffset? LastCookedAt,
       decimal? AverageRating,
       int RatingCount,
       string? LatestCookNote
   );

   public record CookLogMutationResultDto(
       CookLogDto CookLog,
       RecipeCookStatsSummaryDto RecipeStats
   );

   public record UpsertCookLogRequest(
       DateTimeOffset? CookedAt = null,
       [Range(typeof(decimal), "0.25", "999")] decimal? ServingsMade = null,
       [Range(1, 5)] int? Rating = null,
       [MaxLength(2000)] string? Notes = null,
       [MaxLength(500)] string? PhotoImagePath = null,
       [MaxLength(500)] string? PhotoThumbnailPath = null
   );
   ```

2. **Create `Core/Recipes/ICookLogService.cs`** (`namespace App.Core.Recipes`):
   ```csharp
   namespace App.Core.Recipes;

   public interface ICookLogService
   {
       Task<IReadOnlyList<CookLogDto>> GetCookLogsForRecipeAsync(
           Guid recipeId,
           CancellationToken cancellationToken = default
       );

       Task<CookLogMutationResultDto> CreateCookLogAsync(
           Guid recipeId,
           UpsertCookLogRequest request,
           CancellationToken cancellationToken = default
       );

       Task<CookLogMutationResultDto?> UpdateCookLogAsync(
           Guid recipeId,
           Guid cookLogId,
           UpsertCookLogRequest request,
           CancellationToken cancellationToken = default
       );

       Task<bool> DeleteCookLogAsync(
           Guid recipeId,
           Guid cookLogId,
           CancellationToken cancellationToken = default
       );
   }
   ```

3. **Update `Core/Recipes/RecipeModels.cs`**:
   - Append trailing optional parameters to `RecipeSummaryDto` (preserving backward compatibility with all existing positional callers):
     ```csharp
     DateTimeOffset? LastCookedAt = null,
     int RatingCount = 0,
     string? LatestCookNote = null
     ```
   - Append trailing optional parameters to `RecipeDetailDto`:
     ```csharp
     int RatingCount = 0,
     IReadOnlyList<CookLogDto>? CookLogs = null
     ```
     and inside the body add `public IReadOnlyList<CookLogDto> CookLogs { get; init; } = CookLogs ?? [];`.
   - Append trailing optional parameters to `RecipeQueryParameters`:
     ```csharp
     decimal? MinRating = null,
     bool ForgottenFavoritesOnly = false,
     int ForgottenDaysThreshold = 60
     ```

4. **Update `Core/Recipes/ShareLinkModels.cs`**:
   - Add `PublicCookLogDto` (strictly omitting user IDs/names/emails):
     ```csharp
     public record PublicCookLogDto(
         DateTimeOffset CookedAt,
         int? Rating,
         string? Notes,
         string? PhotoImagePath,
         string? PhotoThumbnailPath
     );
     ```
   - Append trailing optional parameters to `PublicSharedRecipeDto`:
     ```csharp
     int TimesCooked = 0,
     decimal? AverageRating = null,
     int RatingCount = 0,
     IReadOnlyList<PublicCookLogDto>? CookLogs = null
     ```
     and inside the body add `public IReadOnlyList<PublicCookLogDto> CookLogs { get; init; } = CookLogs ?? [];`.

5. **Update `Core/Media/MediaModels.cs` & `Core/Media/IMediaStorageService.cs`**:
   - Add `public record UploadedCookLogPhotoDto(string PhotoImagePath, string PhotoThumbnailPath);`.
   - Add `Task<UploadedCookLogPhotoDto> SaveCookLogPhotoAsync(Stream content, string originalFileName, long contentLength, CancellationToken cancellationToken = default);`.

### 2.4 Service Business Logic & Invariants (`Data/Services/` & `Infrastructure/Media/`)

1. **`Infrastructure/Media/LocalFileMediaStorageService.cs` (`SaveCookLogPhotoAsync`)**:
   - Validates content length via `ValidateImageContentLength(contentLength)` and decodes/EXIF-orients via `LoadAndNormalizeImageAsync(content, cancellationToken)`.
   - Writes `{token}-thumb.webp` (`600x450` max) and `{token}-photo.webp` (`1600x1200` max) inside `{_uploadsRoot}/cook-logs/`.
   - Returns `new UploadedCookLogPhotoDto($"/api/uploads/cook-logs/{token}-photo.webp", $"/api/uploads/cook-logs/{token}-thumb.webp")`.

2. **`Data/Services/CookLogService.cs`**:
   - Primary constructor: `public sealed class CookLogService(AppDbContext dbContext, IMediaStorageService mediaStorageService, IUserContext? userContext = null, TimeProvider? timeProvider = null) : ICookLogService`.
   - Register `services.AddScoped<ICookLogService, CookLogService>();` in `Data/DataServiceCollectionExtensions.cs`.
   - **Validation Rules (`ValidateRequest`)**:
     - `Rating`: when non-null, must satisfy `1 <= Rating <= 5` (`DomainValidationException` otherwise).
     - `ServingsMade`: when non-null, must satisfy `0.25m <= ServingsMade <= 999m` (`DomainValidationException` otherwise).
     - `Notes`: trimmed; whitespace-only normalized to `null`; length `<= 2000` (`DomainValidationException` otherwise).
     - `PhotoImagePath` & `PhotoThumbnailPath`: when non-null/non-whitespace, must start with `"/api/uploads/cook-logs/"` and must not contain `".."` or `'\\'` (`DomainValidationException` otherwise).
     - `CookedAt`: defaults to `_timeProvider.GetUtcNow()` on create when omitted; rejects timestamps more than 24 hours ahead of `_timeProvider.GetUtcNow()` (`DomainValidationException("Cook date cannot be in the future.")`).
   - **Actor Attribution (`ResolveActorAsync`)**:
     - Parses `userContext?.UserId` as `Guid`, looks up `Users.AsNoTracking()`, and returns `(user.Id, user.Username)` (falling back to `(null, "Household Member")` when unauthenticated in direct unit tests).
   - **Atomic Stats Recomputation (`ApplyRecomputedStats`)**:
     - Given the recipe's full post-mutation list of `CookLog`s:
       - `recipe.TimesCooked = allLogs.Count;`
       - `recipe.LastCookedAt = allLogs.Count > 0 ? allLogs.Max(l => (DateTimeOffset?)l.CookedAt) : null;`
       - `var rated = allLogs.Where(l => l.Rating.HasValue).Select(l => l.Rating!.Value).ToList();`
       - `recipe.RatingCount = rated.Count;`
       - `recipe.AverageRating = rated.Count > 0 ? Math.Round((decimal)rated.Average(), 2, MidpointRounding.AwayFromZero) : null;`
       - `latestCookNote` = `allLogs.Where(l => !string.IsNullOrWhiteSpace(l.Notes)).OrderByDescending(l => l.CookedAt).ThenByDescending(l => l.CreatedAt).Select(l => l.Notes).FirstOrDefault()`.
   - **Media Cleanup**:
     - `UpdateCookLogAsync`: deletes previous `PhotoImagePath` / `PhotoThumbnailPath` via `mediaStorageService.DeleteMediaFileAsync` whenever replaced or cleared.
     - `DeleteCookLogAsync`: deletes `PhotoImagePath` / `PhotoThumbnailPath` after `SaveChangesAsync`.

3. **`Data/Services/RecipeService.cs`, `ShareLinkService.cs` & `McpCookbookService.cs`**:
   - `RecipeService`:
     - Inject optional `TimeProvider? timeProvider = null`.
     - `GetRecipesAsync`:
       - Filter by `MinRating` when provided (`r.AverageRating != null && r.AverageRating >= query.MinRating.Value`).
       - Filter by `ForgottenFavoritesOnly` when `true`: `cutoff = _timeProvider.GetUtcNow().AddDays(-Math.Clamp(query.ForgottenDaysThreshold, 1, 3650))` and `((r.AverageRating != null && r.AverageRating >= 4.0m) || r.TimesCooked >= 2) && (r.LastCookedAt == null || r.LastCookedAt <= cutoff)`.
       - Extend `Search` predicate with `|| r.CookLogs.Any(cl => cl.Notes != null && cl.Notes.ToLower().Contains(norm))`.
       - Sort `ForgottenFavoritesOnly` results by `r.LastCookedAt` ascending then `r.AverageRating` descending; keep `OrderByDescending(r => r.ModifiedAt)` otherwise.
       - Populate `LastCookedAt`, `RatingCount`, and `LatestCookNote` on `RecipeSummaryDto`.
     - `GetRecipeByIdAsync`: load `CookLogs` (`AsNoTracking()`, ordered by `CookedAt` desc, `CreatedAt` desc) and populate `RatingCount` + `CookLogs`.
     - `DeleteRecipeAsync`: collect `CookLogs` `PhotoImagePath` and `PhotoThumbnailPath` paths prior to removal and delete them via `mediaStorageService.DeleteMediaFileAsync` post-commit.
   - `ShareLinkService.GetPublicSharedRecipeByTokenAsync`:
     - When `shareLink.IncludeCookLogs == true`, query up to 25 most recent `CookLogs` (`AsNoTracking()`) for `recipe.Id` and map to `PublicCookLogDto`, passing `recipe.TimesCooked`, `recipe.AverageRating`, `recipe.RatingCount`, and `publicCookLogs`.
     - When `shareLink.IncludeCookLogs == false`, return `TimesCooked: 0, AverageRating: null, RatingCount: 0, CookLogs: []`.
   - `McpCookbookService`:
     - `SearchRecipesAsync`: add `|| r.CookLogs.Any(cl => cl.Notes != null && cl.Notes.ToLower().Contains(norm))` to keyword search.
     - `GetRecipeAsync`: populate `CookHistoryNotes` from `detail.CookLogs` (`CookedAt`, `CookedByDisplayName`, `Rating`, `Notes`).

---

## 3. API Endpoints (`API/Controllers/`)

| Verb | Route (`/api/...`) | Request Body / Query | Response (`200`/`201`/`204`) | Error Cases (`400`/`401`/`404`) |
| :--- | :--- | :--- | :--- | :--- |
| `GET` | `/recipes` | Extended query: `minRating?`, `forgottenFavoritesOnly?`, `forgottenDaysThreshold?` | `200 OK` (`IReadOnlyList<RecipeSummaryDto>`) | `401 Unauthorized` |
| `GET` | `/recipes/{recipeId:guid}/cook-logs` | — | `200 OK` (`IReadOnlyList<CookLogDto>`) | `401`, `404 Not Found` (recipe missing) |
| `POST` | `/recipes/{recipeId:guid}/cook-logs` | `UpsertCookLogRequest` (JSON) | `201 Created` (`CookLogMutationResultDto`) | `400 Bad Request` (`ProblemDetails`), `401`, `404 Not Found` |
| `PUT` | `/recipes/{recipeId:guid}/cook-logs/{cookLogId:guid}` | `UpsertCookLogRequest` (JSON) | `200 OK` (`CookLogMutationResultDto`) | `400 Bad Request`, `401`, `404 Not Found` |
| `DELETE` | `/recipes/{recipeId:guid}/cook-logs/{cookLogId:guid}` | — | `204 No Content` | `401`, `404 Not Found` |
| `POST` | `/media/cook-log-photo` | `multipart/form-data` (`IFormFile file`, `16MB` limit) | `200 OK` (`UploadedCookLogPhotoDto`) | `400 Bad Request` (`ProblemDetails`), `401` |

- Keep `RecipeCookLogsController` (`[ApiController]`, `[Route("recipes/{recipeId:guid}/cook-logs")]`, `[Authorize]`) and `MediaController` strictly thin HTTP adapters delegating all validation and persistence to `ICookLogService` and `IMediaStorageService`.

---

## 4. Angular Frontend Design (`Client/`)

### 4.1 Models, Utilities & Core Services (`src/app/core/`)

1. **Models (`recipe.models.ts`, `media.models.ts`, `share-link.models.ts`)**:
   - `CookLogDto`, `RecipeCookStatsSummaryDto`, `CookLogMutationResultDto`, `UpsertCookLogRequest`, `UploadedCookLogPhotoDto`, `PublicCookLogDto`.
   - Extend `RecipeSummaryDto` (`lastCookedAt?: string | null`, `ratingCount?: number`, `latestCookNote?: string | null`), `RecipeDetailDto` (`ratingCount?: number`, `cookLogs?: CookLogDto[]`), `RecipeQueryParameters` (`minRating?: number | null`, `forgottenFavoritesOnly?: boolean`, `forgottenDaysThreshold?: number`), and `PublicSharedRecipeDto` (`timesCooked?: number`, `averageRating?: number | null`, `ratingCount?: number`, `cookLogs?: PublicCookLogDto[]`).

2. **`WakeLockService` (`src/app/core/services/wake-lock/wake-lock.service.ts`)**:
   - Decorated with `@Service()`.
   - Exposes `readonly status = signal<'active' | 'unsupported' | 'released'>('released')`.
   - `acquire()`: requests `navigator.wakeLock.request('screen')` when supported, updates `status.set('active')`, attaches a `document` `visibilitychange` listener to automatically re-acquire `'screen'` wake lock when returning to the tab while Cook Mode remains mounted, and gracefully sets `'unsupported'` (never throwing) if unavailable or denied.
   - `release()`: detaches listener, releases active `WakeLockSentinel`, and sets `status.set('released')`.

3. **`KitchenTimerService` (`src/app/core/services/kitchen-timer/kitchen-timer.service.ts`)**:
   - Decorated with `@Service()`.
   - Manages `readonly timers = signal<ActiveKitchenTimer[]>([])` and `readonly hasCompletedAlert = computed(...)`.
   - Uses wall-clock `endsAtMs` timestamps ticked every `250ms` while at least one timer is `'running'` so background tab throttling never causes drift.
   - Methods: `startTimer(...)`, `pauseTimer(id)`, `resumeTimer(id)`, `addSeconds(id, deltaSeconds = 60)` (adds `+1 min` and resumes `'running'` even if timer was `'completed'`), `resetTimer(id)`, `dismissTimer(id)`, `clearAll()`.
   - Plays a pleasant 3-note Web Audio API chime (`AudioContext` inside `try/catch`) and optional `Notification` when a timer reaches `0`.
   - Add `formatCountdown(totalSeconds: number): string` in `src/app/core/utils/culinary-math.ts` (`15:00`, `04:09`, `1:05:00`).

4. **`RecipeService` & `MediaService` Updates**:
   - `RecipeService`: add `getCookLogs(recipeId)`, `createCookLog(recipeId, request)`, `updateCookLog(recipeId, cookLogId, request)`, `deleteCookLog(recipeId, cookLogId)`, and query param serialization for `minRating`, `forgottenFavoritesOnly`, and `forgottenDaysThreshold`.
   - `MediaService`: add `uploadCookLogPhoto(file: File): Observable<UploadedCookLogPhotoDto>` posting to `apiUrl('/media/cook-log-photo')`.

### 4.2 Feature Components & Routes (`src/app/features/`)

Every component strictly uses its own dedicated kebab-case directory with external `.ts`, `.html`, `.scss`, and `.spec.ts` files, `@Service()` / `inject()`, Signals, Material Symbols Outlined icons, and Material 3 CSS variables (`--mat-sys-*`, `--app-success*`, `--app-warning*`):

1. **Top-Level Route (`src/app/app.routes.ts`)**:
   - Register `{ path: 'recipes/:id/cook', canActivate: [authGuard], loadComponent: () => import('./features/recipes/cook-mode/cook-mode.component').then(m => m.CookModeComponent) }` above the `AppShellComponent` route so Cook Mode owns 100% of the viewport height without duplicate nav bars.

2. **`CookModeComponent` (`src/app/features/recipes/cook-mode/`)**:
   - Reads `:id` route param and optional query params `?servings=<number>&units=<Original|UsCustomary|Metric>&from=<meal-plan|recipe>`.
   - Calls `wakeLockService.acquire()` on init and `wakeLockService.release()` on `ngOnDestroy`.
   - **Header Bar (`.cook-top-bar`)**:
     - `Exit` button (`routerLink` back to `/meal-plan` when `from === 'meal-plan'`, otherwise `['/recipes', id]`), recipe title, `.wake-lock-pill` (`Screen Awake` when `status() === 'active'`, or `Standard Screen` when unsupported), compact servings stepper (`-` / `+`, `0.5x`–`3x`), `Original | US | Metric` unit toggle, and `Log Cook` button.
   - **Latest Kitchen Note Callout (`.cook-tweak-callout`)**:
     - Dismissible banner surfacing the most recent non-empty `CookLog.notes` + star rating right at the start of cooking.
   - **Responsive Counter Workspace**:
     - **Desktop / Tablet (`> 900px`)**: Two-column grid with pinned scrollable `.cook-ingredients-pane` on the left (grouped scaled ingredients with checkboxes + expandable linked Sub-Recipes) and `.cook-steps-pane` on the right.
     - **Mobile (`<= 900px`, verified at `360px`)**: Full-screen step focus with an `Ingredients (checked/total)` button toggling `.mobile-ingredients-drawer` (slide-up sheet with backdrop).
   - **Active Step Focus & Navigation**:
     - Progress header (`Step X of N`), oversized active `.cook-step-card.is-active` with high-contrast typography, step completion checkbox, optional step reference image, inline scaled `.cook-step-ingredient-chip` callouts, and 1-tap `.start-step-timer-btn` pills (`Start 15 min · Simmer sauce`).
     - Keyboard `ArrowLeft` / `ArrowRight` host listeners (`host: { '(window:keydown)': 'onWindowKeydown($event)' }`) + oversized thumb/knuckle-friendly `Previous Step` / `Next Step` buttons (`min-height: 52px`), switching to primary **`Finish & Log Cook`** on the final step.
   - **Sticky Floating Multi-Timer Bar (`.floating-timer-tray`)**:
     - Displays all active/paused/completed timers from `KitchenTimerService` with monospace countdown, `+1m` bump button, `Pause`/`Resume`, `Reset`, `Dismiss`, plus a `+ Custom Timer` quick-adder (minutes + label).

3. **`CookLogDialogComponent` (`src/app/features/recipes/cook-log-dialog/`)**:
   - Supports both **Create ("I Made This!")** and **Edit Cook Log** modes.
   - Interactive `1–5` star rating selector (with optional clear to unrated), `Cooked Date` input (defaulting to today), `Servings Made` numeric input (defaulting to Cook Mode / Recipe `targetServings`), `Cook's Notes & Tweaks` `<textarea>` (`maxlength="2000"`), and optional **Dish Photo** upload (`uploadCookLogPhoto` with preview + remove button).

4. **`RecipeDetailComponent`, `RecipeListComponent`, `MealPlanCalendarComponent`, `PublicSharedRecipeComponent` & `AppShellComponent` Updates**:
   - `RecipeDetailComponent`:
     - Add primary **`Start Cooking`** button (`mat-flat-button`, `.start-cooking-btn`) and **`I Made This!`** button (`.log-cook-btn`) in `.detail-top-actions`.
     - Display `AverageRating` (`★ 4.8 (3)`) and `TimesCooked` in `.recipe-stats-strip`.
     - Render `.latest-tweak-banner` above the scaling toolbar when a cook log note exists.
     - Render `.cook-history-section` (`no-print`) listing household `CookLog` entries with star ratings, user attribution (`cookedByDisplayName`), date, servings made, tweak notes, dish photo thumbnail (clickable lightbox), and edit/delete actions.
   - `RecipeListComponent`:
     - Add **`Forgotten Favorites`** toggle chip (`.forgotten-favorites-pill`) and **`Min Rating`** `<mat-select>` (`Any Rating`, `4+ Stars`, `5 Stars`), plus debounced server `search` query execution so Cookbook search matches ingredients, aliases, and cook log notes while keeping synchronous client filtering intact.
     - Render star rating badge (`★ 4.8`) alongside `Cooked Nx` in `.card-footer-row`.
   - `MealPlanCalendarComponent`:
     - Render a 1-tap **`Start Cooking`** button (`.start-cooking-meal-btn`) on Today's scheduled non-leftover recipe cards, and add `Start Cooking` + `Log a Cook` items to recipe card overflow menus (passing `entry.targetServings` into Cook Mode / Cook Log dialog).
   - `PublicSharedRecipeComponent`:
     - When `recipe()!.includeCookLogs && (recipe()!.cookLogs?.length ?? 0) > 0`, render `.public-cook-notes-section` displaying ratings, dates, notes, and dish photos without any user identity fields.
   - `AppShellComponent`:
     - When `kitchenTimerService.timers().length > 0`, show a compact `.active-timer-nav-pill` in `.top-actions` displaying the soonest timer countdown and navigating back to its recipe's Cook Mode on click.

---

## 5. Playwright E2E Test Scenarios (`Tests/Client.E2ETests/CookModeAndCookLogsE2ETests.cs`)

Create `Tests/Client.E2ETests/CookModeAndCookLogsE2ETests.cs` extending `E2ETestBase` with `[TestClass]`:

1. **`[TestMethod] CookMode_WakeLock_LiveScaling_StepNavigation_AndMultiTimers_CompletesAndLogsCook()`**:
   - Installs a deterministic `navigator.wakeLock` spy/shim via `Page.AddInitScriptAsync`, registers & logs in a chef, and creates a published recipe (*"Crispy Honey Garlic Chicken"*, base `4` servings, `Soy Sauce` `2 Tbsp`, `Garlic` `4 Clove`, Step 1 referencing both ingredients with a `15 min` `"Simmer glaze"` timer, Step 2 `"Garnish and serve"`).
   - Clicks **`Start Cooking`** on `RecipeDetailComponent` $\rightarrow$ verifies `/recipes/{id}/cook` renders `.wake-lock-pill.active` (`"Screen Awake"`), pinned ingredients (`2 tbsp Soy Sauce`), and Step 1 inline ingredient callout chips.
   - Clicks **`2x`** multiplier inside Cook Mode $\rightarrow$ verifies both pinned ingredients and Step 1 inline callout chips scale live to `4 tbsp Soy Sauce` and `8 cloves Garlic`, then toggles **`Metric`** (`60 ml`).
   - Starts Step 1's `15 min` `"Simmer glaze"` timer and adds a custom `1 min` `"Toast sesame"` timer $\rightarrow$ asserts `.floating-timer-tray` renders **2 active timers**, clicks `+1m` on `"Simmer glaze"` (verifying countdown exceeds `15:00`), pauses/resumes, and dismisses `"Toast sesame"`.
   - Advances to Step 2 and clicks **`Finish & Log Cook`** $\rightarrow$ fills `CookLogDialogComponent` with **5 stars**, note `"Doubled the lime zest and seared 2 mins longer"`, uploads a PNG dish photo, and saves.
   - Asserts `RecipeDetailComponent` displays `Cooked 1x`, `5` star rating, `.latest-tweak-banner` with the tweak note at the top of the recipe, and `.cook-history-section` with the chef's username and uploaded `/api/uploads/cook-logs/` dish photo.

2. **`[TestMethod] CookLogs_SearchByTweakNote_ForgottenFavoritesFilter_MealPlanStartCooking_AndPublicShareOptIn()`**:
   - Registers & logs in, creates *"Sunday Beef Bourguignon"* (logging two cooks dated 80 and 70 days ago rated `5` and `4` stars with note `"Secret splash of aged balsamic"`) and *"Quick Weeknight Quesadillas"* (logged today with `5` stars).
   - Opens Global Search (`Control+k`), types `"aged balsamic"`, and asserts *"Sunday Beef Bourguignon"* appears via Cook Log note search.
   - Navigates to `/recipes`, toggles **`.forgotten-favorites-pill`** (`"Forgotten Favorites"`), and asserts *"Sunday Beef Bourguignon"* is shown while *"Quick Weeknight Quesadillas"* (cooked today) is excluded.
   - Schedules *"Quick Weeknight Quesadillas"* onto **Today's Dinner** at `8` servings on `/meal-plan`, clicks the 1-tap **`.start-cooking-meal-btn`** on Today's meal card, and verifies Cook Mode launches pre-scaled to **`8` servings**.
   - Creates two Share Links on *"Sunday Beef Bourguignon"* (`includeCookLogs = false` vs `includeCookLogs = true`) and verifies in an anonymous browser context that `/share/{tokenWithoutLogs}` hides `"Secret splash of aged balsamic"` while `/share/{tokenWithLogs}` renders `.public-cook-notes-section` with `"Secret splash of aged balsamic"` and **zero** household username/email leakage.

3. **`[TestMethod] CookMode_MobileViewport_360px_FullScreenSteps_IngredientsDrawer_AndTimerTray_ZeroOverflow()`**:
   - Resizes viewport to `360x640`, registers & logs in, creates a recipe with step timers, and launches Cook Mode (`/recipes/{id}/cook`).
   - Verifies zero horizontal overflow (`document.documentElement.scrollWidth <= document.documentElement.clientWidth`), opens `.mobile-ingredients-drawer` to check off an ingredient, closes the drawer, starts a countdown timer in `.floating-timer-tray`, and verifies clean `360px` layout with zero horizontal overflow.

---

## 6. Step-by-Step Execution Checklist (For Implementation Agent)

- [ ] **Task 1: Backend Core, CookLog Entity, Media Storage & EF Migration**
  - Update `Core/Media/MediaModels.cs`, `Core/Media/IMediaStorageService.cs`, `Core/Recipes/RecipeModels.cs`, and `Core/Recipes/ShareLinkModels.cs`.
  - Create `Core/Recipes/CookLogModels.cs` and `Core/Recipes/ICookLogService.cs`.
  - Create `Data/Entities/CookLog.cs` and `Data/Configurations/CookLogConfiguration.cs`; update `Data/Entities/Recipe.cs`, `Data/Entities/User.cs`, `Data/Configurations/RecipeConfiguration.cs`, and `Data/AppDbContext.cs`.
  - Implement `SaveCookLogPhotoAsync` in `Infrastructure/Media/LocalFileMediaStorageService.cs` (and update test doubles in `Tests/Core.UnitTests/McpCookbookServiceTests.cs` & `Tests/Infrastructure.UnitTests/WebRecipeScraperTests.cs`).
  - Implement `Data/Services/CookLogService.cs`, register `ICookLogService` in `Data/DataServiceCollectionExtensions.cs`, and update `RecipeService.cs`, `ShareLinkService.cs`, and `McpCookbookService.cs`.
  - Generate, apply, and format the EF Core migration:
    ```powershell
    .\scripts\ensure-local-infra.ps1
    dotnet ef migrations add AddCookLogs --project Data/App.Data.csproj
    dotnet ef database update --project Data/App.Data.csproj
    dotnet csharpier format Data/Migrations
    ```
  - Write unit tests in `Tests/Core.UnitTests/CookLogServiceTests.cs` (and update `McpCookbookServiceTests.cs` + `LocalFileMediaStorageServiceTests.cs`), then run unit tests until 100% green:
    ```powershell
    dotnet test --project Tests/Core.UnitTests/App.Core.UnitTests.csproj
    dotnet test --project Tests/Infrastructure.UnitTests/App.Infrastructure.UnitTests.csproj
    ```

- [ ] **Task 2: API Controllers & Integration Tests**
  - Create `API/Controllers/RecipeCookLogsController.cs` and add `POST /api/media/cook-log-photo` to `API/Controllers/MediaController.cs`.
  - Create `Tests/API.IntegrationTests/CookLogsAndDiscoveryIntegrationTests.cs` covering CookLog CRUD, validation (`400`/`404`), dish photo upload, `Recipe` rating/cook-count recomputation, CookLog note search, `ForgottenFavoritesOnly` & `MinRating` filtering, `ShareLink` `IncludeCookLogs` privacy boundary, and MCP `get_recipe` `CookHistoryNotes`.
  - Run API integration tests until 100% green:
    ```powershell
    dotnet test --project Tests/API.IntegrationTests/App.API.IntegrationTests.csproj
    ```

- [ ] **Task 3: Angular Cook Mode, Timers, Cook Logs UI & Unit Tests**
  - Update `media.models.ts`, `recipe.models.ts`, `share-link.models.ts`, `culinary-math.ts`, `media.service.ts`, and `recipe.service.ts`.
  - Create `WakeLockService` (`core/services/wake-lock/`) and `KitchenTimerService` (`core/services/kitchen-timer/`) with `.spec.ts` unit tests.
  - Create `CookModeComponent` (`features/recipes/cook-mode/`) and `CookLogDialogComponent` (`features/recipes/cook-log-dialog/`) with dedicated `.ts`, `.html`, `.scss`, and `.spec.ts` files, and register `recipes/:id/cook` in `app.routes.ts`.
  - Update `RecipeDetailComponent`, `RecipeListComponent`, `MealPlanCalendarComponent`, `PublicSharedRecipeComponent`, and `AppShellComponent` (+ `.spec.ts` tests).
  - Run frontend lint, production build, and unit tests until 100% green:
    ```powershell
    # In Client/:
    npm run lint
    npm run build:production
    npm test -- --watch=false
    ```

- [ ] **Task 4: Playwright E2E Verification & Final Formatting**
  - Create `Tests/Client.E2ETests/CookModeAndCookLogsE2ETests.cs` implementing all 3 scenarios from Section 5.
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
