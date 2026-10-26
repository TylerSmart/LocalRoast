# Phase 03: Print-Friendly Recipe Cards & Revocable Read-Only Share Links

> **Branch:** `feature/phase-03-print-and-share-links`
> **Status:** `[ ]` Planned (`[ ]` Not Started | `[~]` In Progress | `[x]` Completed)
> **PRD Reference:** `docs/prd.md` — Sections 2, 3, 4.1.5, 4.7.3, 5, and 6 (Milestone 1, Slice 3 — Completes Milestone 1)
> **Target Execution:** Run `/execute-phase` to execute this plan step-by-step.

---

## 1. Scope & Objectives

This phase completes **Milestone 1** of **LocalRoast**, delivering revocable external recipe sharing for friends and family alongside crisp, ink-friendly 1–2 page printed recipe cards across both household and public views:

1. **Revocable Read-Only External Share Links (`ShareLink`, `IShareLinkService`, `/share/:token`):**
   - Any authenticated household user can generate one or more named, revocable, optionally expiring share links (`ShareLink`) for any `Published` recipe using a cryptographically random 192-bit URL-safe token (`ShareTokenGenerator`).
   - Household share management (`RecipeShareDialogComponent` launched from `RecipeDetailComponent`) supports optional link labels (e.g., *"Mom & Dad"*, *"Thanksgiving Potluck"*), quick expiration presets (*Never*, *24 hours*, *7 days*, *30 days*), an `IncludeCookLogs` toggle (persisted now for Milestone 4 Cook Logs), 1-click clipboard copy, view counters (`ViewCount`, `LastAccessedAt`), instant revocation, and deletion of old revoked/expired links.
   - Visiting `/share/:token` (`PublicSharedRecipeComponent` backed by `[AllowAnonymous]` `GET /api/public/shares/{token}`) renders an unauthenticated, read-only view scoped **strictly** to that single `Published` recipe—supporting dynamic serving scaling (`0.5x`, `1x`, `1.5x`, `2x`, `3x`, stepper), `Original` / `UsCustomary` / `Metric` unit conversion, inline sub-recipe ingredient/step expansion (only for `Published` sub-recipes), checkable prep rows, Light/Dark theme toggle, and Print View—while exposing **zero** household user emails/usernames, `CreatedBy`/`ModifiedBy` audit IDs, household collections, unpublished recipes, meal plans, or shopping lists.
   - Public share responses set `Cache-Control: no-store` and `X-Robots-Tag: noindex, nofollow` so revoking a link takes effect immediately without browser/proxy cache leakage and prevents search engine indexing.
2. **Print-Friendly Recipe Cards (`@media print` Stylesheet & 1-Click Print Action):**
   - Add a 1-click **Print** action (`window.print()`) on both `RecipeDetailComponent` (`/recipes/:id`) and `PublicSharedRecipeComponent` (`/share/:token`).
   - Implement global (`Client/src/styles.scss`) and component-level `@media print` stylesheets that force crisp black-on-white paper output (even when the user's UI is in Dark Mode), strip all navigation bars, action buttons, interactive steppers, overlays, and ink-heavy photos, reveal a compact `.print-only-meta-line` displaying the active scaled yield and unit system, and arrange scaled ingredients + numbered steps into an ink-friendly 1–2 page layout with `break-inside: avoid`.
   - Polish `RecipeDetailComponent` scaling toolbar (`value="UsCustomary"` toggle alignment with `UnitSystem`, `1.5x` multiplier chip per PRD 4.1.5, and complete `.scaling-toolbar-card` SCSS rules).

### Explicitly Out of Scope (Deferred to Milestone 2+)
- Local LLM / `Ollama` & OpenAI-compatible `/v1` provider settings, persistent `IngestionJob` SQL queue, Side-by-Side Review Inbox, Web URL / Text / Vision / PDF extraction, Personal Access Tokens (`lr_pat_...`), and `/api/mcp` server (**Milestone 2**).
- Multi-slot Meal Planning Calendar, `"On-Deck"` tray, Week Templates, Consolidated Shopping Lists, Store Layouts, and SignalR live co-shopping (**Milestone 3**).
- Wake-locked Hands-Free Cook Mode, live countdown multi-timers, `CookLog` ratings/notes/photos (which will plug into `ShareLink.IncludeCookLogs`), and 1-click AI Kitchen Helpers (**Milestone 4**).
- Calorie / macronutrient fields (deferred per `docs/prd.md` Section 2).

### 1.1 File Inventory (Modify & Create)

#### Existing Backend & Test Files to Modify
- `Data/Entities/Recipe.cs` — Add `public ICollection<ShareLink> ShareLinks { get; set; } = new List<ShareLink>();`.
- `Data/AppDbContext.cs` — Add `public DbSet<ShareLink> ShareLinks => Set<ShareLink>();`.
- `Data/DataServiceCollectionExtensions.cs` — Register `services.AddScoped<IShareLinkService, ShareLinkService>();`.
- `Data/Services/RecipeService.cs` — Remove associated `ShareLinks` explicitly in `DeleteRecipeAsync` prior to removing `Recipe` so both EF Core InMemory and SQL Server stay immediately consistent.
- `Tests/API.IntegrationTests/OpenApiIntegrationTests.cs` — Assert `/recipes/{recipeId}/shares` and `/public/shares/{token}` paths exist in `/api/openapi/v1.json`.

#### New Backend & Backend Test Files to Create
- `Core/Recipes/ShareTokenGenerator.cs`
- `Core/Recipes/ShareLinkModels.cs`
- `Core/Recipes/IShareLinkService.cs`
- `Data/Entities/ShareLink.cs`
- `Data/Configurations/ShareLinkConfiguration.cs`
- `Data/Services/ShareLinkService.cs`
- `API/Controllers/RecipeSharesController.cs`
- `API/Controllers/PublicSharesController.cs`
- `Tests/Core.UnitTests/ShareTokenGeneratorTests.cs`
- `Tests/API.IntegrationTests/ShareLinkIntegrationTests.cs`

#### Existing Frontend Files to Modify (`Client/`)
- `Client/src/styles.scss` — Add global `@media print` reset (`@page`, forced light paper colors overriding dark-theme inline variables, hiding shell navigation/overlays).
- `Client/src/app/app.routes.ts` — Register top-level public lazy route `share/:token` (`PublicSharedRecipeComponent`, no `authGuard` or `guestGuard`).
- `Client/src/app/features/recipes/recipe-detail/recipe-detail.component.ts` (+ `.html`, `.scss`, `.spec.ts`) — Add `Share` button opening `RecipeShareDialogComponent`, `Print` button calling `printRecipe()`, `.print-only-meta-line` print header summary, `1.5x` multiplier chip, `UsCustomary` toggle value fix, complete `.scaling-toolbar-card` styles, `360px` mobile header wrapping, and `@media print` component rules.

#### New Frontend & E2E Files to Create
- `Client/src/app/core/models/share-link.models.ts`
- `Client/src/app/core/services/share-link/share-link.service.ts` (+ `.spec.ts`)
- `Client/src/app/features/recipes/recipe-share-dialog/recipe-share-dialog.component.ts` (+ `.html`, `.scss`, `.spec.ts`)
- `Client/src/app/features/recipes/public-shared-recipe/public-shared-recipe.component.ts` (+ `.html`, `.scss`, `.spec.ts`)
- `Tests/Client.E2ETests/PrintAndShareE2ETests.cs`

---

## 2. Domain & Database Design (`Core/` & `Data/`)

### 2.1 Cryptographic Token Generator & Validator (`Core/Recipes/ShareTokenGenerator.cs`)

Create `public static class ShareTokenGenerator` under namespace `App.Core.Recipes` (zero EF Core or HTTP dependencies):
- `public const int TokenByteLength = 24;` (192 bits of entropy $\rightarrow$ 32 base64url characters).
- `public const int MinTokenLength = 16;`
- `public const int MaxTokenLength = 64;`
- `public static string GenerateToken()`:
  - Allocates `Span<byte> bytes = stackalloc byte[TokenByteLength];` and fills via `RandomNumberGenerator.Fill(bytes);`.
  - Encodes to URL-safe base64 without padding (`Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=')`).
- `public static bool IsWellFormedToken(string? token)`:
  - Returns `false` if `string.IsNullOrWhiteSpace(token)` or `token.Length < MinTokenLength` or `token.Length > MaxTokenLength`.
  - Returns `true` iff every character `c` satisfies `(c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_'`.

### 2.2 Entities (`Data/Entities/`)

1. **`Data/Entities/ShareLink.cs`** (`namespace App.Data.Entities`, implements `IAuditable`):
   ```csharp
   namespace App.Data.Entities;

   public sealed class ShareLink : IAuditable
   {
       public Guid Id { get; set; } = Guid.NewGuid();

       public Guid RecipeId { get; set; }

       public string Token { get; set; } = string.Empty;

       public string? Label { get; set; }

       public bool IncludeCookLogs { get; set; }

       public DateTimeOffset? ExpiresAt { get; set; }

       public DateTimeOffset? RevokedAt { get; set; }

       public int ViewCount { get; set; }

       public DateTimeOffset? LastAccessedAt { get; set; }

       public DateTimeOffset CreatedAt { get; set; }

       public string CreatedBy { get; set; } = "System";

       public DateTimeOffset ModifiedAt { get; set; }

       public string ModifiedBy { get; set; } = "System";

       public Recipe Recipe { get; set; } = null!;
   }
   ```
2. **`Data/Entities/Recipe.cs`**:
   - Add navigation collection:
     ```csharp
     public ICollection<ShareLink> ShareLinks { get; set; } = new List<ShareLink>();
     ```
3. **`Data/AppDbContext.cs`**:
   - Add `public DbSet<ShareLink> ShareLinks => Set<ShareLink>();`.

### 2.3 EF Core Fluent Configurations (`Data/Configurations/ShareLinkConfiguration.cs`)

Create `public sealed class ShareLinkConfiguration : IEntityTypeConfiguration<ShareLink>`:
- `builder.ToTable("ShareLinks");`
- `builder.HasKey(s => s.Id);`
- `builder.Property(s => s.Token).IsRequired().HasMaxLength(64);`
- `builder.Property(s => s.Label).HasMaxLength(120);`
- `builder.Property(s => s.CreatedBy).IsRequired().HasMaxLength(100);`
- `builder.Property(s => s.ModifiedBy).IsRequired().HasMaxLength(100);`
- **Indexes:**
  - `builder.HasIndex(s => s.Token).IsUnique();`
  - `builder.HasIndex(s => s.RecipeId);`
- **Foreign Key & SQL Server Cascade Safety:**
  - `ShareLink` has only a single foreign key (`RecipeId` $\rightarrow$ `Recipes.Id`) and no child tables, so `DeleteBehavior.Cascade` introduces zero multi-cascade path conflicts on SQL Server:
    ```csharp
    builder
        .HasOne(s => s.Recipe)
        .WithMany(r => r.ShareLinks)
        .HasForeignKey(s => s.RecipeId)
        .OnDelete(DeleteBehavior.Cascade);
    ```
- **Exact Migration Name:** `AddRecipeShareLinks` (`dotnet ef migrations add AddRecipeShareLinks --project Data/App.Data.csproj`).

### 2.4 DTOs, Requests & Service Contracts (`Core/Recipes/`)

1. **`Core/Recipes/ShareLinkModels.cs`** (`namespace App.Core.Recipes`):
   ```csharp
   using System.ComponentModel.DataAnnotations;

   namespace App.Core.Recipes;

   public record ShareLinkDto(
       Guid Id,
       Guid RecipeId,
       string Token,
       string? Label,
       bool IncludeCookLogs,
       DateTimeOffset? ExpiresAt,
       DateTimeOffset? RevokedAt,
       bool IsActive,
       int ViewCount,
       DateTimeOffset? LastAccessedAt,
       DateTimeOffset CreatedAt,
       string CreatedBy
   );

   public record CreateShareLinkRequest(
       [MaxLength(120)] string? Label = null,
       bool IncludeCookLogs = false,
       DateTimeOffset? ExpiresAt = null
   );

   public record PublicRecipeTagBadgeDto(string Name, TagCategory Category);

   public record PublicRecipeIngredientDto(
       Guid Id,
       int SortOrder,
       string? SectionName,
       decimal? Quantity,
       decimal? MaxQuantity,
       MeasurementUnit? Unit,
       string? CustomUnitName,
       bool IsPantryStaple,
       Guid? LinkedSubRecipeId,
       string? LinkedSubRecipeTitle,
       string DisplayName,
       string? PreparationNote,
       bool IsOptional
   );

   public record PublicSubRecipeDto(
       Guid Id,
       string Title,
       decimal BaseServings,
       string? YieldDisplay,
       IReadOnlyList<PublicRecipeIngredientDto> Ingredients,
       IReadOnlyList<RecipeStepDto> Steps
   );

   public record PublicSharedRecipeDto(
       string Title,
       string? Description,
       decimal BaseServings,
       string? YieldDisplay,
       int? PrepTimeMinutes,
       int? CookTimeMinutes,
       int? TotalTimeMinutes,
       RecipeDifficulty? Difficulty,
       string? SourceUrl,
       string? SourceName,
       string? SourceAuthor,
       string? SourceBookTitle,
       int? SourceBookPage,
       string? CoverImagePath,
       IReadOnlyList<PublicRecipeTagBadgeDto> Tags,
       IReadOnlyList<PublicRecipeIngredientDto> Ingredients,
       IReadOnlyList<RecipeStepDto> Steps,
       IReadOnlyList<PublicSubRecipeDto> LinkedSubRecipes,
       bool IncludeCookLogs,
       DateTimeOffset? ExpiresAt
   );
   ```

2. **`Core/Recipes/IShareLinkService.cs`** (`namespace App.Core.Recipes`):
   ```csharp
   namespace App.Core.Recipes;

   public interface IShareLinkService
   {
       Task<IReadOnlyList<ShareLinkDto>> GetShareLinksForRecipeAsync(
           Guid recipeId,
           CancellationToken cancellationToken = default
       );

       Task<ShareLinkDto> CreateShareLinkAsync(
           Guid recipeId,
           CreateShareLinkRequest request,
           CancellationToken cancellationToken = default
       );

       Task<ShareLinkDto?> RevokeShareLinkAsync(
           Guid recipeId,
           Guid shareLinkId,
           CancellationToken cancellationToken = default
       );

       Task<bool> DeleteShareLinkAsync(
           Guid recipeId,
           Guid shareLinkId,
           CancellationToken cancellationToken = default
       );

       Task<PublicSharedRecipeDto?> GetPublicSharedRecipeByTokenAsync(
           string token,
           CancellationToken cancellationToken = default
       );
   }
   ```

### 2.5 Service Business Logic & Security Boundary Invariants (`Data/Services/ShareLinkService.cs`)

Create `public sealed class ShareLinkService(AppDbContext dbContext, TimeProvider? timeProvider = null) : IShareLinkService` (using `private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;`) and register `services.AddScoped<IShareLinkService, ShareLinkService>();` in `DataServiceCollectionExtensions.AddDataServices`:

- **`GetShareLinksForRecipeAsync(recipeId, cancellationToken)`:**
  - Verify recipe exists via `dbContext.Recipes.AsNoTracking().AnyAsync(r => r.Id == recipeId, cancellationToken)`. Throw `NotFoundException("Recipe not found.")` if missing.
  - Query `dbContext.ShareLinks.AsNoTracking().Where(s => s.RecipeId == recipeId).OrderByDescending(s => s.CreatedAt)` and map each row to `ShareLinkDto` where `IsActive = s.RevokedAt is null && (!s.ExpiresAt.HasValue || s.ExpiresAt.Value > now)`.
- **`CreateShareLinkAsync(recipeId, request, cancellationToken)`:**
  - Load recipe status via `dbContext.Recipes.AsNoTracking().Select(r => new { r.Id, r.Status }).FirstOrDefaultAsync(r => r.Id == recipeId, cancellationToken)`.
  - If `null`, throw `NotFoundException("Recipe not found.")`.
  - If `recipe.Status != RecipeStatus.Published`, throw `DomainValidationException("Only published recipes can be shared externally. Publish this recipe before creating a share link.")`.
  - Normalize `label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim()`. If `label is { Length: > 120 }`, throw `DomainValidationException("Share link label cannot exceed 120 characters.")`.
  - Let `now = _timeProvider.GetUtcNow()`. If `request.ExpiresAt.HasValue && request.ExpiresAt.Value <= now`, throw `DomainValidationException("Share link expiration date must be in the future.")`.
  - Generate token via `ShareTokenGenerator.GenerateToken()` (verifying `!await dbContext.ShareLinks.AnyAsync(s => s.Token == token, cancellationToken)` with up to 5 retry attempts).
  - Add `ShareLink` entity, call `await dbContext.SaveChangesAsync(cancellationToken)`, and return mapped `ShareLinkDto`.
- **`RevokeShareLinkAsync(recipeId, shareLinkId, cancellationToken)`:**
  - Load tracked `ShareLink` matching `s.Id == shareLinkId && s.RecipeId == recipeId`. Return `null` if not found.
  - If `s.RevokedAt is null`, set `s.RevokedAt = _timeProvider.GetUtcNow()` and `await dbContext.SaveChangesAsync(cancellationToken)` (idempotent if already revoked).
  - Return mapped `ShareLinkDto` (`IsActive = false`).
- **`DeleteShareLinkAsync(recipeId, shareLinkId, cancellationToken)`:**
  - Load tracked `ShareLink` matching `s.Id == shareLinkId && s.RecipeId == recipeId`. Return `false` if not found.
  - Remove entity, `await dbContext.SaveChangesAsync(cancellationToken)`, and return `true`.
- **`GetPublicSharedRecipeByTokenAsync(token, cancellationToken)` (Strict Public Boundary):**
  - Trim `token`. If `!ShareTokenGenerator.IsWellFormedToken(trimmed)`, return `null`.
  - Load tracked `ShareLink` via `dbContext.ShareLinks.FirstOrDefaultAsync(s => s.Token == trimmed, cancellationToken)`.
  - **Case-Exact Enforcement:** Because SQL Server default collation is case-insensitive (`CI`), explicitly check `if (shareLink is null || !string.Equals(shareLink.Token, trimmed, StringComparison.Ordinal)) return null;`.
  - Let `now = _timeProvider.GetUtcNow()`. If `shareLink.RevokedAt.HasValue` or `(shareLink.ExpiresAt.HasValue && shareLink.ExpiresAt.Value <= now)`, return `null`.
  - Query the target `Recipe` with `.AsNoTracking()` including `Ingredients` (`ThenInclude(FoodItem)`, `ThenInclude(LinkedRecipe)`), `Steps` (`ThenInclude(IngredientReferences)`, `ThenInclude(Timers)`), and `RecipeTags` (`ThenInclude(Tag)`) filtered to `r.Id == shareLink.RecipeId && r.Status == RecipeStatus.Published`. If `recipe is null` (e.g., recipe was archived or moved to draft after link creation), return `null`.
  - **Published-Only Sub-Recipe Resolution:** Collect distinct `LinkedRecipeId`s from `recipe.Ingredients` where `i.LinkedRecipe?.Status == RecipeStatus.Published`. Load those published sub-recipes `.AsNoTracking()` with `Ingredients` (`ThenInclude(FoodItem)`) and `Steps` (`ThenInclude(IngredientReferences)`, `ThenInclude(Timers)`). When mapping `PublicRecipeIngredientDto`, populate `LinkedSubRecipeId` and `LinkedSubRecipeTitle` **only** if the referenced sub-recipe is `Published` (scrubbing references to `DraftReview` or `Archived` sub-recipes).
  - Increment `shareLink.ViewCount += 1; shareLink.LastAccessedAt = now; await dbContext.SaveChangesAsync(cancellationToken);`.
  - Project and return `PublicSharedRecipeDto` (exposing zero `CreatedBy`, `ModifiedBy`, user emails/usernames, `Collection` memberships, `FoodItemId`s, `OriginalRawText`, or internal `RecipeId`).
- **`RecipeService.DeleteRecipeAsync` Update:**
  - Remove `dbContext.ShareLinks.Where(s => s.RecipeId == id)` via `RemoveRange` alongside `RecipeStepIngredients` so EF Core InMemory integration tests immediately purge share links when a recipe is deleted.

---

## 3. API Endpoints (`API/Controllers/`)

| Controller | Verb | Route (`/api/...`) | Auth | Request Body / Params | Response | Error Cases |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| `RecipeSharesController` | `GET` | `/recipes/{recipeId:guid}/shares` | `[Authorize]` | Route `recipeId` | `200 OK` (`IReadOnlyList<ShareLinkDto>`) | `401`, `404` (recipe missing) |
| `RecipeSharesController` | `POST` | `/recipes/{recipeId:guid}/shares` | `[Authorize]` | `CreateShareLinkRequest` | `201 Created` (`ShareLinkDto`) | `400` (non-published recipe, past `ExpiresAt`, label > 120), `401`, `404` |
| `RecipeSharesController` | `POST` | `/recipes/{recipeId:guid}/shares/{shareId:guid}/revoke` | `[Authorize]` | Route `recipeId`, `shareId` | `200 OK` (`ShareLinkDto`) | `401`, `404` |
| `RecipeSharesController` | `DELETE` | `/recipes/{recipeId:guid}/shares/{shareId:guid}` | `[Authorize]` | Route `recipeId`, `shareId` | `204 NoContent` | `401`, `404` |
| `PublicSharesController` | `GET` | `/public/shares/{token}` | `[AllowAnonymous]` | Route `token` | `200 OK` (`PublicSharedRecipeDto`) + `Cache-Control: no-store` + `X-Robots-Tag: noindex, nofollow` | `404` (`ProblemDetails` — uniform response for unknown, malformed, expired, revoked, or unpublished) |

- Keep both controllers strictly thin HTTP adapters (`[Route("recipes/{recipeId:guid}/shares")]` and `[Route("public/shares")]` — never prefixing `api/`).

---

## 4. Angular Frontend & Print Stylesheet Design (`Client/`)

### 4.1 Models & Services (`Client/src/app/core/`)

1. **`core/models/share-link.models.ts`:**
   - Define TypeScript interfaces matching backend contracts:
     - `ShareLinkDto` (`id`, `recipeId`, `token`, `label`, `includeCookLogs`, `expiresAt`, `revokedAt`, `isActive`, `viewCount`, `lastAccessedAt`, `createdAt`, `createdBy`)
     - `CreateShareLinkRequest` (`label?: string | null`, `includeCookLogs?: boolean`, `expiresAt?: string | null`)
     - `PublicRecipeTagBadgeDto` (`name`, `category: TagCategory`)
     - `PublicRecipeIngredientDto` (`id`, `sortOrder`, `sectionName`, `quantity`, `maxQuantity`, `unit`, `customUnitName`, `isPantryStaple`, `linkedSubRecipeId`, `linkedSubRecipeTitle`, `displayName`, `preparationNote`, `isOptional`)
     - `PublicSubRecipeDto` (`id`, `title`, `baseServings`, `yieldDisplay`, `ingredients: PublicRecipeIngredientDto[]`, `steps: RecipeStepDto[]`)
     - `PublicSharedRecipeDto` (`title`, `description`, `baseServings`, `yieldDisplay`, `prepTimeMinutes`, `cookTimeMinutes`, `totalTimeMinutes`, `difficulty`, `sourceUrl`, `sourceName`, `sourceAuthor`, `sourceBookTitle`, `sourceBookPage`, `coverImagePath`, `tags`, `ingredients`, `steps`, `linkedSubRecipes`, `includeCookLogs`, `expiresAt`)
2. **`core/services/share-link/share-link.service.ts` (+ `.spec.ts`):**
   - Decorate with `@Service()` from `@angular/core`.
   - Inject `private readonly http = inject(HttpClient);` for authenticated household endpoints and instantiate `private readonly publicHttp = new HttpClient(inject(HttpBackend));` for `getPublicSharedRecipe(token)` so public `/share/:token` requests never attach household `Authorization` headers or trigger `authInterceptor` session logouts if a browser has a stale token in `localStorage` (while keeping `auth.interceptor.ts` untouched for upstream template compatibility and remaining 100% testable with `HttpTestingController`).
   - Methods:
     - `getShareLinks(recipeId: string): Observable<ShareLinkDto[]>`
     - `createShareLink(recipeId: string, request: CreateShareLinkRequest): Observable<ShareLinkDto>`
     - `revokeShareLink(recipeId: string, shareId: string): Observable<ShareLinkDto>`
     - `deleteShareLink(recipeId: string, shareId: string): Observable<void>`
     - `getPublicSharedRecipe(token: string): Observable<PublicSharedRecipeDto>`
     - `buildPublicShareUrl(token: string): string` (`${globalThis.location.origin}/share/${token}`)

### 4.2 Feature Components & Routes (`Client/src/app/features/recipes/`)

Every component **must** reside in its own dedicated kebab-case folder with separate `.ts`, `.html`, `.scss`, and `.spec.ts` files (`templateUrl` + `styleUrl`, no `standalone: true`, signals + `inject()`).

1. **`features/recipes/recipe-share-dialog/` (`RecipeShareDialogComponent`):**
   - Dialog data (`MAT_DIALOG_DATA`): `RecipeShareDialogData { recipeId: string; recipeTitle: string; recipeStatus: RecipeStatus; }`.
   - Signals: `shareLinks = signal<ShareLinkDto[]>([])`, `isLoading = signal<boolean>(true)`, `isSubmitting = signal<boolean>(false)`, `errorMessage = signal<string | null>(null)`, `newlyCreatedToken = signal<string | null>(null)`.
   - Reactive Form (`FormBuilder.nonNullable`):
     - `label`: `''` (`Validators.maxLength(120)`)
     - `expirationPreset`: `'never' | '24h' | '7d' | '30d'` (default `'never'`; converts `'24h'`, `'7d'`, `'30d'` into future UTC ISO string `expiresAt` on submit)
     - `includeCookLogs`: `false`
   - If `data.recipeStatus !== 'Published'`, display an informational alert banner (`role="status"`) explaining that only published recipes can be shared externally and disable the create button.
   - Existing links list:
     - Displays each link's `label || 'Public Share Link'`, status badge (`Active` in `--app-success-container`, `Expired` in `--app-warning-container`, or `Revoked` in `--mat-sys-surface-variant`), view counter (`viewCount` views), expiration/revocation date (`DatePipe`), **Copy Link** button (`navigator.clipboard?.writeText(...)` + snackbar `"Share link copied to clipboard"`), **Open Public View** anchor (`[href]="buildShareUrl(link.token)" target="_blank" rel="noopener noreferrer"` for active links), **Revoke** button (`aria-label="Revoke share link"` for active links), and **Delete** icon button (`aria-label="Delete share link"` for inactive/revoked links).
   - Responsive `360px` layout: form fields stack cleanly in a single column on `<= 600px` and link action buttons wrap without horizontal overflow.

2. **`features/recipes/public-shared-recipe/` (`PublicSharedRecipeComponent`):**
   - Registered as a top-level lazy route in `Client/src/app/app.routes.ts` **outside** `AppShellComponent` (with **no** `authGuard` or `guestGuard`):
     ```ts
     {
       path: 'share/:token',
       loadComponent: () =>
         import('./features/recipes/public-shared-recipe/public-shared-recipe.component').then(
           (m) => m.PublicSharedRecipeComponent,
         ),
     },
     ```
   - Public Header (`.public-top-bar`, hidden in `@media print`):
     - Displays `APP_BRAND_ICON` + `APP_NAME` (from `app.branding.ts`) alongside a `"Shared Recipe"` pill—with **no** cookbook navigation links so unauthenticated guests stay strictly on the shared recipe.
     - Right controls: Theme toggle button (`ThemeService` Light/Dark switch, `aria-label="Toggle color theme"`) and **Print** button (`<mat-icon>print</mat-icon> <span>Print</span>`, `aria-label="Print recipe"`, calling `printRecipe()`).
   - Unavailable / Revoked / Expired State:
     - Renders a centered `.public-unavailable-card` (`role="alert"`) with icon `link_off` and message `"This shared recipe link is unavailable. It may have expired or been revoked by the household."`
   - Active Shared Recipe View:
     - Cover image (`coverImagePath`), title, difficulty badge, typed `tags` pills, description, stats strip (`Prep Time`, `Cook Time`, `Total Time`, `Base Yield`, `Source` attribution, optional `Expires` notice if `expiresAt` is set), and `.print-only-meta-line` (`Scaled Yield: {{ targetServings() }} servings · Units: {{ unitSystemLabel() }}`).
     - Full **Culinary Scaling & Unit Converter Toolbar** (`targetServings` stepper `-` / `+`, quick multipliers `0.5x`, `1x`, `1.5x`, `2x`, `3x`, and `UnitSystem` toggle `Original` | `UsCustomary` | `Metric` using `scaleAndConvert` and `formatDuration` from `core/utils/culinary-math.ts`).
     - Two-column desktop / single-column `360px` mobile body grid:
       - **Ingredients Column:** Section groups, checkable prep checkboxes (`checkedIngredients` signal), scaled amounts + units, optional/pantry tags, and inline `mat-expansion-panel` for any `PublicSubRecipeDto` rendering both **scaled sub-recipe ingredients** (scaled by parent `scaleFactor()`) and **numbered sub-recipe steps** inline (with **no** link to `/recipes/:id`).
       - **Instructions Column:** Section groups, numbered step cards, step reference photos, scaled inline ingredient callout pills, and formatted step timer badges.

3. **`features/recipes/recipe-detail/` (`RecipeDetailComponent`) Updates:**
   - Top Action Bar (`.detail-top-actions`):
     - Add **Share** button (`mat-stroked-button`, `<mat-icon>share</mat-icon> <span>Share</span>`, `aria-label="Share recipe"`) opening `RecipeShareDialogComponent` (`width: '560px'`).
     - Add **Print** button (`mat-stroked-button`, `<mat-icon>print</mat-icon> <span>Print</span>`, `aria-label="Print recipe"`) calling `printRecipe()` (`globalThis.print?.()`).
     - Ensure `.detail-nav-bar` and `.detail-top-actions` wrap cleanly (`flex-wrap: wrap`) on `360px` mobile screens so all buttons (`Organize`, `Share`, `Print`, `Edit`, `Delete`) fit within `360px` with zero horizontal scroll.
   - Scaling Toolbar Fixes & Enhancements:
     - Fix `<mat-button-toggle value="USCustomary">` $\rightarrow$ `<mat-button-toggle value="UsCustomary">` so US Customary unit conversion matches `UnitSystem` in `culinary-math.ts`.
     - Add `1.5x` quick multiplier button alongside `0.5x`, `1x`, `2x`, `3x`.
     - Define crisp styles in `recipe-detail.component.scss` for `.scaling-toolbar-card`, `.scaling-toolbar`, `.servings-stepper`, `.stepper-controls`, `.servings-display`, `.mult-chip.active-mult`, `.toolbar-divider`, and `.unit-system-toggle`.
   - Print Metadata Summary:
     - Add `<div class="print-only-meta-line">` inside `.recipe-hero` displaying `Scaled Yield: {{ targetServings() }} servings` (plus active unit system when not `Original`), hidden on screen (`display: none`) and visible in `@media print`.

### 4.3 Print-Friendly Recipe Card Stylesheet (`Client/src/styles.scss` & Component SCSS)

1. **Global `Client/src/styles.scss` (`@media print` block):**
   - `@page { margin: 12mm 14mm; }`
   - Force light-mode ink & paper regardless of active `data-theme="dark"` or inline `--mat-sys-*` custom properties:
     ```scss
     @media print {
       html,
       body {
         color-scheme: light !important;
         background: #ffffff !important;
         color: #111111 !important;
         height: auto !important;
         min-height: 0 !important;
         overflow: visible !important;
         font-size: 10pt;
       }

       .no-print,
       .cdk-overlay-container,
       app-shell .top-bar,
       app-shell .mobile-bottom-nav {
         display: none !important;
       }

       app-shell,
       app-shell .shell-layout,
       app-shell .main-content {
         display: block !important;
         min-height: 0 !important;
         max-width: 100% !important;
         padding: 0 !important;
         margin: 0 !important;
         background: #ffffff !important;
         color: #111111 !important;
       }
     }
     ```
2. **Component `@media print` Rules (`recipe-detail.component.scss` & `public-shared-recipe.component.scss`):**
   - Hide `.detail-nav-bar`, `.public-top-bar`, `.scaling-toolbar-card`, `.recipe-hero-image`, `.step-reference-wrapper`, `.hero-taxonomy-strip`, and `.sub-recipe-link-bar` (`display: none !important`).
   - Reveal `.print-only-meta-line` (`display: flex !important; gap: 1rem; font-size: 9.5pt; font-weight: 600; color: #333333; border-top: 1px solid #cccccc; border-bottom: 1px solid #cccccc; padding: 0.35rem 0; margin-top: 0.25rem;`).
   - Render `.recipe-body-grid` in a compact two-column print grid (`grid-template-columns: 34% 1fr !important; gap: 1.25rem !important;`) with clean black/dark-gray typography (`#111111`), compact spacing, `break-inside: avoid` on `.ingredient-section-group` and `.step-card`, subtle bottom borders instead of heavy card boxes, and expanded `.sub-recipe-inline-panel .mat-expansion-panel-content` (`height: auto !important; visibility: visible !important;`) so sub-recipe components print legibly alongside parent ingredients.

---

## 5. Playwright E2E Test Scenarios (`Tests/Client.E2ETests/PrintAndShareE2ETests.cs`)

Create `public sealed class PrintAndShareE2ETests : E2ETestBase` in `Tests/Client.E2ETests/PrintAndShareE2ETests.cs` implementing these end-to-end scenarios:

- `[TestMethod] ShareLink_CreateOpenInUnauthenticatedContextScaleAndRevoke_EnforcesStrictBoundaries()`:
  1. Register & log in as a household user, create a `Published` sub-recipe (*"Quick Pickled Onions"*) and a `Published` parent recipe (*"Street Fish Tacos"*, `4` base servings, `200 g` Mahi Mahi, `1` batch linked *"Quick Pickled Onions"*, and a step referencing the ingredient).
  2. On `RecipeDetailComponent`, click **Share** (`button[aria-label='Share recipe']`), fill label `"Family Taco Night"` and expiration `"7 days"` in `RecipeShareDialogComponent`, click **Create Share Link**, verify the active link row appears in the dialog, and extract the `/share/<token>` URL.
  3. Launch a **brand-new unauthenticated Playwright browser context** (`await using var guestContext = await Browser.NewContextAsync(); var guestPage = await guestContext.NewPageAsync();`) with empty storage and navigate directly to `${ServerUrl}/share/${token}`.
  4. Verify `guestPage` renders `"Street Fish Tacos"` without redirecting to `/login`, does **not** render `app-shell` household navigation (`Cookbook`, `Matcher`, `Food Catalog`, or user profile menu), scales servings to `2x` (`8` servings $\rightarrow$ `400 g` Mahi Mahi), switches units to `US` (`oz`), and expands the inline `"Sub-Recipe: Quick Pickled Onions"` panel showing sub-recipe details with zero `/recipes/` links.
  5. Back on the authenticated `Page`, click **Revoke** (`button[aria-label='Revoke share link']`) in `RecipeShareDialogComponent` and verify status updates to `"Revoked"`.
  6. Reload `guestPage` on `/share/${token}` and assert the unavailable alert (`role="alert"`) is displayed and recipe content is no longer accessible.
- `[TestMethod] PrintView_RecipeDetailAndPublicShare_AppliesCrispPrintMediaStylesAndTriggersPrint()`:
  1. Register & log in, create a recipe, open `RecipeDetailComponent`, switch UI theme to **Dark** mode, scale recipe to `2x`, stub `window.print` (`window.__printed = false; window.print = () => { window.__printed = true; };`), click **Print** (`button[aria-label='Print recipe']`), and verify `window.__printed === true`.
  2. Call `await Page.EmulateMediaAsync(new() { Media = Media.Print })` and verify `.top-bar`, `.detail-nav-bar`, and `.scaling-toolbar-card` are hidden (`ToBeHiddenAsync()`), `.print-only-meta-line` is visible showing the scaled servings, `.recipe-title` and scaled ingredients/steps are visible, and `body` computed `backgroundColor` is white (`rgb(255, 255, 255)`).
  3. Create a share link, navigate to `/share/<token>`, emulate `Media.Print`, and verify `.public-top-bar` and `.scaling-toolbar-card` are hidden while `.print-only-meta-line`, `.ingredients-column`, and `.instructions-column` render visibly.
- `[TestMethod] PrintAndShare_MobileViewport_360px_RendersDialogAndPublicPageWithoutOverflow()`:
  1. Set viewport to `360x640` (`await Page.SetViewportSizeAsync(360, 640)`), register & log in, create a recipe, and navigate to `RecipeDetailComponent`.
  2. Verify `Share` and `Print` buttons are visible and `document.documentElement.scrollWidth <= 360`.
  3. Open `RecipeShareDialogComponent`, create a share link on `360px` mobile, and assert dialog content has no horizontal overflow.
  4. Navigate to `/share/<token>` at `360x640`, test multiplier & unit toggles, and verify `document.documentElement.scrollWidth <= 360`.

---

## 6. Step-by-Step Execution Checklist (For Implementation Agent)

- [ ] **Task 1: Backend Core & Data Layer**
  - Create `Core/Recipes/ShareTokenGenerator.cs`, `Core/Recipes/ShareLinkModels.cs`, and `Core/Recipes/IShareLinkService.cs`.
  - Create `Data/Entities/ShareLink.cs` and `Data/Configurations/ShareLinkConfiguration.cs`; update `Data/Entities/Recipe.cs`, `Data/AppDbContext.cs`, `Data/Services/RecipeService.cs` (`DeleteRecipeAsync`), and `Data/DataServiceCollectionExtensions.cs`.
  - Implement `Data/Services/ShareLinkService.cs` enforcing published-only sharing, ordinal token matching, expiration/revocation checks, view counting, and zero-leakage `PublicSharedRecipeDto` projection.
  - Ensure local SQL Server is running, then generate, apply, and format the EF Core migration:
    ```powershell
    .\scripts\ensure-local-infra.ps1
    dotnet ef migrations add AddRecipeShareLinks --project Data/App.Data.csproj
    dotnet ef database update --project Data/App.Data.csproj
    dotnet csharpier format Data/Migrations
    ```
  - Write unit tests in `Tests/Core.UnitTests/ShareTokenGeneratorTests.cs` and run backend unit test suites:
    ```powershell
    dotnet test --project Tests/Core.UnitTests/App.Core.UnitTests.csproj
    dotnet test --project Tests/Infrastructure.UnitTests/App.Infrastructure.UnitTests.csproj
    ```

- [ ] **Task 2: API Controllers & Integration Tests**
  - Create `API/Controllers/RecipeSharesController.cs` and `API/Controllers/PublicSharesController.cs` (with `Cache-Control: no-store` and `X-Robots-Tag: noindex, nofollow`).
  - Update `Tests/API.IntegrationTests/OpenApiIntegrationTests.cs` and create `Tests/API.IntegrationTests/ShareLinkIntegrationTests.cs` covering:
    - Creating, listing, revoking, and deleting share links (`201`, `200`, `204`, `401` unauthenticated).
    - Validation failures (`DraftReview`/`Archived` recipe share attempt $\rightarrow$ `400`, past `ExpiresAt` $\rightarrow$ `400`, missing recipe $\rightarrow$ `404`).
    - Public resolution (`GET /api/public/shares/{token}` without bearer token $\rightarrow$ `200 OK`, `ViewCount` increment, `no-store` header, zero leaked `CreatedBy`/email/collection strings in raw JSON).
    - Strict `404` enforcement on wrong-case token, malformed token, expired token, revoked token, recipe archived after link creation, and unpublished sub-recipe scrubbing.
  - Run API integration tests until 100% green:
    ```powershell
    dotnet test --project Tests/API.IntegrationTests/App.API.IntegrationTests.csproj
    ```

- [ ] **Task 3: Angular Client, Print Stylesheet & Unit Tests**
  - Create `Client/src/app/core/models/share-link.models.ts` and `Client/src/app/core/services/share-link/share-link.service.ts` (+ `.spec.ts`).
  - Create `Client/src/app/features/recipes/recipe-share-dialog/` (`.ts`, `.html`, `.scss`, `.spec.ts`) and `Client/src/app/features/recipes/public-shared-recipe/` (`.ts`, `.html`, `.scss`, `.spec.ts`).
  - Update `Client/src/app/app.routes.ts`, `Client/src/styles.scss`, and `Client/src/app/features/recipes/recipe-detail/` (`.ts`, `.html`, `.scss`, `.spec.ts`).
  - From `Client/`, run lint, production build, and unit tests until 100% green:
    ```powershell
    npm run lint
    npm run build:production
    npm test -- --watch=false
    ```

- [ ] **Task 4: Playwright E2E Verification & Final Formatting**
  - Create `Tests/Client.E2ETests/PrintAndShareE2ETests.cs` implementing all three scenarios from Section 5.
  - Build Angular production bundle (in `Client/`: `npm run build:production`) and run Playwright E2E tests:
    ```powershell
    dotnet test --project Tests/Client.E2ETests/App.Client.E2ETests.csproj
    ```
  - Format all backend and frontend files and run full verification:
    ```powershell
    dotnet csharpier format .
    # In Client/:
    npm run format
    npm run lint:fix
    # At repo root:
    dotnet build App.slnx
    dotnet test App.slnx
    ```
  - Update this plan's status header to `[x] Completed` and commit onto `feature/phase-03-print-and-share-links`.
