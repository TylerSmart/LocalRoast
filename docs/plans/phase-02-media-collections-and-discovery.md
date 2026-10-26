# Phase 02: Local WebP Media Pipeline, Collections & Typed Tags, and Smart Cookbook Discovery

> **Branch:** `feature/phase-02-media-collections-and-discovery`
> **Status:** `[ ]` Not Started
> **PRD Reference:** `docs/prd.md` — Sections 2, 3, 4.1.1, 4.1.4, 4.7.1, 4.7.2, 5, and 6 (Milestone 1, Slice 2)
> **Target Execution:** Run `/execute-phase` to execute this plan step-by-step.

---

## 1. Scope & Objectives

This phase delivers the second major vertical slice of **Milestone 1**, transforming the structured cookbook into a visual, richly organized, searchable kitchen library with anti-link-rot local media storage and ingredient-driven meal discovery:

1. **Local `/data/uploads` WebP Media Pipeline (`IMediaStorageService` & `MediaController`):**
   - Persist uploaded recipe cover photos and per-step reference photos onto the local filesystem (`MediaStorage:UploadsPath` $\rightarrow$ `/data/uploads` in Docker/TrueNAS Scale, `App_Data/uploads` in local development, and isolated temp directories in automated tests).
   - Process all uploaded images (`JPEG`, `PNG`, `WebP`, `GIF`) via pure-managed `SixLabors.ImageSharp`: normalize EXIF orientation (`AutoOrient()`), strip camera EXIF/GPS/XMP/IPTC metadata, generate responsive lossy `WebP` cover (`1600x1200` max), card thumbnail (`600x450` max), and step (`1200x900` max) variants, and clean up unreferenced files when recipes are updated or deleted.
   - Serve `/api/uploads/*` with immutable cache headers and `X-Content-Type-Options: nosniff` so `<img src="/api/uploads/...">` works natively across local Angular dev proxy (`localhost:4200`), production containers, and future public share pages.
2. **Collections (Cookbooks) & Typed Tags Taxonomy (`Collection`, `RecipeCollection`, `Tag`, `RecipeTag`):**
   - Many-to-many household **Collections** (*Weeknight Staples*, *Weekend Projects*, *Holiday & Hosting*, *Sauces & Components*) and **Typed Tags** categorized across `Course`, `Cuisine`, `Dietary`, `Equipment`, and `Custom`—idempotently seeded in `HouseholdCatalogSeeder` and editable by any authenticated household user.
   - Full CRUD API (`/api/collections`, `/api/tags`), inline creation & assignment in `RecipeEditorComponent`, quick 1-click curation dialog (`RecipeOrganizationDialogComponent`) on `RecipeDetailComponent` (`PUT /api/recipes/{id}/organization`), and visual collection/tag chips across Cookbook cards and Recipe Detail.
3. **Smart Search (`Cmd/Ctrl + K` Command Palette + Facets) & `"Use Up Ingredients"` Fridge Matcher:**
   - Expand `GET /api/recipes` search & faceted filtering (`collectionId`, multi-tag `tagIds` AND matching, `Under 45 min` weeknight quick filter, plus keyword matching across titles, descriptions, sources, ingredient display names, canonical `FoodItem` names/aliases, collections, and tags).
   - Add Global Instant Search (`GlobalSearchDialogComponent`) triggered via `Cmd/Ctrl + K` on desktop or top-bar search button on `360px` mobile.
   - Implement the deterministic **`"Use Up Ingredients"` Fridge Matcher** (`IngredientMatcher` in `App.Core`, `POST /api/recipes/match-ingredients`, and `FridgeMatcherComponent` at `/matcher`) ranking published recipes by (1) matched on-hand ingredients descending and (2) fewest additional non-staple required ingredients ascending (ignoring `IsPantryStaple = true` items).

### Explicitly Out of Scope (Deferred to Phase 03+)
- Crisp `@media print` 1–2 page recipe stylesheet and revocable `/share/<token>` public read-only recipe share links (**Phase 03** — completes Milestone 1).
- Local LLM / `Ollama` async ingestion queue, Vision/PDF parsing, Personal Access Tokens, and `/api/mcp` server (**Milestone 2**).
- Meal Planning Calendar, Smart Shopping Lists, SignalR sync, Hands-Free Cook Mode, and Cook Logs (**Milestones 3–4**).
- Nutritional / macro tracking (deferred per `docs/prd.md` Section 2).

### 1.1 File Inventory (Modify & Create)

#### Existing Backend & Test Files to Modify
- `Infrastructure/App.Infrastructure.csproj` — Add `<PackageReference Include="SixLabors.ImageSharp" Version="3.1.12" />`.
- `Infrastructure/InfrastructureServiceCollectionExtensions.cs` — Configure `MediaStorageOptions` and register `IMediaStorageService` / `LocalFileMediaStorageService`.
- `Core/Recipes/RecipeEnums.cs` — Add `TagCategory` enum (`Course`, `Cuisine`, `Dietary`, `Equipment`, `Custom`).
- `Core/Recipes/RecipeModels.cs` — Add `RecipeCollectionBadgeDto`, `RecipeTagBadgeDto`, `UpdateRecipeOrganizationRequest`, `IngredientMatchRequest`, `IngredientMatchResultDto`; extend `RecipeSummaryDto`, `RecipeDetailDto`, `RecipeQueryParameters`, `RecipeStepInput`, and `UpsertRecipeRequest`.
- `Core/Recipes/IRecipeService.cs` — Add `UpdateRecipeOrganizationAsync` and `MatchRecipesByIngredientsAsync`.
- `Data/Entities/Recipe.cs` — Add `RecipeCollections` and `RecipeTags` navigation collections.
- `Data/AppDbContext.cs` — Add `DbSet`s for `Collection`, `RecipeCollection`, `Tag`, and `RecipeTag`.
- `Data/DataServiceCollectionExtensions.cs` — Register `ITaxonomyService` / `TaxonomyService`.
- `Data/Seeding/HouseholdCatalogSeeder.cs` — Seed default household `Collections` and categorized `Tags` idempotently.
- `Data/Services/RecipeService.cs` — Inject `IMediaStorageService`; persist & validate cover/thumbnail/step `/api/uploads/` paths, clean up orphaned media on update/delete, project & sync `Collections` and `Tags`, expand `GetRecipesAsync` facets/search, and implement `UpdateRecipeOrganizationAsync` + `MatchRecipesByIngredientsAsync`.
- `API/Controllers/RecipesController.cs` — Add `PUT {id:guid}/organization` and `POST match-ingredients` endpoints.
- `API/Program.cs` — Ensure `MediaStorage` uploads directory exists on startup and mount `UseStaticFiles` for `/api/uploads` with `.webp`/image content types, `nosniff`, and immutable cache headers.
- `API/appsettings.json` — Add `"MediaStorage": { "UploadsPath": "/data/uploads", "MaxUploadBytes": 15728640 }`.
- `API/appsettings.Development.json` — Add `"MediaStorage": { "UploadsPath": "App_Data/uploads", "MaxUploadBytes": 15728640 }`.
- `API/Dockerfile` — Create `/data/uploads` directory in runtime stage (`RUN mkdir -p /data/uploads`).
- `Tests/API.IntegrationTests/AppWebApplicationFactory.cs` — Configure isolated per-factory temp `MediaStorage:UploadsPath` and delete on `Dispose`.
- `Tests/API.IntegrationTests/OpenApiIntegrationTests.cs` — Assert `/collections`, `/tags`, and `/media` paths exist in `/api/openapi/v1.json`.
- `Tests/Client.E2ETests/PlaywrightServerFixture.cs` — Configure isolated temp `MediaStorage__UploadsPath` env var and clean up on `DisposeAsync`.

#### New Backend & Backend Test Files to Create
- `Core/Media/MediaModels.cs`
- `Core/Media/IMediaStorageService.cs`
- `Core/Recipes/TaxonomyModels.cs`
- `Core/Recipes/ITaxonomyService.cs`
- `Core/Recipes/IngredientMatcher.cs`
- `Infrastructure/Media/MediaStorageOptions.cs`
- `Infrastructure/Media/LocalFileMediaStorageService.cs`
- `Data/Entities/Collection.cs`
- `Data/Entities/RecipeCollection.cs`
- `Data/Entities/Tag.cs`
- `Data/Entities/RecipeTag.cs`
- `Data/Configurations/CollectionConfiguration.cs`
- `Data/Configurations/RecipeCollectionConfiguration.cs`
- `Data/Configurations/TagConfiguration.cs`
- `Data/Configurations/RecipeTagConfiguration.cs`
- `Data/Services/TaxonomyService.cs`
- `API/Controllers/MediaController.cs`
- `API/Controllers/CollectionsController.cs`
- `API/Controllers/TagsController.cs`
- `Tests/Core.UnitTests/IngredientMatcherTests.cs`
- `Tests/Infrastructure.UnitTests/LocalFileMediaStorageServiceTests.cs`
- `Tests/API.IntegrationTests/MediaUploadIntegrationTests.cs`
- `Tests/API.IntegrationTests/TaxonomyIntegrationTests.cs`
- `Tests/API.IntegrationTests/RecipeDiscoveryIntegrationTests.cs`

#### Existing Frontend Files to Modify (`Client/src/app/`)
- `core/models/recipe.models.ts` — Add `TagCategory`, taxonomy/media/matcher interfaces, and extend `RecipeSummaryDto`, `RecipeDetailDto`, `RecipeQueryParameters`, `RecipeStepInput`, and `UpsertRecipeRequest`.
- `core/services/recipe/recipe.service.ts` (+ `.spec.ts`) — Support `collectionId` and `tagIds` query params, `updateRecipeOrganization`, and `matchRecipesByIngredients`.
- `app.routes.ts` — Register lazy route `matcher` (`FridgeMatcherComponent`) under `AppShellComponent`.
- `features/shell/app-shell/app-shell.component.ts` (+ `.html`, `.scss`, `.spec.ts`) — Add `Matcher` link to desktop & mobile navs, add Global Search button + `Cmd/Ctrl + K` shortcut opening `GlobalSearchDialogComponent`.
- `features/recipes/recipe-list/recipe-list.component.ts` (+ `.html`, `.scss`, `.spec.ts`) — Add Collection filter chips (+ create/edit/delete collection actions), multi-select Tag filter, `Under 45 min` option, `"Use Up Ingredients"` action button, card thumbnail rendering, and collection/tag pills (updating mock DTOs in `.spec.ts`).
- `features/recipes/recipe-detail/recipe-detail.component.ts` (+ `.html`, `.scss`, `.spec.ts`) — Render cover hero image, step reference photos, collection/tag badges, and `"Organize"` button opening `RecipeOrganizationDialogComponent` (updating mock DTOs in `.spec.ts`).
- `features/recipes/recipe-editor/recipe-editor.component.ts` (+ `.html`, `.scss`, `.spec.ts`) — Add Cover Photo WebP uploader/preview, Per-Step Photo uploader/preview, and Collections + Typed Tags multi-selectors with inline `+ New Collection` / `+ New Tag` dialog triggers (updating mock DTOs in `.spec.ts`).

#### New Frontend & E2E Files to Create
- `Client/src/app/core/models/media.models.ts`
- `Client/src/app/core/models/taxonomy.models.ts`
- `Client/src/app/core/services/media/media.service.ts` (+ `.spec.ts`)
- `Client/src/app/core/services/taxonomy/taxonomy.service.ts` (+ `.spec.ts`)
- `Client/src/app/features/recipes/collection-dialog/collection-dialog.component.ts` (+ `.html`, `.scss`, `.spec.ts`)
- `Client/src/app/features/recipes/tag-dialog/tag-dialog.component.ts` (+ `.html`, `.scss`, `.spec.ts`)
- `Client/src/app/features/recipes/recipe-organization-dialog/recipe-organization-dialog.component.ts` (+ `.html`, `.scss`, `.spec.ts`)
- `Client/src/app/features/recipes/fridge-matcher/fridge-matcher.component.ts` (+ `.html`, `.scss`, `.spec.ts`)
- `Client/src/app/features/shell/global-search-dialog/global-search-dialog.component.ts` (+ `.html`, `.scss`, `.spec.ts`)
- `Tests/Client.E2ETests/DiscoveryAndMediaE2ETests.cs`

---

## 2. Domain, Media Infrastructure & Database Design (`Core/`, `Data/`, `Infrastructure/`)

### 2.1 Enums & Pure Fridge Matcher (`Core/Recipes/`)

1. **`Core/Recipes/RecipeEnums.cs`** — Add `TagCategory` under `App.Core.Recipes`:
   ```csharp
   [JsonConverter(typeof(JsonStringEnumConverter))]
   public enum TagCategory
   {
       Course = 0,
       Cuisine = 1,
       Dietary = 2,
       Equipment = 3,
       Custom = 4,
   }
   ```

2. **`Core/Recipes/IngredientMatcher.cs`** (`public static class IngredientMatcher`):
   - Pure deterministic domain evaluator (zero EF Core / HTTP dependencies) so `Tests/Core.UnitTests/IngredientMatcherTests.cs` can verify every edge case directly:
     ```csharp
     public sealed record MatchableOnHandItem(
         Guid FoodItemId,
         string Name,
         string NormalizedName,
         IReadOnlyList<string> NormalizedAliases
     );

     public sealed record MatchableRecipeIngredient(
         Guid? FoodItemId,
         string DisplayName,
         bool IsOptional,
         bool IsPantryStaple
     );

     public sealed record MatchableRecipeCandidate(
         RecipeSummaryDto Summary,
         IReadOnlyList<MatchableRecipeIngredient> Ingredients
     );
     ```
   - `public static IReadOnlyList<IngredientMatchResultDto> RankRecipes(IReadOnlyList<MatchableRecipeCandidate> candidates, IReadOnlyList<MatchableOnHandItem> onHandItems)`:
     - Deduplicate `onHandItems` by `FoodItemId`. If `onHandItems.Count == 0`, return `[]`.
     - For each candidate recipe:
       - Match an ingredient to an `onHandItem` if `ing.FoodItemId == onHand.FoodItemId`, OR if `ing.FoodItemId == null` and normalized `ing.DisplayName` equals `onHand.NormalizedName` or any `onHand.NormalizedAliases`.
       - **Matched On-Hand Set:** Distinct `onHand.FoodItemId`s matched across any of the recipe's ingredients (including optional ingredients, so optional garnishes like *Cilantro* can still help use up on-hand items). `MatchedIngredientNames` lists the canonical `onHand.Name`s in deterministic order.
       - If `MatchedCount == 0`, exclude the recipe from results.
       - **Additional Non-Staple Ingredients Needed:** Across required (`!ing.IsOptional`) ingredients that did **not** match any `onHandItem` and do **not** have `ing.IsPantryStaple == true`, deduplicate by `FoodItemId?.ToString() ?? ing.DisplayName.Trim().ToLowerInvariant()`, collecting display names into `MissingNonStapleNames` (`AdditionalNonStapleCount = MissingNonStapleNames.Count`).
       - **Pantry Staples Used Count:** Distinct ingredients in the recipe where `ing.IsPantryStaple == true` (and not already in `MatchedOnHand`).
     - Sort results deterministically by:
       1. `MatchedCount` **descending**
       2. `AdditionalNonStapleCount` **ascending**
       3. `Recipe.TotalTimeMinutes ?? int.MaxValue` **ascending**
       4. `Recipe.Title` **ascending** (`StringComparer.OrdinalIgnoreCase`)

### 2.2 Entities (`Data/Entities/`)

All new entities reside in namespace `App.Data.Entities` and implement `IAuditable`:

1. **`Collection.cs`**:
   - `Guid Id { get; set; } = Guid.NewGuid();`
   - `string Name { get; set; } = string.Empty;`
   - `string NormalizedName { get; set; } = string.Empty;`
   - `string Slug { get; set; } = string.Empty;`
   - `string? Description { get; set; } `
   - `int SortOrder { get; set; }`
   - `bool IsSystemSeeded { get; set; }`
   - `IAuditable` properties (`CreatedAt`, `CreatedBy`, `ModifiedAt`, `ModifiedBy`)
   - `ICollection<RecipeCollection> RecipeCollections { get; set; } = new List<RecipeCollection>();`

2. **`RecipeCollection.cs`**:
   - `Guid Id { get; set; } = Guid.NewGuid();`
   - `Guid RecipeId { get; set; }`
   - `Guid CollectionId { get; set; }`
   - `IAuditable` properties
   - `Recipe Recipe { get; set; } = null!;`
   - `Collection Collection { get; set; } = null!;`

3. **`Tag.cs`**:
   - `Guid Id { get; set; } = Guid.NewGuid();`
   - `string Name { get; set; } = string.Empty;`
   - `string NormalizedName { get; set; } = string.Empty;`
   - `string Slug { get; set; } = string.Empty;`
   - `TagCategory Category { get; set; } = TagCategory.Custom;`
   - `int SortOrder { get; set; }`
   - `bool IsSystemSeeded { get; set; }`
   - `IAuditable` properties
   - `ICollection<RecipeTag> RecipeTags { get; set; } = new List<RecipeTag>();`

4. **`RecipeTag.cs`**:
   - `Guid Id { get; set; } = Guid.NewGuid();`
   - `Guid RecipeId { get; set; }`
   - `Guid TagId { get; set; }`
   - `IAuditable` properties
   - `Recipe Recipe { get; set; } = null!;`
   - `Tag Tag { get; set; } = null!;`

5. **Modify `Data/Entities/Recipe.cs`**:
   - Add `public ICollection<RecipeCollection> RecipeCollections { get; set; } = new List<RecipeCollection>();`
   - Add `public ICollection<RecipeTag> RecipeTags { get; set; } = new List<RecipeTag>();`

### 2.3 EF Core Fluent Configurations (`Data/Configurations/`)

- **Important EF Core Key Rule:** Configure `builder.Property(x => x.Id).ValueGeneratedNever();` on `Collection`, `RecipeCollection`, `Tag`, and `RecipeTag` so client-initialized `Guid.NewGuid()` keys never trigger spurious `DbUpdateConcurrencyException`s when added via `DbContext` or navigation collections.
- **`CollectionConfiguration.cs`** (`ToTable("Collections")`):
  - `Name`: required, `HasMaxLength(100)`.
  - `NormalizedName`: required, `HasMaxLength(100)`, **unique index** (`HasIndex(c => c.NormalizedName).IsUnique()`).
  - `Slug`: required, `HasMaxLength(120)`, **unique index** (`HasIndex(c => c.Slug).IsUnique()`).
  - `Description`: `HasMaxLength(500)`.
  - `CreatedBy` / `ModifiedBy`: required, `HasMaxLength(256)`.
- **`RecipeCollectionConfiguration.cs`** (`ToTable("RecipeCollections")`):
  - Unique composite index on `(RecipeId, CollectionId)`.
  - `HasOne(rc => rc.Recipe).WithMany(r => r.RecipeCollections).HasForeignKey(rc => rc.RecipeId).OnDelete(DeleteBehavior.Cascade)`.
  - `HasOne(rc => rc.Collection).WithMany(c => c.RecipeCollections).HasForeignKey(rc => rc.CollectionId).OnDelete(DeleteBehavior.Cascade)`.
  - *(SQL Server Cascade Safety: `Recipes` and `Collections` have no direct FK between each other, so single cascade paths from each root table into `RecipeCollections` are 100% valid in SQL Server.)*
- **`TagConfiguration.cs`** (`ToTable("Tags")`):
  - `Name`: required, `HasMaxLength(80)`.
  - `NormalizedName`: required, `HasMaxLength(80)`.
  - `Slug`: required, `HasMaxLength(100)`, **unique index** (`HasIndex(t => t.Slug).IsUnique()`).
  - Unique composite index on `(Category, NormalizedName)` (`HasIndex(t => new { t.Category, t.NormalizedName }).IsUnique()`).
  - `CreatedBy` / `ModifiedBy`: required, `HasMaxLength(256)`.
- **`RecipeTagConfiguration.cs`** (`ToTable("RecipeTags")`):
  - Unique composite index on `(RecipeId, TagId)`.
  - `HasOne(rt => rt.Recipe).WithMany(r => r.RecipeTags).HasForeignKey(rt => rt.RecipeId).OnDelete(DeleteBehavior.Cascade)`.
  - `HasOne(rt => rt.Tag).WithMany(t => t.RecipeTags).HasForeignKey(rt => rt.TagId).OnDelete(DeleteBehavior.Cascade)`.

### 2.4 DTOs, Requests & Service Contracts (`Core/`)

1. **`Core/Media/MediaModels.cs` & `Core/Media/IMediaStorageService.cs`** (`namespace App.Core.Media`):
   ```csharp
   public record UploadedRecipeCoverDto(string CoverImagePath, string ThumbnailImagePath);

   public record UploadedMediaDto(string ImagePath);

   public interface IMediaStorageService
   {
       Task<UploadedRecipeCoverDto> SaveRecipeCoverAsync(
           Stream content,
           string originalFileName,
           long contentLength,
           CancellationToken cancellationToken = default
       );

       Task<UploadedMediaDto> SaveStepImageAsync(
           Stream content,
           string originalFileName,
           long contentLength,
           CancellationToken cancellationToken = default
       );

       Task DeleteMediaFileAsync(string? mediaUrlPath, CancellationToken cancellationToken = default);
   }
   ```

2. **`Core/Recipes/TaxonomyModels.cs` & `Core/Recipes/ITaxonomyService.cs`** (`namespace App.Core.Recipes`):
   ```csharp
   public record CollectionDto(
       Guid Id,
       string Name,
       string Slug,
       string? Description,
       int SortOrder,
       bool IsSystemSeeded,
       int RecipeCount
   );

   public record UpsertCollectionRequest(
       [Required, MaxLength(100)] string Name,
       [MaxLength(500)] string? Description = null,
       int SortOrder = 0
   );

   public record TagDto(
       Guid Id,
       string Name,
       string Slug,
       TagCategory Category,
       int SortOrder,
       bool IsSystemSeeded,
       int RecipeCount
   );

   public record UpsertTagRequest(
       [Required, MaxLength(80)] string Name,
       TagCategory Category = TagCategory.Custom,
       int SortOrder = 0
   );

   public interface ITaxonomyService
   {
       Task<IReadOnlyList<CollectionDto>> GetCollectionsAsync(CancellationToken cancellationToken = default);
       Task<CollectionDto> CreateCollectionAsync(UpsertCollectionRequest request, CancellationToken cancellationToken = default);
       Task<CollectionDto?> UpdateCollectionAsync(Guid id, UpsertCollectionRequest request, CancellationToken cancellationToken = default);
       Task<bool> DeleteCollectionAsync(Guid id, CancellationToken cancellationToken = default);

       Task<IReadOnlyList<TagDto>> GetTagsAsync(TagCategory? category = null, CancellationToken cancellationToken = default);
       Task<TagDto> CreateTagAsync(UpsertTagRequest request, CancellationToken cancellationToken = default);
       Task<TagDto?> UpdateTagAsync(Guid id, UpsertTagRequest request, CancellationToken cancellationToken = default);
       Task<bool> DeleteTagAsync(Guid id, CancellationToken cancellationToken = default);
   }
   ```

3. **Updates in `Core/Recipes/RecipeModels.cs` & `Core/Recipes/IRecipeService.cs`**:
   - Add badge & matcher records:
     ```csharp
     public record RecipeCollectionBadgeDto(Guid Id, string Name, string Slug);

     public record RecipeTagBadgeDto(Guid Id, string Name, string Slug, TagCategory Category);

     public record UpdateRecipeOrganizationRequest(
         IReadOnlyList<Guid>? CollectionIds = null,
         IReadOnlyList<Guid>? TagIds = null
     );

     public record IngredientMatchRequest([Required] IReadOnlyList<Guid> FoodItemIds);

     public record IngredientMatchResultDto(
         RecipeSummaryDto Recipe,
         int MatchedCount,
         int SelectedOnHandCount,
         int AdditionalNonStapleCount,
         int PantryStaplesUsedCount,
         IReadOnlyList<string> MatchedIngredientNames,
         IReadOnlyList<string> MissingNonStapleNames
     );
     ```
   - Extend `RecipeSummaryDto` with `IReadOnlyList<RecipeCollectionBadgeDto> Collections` and `IReadOnlyList<RecipeTagBadgeDto> Tags`.
   - Extend `RecipeDetailDto` with `IReadOnlyList<RecipeCollectionBadgeDto> Collections` and `IReadOnlyList<RecipeTagBadgeDto> Tags`.
   - Extend `RecipeQueryParameters` with `Guid? CollectionId = null` and `Guid[]? TagIds = null`.
   - Extend `RecipeStepInput` with `[MaxLength(500)] string? ImagePath = null`.
   - Extend `UpsertRecipeRequest` with:
     - `[MaxLength(500)] string? CoverImagePath = null`
     - `[MaxLength(500)] string? ThumbnailImagePath = null`
     - `IReadOnlyList<Guid>? CollectionIds = null`
     - `IReadOnlyList<Guid>? TagIds = null`
   - Extend `IRecipeService` with:
     - `Task<RecipeDetailDto?> UpdateRecipeOrganizationAsync(Guid id, UpdateRecipeOrganizationRequest request, CancellationToken cancellationToken = default);`
     - `Task<IReadOnlyList<IngredientMatchResultDto>> MatchRecipesByIngredientsAsync(IngredientMatchRequest request, CancellationToken cancellationToken = default);`

### 2.5 Service & Media Business Logic (`Infrastructure/Media/` & `Data/Services/`)

- **`Infrastructure/Media/MediaStorageOptions.cs` & `LocalFileMediaStorageService.cs`**:
  - `SectionName = "MediaStorage"`; properties: `string UploadsPath { get; set; } = "App_Data/uploads";` and `long MaxUploadBytes { get; set; } = 15 * 1024 * 1024;` (`15 MB`).
  - Helper `ResolveUploadsRoot(string? configuredPath, string contentRootPath)`: uses `configuredPath` directly when `Path.IsPathRooted(configuredPath)` is true; otherwise combines with `contentRootPath` via `Path.GetFullPath`.
  - Validation: reject `contentLength <= 0` or `> MaxUploadBytes` with `DomainValidationException`. Load image with `SixLabors.ImageSharp.Image.LoadAsync(content, cancellationToken)` (catching `UnknownImageFormatException` / `InvalidImageContentException` $\rightarrow$ throw `DomainValidationException("Unsupported or corrupted image file. Upload a valid JPEG, PNG, WebP, or GIF image.")`).
  - Orientation & Privacy Hygiene: apply `image.Mutate(x => x.AutoOrient());` and clear `image.Metadata.ExifProfile = null; image.Metadata.IptcProfile = null; image.Metadata.XmpProfile = null;`.
  - Variant Output (`WebpEncoder { Quality = 82, FileFormat = WebpFileFormatType.Lossy }`):
    - `SaveRecipeCoverAsync`: creates `<UploadsRoot>/recipes/{guid:N}-cover.webp` (resized with `ResizeMode.Max` to `1600x1200` only if dimensions exceed `1600x1200`) and `<UploadsRoot>/recipes/{guid:N}-thumb.webp` (cloned & resized with `ResizeMode.Max` to `600x450` only if dimensions exceed `600x450`). Returns `/api/uploads/recipes/{guid:N}-cover.webp` and `/api/uploads/recipes/{guid:N}-thumb.webp`.
    - `SaveStepImageAsync`: creates `<UploadsRoot>/steps/{guid:N}.webp` (resized with `ResizeMode.Max` to `1200x900` only if dimensions exceed `1200x900`). Returns `/api/uploads/steps/{guid:N}.webp`.
    - `DeleteMediaFileAsync`: accepts `/api/uploads/<subPath>`; strips prefix, resolves `Path.GetFullPath(Path.Combine(uploadsRoot, subPath))`, verifies `fullPath.StartsWith(uploadsRootWithSeparator, StringComparison.OrdinalIgnoreCase)` (blocking `..` traversal), and deletes the file if it exists.
- **`Data/Seeding/HouseholdCatalogSeeder.cs`**:
  - Seed 4 starter `Collections` when `!await db.Collections.AnyAsync()`: *Weeknight Staples* (`10`), *Weekend Projects* (`20`), *Holiday & Hosting* (`30`), *Sauces & Components* (`40`).
  - Seed starter `Tags` when `!await db.Tags.AnyAsync()`:
    - `Course`: *Main*, *Side*, *Breakfast*, *Lunch*, *Dessert*, *Appetizer*, *Sauce / Component*
    - `Cuisine`: *American*, *Italian*, *Mexican*, *Thai*, *Japanese*, *Indian*, *Mediterranean*
    - `Dietary`: *Vegetarian*, *Vegan*, *Gluten-Free*, *Dairy-Free*
    - `Equipment`: *Sheet Pan*, *One-Pot*, *Instant Pot*, *Air Fryer*, *Dutch Oven*, *Grill*, *Slow Cooker*
- **`Data/Services/TaxonomyService.cs`**:
  - Read queries use `.AsNoTracking()` ordered by `SortOrder` then `Name` (and `Category` first for tags), projecting `RecipeCount = x.RecipeCollections.Count(rc => rc.Recipe.Status != RecipeStatus.Archived)` (and `RecipeTags` analogously).
  - Validate required non-whitespace `Name`, enforce uniqueness on `Collection.NormalizedName` and `Tag(Category, NormalizedName)` throwing `ConflictException` (`409`), and generate unique kebab-case `Slug`s (`{category}-{kebab}` for tags, `{kebab}` for collections).
- **`Data/Services/RecipeService.cs`**:
  - **Media Path Validation & Cleanup:** Helper `ValidateAndNormalizeMediaPath(string? path)` returns `null` if null/whitespace, or throws `DomainValidationException` if path does not start with `"/api/uploads/"` or contains `".."` or backslashes. On `UpdateRecipeAsync` and `DeleteRecipeAsync`, compare previously stored `CoverImagePath`, `ThumbnailImagePath`, and step `ImagePath`s against newly saved paths and call `mediaStorageService.DeleteMediaFileAsync` for any removed paths.
  - **Taxonomy Sync:** Validate all `CollectionIds` exist in `dbContext.Collections` and all `TagIds` exist in `dbContext.Tags` (else throw `DomainValidationException`). On `CreateRecipeAsync`, add `RecipeCollection` / `RecipeTag` rows when provided. On `UpdateRecipeAsync`, if `request.CollectionIds is not null`, replace `RecipeCollections` with distinct IDs; if `request.TagIds is not null`, replace `RecipeTags` with distinct IDs.
  - **`GetRecipesAsync` Facets & Search:**
    - If `query.CollectionId.HasValue`, filter `q.Where(r => r.RecipeCollections.Any(rc => rc.CollectionId == query.CollectionId.Value))`.
    - If `query.TagIds is { Length: > 0 }`, iterate `foreach (var tagId in query.TagIds.Distinct())` chaining `q = q.Where(r => r.RecipeTags.Any(rt => rt.TagId == currentTagId));` (SQL Server + EF InMemory safe AND-semantics).
    - Expand `query.Search` predicate to also match `r.RecipeTags.Any(rt => rt.Tag.NormalizedName.Contains(norm))` and `r.RecipeCollections.Any(rc => rc.Collection.NormalizedName.Contains(norm))`.
  - **`MatchRecipesByIngredientsAsync`:**
    - Validate `request.FoodItemIds` has between `1` and `20` IDs (else `DomainValidationException`).
    - Load matching `FoodItem`s (with `Aliases`) as `AsNoTracking()` (throw `DomainValidationException` if none of the provided IDs exist).
    - Load all `Published` recipes (`AsNoTracking()`) with `Ingredients` + `FoodItem`, `RecipeCollections` + `Collection`, `RecipeTags` + `Tag` (plus 1-level linked sub-recipe ingredients when `LinkedRecipeId != null`), build `MatchableRecipeCandidate` items, and delegate to `IngredientMatcher.RankRecipes(...)`.

---

## 3. API Endpoints & Static Media Hosting (`API/`)

All controllers reside in `API/Controllers/`, carry `[ApiController]` and `[Authorize]`, use relative routes (mounted under `/api` by `Program.cs`), and remain thin HTTP adapters translating `DomainValidationException` $\rightarrow$ `400`, `NotFound` $\rightarrow$ `404`, and `ConflictException` $\rightarrow$ `409`.

| Controller | Verb | Route (`/api/...`) | Request Body / Query | Response (`200`/`201`/`204`) | Error Cases |
| :--- | :--- | :--- | :--- | :--- | :--- |
| `MediaController` | `POST` | `/media/recipe-cover` | `multipart/form-data` (`IFormFile file`, `[RequestSizeLimit(16_777_216)]`) | `200 OK` (`UploadedRecipeCoverDto`) | `400` invalid/empty/oversize image, `401` |
| `MediaController` | `POST` | `/media/recipe-step` | `multipart/form-data` (`IFormFile file`, `[RequestSizeLimit(16_777_216)]`) | `200 OK` (`UploadedMediaDto`) | `400` invalid/empty/oversize image, `401` |
| `CollectionsController` | `GET` | `/collections` | — | `200 OK` (`IReadOnlyList<CollectionDto>`) | `401` |
| `CollectionsController` | `POST` | `/collections` | `UpsertCollectionRequest` | `201 Created` (`CollectionDto`) | `400`, `401`, `409` duplicate name |
| `CollectionsController` | `PUT` | `/collections/{id:guid}` | `UpsertCollectionRequest` | `200 OK` (`CollectionDto`) | `400`, `401`, `404`, `409` |
| `CollectionsController` | `DELETE` | `/collections/{id:guid}` | — | `204 No Content` | `401`, `404` |
| `TagsController` | `GET` | `/tags` | `?category=` (optional `TagCategory`) | `200 OK` (`IReadOnlyList<TagDto>`) | `401` |
| `TagsController` | `POST` | `/tags` | `UpsertTagRequest` | `201 Created` (`TagDto`) | `400`, `401`, `409` duplicate in category |
| `TagsController` | `PUT` | `/tags/{id:guid}` | `UpsertTagRequest` | `200 OK` (`TagDto`) | `400`, `401`, `404`, `409` |
| `TagsController` | `DELETE` | `/tags/{id:guid}` | — | `204 No Content` | `401`, `404` |
| `RecipesController` | `GET` | `/recipes` | `[FromQuery] RecipeQueryParameters` (`search`, `status`, `difficulty`, `maxTotalTimeMinutes`, `foodItemId`, `collectionId`, `tagIds`) | `200 OK` (`IReadOnlyList<RecipeSummaryDto>`) | `401` |
| `RecipesController` | `PUT` | `/recipes/{id:guid}/organization` | `UpdateRecipeOrganizationRequest` | `200 OK` (`RecipeDetailDto`) | `400`, `401`, `404` |
| `RecipesController` | `POST` | `/recipes/match-ingredients` | `IngredientMatchRequest` | `200 OK` (`IReadOnlyList<IngredientMatchResultDto>`) | `400`, `401` |

- **Static Uploads Middleware (`API/Program.cs`):**
  - Resolve `uploadsRoot` via `MediaStorageOptions.ResolveUploadsRoot(builder.Configuration["MediaStorage:UploadsPath"], app.Environment.ContentRootPath)` and call `Directory.CreateDirectory(uploadsRoot)`.
  - Configure `FileExtensionContentTypeProvider` registering `.webp` (`"image/webp"`), `.jpg`/`.jpeg` (`"image/jpeg"`), `.png` (`"image/png"`), `.gif` (`"image/gif"`), and `.pdf` (`"application/pdf"`).
  - Mount `app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(uploadsRoot), RequestPath = "/api/uploads", ContentTypeProvider = provider, OnPrepareResponse = ... })` **before** `app.MapFallbackToFile("index.html")`.

---

## 4. Angular Frontend Design (`Client/`)

All Angular services use `@Service()` and `apiUrl(...)`. Every component resides in its own dedicated kebab-case folder with external `.ts`, `.html`, `.scss`, and `.spec.ts` files, using signals (`signal()`, `computed()`) and Material 3 CSS variables (`--mat-sys-*`, `--app-success*`, `--app-warning*`).

### 4.1 Models & Services (`Client/src/app/core/`)

1. **`core/models/media.models.ts`**:
   - `UploadedRecipeCoverDto { coverImagePath: string; thumbnailImagePath: string; }`
   - `UploadedMediaDto { imagePath: string; }`
2. **`core/models/taxonomy.models.ts`**:
   - `TagCategory = 'Course' | 'Cuisine' | 'Dietary' | 'Equipment' | 'Custom'`
   - `CollectionDto`, `UpsertCollectionRequest`, `TagDto`, `UpsertTagRequest`, `RecipeCollectionBadgeDto`, `RecipeTagBadgeDto`
3. **`core/models/recipe.models.ts`**:
   - Add `collections: RecipeCollectionBadgeDto[]` and `tags: RecipeTagBadgeDto[]` to `RecipeSummaryDto` and `RecipeDetailDto`.
   - Add `collectionId?: string | null` and `tagIds?: string[] | null` to `RecipeQueryParameters`.
   - Add `imagePath?: string | null` to `RecipeStepInput`, and `coverImagePath?: string | null`, `thumbnailImagePath?: string | null`, `collectionIds?: string[] | null`, `tagIds?: string[] | null` to `UpsertRecipeRequest`.
   - Add `UpdateRecipeOrganizationRequest`, `IngredientMatchRequest`, and `IngredientMatchResultDto`.
4. **`core/services/media/media.service.ts`** (`@Service()`):
   - `uploadRecipeCover(file: File): Observable<UploadedRecipeCoverDto>` (`FormData` with field `'file'` $\rightarrow$ `POST apiUrl('/media/recipe-cover')`)
   - `uploadStepImage(file: File): Observable<UploadedMediaDto>` (`FormData` with field `'file'` $\rightarrow$ `POST apiUrl('/media/recipe-step')`)
5. **`core/services/taxonomy/taxonomy.service.ts`** (`@Service()`):
   - `getCollections()`, `createCollection(req)`, `updateCollection(id, req)`, `deleteCollection(id)`
   - `getTags(category?)`, `createTag(req)`, `updateTag(id, req)`, `deleteTag(id)`
6. **`core/services/recipe/recipe.service.ts`**:
   - Append `collectionId` and repeated `tagIds` (`for (const id of query.tagIds) params = params.append('tagIds', id)`) in `getRecipes`.
   - Add `updateRecipeOrganization(id: string, req: UpdateRecipeOrganizationRequest): Observable<RecipeDetailDto>` and `matchRecipesByIngredients(req: IngredientMatchRequest): Observable<IngredientMatchResultDto[]>`.

### 4.2 Feature Components & Routes (`Client/src/app/features/`)

1. **`features/shell/app-shell/` & `features/shell/global-search-dialog/`**:
   - **Navigation Updates (`AppShellComponent`):**
     - Desktop nav (`nav.desktop-nav`): `Cookbook` (`/recipes`, icon `menu_book`), `Matcher` (`/matcher`, icon `blender`), `Food Catalog` (`/catalog`, icon `kitchen`).
     - Mobile bottom nav (`nav.mobile-bottom-nav` at `<= 768px` / `360px`): `Cookbook` (`/recipes`), `Matcher` (`/matcher`), `Catalog` (`/catalog`).
     - Top bar Quick Search trigger: desktop pill button (`search` icon + `"Search..."` + `Ctrl+K` hint badge, `aria-label="Open quick search"`) collapsing cleanly into an icon button on `360px` mobile.
     - Global keyboard shortcut via `host: { '(window:keydown)': 'onGlobalKeydown($event)' }`: intercepts `Ctrl+K` / `Meta+K` (`event.preventDefault()`) and opens `GlobalSearchDialogComponent` (avoiding duplicate opens if already open).
   - **`GlobalSearchDialogComponent` (`features/shell/global-search-dialog/`):**
     - Autofocused search input filtering recipes (`RecipeService.getRecipes({ search, status: 'All' })`) and collections (`TaxonomyService.getCollections()`), plus quick navigation shortcuts (*New Recipe*, *Use Up Ingredients*, *Food Catalog*).
     - Supports `ArrowDown` / `ArrowUp` active result highlight and `Enter` selection, closing the dialog and navigating to the target route.

2. **`features/recipes/collection-dialog/`, `tag-dialog/` & `recipe-organization-dialog/`**:
   - **`CollectionDialogComponent`:** Create or edit a `Collection` (`Name`, `Description`, `SortOrder`). Returns saved `CollectionDto`.
   - **`TagDialogComponent`:** Create or edit a `Tag` (`Name`, `Category` select across `Course`, `Cuisine`, `Dietary`, `Equipment`, `Custom`). Returns saved `TagDto`.
   - **`RecipeOrganizationDialogComponent`:** Quick modal opened from `RecipeDetailComponent` (`"Organize"` button) with selectable Collection checkboxes/chips, categorized Tag chips, inline `+ New Collection` / `+ New Tag` buttons, and Save calling `recipeService.updateRecipeOrganization(...)`.

3. **Enhancements to `features/recipes/recipe-editor/`**:
   - **Cover Photo Section:** File input (`accept="image/jpeg,image/png,image/webp,image/gif"`, `class="cover-file-input"`) + upload button (`"Upload Cover Photo"`), uploading spinner, live WebP cover preview (`img.cover-preview-img`), and `"Remove Cover Photo"` button storing `coverImagePath` and `thumbnailImagePath` signals.
   - **Collections & Tags Card:** Multi-select `mat-select` or toggleable filter chips for `Collections` (`selectedCollectionIds` signal) and `Tags` grouped by `TagCategory` (`selectedTagIds` signal), plus `+ New Collection` and `+ New Tag` buttons opening `CollectionDialogComponent` / `TagDialogComponent` and auto-selecting the newly created item.
   - **Per-Step Photo Controls:** Each step group in `stepsArray` includes `imagePath: [step.imagePath ?? null]`, an `"Add Step Photo"` file input trigger (`input.step-file-input`), preview (`img.step-preview-img`), and `"Remove Step Photo"` button.

4. **Enhancements to `features/recipes/recipe-list/`**:
   - **Header Actions:** Add secondary stroked button `routerLink="/matcher"` (`mat-icon` `blender`, label `"Use Up Ingredients"`) next to `"New Recipe"`.
   - **Collections Strip:** Horizontal scrollable pill strip (`All Cookbooks` + each `CollectionDto` showing name + recipe count, plus `+ New Collection` chip and edit/delete context menu on active collection). Selecting a collection sets `selectedCollectionId` signal.
   - **Faceted Filter Row:** Keep existing `.search-field`, Status `mat-select` (default `'Published'`), Difficulty `mat-select`, and Max Time `mat-select` (adding `<mat-option [value]="45">Under 45 min</mat-option>`), plus a **Tags** multi-select `mat-select` (`optgroup`s by `TagCategory`) and `"Clear Filters"` action when any facet is active. Client/server filtering matches search text across title, description, source, collection names, and tag names (or re-queries `getRecipes` when facets/search include ingredient/alias terms).
   - **Recipe Cards (`.recipe-card`):** Display `img.recipe-card-thumb` when `recipe.thumbnailImagePath` is non-null, and render compact collection & tag chips cleanly at both desktop and `360px` mobile widths.

5. **Enhancements to `features/recipes/recipe-detail/`**:
   - Render `img.recipe-hero-image` when `recipe()!.coverImagePath` is present.
   - Render Collection badges and categorized Tag chips in `.recipe-hero`, plus an `"Organize"` button (`aria-label="Organize collections and tags"`) in `.detail-top-actions` opening `RecipeOrganizationDialogComponent` and updating the `recipe` signal on save.
   - Render `img.step-reference-image` inside each step card when `step.imagePath` is non-null.

6. **`features/recipes/fridge-matcher/` (`/matcher` — `"Use Up Ingredients"` Fridge Matcher)**:
   - Loads `FoodCatalogService.getFoodItems()` on init.
   - **On-Hand Picker Card:** Autocomplete search input (matching canonical `FoodItem.name` and `aliases`) + removable selected `FoodItem` chips + quick-tap suggestion chips of non-staple catalog items (e.g., *Chicken Thighs*, *Cilantro*, *Lime*, *Garlic*, *Yellow Onion*, *Green Onion*, *Large Eggs*).
   - Automatically invokes `recipeService.matchRecipesByIngredients({ foodItemIds })` whenever 1+ on-hand items are selected.
   - **Ranked Results List:** Displays `.matcher-result-card` items ordered by best match:
     - Prominent match badge (`"2 of 2 matched"`), readiness badge (`"Ready to cook!"` in `--app-success` when `additionalNonStapleCount === 0`, or `"Needs N more"`), matched ingredient chips, missing non-staple chips, and pantry-staple note (`"+ N pantry staples ignored"`).
     - Clicking any result card navigates to `/recipes/:id`.
   - Mobile `360px` layout stacks picker chips and result cards cleanly in a single column without horizontal scrolling.

---

## 5. Playwright E2E Test Scenarios (`Tests/Client.E2ETests/DiscoveryAndMediaE2ETests.cs`)

Create `Tests/Client.E2ETests/DiscoveryAndMediaE2ETests.cs` inheriting from `E2ETestBase` with the following `[TestMethod]` end-to-end scenarios:

- `[TestMethod] RecipeMediaAndTaxonomy_UploadPhotosAndOrganize_FiltersInCookbook()`:
  1. Registers/logs in via `RegisterAndLoginAsync()`.
  2. Creates a custom Collection (e.g., `"Summer Grill Nights"`) from the Cookbook collections bar via `CollectionDialogComponent`.
  3. Navigates to `/recipes/new`, fills recipe metadata/ingredient/step, uploads an in-memory generated valid PNG byte payload (`FilePayload` with `MimeType = "image/png"`) to both the Cover Photo input (`input.cover-file-input`) and Step 1 photo input (`input.step-file-input`), selects `"Summer Grill Nights"` collection and seeded tags (`"Vegetarian"`, `"Sheet Pan"`), and saves.
  4. Verifies `RecipeDetailComponent` renders `img.recipe-hero-image` (with `.webp` `src` under `/api/uploads/recipes/` and `naturalWidth > 0`), `img.step-reference-image` (`naturalWidth > 0`), `"Summer Grill Nights"`, `"Vegetarian"`, and `"Sheet Pan"`.
  5. Navigates back to `/recipes`, verifies the card displays the thumbnail (`img.recipe-card-thumb`) and filters cleanly when clicking the `"Summer Grill Nights"` collection chip and selecting tag filters.
- `[TestMethod] FridgeMatcherAndGlobalSearch_RanksRecipesIgnoringPantryStaples_AndNavigatesViaCmdK()`:
  1. Registers/logs in and creates two `Published` recipes linked to seeded `FoodItem`s:
     - **Recipe A (*Weeknight Lime Chicken*):** uses `Chicken Thighs` + `Lime` + `Kosher Salt` (`IsPantryStaple = true`) + `Extra-Virgin Olive Oil` (`IsPantryStaple = true`) $\rightarrow$ `0` additional non-staples needed.
     - **Recipe B (*Loaded Chicken Bowls*):** uses `Chicken Thighs` + `Lime` + `Cilantro` + `Yellow Onion` $\rightarrow$ `2` additional non-staples needed.
  2. Navigates to `/matcher` (`Fridge Matcher`), selects `Chicken Thighs` and `Lime` as on-hand ingredients, and asserts both recipes appear with **Recipe A** ranked first (`"2 of 2 matched"` and `"Ready to cook!"` / `0` additional non-staples) above **Recipe B** (`2` additional non-staples).
  3. Triggers Global Instant Search via `Page.Keyboard.PressAsync("Control+k")`, types `"Weeknight Lime Chicken"`, and selects the result to navigate directly to Recipe A's detail page.
- `[TestMethod] DiscoveryAndMatcher_MobileViewport_360px_RendersAndNavigatesCleanly()`:
  1. Registers/logs in and resizes viewport to `360x640`.
  2. Verifies `nav.mobile-bottom-nav` displays `Cookbook`, `Matcher`, and `Catalog` tabs without horizontal page overflow (`scrollWidth <= clientWidth`).
  3. Taps `Matcher` in the mobile bottom bar, selects an on-hand ingredient chip, verifies mobile layout renders cleanly, and opens `GlobalSearchDialogComponent` via the mobile top-bar search icon button (`button[aria-label='Open quick search']`).

---

## 6. Step-by-Step Execution Checklist (For Implementation Agent)

- [ ] **Task 1: Backend Core, Media Infrastructure & Data Layer**
  - Add `SixLabors.ImageSharp` (`3.1.12`) to `Infrastructure/App.Infrastructure.csproj`.
  - Create `Core/Media/MediaModels.cs`, `Core/Media/IMediaStorageService.cs`, `Infrastructure/Media/MediaStorageOptions.cs`, and `Infrastructure/Media/LocalFileMediaStorageService.cs`; register in `InfrastructureServiceCollectionExtensions.cs`.
  - Update `Core/Recipes/RecipeEnums.cs`, `Core/Recipes/RecipeModels.cs`, `Core/Recipes/IRecipeService.cs`, and create `Core/Recipes/TaxonomyModels.cs`, `Core/Recipes/ITaxonomyService.cs`, and `Core/Recipes/IngredientMatcher.cs`.
  - Create `Collection`, `RecipeCollection`, `Tag`, and `RecipeTag` entities & EF configurations (`ValueGeneratedNever()` on client-assigned `Guid` IDs); update `Recipe.cs`, `AppDbContext.cs`, `HouseholdCatalogSeeder.cs`, `TaxonomyService.cs`, `RecipeService.cs`, and `DataServiceCollectionExtensions.cs`.
  - Ensure Docker SQL Server is running, then generate, apply, and format the EF Core migration:
    ```powershell
    .\scripts\ensure-local-infra.ps1
    dotnet ef migrations add AddCollectionsAndTags --project Data/App.Data.csproj
    dotnet ef database update --project Data/App.Data.csproj
    dotnet csharpier format Data/Migrations
    ```
  - Write and run backend unit tests (`IngredientMatcherTests.cs` & `LocalFileMediaStorageServiceTests.cs`) until 100% green:
    ```powershell
    dotnet test --project Tests/Core.UnitTests/App.Core.UnitTests.csproj
    dotnet test --project Tests/Infrastructure.UnitTests/App.Infrastructure.UnitTests.csproj
    ```

- [ ] **Task 2: API Controllers, Static Uploads Hosting & Integration Tests**
  - Update `API/appsettings.json`, `API/appsettings.Development.json`, `API/Dockerfile`, and `API/Program.cs` (`/api/uploads` static file serving).
  - Create `API/Controllers/MediaController.cs`, `API/Controllers/CollectionsController.cs`, `API/Controllers/TagsController.cs`, and add `PUT {id:guid}/organization` + `POST match-ingredients` to `API/Controllers/RecipesController.cs`.
  - Update `AppWebApplicationFactory.cs` (isolated temp `MediaStorage:UploadsPath` + cleanup) and `OpenApiIntegrationTests.cs`.
  - Create `MediaUploadIntegrationTests.cs`, `TaxonomyIntegrationTests.cs`, and `RecipeDiscoveryIntegrationTests.cs`.
  - Run API integration tests until 100% green:
    ```powershell
    dotnet test --project Tests/API.IntegrationTests/App.API.IntegrationTests.csproj
    ```

- [ ] **Task 3: Angular Client & Frontend Unit Tests**
  - Create `media.models.ts`, `taxonomy.models.ts`, `MediaService` (+ `.spec.ts`), `TaxonomyService` (+ `.spec.ts`), and update `recipe.models.ts` and `RecipeService` (+ `.spec.ts`).
  - Create dedicated component folders (`collection-dialog/`, `tag-dialog/`, `recipe-organization-dialog/`, `fridge-matcher/`, `global-search-dialog/`) with external `.ts`, `.html`, `.scss`, and `.spec.ts` files.
  - Update `app.routes.ts`, `app-shell/`, `recipe-editor/`, `recipe-list/`, and `recipe-detail/` (including updating mock DTOs in existing `.spec.ts` files).
  - Run Angular lint, production build, and unit tests in `Client/` until 100% green:
    ```powershell
    # In Client/:
    npm run lint
    npm run build:production
    npm test -- --watch=false
    ```

- [ ] **Task 4: Playwright E2E Verification & Final Formatting**
  - Update `Tests/Client.E2ETests/PlaywrightServerFixture.cs` with isolated temp `MediaStorage__UploadsPath` cleanup and create `Tests/Client.E2ETests/DiscoveryAndMediaE2ETests.cs` implementing all scenarios in Section 5.
  - Build solution & Angular production bundle, then run Playwright E2E tests until 100% green:
    ```powershell
    dotnet build App.slnx
    dotnet test --project Tests/Client.E2ETests/App.Client.E2ETests.csproj
    ```
  - Format all backend and frontend files and run the full solution test suite:
    ```powershell
    dotnet csharpier format .
    # In Client/:
    npm run format
    npm run lint:fix
    # From repo root:
    dotnet test App.slnx
    ```
  - Update this plan's status header to `[x] Completed`.
