# Phase 06: Personal Access Tokens (`lr_pat_...`) & Embedded MCP Server (`/api/mcp` — Cookbook & Ingestion Tools)

> **Branch:** `feature/phase-06-pat-and-mcp-server`
> **Status:** `[ ]` Not Started (`[ ]` Not Started | `[~]` In Progress | `[x]` Completed)
> **PRD Reference:** `docs/prd.md` — Sections 1, 2, 3, 4.3 (4.3.1 & 4.3.2), 5, and 6 (Milestone 2, Slice 3 of 3)
> **Target Execution:** Run `/execute-phase` to execute this plan step-by-step.

---

## 1. Scope & Objectives

This phase completes **Milestone 2** of **LocalRoast** by delivering **Personal Access Tokens (`lr_pat_...`)** with SHA-256 hash storage, strict MCP/REST authentication scheme isolation, an **Account Settings MCP & API Tokens** management UI with 1-click client configuration snippets, and the **Embedded Model Context Protocol (`MCP`) Streamable HTTP Server** mounted at `/api/mcp` exposing all Milestone 2 Cookbook, Discovery, Ingestion, and Recipe Authoring tools:

1. **Named, Revocable Personal Access Tokens (`PersonalAccessToken`, `IPersonalAccessTokenService`, `/api/account/tokens`):**
   - Generate cryptographically random tokens formatted as `lr_pat_<43-char-base64url>` (`256 bits` entropy via `RandomNumberGenerator.Fill`), returning the plaintext token **once and only once** at creation time.
   - Store **only** the lowercase hex SHA-256 `TokenHash` (unique index) and a safe display `TokenPrefix` (e.g., `lr_pat_k9Qp2XvA`) in SQL Server alongside `Name`, `UserId`, `ExpiresAt` (optional), `LastUsedAt`, and `RevokedAt`.
   - Enforce per-user ownership on `/api/account/tokens` (`GET`, `POST`, `POST {id}/revoke`, `DELETE {id}`): users can only view, revoke, or delete their own tokens (`403 Forbidden` if targeting another household user's token ID).

2. **Strict Authentication Scheme Isolation (`PersonalAccessTokenAuthenticationHandler` vs `DualBearerAuthenticationHandler`):**
   - Register a dedicated `McpPat` authentication scheme (`PersonalAccessTokenAuthenticationHandler`) and `McpAuthorizationPolicy` exclusively protecting `/api/mcp`:
     - Authenticates `Authorization: Bearer lr_pat_...` headers via SHA-256 lookup + `CryptographicOperations.FixedTimeEquals`, rejecting anonymous requests, browser JWTs, malformed tokens, expired tokens, or revoked tokens with `401 Unauthorized`.
     - Updates `LastUsedAt` on first use and throttled once per `60` seconds on subsequent calls.
     - Populates `HttpContext.User` (and `McpUserContextAccessor` fallback) with the token owner's `UserId`, `Username`, and `Email`, ensuring `AuditSaveChangesInterceptor` attributes `CreatedBy` / `ModifiedBy` on every MCP write (`Recipe`, `FoodItem`, `IngestionJob`) to the token's owning household user.
   - Harden `DualBearerAuthenticationHandler` (`Bearer` scheme on regular REST controllers) to immediately fail any token starting with `lr_pat_` so a leaked desktop MCP config file can **never** change passwords, emails, or mint new PATs on `/api/account/*`.

3. **Embedded Stateless Streamable HTTP MCP Server (`/api/mcp`, `ModelContextProtocol.AspNetCore` `2.3.0`):**
   - Mount `app.MapMcp("/api/mcp")` inside ASP.NET Core 10 using stateless Streamable HTTP (`options.Stateless = true`), protected by `RequireAuthorization(McpAuthorizationPolicy)` and `.ExcludeFromDescription()` so REST OpenAPI (`/api/openapi/v1.json`) remains clean.
   - Keep MCP tool classes (`CookbookMcpTools`, `IngestionMcpTools`) as thin transport adapters delegating all EF Core queries, matching, normalization, and mutations to `IMcpCookbookService` (`Data/Services/McpCookbookService.cs`) and existing `Core` services:
     - `search_recipes`: Keyword, tag names/slugs/IDs, collection name/slug/ID, `maxTotalTimeMinutes`, `minRating`, `difficulty`, and `forgottenFavoritesOnly` (`>= 4` stars or `>= 2` `TimesCooked`, and `LastCookedAt` null or older than `60` days).
     - `get_recipe`: Full structured recipe lookup by `recipeId` (`Guid`) or `slug` (`string`), returning ingredients, sections, steps, inline step ingredient callouts, timers, sub-recipes, taxonomy badges, and cook history summary.
     - `match_recipes_by_ingredients`: Natural-language on-hand ingredient matcher resolving freeform ingredient names (via `NormalizedName`, `FoodItemAlias`, and singularization) to canonical `FoodItem`s and ranking recipes via `IRecipeService.MatchRecipesByIngredientsAsync`.
     - `list_collections_and_tags`: Household collections, categorized tags (`Course`, `Cuisine`, `Dietary`, `Equipment`, `Custom`), ordered `StoreAisle`s, and valid `MeasurementUnit` codes.
     - `enqueue_recipe_ingestion`: Enqueue a Web Recipe URL or Raw Text / Markdown block into the persistent async SQL ingestion queue (`IIngestionService`), attributed to the PAT owner.
     - `save_structured_recipe`: Directly create or update a complete structured recipe from an external AI conversation (`publishImmediately` defaulting to `false` $\rightarrow$ `DraftReview`, or `true` $\rightarrow$ `Published`), automatically normalizing units, detecting step timers, matching/creating canonical `FoodItem`s with aisle mapping, and linking collections/tags by name, slug, or ID.

4. **Account Settings "MCP & API Tokens" UI (`PersonalAccessTokensPanelComponent` + `AccountSettingsDialogComponent`):**
   - Create dedicated `PersonalAccessTokensPanelComponent` (`Client/src/app/features/mcp-tokens/personal-access-tokens-panel/`) with strictly separate `.ts`, `.html`, `.scss`, and `.spec.ts` files, embedded inside a new 3rd **"MCP Tokens"** tab in `AccountSettingsDialogComponent` (and reachable in 1 click via **"MCP & API Tokens"** in the `AppShellComponent` user menu).
   - Support creating named tokens with expiration presets (`Never`, `7 days`, `30 days`, `90 days`, `1 year`), displaying a one-time plaintext `lr_pat_...` reveal banner with 1-click Copy, rendering a ready-to-paste `mcpServers` Streamable HTTP JSON configuration block (`${origin}/api/mcp`) for Claude Desktop / Antigravity / Open WebUI, inspecting `Created` / `Expires` / `Last used` status, and revoking/deleting tokens cleanly across desktop and `360px` mobile viewports.

### Explicitly Out of Scope (Deferred to Milestones 3 & 4)
- Meal Planning Calendar MCP tools (`get_meal_plan`, `plan_meals`, `remove_meal_plan_entry`) and Shopping List MCP tools (`get_shopping_lists`, `generate_shopping_list_from_meal_plan`, `upsert_shopping_list_items`, `set_shopping_list_item_checked`) — delivered in **Milestone 3** alongside the Meal Plan & Shopping List domain slices.
- `CookLog` history entries (`CookLogs` table), Hands-Free Cook Mode, `FoodItem` duplicate merge, and 1-click in-app AI Kitchen Helpers — delivered in **Milestone 4**.

### 1.1 File Inventory (Modify & Create)

#### Existing Backend & Test Files to Modify
- `Data/Entities/User.cs` — Add `public ICollection<PersonalAccessToken> PersonalAccessTokens { get; set; } = new List<PersonalAccessToken>();`.
- `Data/AppDbContext.cs` — Add `public DbSet<PersonalAccessToken> PersonalAccessTokens => Set<PersonalAccessToken>();`.
- `Data/DataServiceCollectionExtensions.cs` — Register `IPersonalAccessTokenService` and `IMcpCookbookService`.
- `API/App.API.csproj` — Add `<PackageReference Include="ModelContextProtocol.AspNetCore" Version="2.3.0" />`.
- `API/Authentication/HttpContextUserContext.cs` — Fall back to `McpUserContextAccessor.CurrentUserId` when `HttpContext.User` is unavailable.
- `API/Authentication/DualBearerAuthenticationHandler.cs` — Immediately reject `lr_pat_` tokens on regular REST endpoints (`AuthenticateResult.Fail`).
- `API/Authentication/AuthenticationServiceCollectionExtensions.cs` — Register `McpPat` scheme (`PersonalAccessTokenAuthenticationHandler`) and `McpAuthorizationPolicy`.
- `API/Program.cs` — Register `builder.Services.AddAppMcpServer()` and map `app.MapMcp("/api/mcp").RequireAuthorization(AuthenticationServiceCollectionExtensions.McpAuthorizationPolicy).ExcludeFromDescription()`.
- `Tests/API.IntegrationTests/OpenApiIntegrationTests.cs` — Assert `/account/tokens` is documented in `/api/openapi/v1.json` and `/mcp` raw transport endpoints are excluded.
- `Tests/Client.E2ETests/App.Client.E2ETests.csproj` — Add `<PackageReference Include="ModelContextProtocol" Version="2.3.0" />` for real Streamable HTTP `McpClient` verification in Playwright E2E tests.

#### New Backend & Backend Test Files to Create
- `Core/Authentication/PersonalAccessTokenGenerator.cs`
- `Core/Authentication/PersonalAccessTokenModels.cs`
- `Core/Authentication/IPersonalAccessTokenService.cs`
- `Core/Mcp/McpModels.cs`
- `Core/Mcp/IMcpCookbookService.cs`
- `Data/Entities/PersonalAccessToken.cs`
- `Data/Configurations/PersonalAccessTokenConfiguration.cs`
- `Data/Services/PersonalAccessTokenService.cs`
- `Data/Services/McpCookbookService.cs`
- `API/Authentication/McpUserContextAccessor.cs`
- `API/Authentication/PersonalAccessTokenAuthenticationHandler.cs`
- `API/Controllers/PersonalAccessTokensController.cs`
- `API/Mcp/McpToolResultHelper.cs`
- `API/Mcp/CookbookMcpTools.cs`
- `API/Mcp/IngestionMcpTools.cs`
- `API/Mcp/McpServiceCollectionExtensions.cs`
- `Tests/Core.UnitTests/PersonalAccessTokenGeneratorTests.cs`
- `Tests/Core.UnitTests/PersonalAccessTokenServiceTests.cs`
- `Tests/Core.UnitTests/McpCookbookServiceTests.cs`
- `Tests/API.IntegrationTests/PersonalAccessTokensIntegrationTests.cs`
- `Tests/API.IntegrationTests/McpServerIntegrationTests.cs`

#### Existing Frontend Files to Modify (`Client/`)
- `Client/src/app/core/services/account/account.service.ts` (+ `.spec.ts`) — Accept optional `initialTab: 'profile' | 'password' | 'tokens' = 'profile'` in `openAccountSettingsDialog(initialTab?)`, pass `data: { initialTab }`, and widen dialog to `width: '640px', maxWidth: '95vw'`.
- `Client/src/app/features/account-settings-dialog/account-settings-dialog.component.ts` (+ `.html`, `.scss`, `.spec.ts`) — Read optional `MAT_DIALOG_DATA` `{ initialTab }`, bind `[selectedIndex]="selectedTabIndex()"`, update dialog subtitle, and render `<mat-tab label="MCP Tokens"><app-personal-access-tokens-panel /></mat-tab>`.
- `Client/src/app/features/shell/app-shell/app-shell.component.html` (+ `.spec.ts`) — Add **"MCP & API Tokens"** button (`<mat-icon>hub</mat-icon>`) in the user menu calling `accountService.openAccountSettingsDialog('tokens')`.

#### New Frontend & E2E Files to Create
- `Client/src/app/core/models/personal-access-token.models.ts`
- `Client/src/app/core/services/personal-access-token/personal-access-token.service.ts` (+ `.spec.ts`)
- `Client/src/app/features/mcp-tokens/personal-access-tokens-panel/personal-access-tokens-panel.component.ts` (+ `.html`, `.scss`, `.spec.ts`)
- `Tests/Client.E2ETests/McpAndPatE2ETests.cs`

---

## 2. Domain, Authentication & Database Design (`Core/`, `Data/`, `API/`)

### 2.1 Personal Access Token Generator & Hashing (`Core/Authentication/PersonalAccessTokenGenerator.cs`)

Create `Core/Authentication/PersonalAccessTokenGenerator.cs` (`namespace App.Core.Authentication`):
- Constants:
  - `public const string Prefix = "lr_pat_";`
  - `public const int SecretByteLength = 32;` (`256-bit` CSPRNG secret $\rightarrow$ `43` URL-safe Base64 chars; total token length `50` chars)
  - `public const int DisplayPrefixLength = 15;` (`"lr_pat_"` + first `8` random chars, e.g., `"lr_pat_k9Qp2XvA"`)
  - `public const int MinTokenLength = 32;`
  - `public const int MaxTokenLength = 128;`
- Methods:
  - `public static GeneratedPersonalAccessToken GenerateToken()` returning `readonly record struct GeneratedPersonalAccessToken(string PlaintextToken, string TokenPrefix, string TokenHash)`:
    - Fill `Span<byte> bytes = stackalloc byte[SecretByteLength]` via `RandomNumberGenerator.Fill(bytes)`.
    - Encode secret as URL-safe Base64 without padding (`+` $\rightarrow$ `-`, `/` $\rightarrow$ `_`, `.TrimEnd('=')`).
    - `PlaintextToken = $"{Prefix}{secret}"`.
    - `TokenPrefix = PlaintextToken[..DisplayPrefixLength]`.
    - `TokenHash = ComputeSha256Hex(PlaintextToken)`.
  - `public static string ComputeSha256Hex(string plaintextToken)`:
    - Computes `SHA256.HashData(Encoding.UTF8.GetBytes(plaintextToken.Trim()))` and returns `Convert.ToHexStringLower(hashBytes)` (`64` lowercase hex characters).
  - `public static bool IsWellFormedToken(string? token)`:
    - Returns `false` if null/whitespace; trims input; verifies `trimmed.StartsWith(Prefix, StringComparison.Ordinal)`, length between `MinTokenLength` and `MaxTokenLength`, and all characters after `Prefix` are ASCII `[A-Za-z0-9_-]`.
  - `public static bool FixedTimeHashEquals(string expectedHexHash, string actualHexHash)`:
    - Compares UTF-8 byte spans using `CryptographicOperations.FixedTimeEquals`.

### 2.2 Entities & EF Core Fluent Configurations (`Data/`)

1. **`Data/Entities/PersonalAccessToken.cs`** (`namespace App.Data.Entities`):
   ```csharp
   namespace App.Data.Entities;

   public sealed class PersonalAccessToken : IAuditable
   {
       public Guid Id { get; set; } = Guid.NewGuid();

       public Guid UserId { get; set; }

       public string Name { get; set; } = string.Empty;

       public string TokenPrefix { get; set; } = string.Empty;

       public string TokenHash { get; set; } = string.Empty;

       public DateTimeOffset? ExpiresAt { get; set; }

       public DateTimeOffset? LastUsedAt { get; set; }

       public DateTimeOffset? RevokedAt { get; set; }

       public DateTimeOffset CreatedAt { get; set; }

       public string CreatedBy { get; set; } = "System";

       public DateTimeOffset ModifiedAt { get; set; }

       public string ModifiedBy { get; set; } = "System";

       public User User { get; set; } = null!;
   }
   ```

2. **`Data/Configurations/PersonalAccessTokenConfiguration.cs`** (`namespace App.Data.Configurations`):
   - Table: `"PersonalAccessTokens"`, PK: `Id`.
   - `Name`: `IsRequired().HasMaxLength(100)`.
   - `TokenPrefix`: `IsRequired().HasMaxLength(24)`.
   - `TokenHash`: `IsRequired().HasMaxLength(64)`.
   - `CreatedBy` & `ModifiedBy`: `IsRequired().HasMaxLength(256)`.
   - Indexes:
     - `builder.HasIndex(t => t.TokenHash).IsUnique();`
     - `builder.HasIndex(t => new { t.UserId, t.CreatedAt });`
   - Relationship:
     - `builder.HasOne(t => t.User).WithMany(u => u.PersonalAccessTokens).HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);`
     - **SQL Server Note:** `PersonalAccessToken` has only a single foreign key (`UserId` $\rightarrow$ `Users.Id`) and no child tables referencing it, so `DeleteBehavior.Cascade` introduces zero multiple-cascade-path conflicts in SQL Server.

3. **EF Core Migration Name:** `AddPersonalAccessTokens`.

### 2.3 Personal Access Token Contracts & Service (`Core/Authentication/` & `Data/Services/`)

1. **`Core/Authentication/PersonalAccessTokenModels.cs`** (`namespace App.Core.Authentication`):
   ```csharp
   using System.ComponentModel.DataAnnotations;

   namespace App.Core.Authentication;

   public sealed record PersonalAccessTokenDto(
       Guid Id,
       string Name,
       string TokenPrefix,
       DateTimeOffset CreatedAt,
       DateTimeOffset? ExpiresAt,
       DateTimeOffset? LastUsedAt,
       DateTimeOffset? RevokedAt,
       bool IsActive
   );

   public sealed record CreatedPersonalAccessTokenDto(
       PersonalAccessTokenDto Token,
       string PlaintextToken
   );

   public sealed record CreatePersonalAccessTokenRequest(
       [Required, MinLength(2), MaxLength(100)] string Name,
       DateTimeOffset? ExpiresAt = null
   );

   public sealed record ValidatedPersonalAccessTokenUser(
       Guid TokenId,
       Guid UserId,
       string Email,
       string Username
   );
   ```

2. **`Core/Authentication/IPersonalAccessTokenService.cs`** (`namespace App.Core.Authentication`):
   ```csharp
   namespace App.Core.Authentication;

   public interface IPersonalAccessTokenService
   {
       Task<IReadOnlyList<PersonalAccessTokenDto>> GetTokensForUserAsync(
           Guid userId,
           CancellationToken cancellationToken = default
       );

       Task<CreatedPersonalAccessTokenDto> CreateTokenAsync(
           Guid userId,
           CreatePersonalAccessTokenRequest request,
           CancellationToken cancellationToken = default
       );

       Task<PersonalAccessTokenDto?> RevokeTokenAsync(
           Guid userId,
           Guid tokenId,
           CancellationToken cancellationToken = default
       );

       Task<bool> DeleteTokenAsync(
           Guid userId,
           Guid tokenId,
           CancellationToken cancellationToken = default
       );

       Task<ValidatedPersonalAccessTokenUser?> ValidateAndTouchTokenAsync(
           string plaintextToken,
           CancellationToken cancellationToken = default
       );
   }
   ```

3. **`Data/Services/PersonalAccessTokenService.cs`** (`namespace App.Data.Services`):
   - Primary constructor: `public sealed class PersonalAccessTokenService(AppDbContext dbContext, TimeProvider? timeProvider = null) : IPersonalAccessTokenService`
   - **Business Rules & Invariants:**
     - `GetTokensForUserAsync`: Uses `.AsNoTracking()`, filters `t.UserId == userId`, orders by `IsActive` (`RevokedAt == null && (!ExpiresAt.HasValue || ExpiresAt > now)`) descending then `CreatedAt` descending.
     - `CreateTokenAsync`:
       - Verifies `userId` exists in `Users` (`NotFoundException` if missing).
       - Trims `request.Name`; validates length `2..100` (`DomainValidationException` if invalid).
       - Validates `request.ExpiresAt` is strictly `> now` when provided (`DomainValidationException("Token expiration date must be in the future.")`).
       - Caps active unrevoked tokens per user at `25` (`DomainValidationException("Maximum of 25 active personal access tokens reached. Revoke unused tokens first.")`).
       - Generates unique `TokenHash` via `PersonalAccessTokenGenerator.GenerateToken()` (up to 5 collision-check attempts), persists `PersonalAccessToken`, and returns `CreatedPersonalAccessTokenDto(dto, generated.PlaintextToken)`.
     - `RevokeTokenAsync`:
       - Queries `PersonalAccessTokens.FirstOrDefaultAsync(t => t.Id == tokenId)`. Returns `null` if not found.
       - **Ownership Invariant:** If `token.UserId != userId`, throws `ForbiddenException("You do not have permission to modify another user's personal access token.")`.
       - Sets `RevokedAt = now` if currently `null`, saves, and returns `PersonalAccessTokenDto` (`IsActive = false`).
     - `DeleteTokenAsync`:
       - Queries by `tokenId`; returns `false` if not found; throws `ForbiddenException` if `token.UserId != userId`; removes entity and saves.
     - `ValidateAndTouchTokenAsync`:
       - Returns `null` immediately if `!PersonalAccessTokenGenerator.IsWellFormedToken(plaintextToken)`.
       - Computes `hash = PersonalAccessTokenGenerator.ComputeSha256Hex(plaintextToken)` and queries `PersonalAccessTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.TokenHash == hash)`.
       - Verifies `token is not null`, `PersonalAccessTokenGenerator.FixedTimeHashEquals(token.TokenHash, hash)`, `token.RevokedAt is null`, `!token.ExpiresAt.HasValue || token.ExpiresAt.Value > now`, and `token.User is not null` (returning `null` on any failure).
       - Updates `token.LastUsedAt = now` whenever `token.LastUsedAt is null || (now - token.LastUsedAt.Value) >= TimeSpan.FromSeconds(60)` and saves changes (preserving `token.ModifiedBy = token.UserId.ToString()` via `McpUserContextAccessor` scope).
       - Returns `new ValidatedPersonalAccessTokenUser(token.Id, token.User.Id, token.User.Email, token.User.Username)`.

### 2.4 MCP Domain Contracts & Service (`Core/Mcp/` & `Data/Services/McpCookbookService.cs`)

1. **`Core/Mcp/McpModels.cs`** (`namespace App.Core.Mcp`):
   - `McpRecipeSearchRequest`:
     ```csharp
     public sealed record McpRecipeSearchRequest(
         string? Query = null,
         string? Tag = null,
         string? Collection = null,
         int? MaxTotalTimeMinutes = null,
         decimal? MinRating = null,
         RecipeDifficulty? Difficulty = null,
         bool ForgottenFavoritesOnly = false,
         int ForgottenDaysThreshold = 60,
         bool IncludeDrafts = false,
         int Limit = 25
     );
     ```
   - `McpCookHistoryNoteDto(DateTimeOffset CookedAt, string CookedBy, int? Rating, string? Notes)` (empty `[]` until Milestone 4 `CookLog` is added, keeping MCP payload schema forward-compatible).
   - `McpStepIngredientCalloutDto(Guid IngredientId, string DisplayName, decimal? Quantity, decimal? MaxQuantity, MeasurementUnit? Unit, string? CustomUnitName, string? PreparationNote)`.
   - `McpRecipeStepDetailDto(int StepNumber, string? SectionName, string Instruction, IReadOnlyList<McpStepIngredientCalloutDto> StepIngredients, IReadOnlyList<RecipeStepTimerDto> Timers)`.
   - `McpRecipeDetailDto`: Wraps full `RecipeDetailDto` metadata, `Ingredients`, `Steps` (`IReadOnlyList<McpRecipeStepDetailDto>`), `LinkedSubRecipes`, `Collections`, `Tags`, `TimesCooked`, `LastCookedAt`, `AverageRating`, and `IReadOnlyList<McpCookHistoryNoteDto> CookHistoryNotes`.
   - `McpResolvedFoodItemDto(string InputName, Guid FoodItemId, string CanonicalName, bool IsPantryStaple)`.
   - `McpIngredientMatchResponseDto(IReadOnlyList<McpResolvedFoodItemDto> ResolvedIngredients, IReadOnlyList<string> UnresolvedIngredientNames, IReadOnlyList<IngredientMatchResultDto> Matches)`.
   - `McpHouseholdTaxonomyDto(IReadOnlyList<CollectionDto> Collections, IReadOnlyList<TagDto> Tags, IReadOnlyList<StoreAisleDto> StoreAisles, IReadOnlyList<string> SupportedUnits)`.
   - `McpEnqueueIngestionRequest(string? Url = null, string? RawText = null, string? TitleHint = null)`.
   - `McpStructuredIngredientInput`:
     ```csharp
     public sealed record McpStructuredIngredientInput(
         string DisplayName,
         string? CanonicalFoodName = null,
         string? SectionName = null,
         decimal? Quantity = null,
         decimal? MaxQuantity = null,
         string? Unit = null,
         string? PreparationNote = null,
         bool IsOptional = false,
         string? OriginalRawText = null,
         string? SuggestedAisleName = null,
         bool IsPantryStaple = false
     );
     ```
   - `McpStructuredStepInput`:
     ```csharp
     public sealed record McpStructuredStepInput(
         string Instruction,
         string? SectionName = null,
         IReadOnlyList<int>? IngredientIndices = null,
         IReadOnlyList<RecipeStepTimerInput>? Timers = null
     );
     ```
   - `McpSaveStructuredRecipeRequest`:
     ```csharp
     public sealed record McpSaveStructuredRecipeRequest(
         string Title,
         Guid? RecipeId = null,
         string? Description = null,
         bool PublishImmediately = false,
         decimal BaseServings = 4m,
         string? YieldDisplay = null,
         int? PrepTimeMinutes = null,
         int? CookTimeMinutes = null,
         int? TotalTimeMinutes = null,
         string? Difficulty = null,
         string? SourceUrl = null,
         string? SourceName = null,
         string? SourceAuthor = null,
         string? SourceBookTitle = null,
         int? SourceBookPage = null,
         IReadOnlyList<McpStructuredIngredientInput>? Ingredients = null,
         IReadOnlyList<McpStructuredStepInput>? Steps = null,
         IReadOnlyList<string>? Collections = null,
         IReadOnlyList<string>? Tags = null
     );
     ```

2. **`Core/Mcp/IMcpCookbookService.cs`** (`namespace App.Core.Mcp`):
   ```csharp
   using App.Core.Ingestion;
   using App.Core.Recipes;

   namespace App.Core.Mcp;

   public interface IMcpCookbookService
   {
       Task<IReadOnlyList<RecipeSummaryDto>> SearchRecipesAsync(
           McpRecipeSearchRequest request,
           CancellationToken cancellationToken = default
       );

       Task<McpRecipeDetailDto?> GetRecipeAsync(
           Guid? recipeId = null,
           string? slug = null,
           CancellationToken cancellationToken = default
       );

       Task<McpIngredientMatchResponseDto> MatchRecipesByIngredientNamesAsync(
           IReadOnlyList<string> ingredientNames,
           int limit = 15,
           CancellationToken cancellationToken = default
       );

       Task<McpHouseholdTaxonomyDto> GetHouseholdTaxonomyAsync(
           CancellationToken cancellationToken = default
       );

       Task<IngestionJobSummaryDto> EnqueueIngestionAsync(
           McpEnqueueIngestionRequest request,
           CancellationToken cancellationToken = default
       );

       Task<RecipeDetailDto> SaveStructuredRecipeAsync(
           McpSaveStructuredRecipeRequest request,
           CancellationToken cancellationToken = default
       );
   }
   ```

3. **`Data/Services/McpCookbookService.cs`** (`namespace App.Data.Services`):
   - Primary constructor injecting `AppDbContext dbContext`, `IRecipeService recipeService`, `IIngestionService ingestionService`, `ITaxonomyService taxonomyService`, `IFoodCatalogService foodCatalogService`, and `TimeProvider? timeProvider = null`.
   - **Method Specifications:**
     - `SearchRecipesAsync`:
       - Clamps `Limit` to `1..100` (default `25`).
       - Filters `Recipes.AsNoTracking()` excluding `Archived` (and restricting to `RecipeStatus.Published` when `!request.IncludeDrafts`).
       - Resolves `request.Tag` (if non-empty) by `Guid.TryParse` OR case-insensitive match against `Tag.Slug` or `Tag.NormalizedName` (if no tag matches the requested filter, returns `[]`).
       - Resolves `request.Collection` (if non-empty) by `Guid.TryParse` OR case-insensitive match against `Collection.Slug` or `Collection.NormalizedName` (if no collection matches, returns `[]`).
       - Applies `MaxTotalTimeMinutes`, `Difficulty`, `MinRating` (`r.AverageRating != null && r.AverageRating >= request.MinRating.Value`), and `ForgottenFavoritesOnly`:
         - Cutoff `cutoff = now.AddDays(-Math.Clamp(request.ForgottenDaysThreshold, 1, 3650))`.
         - Matches `( (r.AverageRating != null && r.AverageRating >= 4.0m) || r.TimesCooked >= 2 ) && (r.LastCookedAt == null || r.LastCookedAt <= cutoff)`.
       - Applies keyword `Query` filter matching `Title`, `Description`, `SourceName`, `SourceAuthor`, `SourceBookTitle`, `Collection.NormalizedName`, `Tag.NormalizedName`, `Ingredients.DisplayName`, `FoodItem.NormalizedName`, and `FoodItem.Aliases`.
       - Orders by `ModifiedAt` descending, takes `Limit`, and projects `RecipeSummaryDto`.
     - `GetRecipeAsync`:
       - Requires either `recipeId.HasValue` or non-whitespace `slug` (`DomainValidationException("Provide either recipeId or slug.")` if both are empty).
       - If only `slug` is provided, resolves `id` via `Recipes.AsNoTracking().Where(r => r.Slug == slug.Trim().ToLowerInvariant()).Select(r => (Guid?)r.Id).FirstOrDefaultAsync()`.
       - Calls `recipeService.GetRecipeByIdAsync(resolvedId)` and maps each step's `IngredientIds` into inline `McpStepIngredientCalloutDto` items so AI assistants immediately see step-level ingredient amounts without cross-indexing GUIDs.
     - `MatchRecipesByIngredientNamesAsync`:
       - Validates `ingredientNames` has at least `1` non-empty item (`DomainValidationException` otherwise).
       - Loads `FoodItems.AsNoTracking().Include(f => f.Aliases)` once, resolves each input name by exact `NormalizedName` / `NormalizedAlias`, then singularized form (`ies` $\rightarrow$ `y`, `es`/`s` stripping), then unambiguous whole-word/contains match.
       - If `resolvedFoodIds.Count > 0`, calls `recipeService.MatchRecipesByIngredientsAsync(new IngredientMatchRequest(resolvedFoodIds))` and takes `Math.Clamp(limit, 1, 50)`.
       - Returns `McpIngredientMatchResponseDto(resolved, unresolved, matches)`.
     - `GetHouseholdTaxonomyAsync`:
       - Aggregates `taxonomyService.GetCollectionsAsync()`, `taxonomyService.GetTagsAsync()`, `foodCatalogService.GetStoreAislesAsync()`, and all snake_case/standard unit codes (`tsp`, `tbsp`, `fl_oz`, `cup`, `pt`, `qt`, `gal`, `ml`, `l`, `g`, `kg`, `oz`, `lb`, `pinch`, `dash`, `clove`, `can`, `bunch`, `slice`, `whole`, `custom`).
     - `EnqueueIngestionAsync`:
       - Validates that **exactly one** of `request.Url` or `request.RawText` is non-whitespace (`DomainValidationException("Provide either 'url' or 'rawText' (not both).")`).
       - Delegates to `ingestionService.EnqueueWebUrlsAsync(new EnqueueWebUrlIngestionRequest([request.Url.Trim()]))` (returning job `[0]`) or `ingestionService.EnqueueRawTextAsync(new EnqueueRawTextIngestionRequest(request.RawText, request.TitleHint))`.
     - `SaveStructuredRecipeAsync`:
       - Validates `Title` is non-whitespace (`DomainValidationException` if empty).
       - Parses optional `Difficulty` (`Easy`, `Medium`, `Project`, case-insensitive; throws `DomainValidationException` if unrecognized non-empty value).
       - Normalizes each `McpStructuredIngredientInput`:
         - Normalizes unit via `RecipeDraftNormalizer.NormalizeUnit(ing.Unit)`.
         - Matches existing `FoodItem` by `CanonicalFoodName ?? DisplayName` (exact `NormalizedName`, `NormalizedAlias`, or singularized form) OR creates a new `FoodItem` (TitleCase name, resolving `SuggestedAisleName` against `StoreAisles` with `"Other"` fallback, setting `IsPantryStaple = ing.IsPantryStaple`).
       - Maps `McpStructuredStepInput` rows: auto-detects timers via `StepTimerExtractor.DetectTimers(step.Instruction, index + 1)` whenever `step.Timers` is null/empty, and filters valid `0 <= idx < ingredientCount` `IngredientIndices`.
       - Resolves `Collections` (by `Guid`, `Slug`, or `NormalizedName`) and `Tags` (by `Guid`, `Slug`, or `NormalizedName`; if an unmatched tag name is non-empty and `<= 80` chars, creates a `TagCategory.Custom` tag via `taxonomyService.CreateTagAsync` so AI-authored tags are preserved cleanly).
       - Sets `Status = request.PublishImmediately ? RecipeStatus.Published : RecipeStatus.DraftReview`.
       - If `request.RecipeId.HasValue`: calls `recipeService.UpdateRecipeAsync(request.RecipeId.Value, upsertRequest)` (throwing `NotFoundException("Recipe not found.")` if `null`, and preserving existing `CoverImagePath`/`ThumbnailImagePath` from current recipe so MCP edits never wipe existing photos!).
       - Otherwise calls `recipeService.CreateRecipeAsync(upsertRequest)`.

### 2.5 Strict Authentication Schemes & Context Attribution (`API/Authentication/`)

1. **`API/Authentication/McpUserContextAccessor.cs`** (`namespace App.API.Authentication`):
   - Provides `AsyncLocal<string?>` ambient user ID scope (`public static IDisposable BeginScope(Guid userId)`) so `HttpContextUserContext` and `AuditSaveChangesInterceptor` always resolve the PAT owner's `UserId` both during `ValidateAndTouchTokenAsync` and inside any async MCP tool invocation.
2. **`API/Authentication/HttpContextUserContext.cs`**:
   - Checks `httpContextAccessor.HttpContext?.User` (`ClaimTypes.NameIdentifier` / `"sub"`), then falls back to `McpUserContextAccessor.CurrentUserId` before throwing `InvalidOperationException`.
3. **`API/Authentication/DualBearerAuthenticationHandler.cs`**:
   - Immediately after extracting `token`, if `token.StartsWith(PersonalAccessTokenGenerator.Prefix, StringComparison.OrdinalIgnoreCase)`, return `AuthenticateResult.Fail("Personal access tokens are only valid on the /api/mcp endpoint.")`.
4. **`API/Authentication/PersonalAccessTokenAuthenticationHandler.cs`**:
   - Scheme name constant in `AuthenticationServiceCollectionExtensions`:
     - `public const string McpPatScheme = "McpPat";`
     - `public const string McpAuthorizationPolicy = "McpPersonalAccessTokenPolicy";`
   - Reads `Authorization: Bearer <token>` header.
   - If missing, returns `AuthenticateResult.NoResult()`.
   - If `!token.StartsWith(PersonalAccessTokenGenerator.Prefix, StringComparison.Ordinal)`, returns `AuthenticateResult.Fail("Personal access token (lr_pat_...) required for /api/mcp.")`.
   - Calls `personalAccessTokenService.ValidateAndTouchTokenAsync(token, Context.RequestAborted)`.
   - On match, builds `ClaimsPrincipal` with `ClaimTypes.NameIdentifier`, `"sub"`, `ClaimTypes.Email`, `ClaimTypes.Name`, and `"token_type" = "pat"`, returning `AuthenticateResult.Success(ticket)`.
   - `HandleChallengeAsync`: sets `401 Unauthorized` and `WWW-Authenticate: Bearer realm="LocalRoast MCP", error="invalid_token"`.

### 2.6 Embedded MCP Server & Tool Adapters (`API/Mcp/` & `API/Program.cs`)

1. **`API/Mcp/McpToolResultHelper.cs`** (`namespace App.API.Mcp`):
   - Serializes tool results with camelCase `JsonSerializerOptions` (`JsonStringEnumConverter`, `DefaultIgnoreCondition = WhenWritingNull`, `WriteIndented = false`).
   - `ExecuteAsync(ClaimsPrincipal? user, Func<Task<object?>> action)`:
     - Extracts `Guid` user ID from `user` (`ClaimTypes.NameIdentifier` / `"sub"`) if present and wraps `action()` in `McpUserContextAccessor.BeginScope(userId)`.
     - Returns `new CallToolResult { IsError = false, Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload, JsonOptions) }] }`.
     - Catches `DomainValidationException`, `NotFoundException`, `ConflictException`, and `ForbiddenException` and returns `new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = ex.Message }] }` so MCP LLM clients receive actionable self-correcting error text instead of opaque protocol errors.

2. **`API/Mcp/CookbookMcpTools.cs`** (`[McpServerToolType]`):
   - `[McpServerTool(Name = "search_recipes", ReadOnly = true)]` — Parameters: `query`, `tag`, `collection`, `maxTotalTimeMinutes`, `minRating`, `difficulty`, `forgottenFavoritesOnly`, `includeDrafts`, `limit`, plus injected `IMcpCookbookService`, `ClaimsPrincipal? user`, `CancellationToken`.
   - `[McpServerTool(Name = "get_recipe", ReadOnly = true)]` — Parameters: `recipeId` (`Guid?`), `slug` (`string?`), plus injected `IMcpCookbookService`, `ClaimsPrincipal? user`, `CancellationToken`. Returns `McpToolResultHelper.Error("Recipe not found.")` if `null`.
   - `[McpServerTool(Name = "match_recipes_by_ingredients", ReadOnly = true)]` — Parameters: `ingredients` (`string[]`), `limit` (`int = 15`), plus injected `IMcpCookbookService`, `ClaimsPrincipal? user`, `CancellationToken`.
   - `[McpServerTool(Name = "list_collections_and_tags", ReadOnly = true)]` — No required input parameters; returns `McpHouseholdTaxonomyDto`.

3. **`API/Mcp/IngestionMcpTools.cs`** (`[McpServerToolType]`):
   - `[McpServerTool(Name = "enqueue_recipe_ingestion")]` — Parameters: `url` (`string? = null`), `rawText` (`string? = null`), `titleHint` (`string? = null`), plus injected `IMcpCookbookService`, `ClaimsPrincipal? user`, `CancellationToken`.
   - `[McpServerTool(Name = "save_structured_recipe")]` — Parameters matching `McpSaveStructuredRecipeRequest` (`title`, `recipeId`, `description`, `publishImmediately = false`, `baseServings = 4m`, `yieldDisplay`, `prepTimeMinutes`, `cookTimeMinutes`, `totalTimeMinutes`, `difficulty`, `sourceUrl`, `sourceName`, `sourceAuthor`, `sourceBookTitle`, `sourceBookPage`, `ingredients`, `steps`, `collections`, `tags`), plus injected `IMcpCookbookService`, `ClaimsPrincipal? user`, `CancellationToken`.

4. **`API/Mcp/McpServiceCollectionExtensions.cs`**:
   ```csharp
   using ModelContextProtocol.Protocol;

   namespace App.API.Mcp;

   public static class McpServiceCollectionExtensions
   {
       public static IServiceCollection AddAppMcpServer(this IServiceCollection services)
       {
           services
               .AddMcpServer(options =>
               {
                   options.ServerInfo = new Implementation
                   {
                       Name = "LocalRoast Household MCP Server",
                       Version = "1.0.0",
                   };
               })
               .WithHttpTransport(options =>
               {
                   options.Stateless = true;
               })
               .WithTools<CookbookMcpTools>()
               .WithTools<IngestionMcpTools>();

           return services;
       }
   }
   ```

5. **`API/Program.cs`**:
   - Register `builder.Services.AddAppMcpServer();` right after `builder.Services.AddAppAuthentication(builder.Configuration);`.
   - Map MCP endpoint right after `app.MapGroup("/api").MapControllers();` and **before** `app.MapFallbackToFile("index.html");`:
     ```csharp
     app.MapMcp("/api/mcp")
         .RequireAuthorization(AuthenticationServiceCollectionExtensions.McpAuthorizationPolicy)
         .ExcludeFromDescription();
     ```

---

## 3. API Endpoints (`API/Controllers/PersonalAccessTokensController.cs` & `/api/mcp`)

### 3.1 REST Endpoints (`[Route("account/tokens")]`, `[Authorize]`)

| Verb | Route (`/api/...`) | Request Body / Params | Response (`200`/`201`/`204`) | Error Cases (`400`/`401`/`403`/`404`) |
| :--- | :--- | :--- | :--- | :--- |
| `GET` | `/account/tokens` | — | `200 OK` (`IReadOnlyList<PersonalAccessTokenDto>`) | `401` unauthenticated or PAT used on REST |
| `POST` | `/account/tokens` | `CreatePersonalAccessTokenRequest` (`Name`, `ExpiresAt?`) | `201 Created` (`CreatedPersonalAccessTokenDto`) | `400` invalid name / past expiry / `>25` active cap; `401`; `404` user missing |
| `POST` | `/account/tokens/{id:guid}/revoke` | `id` route param | `200 OK` (`PersonalAccessTokenDto`) | `401`; `403` token owned by another user; `404` token not found |
| `DELETE` | `/account/tokens/{id:guid}` | `id` route param | `204 No Content` | `401`; `403` token owned by another user; `404` token not found |

### 3.2 Embedded MCP Streamable HTTP Endpoint (`/api/mcp`, `McpPat` Scheme)

| Transport | Route | Auth Header | Exposed MCP Tools (Milestone 2) | Error / Security Behavior |
| :--- | :--- | :--- | :--- | :--- |
| Streamable HTTP (`POST`) | `/api/mcp` | `Authorization: Bearer lr_pat_<secret>` | `search_recipes`, `get_recipe`, `match_recipes_by_ingredients`, `list_collections_and_tags`, `enqueue_recipe_ingestion`, `save_structured_recipe` | `401 Unauthorized` when missing header, browser JWT, malformed `lr_pat_`, revoked token, or expired token; domain errors return MCP `CallToolResult` (`isError: true`) |

---

## 4. Angular Frontend Design (`Client/`)

### 4.1 Models & Services (`src/app/core/`)

1. **`Client/src/app/core/models/personal-access-token.models.ts`**:
   ```typescript
   export interface PersonalAccessTokenDto {
     id: string;
     name: string;
     tokenPrefix: string;
     createdAt: string;
     expiresAt: string | null;
     lastUsedAt: string | null;
     revokedAt: string | null;
     isActive: boolean;
   }

   export interface CreatedPersonalAccessTokenDto {
     token: PersonalAccessTokenDto;
     plaintextToken: string;
   }

   export interface CreatePersonalAccessTokenRequest {
     name: string;
     expiresAt?: string | null;
   }
   ```

2. **`Client/src/app/core/services/personal-access-token/personal-access-token.service.ts`** (+ `.spec.ts`):
   - Decorated with `@Service()` from `@angular/core`, using `inject(HttpClient)` and `apiUrl('/account/tokens')`:
     - `getTokens(): Observable<PersonalAccessTokenDto[]>`
     - `createToken(request: CreatePersonalAccessTokenRequest): Observable<CreatedPersonalAccessTokenDto>`
     - `revokeToken(id: string): Observable<PersonalAccessTokenDto>`
     - `deleteToken(id: string): Observable<void>`

3. **`Client/src/app/core/services/account/account.service.ts`** (+ `.spec.ts`):
   - Update `openAccountSettingsDialog(initialTab: 'profile' | 'password' | 'tokens' = 'profile')` to pass `data: { initialTab }` and `width: '640px', maxWidth: '95vw'`.

### 4.2 Feature Components (`src/app/features/`)

1. **`Client/src/app/features/mcp-tokens/personal-access-tokens-panel/`** (strictly separate `personal-access-tokens-panel.component.ts`, `.html`, `.scss`, `.spec.ts`):
   - Selector: `app-personal-access-tokens-panel`.
   - Signals & Computed State:
     - `tokens = signal<PersonalAccessTokenDto[]>([])`
     - `isLoading = signal<boolean>(true)`
     - `loadError = signal<string | null>(null)`
     - `isCreating = signal<boolean>(false)`
     - `createError = signal<string | null>(null)`
     - `newlyCreatedToken = signal<CreatedPersonalAccessTokenDto | null>(null)`
     - `busyTokenId = signal<string | null>(null)`
     - `mcpEndpointUrl = computed(() => `${window.location.origin}/api/mcp`)`
     - `mcpConfigSnippet = computed(() => ...)` formatting JSON for `"mcpServers": { "localroast": { "url": mcpEndpointUrl(), "headers": { "Authorization": "Bearer " + (newlyCreatedToken()?.plaintextToken ?? "<YOUR_LR_PAT_TOKEN>") } } }`
   - Reactive Form (`FormBuilder.nonNullable`):
     - `name`: `['', [Validators.required, Validators.minLength(2), Validators.maxLength(100)]]`
     - `expirationDays`: `[0]` (`0` = Never expires, `7` = 7 days, `30` = 30 days, `90` = 90 days, `365` = 1 year — converts positive days to ISO UTC timestamp `new Date(Date.now() + days * 86_400_000).toISOString()` on submit).
   - Interactive Capabilities:
     - **One-Time Secret Banner:** Displayed immediately after `createToken()` succeeds with `<code class="pat-secret-value">`, 1-click **Copy Token** button (`aria-label="Copy personal access token"`), and **Dismiss** button.
     - **Collapsible / Visible MCP Client Setup Card:** Shows `/api/mcp` endpoint URL + formatted `mcpServers` JSON snippet with 1-click **Copy MCP Config** button (`aria-label="Copy MCP JSON configuration"`) for Claude Desktop, Antigravity, and Open WebUI.
     - **Token Inventory List:** Displays each token's `name`, `tokenPrefix` (`lr_pat_xxxx...`), status badge (`Active` using `--app-success*`, `Revoked` / `Expired` using `--mat-sys-surface-container-highest` / `--mat-sys-error-container`), `Created`, `Expires` (`Never` if null), and `Last used` (`Never used` if null, else formatted date/time).
     - **Actions:** `Revoke` button on active tokens (`revokeToken(id)`) and `Delete` icon/button on revoked/expired (or any) token (`deleteToken(id)`).
   - **Responsive `360px` Mobile & Accessibility Requirements:**
     - Create form fields (`Token Name` + `Expiration` + `Generate Token`) arrange in a 2-column row on desktop and stack cleanly into a single full-width column below `560px` (`360px` mobile).
     - Monospace token strings and `<pre>` JSON blocks use `overflow-x: auto; max-width: 100%; word-break: break-all` where appropriate so the dialog never overflows horizontally at `360x640`.
     - All icon buttons include explicit `aria-label` attributes and all `<mat-form-field>` controls include `<mat-label>`.

2. **`Client/src/app/features/account-settings-dialog/`** (`.ts`, `.html`, `.scss`, `.spec.ts`):
   - Inject `MAT_DIALOG_DATA` (`{ optional: true }`) supporting `{ initialTab?: 'profile' | 'password' | 'tokens' }`.
   - Add `selectedTabIndex = signal<number>(...)` (`0` = Profile, `1` = Password, `2` = MCP Tokens) bound to `<mat-tab-group [selectedIndex]="selectedTabIndex()" (selectedIndexChange)="selectedTabIndex.set($event)">`.
   - Import `PersonalAccessTokensPanelComponent` and add 3rd tab `<mat-tab label="MCP Tokens"><app-personal-access-tokens-panel /></mat-tab>`.
   - Update dialog subtitle to `"Manage your profile, password, and MCP personal access tokens."`.

3. **`Client/src/app/features/shell/app-shell/app-shell.component.html`** (+ `.spec.ts`):
   - Add menu button in `#userMenu` right above `Account Settings`:
     ```html
     <button mat-menu-item (click)="accountService.openAccountSettingsDialog('tokens')">
       <mat-icon>hub</mat-icon>
       <span>MCP &amp; API Tokens</span>
     </button>
     ```

---

## 5. Playwright E2E Test Scenarios (`Tests/Client.E2ETests/McpAndPatE2ETests.cs`)

Create `Tests/Client.E2ETests/McpAndPatE2ETests.cs` inheriting from `E2ETestBase`:

- `[TestMethod] PersonalAccessToken_MintInUI_ExecuteMcpTools_VerifyCookbookAttributionAndLastUsed()`:
  1. Registers and logs in a new household chef (`RegisterAndLoginAsync`).
  2. Opens the user profile menu, clicks **"MCP & API Tokens"**, verifies `AccountSettingsDialogComponent` opens directly on the **MCP Tokens** tab and displays the `${ServerUrl}/api/mcp` configuration snippet.
  3. Fills `Token Name = "Antigravity Kitchen Assistant"`, selects `30 days` expiration, clicks **Generate Token**, and extracts the one-time `lr_pat_...` plaintext token from `.pat-secret-value`.
  4. Connects a real `ModelContextProtocol.Client.McpClient` (`HttpClientTransport` with `HttpTransportMode.StreamableHttp` and `Authorization: Bearer <lr_pat_...>`) to `${ServerUrl}/api/mcp`:
     - Calls `ListToolsAsync()` and asserts all 6 tools (`search_recipes`, `get_recipe`, `match_recipes_by_ingredients`, `list_collections_and_tags`, `enqueue_recipe_ingestion`, `save_structured_recipe`) are advertised.
     - Calls `save_structured_recipe` (`publishImmediately: true`, `title: "MCP Charred Scallion Ramen"`, structured ingredients with quantities/units, steps with timer text `"simmer broth for 20 minutes"`, and tag `"Weeknight"`).
     - Calls `search_recipes` (`query: "Scallion Ramen"`) and `match_recipes_by_ingredients` (`ingredients: ["scallion"]`) and verifies the newly saved recipe is returned.
     - Calls `enqueue_recipe_ingestion` (`rawText: "Quick Garlic Butter Pasta\nIngredients:\n- 200g spaghetti\n- 4 cloves garlic\nSteps:\n1. Boil pasta for 9 minutes."`, `titleHint: "MCP Queued Pasta"`).
  5. Closes and re-opens **MCP & API Tokens** in the browser UI and verifies the token row now displays a populated **Last used** timestamp instead of `"Never used"`.
  6. Navigates to `/recipes` in the browser, verifies `"MCP Charred Scallion Ramen"` renders in the Cookbook, opens its detail page, and verifies structured ingredients and the auto-detected `20 min` step timer are present.

- `[TestMethod] PersonalAccessToken_RevokeInUI_ImmediatelyBlocksMcpAccessAndRestEndpointsRejectPat()`:
  1. Registers/logs in, mints a token `"Temporary Voice Agent"` via the **MCP Tokens** tab, and verifies an initial `McpClient` connection to `/api/mcp` succeeds.
  2. Verifies sending `Authorization: Bearer <lr_pat_...>` to `GET /api/account` returns `401 Unauthorized` (strict MCP boundary).
  3. Clicks **Revoke** on `"Temporary Voice Agent"` in the **MCP Tokens** tab, verifies the UI badge transitions to `Revoked`, and verifies a subsequent HTTP/MCP call to `/api/mcp` with that token immediately fails with `401 Unauthorized`.
  4. Clicks **Delete** on the revoked token and verifies it is removed from the list.

- `[TestMethod] PersonalAccessToken_MobileViewport_360px_DialogTabsAndSnippetRenderCleanly()`:
  1. Sets viewport to `360x640` (`Page.SetViewportSizeAsync(360, 640)`).
  2. Logs in, opens the user menu, taps **MCP & API Tokens**, verifies the dialog fits within the `360px` viewport without horizontal page overflow (`document.documentElement.scrollWidth <= 360`), generates a token `"Mobile Test PAT"`, verifies the one-time reveal box and **Copy MCP Config** button are reachable and clickable, and revokes the token cleanly.

---

## 6. Step-by-Step Execution Checklist (For Implementation Agent)

- [ ] **Task 1: Backend Core, Data Layer & EF Migration**
  - Create `Core/Authentication/PersonalAccessTokenGenerator.cs`, `PersonalAccessTokenModels.cs`, `IPersonalAccessTokenService.cs`, `Core/Mcp/McpModels.cs`, and `IMcpCookbookService.cs`.
  - Create `Data/Entities/PersonalAccessToken.cs` and `Data/Configurations/PersonalAccessTokenConfiguration.cs`; update `Data/Entities/User.cs`, `Data/AppDbContext.cs`, and `Data/DataServiceCollectionExtensions.cs`.
  - Implement `Data/Services/PersonalAccessTokenService.cs` and `Data/Services/McpCookbookService.cs`.
  - Ensure local SQL Server is running, generate, apply, and format EF migration:
    ```powershell
    .\scripts\ensure-local-infra.ps1
    dotnet ef migrations add AddPersonalAccessTokens --project Data/App.Data.csproj
    dotnet ef database update --project Data/App.Data.csproj
    dotnet csharpier format Data/Migrations
    ```
  - Create and run unit tests (`Tests/Core.UnitTests/PersonalAccessTokenGeneratorTests.cs`, `PersonalAccessTokenServiceTests.cs`, and `McpCookbookServiceTests.cs`):
    ```powershell
    dotnet test --project Tests/Core.UnitTests/App.Core.UnitTests.csproj
    dotnet test --project Tests/Infrastructure.UnitTests/App.Infrastructure.UnitTests.csproj
    ```

- [ ] **Task 2: Authentication Handlers, PAT Controller, Embedded `/api/mcp` Server & Integration Tests**
  - Add `ModelContextProtocol.AspNetCore` (`2.3.0`) to `API/App.API.csproj`.
  - Create `API/Authentication/McpUserContextAccessor.cs` and `PersonalAccessTokenAuthenticationHandler.cs`; update `HttpContextUserContext.cs`, `DualBearerAuthenticationHandler.cs`, and `AuthenticationServiceCollectionExtensions.cs`.
  - Create thin controller `API/Controllers/PersonalAccessTokensController.cs`.
  - Create `API/Mcp/McpToolResultHelper.cs`, `CookbookMcpTools.cs`, `IngestionMcpTools.cs`, and `McpServiceCollectionExtensions.cs`; wire `AddAppMcpServer()` and `app.MapMcp("/api/mcp")` in `API/Program.cs`.
  - Update `Tests/API.IntegrationTests/OpenApiIntegrationTests.cs` and create `PersonalAccessTokensIntegrationTests.cs` (CRUD, validation, `403` cross-user ownership boundary, PAT rejected on `/api/account` & `/api/recipes`) and `McpServerIntegrationTests.cs` (anonymous/JWT/expired/revoked `401` on `/api/mcp`, valid PAT `McpClient` Streamable HTTP round-trips for all 6 tools, `LastUsedAt` update, and `CreatedBy` attribution to PAT owner).
  - Run API integration tests until 100% green:
    ```powershell
    dotnet test --project Tests/API.IntegrationTests/App.API.IntegrationTests.csproj
    ```

- [ ] **Task 3: Angular Client & Unit Tests**
  - Create `Client/src/app/core/models/personal-access-token.models.ts` and `Client/src/app/core/services/personal-access-token/personal-access-token.service.ts` (+ `.spec.ts`).
  - Create dedicated `PersonalAccessTokensPanelComponent` in `Client/src/app/features/mcp-tokens/personal-access-tokens-panel/` with separate `.ts`, `.html`, `.scss`, and `.spec.ts` files.
  - Update `AccountService`, `AccountSettingsDialogComponent`, and `AppShellComponent` (+ `.spec.ts` unit tests).
  - Run frontend lint, production build, and unit tests until 100% green:
    ```powershell
    # In Client/:
    npm run lint
    npm run build:production
    npm test -- --watch=false
    ```

- [ ] **Task 4: Playwright E2E Verification & Final Formatting**
  - Add `ModelContextProtocol` (`2.3.0`) to `Tests/Client.E2ETests/App.Client.E2ETests.csproj` and implement `Tests/Client.E2ETests/McpAndPatE2ETests.cs` covering all 3 scenarios from Section 5.
  - Rebuild Angular production bundle (`npm run build:production` in `Client/`) and run Playwright E2E tests:
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
  - Run full solution build and test suite (`dotnet build App.slnx` and `dotnet test App.slnx`).
  - Mark this plan's status as `[x] Completed` and commit onto `feature/phase-06-pat-and-mcp-server`.
