# Phase 04: Local LLM Provider Settings, Async Ingestion Queue & Side-by-Side Review Inbox

> **Branch:** `feature/phase-04-llm-ingestion-and-review-inbox`
> **Status:** `[x]` Completed (`[ ]` Not Started | `[~]` In Progress | `[x]` Completed)
> **PRD Reference:** `docs/prd.md` — Sections 1, 2, 3, 4.1.1–4.1.4, 4.2.1–4.2.4, 5, and 6 (Milestone 2, Slice 1 of 3)
> **Target Execution:** Run `/execute-phase` to execute this plan step-by-step.

---

## 1. Scope & Objectives

This phase launches **Milestone 2** of **LocalRoast**, delivering provider-agnostic local LLM configuration, an offline-resilient persistent SQL ingestion queue with background worker processing, hybrid Web Recipe URL scraping + Raw Text structured extraction, and the badged **Side-by-Side Review Inbox**:

1. **Provider-Agnostic Household LLM Settings (`HouseholdLlmSettings`, `ILlmSettingsService`, `LlmSettingsDialogComponent`):**
   - Persist household LLM configuration in SQL Server (`HouseholdLlmSettings`, singleton key `"Default"`), seeded automatically from optional `Llm__*` configuration/environment variables (`Llm__Provider`, `Llm__BaseUrl`, `Llm__ApiKey`, `Llm__TextModel`, `Llm__VisionModel`, `Llm__OllamaKeepAlive`, `Llm__RequestTimeoutSeconds`, `Llm__AutoPublishHighConfidenceText`).
   - Support both native **Ollama API** (`/api/tags`, `/api/chat` with structured JSON Schema `format` and `keep_alive`) and **OpenAI-Compatible `/v1` API** (`/v1/models`, `/v1/chat/completions` with `response_format: { "type": "json_object" }` and `Bearer` API key authentication for `LM Studio`, `llama.cpp`, `vLLM`, `LiteLLM`, `OpenRouter`, etc.).
   - Never expose stored plaintext `ApiKey` values on `GET /api/llm-settings` (return `HasApiKey` + masked preview `ApiKeyMasked`).
   - Provide a live **"Test Connection & Refresh Models"** workflow (`POST /api/llm-settings/test`) that probes reachability, measures round-trip latency, and populates independent **Text Model** and **Vision Model** combobox/autocomplete selectors (allowing manual typing even when the GPU workstation is powered off).

2. **Persistent Async Ingestion Queue & Offline-Resilient Worker (`IngestionJob`, `IIngestionService`, `IIngestionProcessor`, `IngestionBackgroundWorker`):**
   - Submitting one or more Web Recipe URLs or a Raw Text / Markdown block (`POST /api/ingestion/jobs`) persists `Queued` `IngestionJob` row(s) in SQL Server, signals `IIngestionQueueSignal`, and returns `202 Accepted` in `< 200ms` without blocking HTTP threads on scraping or LLM inference.
   - Full lifecycle state machine: `Queued` $\rightarrow$ `Preprocessing` $\rightarrow$ `Extracting` $\rightarrow$ `ReadyForReview` (or `Published` when `AutoPublishHighConfidenceText` is enabled and confidence criteria are met), plus `WaitingForLlmHost`, `Failed`, and `Discarded`.
   - **Offline / Asleep GPU Host Resilience:** If no `TextModel` is configured yet or the LAN LLM host is unreachable, refuses connections, returns `408`/`429`/`502`/`503`/`504`, or times out during inference, `IngestionProcessor` catches `LlmHostUnavailableException` and transitions the job safely to `WaitingForLlmHost`—preserving all scraped HTML/JSON-LD, raw text, and downloaded local WebP cover/thumbnail files on disk/DB.
   - `IngestionBackgroundWorker` periodically probes host health with exponential backoff (`15s` $\rightarrow$ `30s` $\rightarrow$ `60s` $\rightarrow$ max `300s`), automatically resuming `WaitingForLlmHost` jobs when the GPU host comes online, alongside a 1-click **"Retry All Waiting Jobs"** action (`POST /api/ingestion/jobs/retry-waiting`).

3. **Hybrid Web Recipe URL Scraper & Deterministic Draft Enrichment (`WebRecipeScraper`, `RecipeDraftNormalizer`, `StepTimerExtractor`):**
   - Fetch recipe web pages with polite browser headers, scheme validation (`http`/`https`), and bounded payload limits; extract `schema.org/Recipe` JSON-LD (`@graph`, `HowToStep`, `HowToSection`, `ImageObject`, ISO-8601 durations) alongside cleaned readable DOM text; and download the primary recipe photo locally into `/api/uploads/recipes/*.webp` via `IMediaStorageService.SaveRecipeCoverAsync`.
   - Feed distilled source payloads + household `StoreAisle` and `Tag` context to the configured **Text LLM**, then run deterministic C# post-processing (`RecipeDraftNormalizer` + `StepTimerExtractor`) to normalize `MeasurementUnit`s, bind existing canonical `FoodItem`s (matching `NormalizedName` + `FoodItemAlias` + singularization), flag **New FoodItems** with AI-suggested `StoreAisleId`s, detect missing step countdown timers (`"simmer for 15 minutes"` $\rightarrow$ `900s`), and resolve suggested household `Tag`s.

4. **Quick Ingest Workspace & Badged Side-by-Side Review Inbox (`/ingest`, `/ingest/review/:id`):**
   - Add **Ingest** navigation item with live **Review Inbox badge** (`readyForReviewCount`) to both desktop top bar and `360px` mobile bottom navigation bar in `AppShellComponent`, plus **"LLM Provider Settings"** in the user menu and `/ingest` header.
   - Build `IngestionHubComponent` (`/ingest`) for rapid single/batch URL submission, Raw Text / Markdown paste, `WaitingForLlmHost` status banner with 1-click **"Retry All Waiting Jobs"**, and live-updating Inbox/Queue cards.
   - Build `IngestionReviewComponent` (`/ingest/review/:id`) with a **Side-by-Side Split View** on desktop (original source provenance, extracted raw lines, and cleaned source text on the left; full structured recipe draft editor on the right highlighting **New FoodItems** + AI-suggested grocery aisles, step ingredient callouts, timers, and tags) and a clean `Source` | `Structured Draft` tab switcher at `360px` mobile widths.

### Explicitly Out of Scope (Deferred to Phases 05–06 & Milestones 3–4)
- Multi-image / mobile camera cookbook page uploads (`PNG`, `JPG`, `WEBP`, `HEIC`), PDF text-density inspection & page rasterization for Vision LLM, zoomable image/PDF source viewer panes, and original scan/PDF recipe attachment retention (**Phase 05** — `VisionModel` selector and `Images`/`Pdf` enum values are laid down here so Phase 05 plugs straight in).
- Personal Access Tokens (`lr_pat_...`) and the embedded `/api/mcp` Streamable HTTP server (**Phase 06**).
- Meal Planning Calendar, Shopping Lists, and SignalR live co-shopping (**Milestone 3**).
- Hands-Free Cook Mode, `CookLog` history, and 1-click AI Kitchen Helpers (**Milestone 4**).

### 1.1 File Inventory (Modify & Create)

#### Existing Backend & Test Files to Modify
- `Data/Entities/Recipe.cs` — Add `public ICollection<IngestionJob> IngestionJobs { get; set; } = new List<IngestionJob>();`.
- `Data/AppDbContext.cs` — Add `DbSet<HouseholdLlmSettings>` and `DbSet<IngestionJob>`.
- `Data/DataServiceCollectionExtensions.cs` — Register `ILlmSettingsService` and `IIngestionService` + `IIngestionProcessor`.
- `Data/Seeding/HouseholdCatalogSeeder.cs` — Ensure default `HouseholdLlmSettings` row exists if none is present when seeding.
- `Data/Services/RecipeService.cs` — In `DeleteRecipeAsync`, null out `ResultingRecipeId` on any `IngestionJob` referencing the deleted recipe prior to `dbContext.Recipes.Remove(recipe)` for EF InMemory + SQL Server parity.
- `Infrastructure/InfrastructureServiceCollectionExtensions.cs` — Bind `LlmDefaultsOptions`, register `IIngestionQueueSignal`, `HttpClient`-backed `ILlmExtractionClient` and `IWebRecipeScraper`, and hosted service `IngestionBackgroundWorker`.
- `API/appsettings.json` & `API/appsettings.Development.json` — Add `"Llm"` default configuration section.
- `Tests/API.IntegrationTests/AppWebApplicationFactory.cs` — Set `"Llm:EnableBackgroundWorker"` default to `"false"` in integration test host (while allowing per-test override) so API/service integration tests can drive `IIngestionProcessor` deterministically without background race conditions.
- `Tests/API.IntegrationTests/OpenApiIntegrationTests.cs` — Assert `/llm-settings` and `/ingestion/jobs` appear in `/api/openapi/v1.json`.

#### New Backend & Backend Test Files to Create
- `Core/Ingestion/IngestionEnums.cs`
- `Core/Ingestion/LlmHostUnavailableException.cs`
- `Core/Ingestion/LlmSettingsModels.cs`
- `Core/Ingestion/IngestionModels.cs`
- `Core/Ingestion/StepTimerExtractor.cs`
- `Core/Ingestion/RecipeDraftNormalizer.cs`
- `Core/Ingestion/ILlmSettingsService.cs`
- `Core/Ingestion/ILlmExtractionClient.cs`
- `Core/Ingestion/IWebRecipeScraper.cs`
- `Core/Ingestion/IIngestionQueueSignal.cs`
- `Core/Ingestion/IIngestionProcessor.cs`
- `Core/Ingestion/IIngestionService.cs`
- `Data/Entities/HouseholdLlmSettings.cs`
- `Data/Entities/IngestionJob.cs`
- `Data/Configurations/HouseholdLlmSettingsConfiguration.cs`
- `Data/Configurations/IngestionJobConfiguration.cs`
- `Data/Services/LlmSettingsService.cs`
- `Data/Services/IngestionService.cs`
- `Data/Services/IngestionProcessor.cs`
- `Infrastructure/Ingestion/IngestionQueueSignal.cs`
- `Infrastructure/Ingestion/WebRecipeScraper.cs`
- `Infrastructure/Ingestion/LlmExtractionClient.cs`
- `Infrastructure/Ingestion/IngestionBackgroundWorker.cs`
- `API/Controllers/LlmSettingsController.cs`
- `API/Controllers/IngestionJobsController.cs`
- `Tests/Core.UnitTests/StepTimerExtractorTests.cs`
- `Tests/Core.UnitTests/RecipeDraftNormalizerTests.cs`
- `Tests/Infrastructure.UnitTests/WebRecipeScraperTests.cs`
- `Tests/Infrastructure.UnitTests/LlmExtractionClientTests.cs`
- `Tests/API.IntegrationTests/LlmSettingsIntegrationTests.cs`
- `Tests/API.IntegrationTests/IngestionIntegrationTests.cs`

#### Existing Frontend Files to Modify (`Client/`)
- `Client/src/app/app.routes.ts` — Register lazy routes `ingest` (`IngestionHubComponent`) and `ingest/review/:id` (`IngestionReviewComponent`) under `AppShellComponent`.
- `Client/src/app/features/shell/app-shell/app-shell.component.ts` (+ `.html`, `.scss`, `.spec.ts`) — Inject `IngestionService`, render `Ingest` link + live Review Inbox badge in desktop nav and `360px` mobile bottom nav (adjusting `.mobile-tab` padding/min-width for 4 tabs at `360px`), and add **"LLM Provider Settings"** menu item in the user menu.
- `Client/src/app/features/recipes/recipe-list/recipe-list.component.html` (+ `.scss`) — Add secondary `Import Recipe` button (`routerLink="/ingest"`) alongside `Use Up Ingredients` and `New Recipe`.

#### New Frontend & E2E Files to Create
- `Client/src/app/core/models/ingestion.models.ts`
- `Client/src/app/core/services/llm-settings/llm-settings.service.ts` (+ `.spec.ts`)
- `Client/src/app/core/services/ingestion/ingestion.service.ts` (+ `.spec.ts`)
- `Client/src/app/features/ingestion/llm-settings-dialog/llm-settings-dialog.component.ts` (+ `.html`, `.scss`, `.spec.ts`)
- `Client/src/app/features/ingestion/ingestion-hub/ingestion-hub.component.ts` (+ `.html`, `.scss`, `.spec.ts`)
- `Client/src/app/features/ingestion/ingestion-review/ingestion-review.component.ts` (+ `.html`, `.scss`, `.spec.ts`)
- `Tests/Client.E2ETests/LlmIngestionE2ETests.cs`

---

## 2. Domain, Infrastructure & Database Design (`Core/`, `Data/`, `Infrastructure/`)

### 2.1 Enums, Exceptions & Configuration Options (`Core/Ingestion/`)

1. **`Core/Ingestion/IngestionEnums.cs`** (`namespace App.Core.Ingestion`):
   ```csharp
   using System.Text.Json.Serialization;

   namespace App.Core.Ingestion;

   [JsonConverter(typeof(JsonStringEnumConverter))]
   public enum LlmProvider
   {
       Ollama = 0,
       OpenAiCompatible = 1,
   }

   [JsonConverter(typeof(JsonStringEnumConverter))]
   public enum IngestionChannel
   {
       WebUrl = 0,
       RawText = 1,
       Images = 2,
       Pdf = 3,
   }

   [JsonConverter(typeof(JsonStringEnumConverter))]
   public enum IngestionJobStatus
   {
       Queued = 0,
       Preprocessing = 1,
       Extracting = 2,
       WaitingForLlmHost = 3,
       ReadyForReview = 4,
       Published = 5,
       Failed = 6,
       Discarded = 7,
   }
   ```

2. **`Core/Ingestion/LlmHostUnavailableException.cs`** (`namespace App.Core.Ingestion`):
   ```csharp
   namespace App.Core.Ingestion;

   public sealed class LlmHostUnavailableException(
       string message,
       Exception? innerException = null
   ) : Exception(message, innerException);
   ```

3. **`Core/Ingestion/LlmSettingsModels.cs`** (`namespace App.Core.Ingestion`):
   ```csharp
   using System.ComponentModel.DataAnnotations;

   namespace App.Core.Ingestion;

   public sealed class LlmDefaultsOptions
   {
       public const string SectionName = "Llm";

       public LlmProvider Provider { get; set; } = LlmProvider.Ollama;

       public string BaseUrl { get; set; } = "http://localhost:11434";

       public string? ApiKey { get; set; }

       public string? TextModel { get; set; }

       public string? VisionModel { get; set; }

       public string OllamaKeepAlive { get; set; } = "5m";

       public int RequestTimeoutSeconds { get; set; } = 120;

       public bool AutoPublishHighConfidenceText { get; set; }

       public bool EnableBackgroundWorker { get; set; } = true;

       public int WorkerPollIntervalSeconds { get; set; } = 3;

       public int OfflineProbeMaxBackoffSeconds { get; set; } = 300;
   }

   public record LlmSettingsDto(
       LlmProvider Provider,
       string BaseUrl,
       bool HasApiKey,
       string? ApiKeyMasked,
       string? TextModel,
       string? VisionModel,
       string OllamaKeepAlive,
       int RequestTimeoutSeconds,
       bool AutoPublishHighConfidenceText,
       DateTimeOffset? ModifiedAt
   );

   public record EffectiveLlmConnectionConfig(
       LlmProvider Provider,
       string BaseUrl,
       string? ApiKey,
       string? TextModel,
       string? VisionModel,
       string OllamaKeepAlive,
       int RequestTimeoutSeconds,
       bool AutoPublishHighConfidenceText
   );

   public record UpdateLlmSettingsRequest(
       LlmProvider Provider = LlmProvider.Ollama,
       [Required, MaxLength(500)] string BaseUrl = "http://localhost:11434",
       [MaxLength(500)] string? ApiKey = null,
       bool ClearApiKey = false,
       [MaxLength(200)] string? TextModel = null,
       [MaxLength(200)] string? VisionModel = null,
       [MaxLength(32)] string OllamaKeepAlive = "5m",
       [Range(5, 600)] int RequestTimeoutSeconds = 120,
       bool AutoPublishHighConfidenceText = false
   );

   public record TestLlmConnectionRequest(
       LlmProvider? Provider = null,
       [MaxLength(500)] string? BaseUrl = null,
       [MaxLength(500)] string? ApiKey = null,
       bool ClearApiKey = false,
       [Range(5, 120)] int? TimeoutSeconds = 15
   );

   public record LlmConnectionTestResultDto(
       bool IsReachable,
       LlmProvider Provider,
       string NormalizedBaseUrl,
       int LatencyMs,
       IReadOnlyList<string> Models,
       string? ErrorMessage
   );
   ```

### 2.2 Entities (`Data/Entities/`)

1. **`Data/Entities/HouseholdLlmSettings.cs`** (`namespace App.Data.Entities`, implements `IAuditable`):
   ```csharp
   using App.Core.Ingestion;

   namespace App.Data.Entities;

   public sealed class HouseholdLlmSettings : IAuditable
   {
       public const string DefaultSingletonKey = "Default";

       public Guid Id { get; set; } = Guid.NewGuid();

       public string SingletonKey { get; set; } = DefaultSingletonKey;

       public LlmProvider Provider { get; set; } = LlmProvider.Ollama;

       public string BaseUrl { get; set; } = "http://localhost:11434";

       public string? ApiKey { get; set; }

       public string? TextModel { get; set; }

       public string? VisionModel { get; set; }

       public string OllamaKeepAlive { get; set; } = "5m";

       public int RequestTimeoutSeconds { get; set; } = 120;

       public bool AutoPublishHighConfidenceText { get; set; }

       public DateTimeOffset CreatedAt { get; set; }

       public string CreatedBy { get; set; } = "System";

       public DateTimeOffset ModifiedAt { get; set; }

       public string ModifiedBy { get; set; } = "System";
   }
   ```

2. **`Data/Entities/IngestionJob.cs`** (`namespace App.Data.Entities`, implements `IAuditable`):
   ```csharp
   using App.Core.Ingestion;

   namespace App.Data.Entities;

   public sealed class IngestionJob : IAuditable
   {
       public Guid Id { get; set; } = Guid.NewGuid();

       public IngestionChannel Channel { get; set; }

       public IngestionJobStatus Status { get; set; } = IngestionJobStatus.Queued;

       public string DisplayTitle { get; set; } = string.Empty;

       public string? SourceUrl { get; set; }

       public string? RawText { get; set; }

       public string? ScrapedPageJson { get; set; }

       public string? NormalizedSourceText { get; set; }

       public string? DownloadedCoverImagePath { get; set; }

       public string? DownloadedThumbnailImagePath { get; set; }

       public string? ExtractedDraftJson { get; set; }

       public decimal? ConfidenceScore { get; set; }

       public string? StatusDetail { get; set; }

       public int AttemptCount { get; set; }

       public DateTimeOffset? NextRetryAt { get; set; }

       public DateTimeOffset? ProcessedAt { get; set; }

       public Guid? ResultingRecipeId { get; set; }

       public DateTimeOffset CreatedAt { get; set; }

       public string CreatedBy { get; set; } = "System";

       public DateTimeOffset ModifiedAt { get; set; }

       public string ModifiedBy { get; set; } = "System";

       public Recipe? ResultingRecipe { get; set; }
   }
   ```

3. **`Data/Entities/Recipe.cs`**:
   - Add navigation property:
     ```csharp
     public ICollection<IngestionJob> IngestionJobs { get; set; } = new List<IngestionJob>();
     ```

4. **`Data/AppDbContext.cs`**:
   - Add:
     ```csharp
     public DbSet<HouseholdLlmSettings> HouseholdLlmSettings => Set<HouseholdLlmSettings>();

     public DbSet<IngestionJob> IngestionJobs => Set<IngestionJob>();
     ```

### 2.3 EF Core Fluent Configurations (`Data/Configurations/`)

1. **`Data/Configurations/HouseholdLlmSettingsConfiguration.cs`**:
   - `builder.ToTable("HouseholdLlmSettings");`
   - `builder.HasKey(s => s.Id);`
   - `builder.Property(s => s.SingletonKey).IsRequired().HasMaxLength(32);`
   - `builder.HasIndex(s => s.SingletonKey).IsUnique();`
   - `builder.Property(s => s.Provider).HasConversion<string>().HasMaxLength(32).IsRequired();`
   - `builder.Property(s => s.BaseUrl).IsRequired().HasMaxLength(500);`
   - `builder.Property(s => s.ApiKey).HasMaxLength(500);`
   - `builder.Property(s => s.TextModel).HasMaxLength(200);`
   - `builder.Property(s => s.VisionModel).HasMaxLength(200);`
   - `builder.Property(s => s.OllamaKeepAlive).IsRequired().HasMaxLength(32);`
   - `builder.Property(s => s.CreatedBy).IsRequired().HasMaxLength(256);`
   - `builder.Property(s => s.ModifiedBy).IsRequired().HasMaxLength(256);`

2. **`Data/Configurations/IngestionJobConfiguration.cs`**:
   - `builder.ToTable("IngestionJobs");`
   - `builder.HasKey(j => j.Id);`
   - `builder.Property(j => j.Channel).HasConversion<string>().HasMaxLength(32).IsRequired();`
   - `builder.Property(j => j.Status).HasConversion<string>().HasMaxLength(32).IsRequired();`
   - `builder.Property(j => j.DisplayTitle).IsRequired().HasMaxLength(250);`
   - `builder.Property(j => j.SourceUrl).HasMaxLength(2048);`
   - `builder.Property(j => j.RawText).HasMaxLength(100000);`
   - `builder.Property(j => j.ScrapedPageJson).HasMaxLength(200000);`
   - `builder.Property(j => j.NormalizedSourceText).HasMaxLength(100000);`
   - `builder.Property(j => j.DownloadedCoverImagePath).HasMaxLength(500);`
   - `builder.Property(j => j.DownloadedThumbnailImagePath).HasMaxLength(500);`
   - `builder.Property(j => j.ExtractedDraftJson).HasMaxLength(200000);`
   - `builder.Property(j => j.ConfidenceScore).HasPrecision(4, 3);`
   - `builder.Property(j => j.StatusDetail).HasMaxLength(2000);`
   - `builder.Property(j => j.CreatedBy).IsRequired().HasMaxLength(256);`
   - `builder.Property(j => j.ModifiedBy).IsRequired().HasMaxLength(256);`
   - **Indexes:**
     - `builder.HasIndex(j => j.Status);`
     - `builder.HasIndex(j => new { j.Status, j.NextRetryAt });`
     - `builder.HasIndex(j => j.CreatedAt);`
     - `builder.HasIndex(j => j.ResultingRecipeId);`
   - **Foreign Key & SQL Server Cascade Safety:**
     - `IngestionJob` has only one nullable FK (`ResultingRecipeId` $\rightarrow$ `Recipes.Id`) and no child tables in Phase 04. Configure `DeleteBehavior.SetNull` so deleting a published `Recipe` preserves historical ingestion audit records without blocking recipe deletion or creating multi-cascade path cycles:
       ```csharp
       builder
           .HasOne(j => j.ResultingRecipe)
           .WithMany(r => r.IngestionJobs)
           .HasForeignKey(j => j.ResultingRecipeId)
           .OnDelete(DeleteBehavior.SetNull);
       ```
     - Also update `RecipeService.DeleteRecipeAsync` to explicitly null `ResultingRecipeId` on matching `IngestionJobs` before `dbContext.Recipes.Remove(recipe)` so EF Core InMemory behaves identically to SQL Server.
   - **Exact Migration Name:** `AddLlmSettingsAndIngestionQueue` (`dotnet ef migrations add AddLlmSettingsAndIngestionQueue --project Data/App.Data.csproj`).

### 2.4 DTOs, Requests, Deterministic Normalizers & Service Contracts (`Core/Ingestion/`)

1. **`Core/Ingestion/IngestionModels.cs`** (`namespace App.Core.Ingestion`):
   ```csharp
   using System.ComponentModel.DataAnnotations;
   using App.Core.Recipes;

   namespace App.Core.Ingestion;

   public record ScrapedWebRecipePayload(
       string CanonicalUrl,
       string? PageTitle,
       string? SiteName,
       string? Author,
       string? Description,
       string? PrepTimeIso,
       int? PrepTimeMinutes,
       string? CookTimeIso,
       int? CookTimeMinutes,
       string? TotalTimeIso,
       int? TotalTimeMinutes,
       string? RecipeYieldText,
       string? PrimaryImageUrl,
       IReadOnlyList<string> RawIngredientLines,
       IReadOnlyList<string> RawInstructionLines,
       string CleanedBodyText,
       bool HadRecipeJsonLd
   );

   public record WebScrapeResult(
       ScrapedWebRecipePayload Payload,
       string? DownloadedCoverImagePath,
       string? DownloadedThumbnailImagePath
   );

   // Raw JSON contract returned by Ollama / OpenAI-compatible Text LLM
   public record LlmExtractedTimerItem(int DurationSeconds, string Label);

   public record LlmExtractedIngredientItem(
       string? SectionName,
       decimal? Quantity,
       decimal? MaxQuantity,
       string? Unit,
       string? CanonicalFoodName,
       string DisplayName,
       string? PreparationNote,
       bool IsOptional,
       string? OriginalRawText,
       string? SuggestedAisleName,
       bool SuggestedIsPantryStaple = false
   );

   public record LlmExtractedStepItem(
       string? SectionName,
       string Instruction,
       IReadOnlyList<int>? IngredientIndices,
       IReadOnlyList<LlmExtractedTimerItem>? Timers
   );

   public record LlmExtractedRecipePayload(
       string Title,
       string? Description,
       decimal? BaseServings,
       string? YieldDisplay,
       int? PrepTimeMinutes,
       int? CookTimeMinutes,
       int? TotalTimeMinutes,
       string? Difficulty,
       string? SourceName,
       string? SourceAuthor,
       decimal? ConfidenceScore,
       IReadOnlyList<string>? SuggestedTagNames,
       IReadOnlyList<LlmExtractedIngredientItem>? Ingredients,
       IReadOnlyList<LlmExtractedStepItem>? Steps
   );

   // Normalized structured draft stored in IngestionJob.ExtractedDraftJson & edited in Review Inbox
   public record IngestionDraftIngredientDto(
       string? SectionName = null,
       decimal? Quantity = null,
       decimal? MaxQuantity = null,
       MeasurementUnit? Unit = null,
       string? CustomUnitName = null,
       Guid? MatchedFoodItemId = null,
       string? MatchedFoodItemName = null,
       bool IsNewFoodItem = false,
       [MaxLength(150)] string? ProposedFoodItemName = null,
       Guid? SuggestedStoreAisleId = null,
       bool SuggestedIsPantryStaple = false,
       bool CreateNewFoodItem = true,
       [Required, MaxLength(200)] string DisplayName = "",
       [MaxLength(200)] string? PreparationNote = null,
       bool IsOptional = false,
       [MaxLength(500)] string? OriginalRawText = null
   );

   public record IngestionDraftStepDto(
       [MaxLength(150)] string? SectionName = null,
       [Required, MaxLength(4000)] string Instruction = "",
       IReadOnlyList<int>? IngredientIndices = null,
       IReadOnlyList<RecipeStepTimerInput>? Timers = null,
       [MaxLength(500)] string? ImagePath = null
   );

   public record IngestionExtractedDraftDto(
       [Required, MaxLength(200)] string Title,
       [MaxLength(2000)] string? Description = null,
       [Range(typeof(decimal), "0.25", "999")] decimal BaseServings = 4m,
       [MaxLength(100)] string? YieldDisplay = null,
       [Range(0, 10080)] int? PrepTimeMinutes = null,
       [Range(0, 10080)] int? CookTimeMinutes = null,
       [Range(0, 10080)] int? TotalTimeMinutes = null,
       RecipeDifficulty? Difficulty = null,
       [MaxLength(2048)] string? SourceUrl = null,
       [MaxLength(200)] string? SourceName = null,
       [MaxLength(200)] string? SourceAuthor = null,
       [MaxLength(500)] string? CoverImagePath = null,
       [MaxLength(500)] string? ThumbnailImagePath = null,
       IReadOnlyList<Guid>? SuggestedCollectionIds = null,
       IReadOnlyList<Guid>? SuggestedTagIds = null,
       IReadOnlyList<IngestionDraftIngredientDto>? Ingredients = null,
       IReadOnlyList<IngestionDraftStepDto>? Steps = null,
       decimal ConfidenceScore = 0.8m
   );

   public record EnqueueWebUrlIngestionRequest(
       [Required] IReadOnlyList<string> Urls
   );

   public record EnqueueRawTextIngestionRequest(
       [Required, MaxLength(100000)] string RawText,
       [MaxLength(200)] string? TitleHint = null
   );

   public record IngestionQueueSummaryDto(
       int ReadyForReviewCount,
       int ActiveProcessingCount,
       int WaitingForLlmHostCount,
       int FailedCount
   );

   public record IngestionJobSummaryDto(
       Guid Id,
       IngestionChannel Channel,
       IngestionJobStatus Status,
       string DisplayTitle,
       string? SourceUrl,
       string? ThumbnailImagePath,
       decimal? ConfidenceScore,
       string? StatusDetail,
       int AttemptCount,
       DateTimeOffset? NextRetryAt,
       Guid? ResultingRecipeId,
       string? ResultingRecipeSlug,
       string? SubmittedByUsername,
       DateTimeOffset CreatedAt,
       DateTimeOffset ModifiedAt
   );

   public record IngestionJobDetailDto(
       Guid Id,
       IngestionChannel Channel,
       IngestionJobStatus Status,
       string DisplayTitle,
       string? SourceUrl,
       string? RawText,
       ScrapedWebRecipePayload? ScrapedPage,
       string? NormalizedSourceText,
       string? DownloadedCoverImagePath,
       string? DownloadedThumbnailImagePath,
       IngestionExtractedDraftDto? ExtractedDraft,
       decimal? ConfidenceScore,
       string? StatusDetail,
       int AttemptCount,
       DateTimeOffset? NextRetryAt,
       DateTimeOffset? ProcessedAt,
       Guid? ResultingRecipeId,
       string? ResultingRecipeSlug,
       string? SubmittedByUsername,
       DateTimeOffset CreatedAt,
       DateTimeOffset ModifiedAt
   );

   public record RetryWaitingJobsResultDto(int RequeuedCount);
   ```

2. **`Core/Ingestion/StepTimerExtractor.cs`** (`namespace App.Core.Ingestion`):
   - Pure static helper `public static class StepTimerExtractor`:
     - `public static IReadOnlyList<RecipeStepTimerInput> DetectTimers(string? instruction, int stepNumber = 1)`:
       - Scans `instruction` using compiled regexes with `TimeSpan.FromSeconds(1)` timeout for explicit durations such as `"15 minutes"`, `"25-30 min"`, `"25 to 30 mins"`, `"1 hour 20 minutes"`, `"1.5 hours"`, `"45 seconds"`.
       - Uses the lower numeric bound when a range is written (`"25–30 min"` $\rightarrow$ `25 * 60 = 1500` seconds), clamps within `[1, 864000]`, deduplicates identical durations within the same step, and infers a concise culinary verb label when an action verb precedes the duration (`"Simmer"`, `"Bake"`, `"Roast"`, `"Rest"`, `"Boil"`, `"Marinate"`, falling back to `"Step {stepNumber} timer"`).

3. **`Core/Ingestion/RecipeDraftNormalizer.cs`** (`namespace App.Core.Ingestion`):
   - Pure static deterministic normalizer `public static class RecipeDraftNormalizer`:
     - Define lightweight catalog lookup records in `RecipeDraftNormalizer.cs`:
       - `public record CatalogFoodLookupItem(Guid Id, string Name, string NormalizedName, Guid? DefaultStoreAisleId, bool IsPantryStaple, IReadOnlyList<string> NormalizedAliases);`
       - `public record CatalogAisleLookupItem(Guid Id, string Name, string NormalizedName);`
       - `public record CatalogTagLookupItem(Guid Id, string Name, string NormalizedName, string Slug);`
     - `public static (MeasurementUnit? Unit, string? CustomUnitName) NormalizeUnit(string? rawUnit)`:
       - Maps common culinary unit strings (`"tsp"`, `"teaspoon"`, `"teaspoons"`, `"t"`, `"tbsp"`, `"tablespoon"`, `"tablespoons"`, `"fl oz"`, `"fl_oz"`, `"fluid ounce"`, `"cup"`, `"cups"`, `"c"`, `"pt"`, `"pint"`, `"qt"`, `"quart"`, `"gal"`, `"gallon"`, `"ml"`, `"milliliter"`, `"l"`, `"liter"`, `"oz"`, `"ounce"`, `"ounces"`, `"lb"`, `"lbs"`, `"pound"`, `"pounds"`, `"g"`, `"gram"`, `"grams"`, `"kg"`, `"kilogram"`, `"pinch"`, `"dash"`, `"clove"`, `"cloves"`, `"can"`, `"cans"`, `"bunch"`, `"bunches"`, `"slice"`, `"slices"`, `"whole"`, `"large"`, `"medium"`, `"small"`) to `MeasurementUnit`.
       - Returns `(null, null)` when empty/whitespace; returns `(MeasurementUnit.Custom, trimmed[..Math.Min(50, trimmed.Length)])` when unrecognized.
     - `public static IngestionExtractedDraftDto Normalize(LlmExtractedRecipePayload raw, IReadOnlyList<CatalogFoodLookupItem> catalogFoods, IReadOnlyList<CatalogAisleLookupItem> aisles, IReadOnlyList<CatalogTagLookupItem> tags, string? sourceUrl, string? fallbackSourceName, string? fallbackSourceAuthor, string? coverImagePath, string? thumbnailImagePath)`:
       - Matches each ingredient's `CanonicalFoodName` (or cleaned `DisplayName`) against `catalogFoods` by exact `NormalizedName`, exact `NormalizedAliases`, or simple English singularization (`-ies` $\rightarrow$ `-y`, `-oes`/`-ses` $\rightarrow$ `-o`/`-s`, trailing `-s` stripped when length > 3).
       - When matched: sets `MatchedFoodItemId`, `MatchedFoodItemName`, `IsNewFoodItem = false`, `CreateNewFoodItem = false`, `SuggestedStoreAisleId = matched.DefaultStoreAisleId`, `SuggestedIsPantryStaple = matched.IsPantryStaple`.
       - When unmatched: sets `MatchedFoodItemId = null`, `IsNewFoodItem = true`, `CreateNewFoodItem = true`, `ProposedFoodItemName = ToTitleCase(cleanedCanonicalName)`, matches `SuggestedAisleName` against `aisles` (falling back to `"other"` aisle ID if present), and preserves `SuggestedIsPantryStaple`.
       - Ensures every step has valid `IngredientIndices` (within `[0, ingredientCount - 1]`, deduplicated) and uses `StepTimerExtractor.DetectTimers` whenever the LLM returned zero timers for a step containing duration text.
       - Parses `Difficulty` (`Easy`, `Medium`, `Project`) case-insensitively and clamps `BaseServings` to `[0.25m, 999m]` (default `4m`).
       - Matches `SuggestedTagNames` against `tags` (`NormalizedName` or `Slug`) into distinct `SuggestedTagIds`.

4. **Service Interfaces (`Core/Ingestion/`)**:
   - **`ILlmSettingsService.cs`**:
     ```csharp
     namespace App.Core.Ingestion;

     public interface ILlmSettingsService
     {
         Task<LlmSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default);

         Task<EffectiveLlmConnectionConfig> GetEffectiveConfigAsync(
             CancellationToken cancellationToken = default
         );

         Task<LlmSettingsDto> UpdateSettingsAsync(
             UpdateLlmSettingsRequest request,
             CancellationToken cancellationToken = default
         );

         Task<LlmConnectionTestResultDto> TestConnectionAsync(
             TestLlmConnectionRequest request,
             CancellationToken cancellationToken = default
         );
     }
     ```
   - **`ILlmExtractionClient.cs`**:
     ```csharp
     namespace App.Core.Ingestion;

     public record LlmExtractionContext(
         string SourceContent,
         IReadOnlyList<string> AvailableAisleNames,
         IReadOnlyList<string> AvailableTagNames
     );

     public interface ILlmExtractionClient
     {
         Task<LlmConnectionTestResultDto> ProbeAndListModelsAsync(
             EffectiveLlmConnectionConfig config,
             int timeoutSeconds = 15,
             CancellationToken cancellationToken = default
         );

         Task<LlmExtractedRecipePayload> ExtractRecipeFromTextAsync(
             EffectiveLlmConnectionConfig config,
             LlmExtractionContext context,
             CancellationToken cancellationToken = default
         );
     }
     ```
   - **`IWebRecipeScraper.cs`**:
     ```csharp
     namespace App.Core.Ingestion;

     public interface IWebRecipeScraper
     {
         Task<WebScrapeResult> ScrapeRecipePageAsync(
             string url,
             CancellationToken cancellationToken = default
         );
     }
     ```
   - **`IIngestionQueueSignal.cs`**:
     ```csharp
     namespace App.Core.Ingestion;

     public interface IIngestionQueueSignal
     {
         void NotifyWorkAvailable();

         ValueTask<bool> WaitForWorkAsync(
             TimeSpan timeout,
             CancellationToken cancellationToken = default
         );
     }
     ```
   - **`IIngestionProcessor.cs`**:
     ```csharp
     namespace App.Core.Ingestion;

     public interface IIngestionProcessor
     {
         Task<int> RecoverInterruptedJobsAsync(CancellationToken cancellationToken = default);

         Task<bool> ProcessNextQueuedJobAsync(CancellationToken cancellationToken = default);

         Task<bool> ProcessJobByIdAsync(Guid jobId, CancellationToken cancellationToken = default);

         Task<int> ProbeAndPromoteWaitingJobsAsync(CancellationToken cancellationToken = default);
     }
     ```
   - **`IIngestionService.cs`**:
     ```csharp
     using App.Core.Recipes;

     namespace App.Core.Ingestion;

     public interface IIngestionService
     {
         Task<IngestionQueueSummaryDto> GetSummaryAsync(
             CancellationToken cancellationToken = default
         );

         Task<IReadOnlyList<IngestionJobSummaryDto>> GetJobsAsync(
             IngestionJobStatus? status = null,
             CancellationToken cancellationToken = default
         );

         Task<IngestionJobDetailDto?> GetJobByIdAsync(
             Guid id,
             CancellationToken cancellationToken = default
         );

         Task<IReadOnlyList<IngestionJobSummaryDto>> EnqueueWebUrlsAsync(
             EnqueueWebUrlIngestionRequest request,
             CancellationToken cancellationToken = default
         );

         Task<IngestionJobSummaryDto> EnqueueRawTextAsync(
             EnqueueRawTextIngestionRequest request,
             CancellationToken cancellationToken = default
         );

         Task<IngestionJobDetailDto?> SaveDraftAsync(
             Guid id,
             IngestionExtractedDraftDto draft,
             CancellationToken cancellationToken = default
         );

         Task<RecipeDetailDto> ApproveAndPublishAsync(
             Guid id,
             IngestionExtractedDraftDto draft,
             CancellationToken cancellationToken = default
         );

         Task<IngestionJobSummaryDto?> RetryJobAsync(
             Guid id,
             CancellationToken cancellationToken = default
         );

         Task<RetryWaitingJobsResultDto> RetryAllWaitingJobsAsync(
             CancellationToken cancellationToken = default
         );

         Task<bool> DiscardJobAsync(Guid id, CancellationToken cancellationToken = default);
     }
     ```

### 2.5 Infrastructure Services (`Infrastructure/Ingestion/`)

1. **`Infrastructure/Ingestion/IngestionQueueSignal.cs`**:
   - Thread-safe singleton using `Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest })`.
   - `NotifyWorkAvailable()` calls `_channel.Writer.TryWrite(true)`.
   - `WaitForWorkAsync(TimeSpan timeout, CancellationToken cancellationToken)` races `_channel.Reader.WaitToReadAsync(...)` with `Task.Delay(timeout, cancellationToken)` and drains the item if signaled.

2. **`Infrastructure/Ingestion/WebRecipeScraper.cs`** (`IWebRecipeScraper`):
   - Injects `HttpClient` and `IMediaStorageService`.
   - Validates `Uri.TryCreate(url, UriKind.Absolute, out var uri)` and requires `uri.Scheme is "http" or "https"` (throws `DomainValidationException` otherwise).
   - Sends `GET` with polite headers (`User-Agent: Mozilla/5.0 (compatible; LocalRoast/1.0)`, `Accept: text/html,application/xhtml+xml`). Reads bounded HTML stream (max `5 MB`).
   - Parses `<script type="application/ld+json">` blocks using `JsonDocument` (tolerating malformed script blocks by continuing to next block). Recursively searches root objects, arrays, and `"@graph"` arrays for nodes whose `"@type"` string or string-array contains `"Recipe"` (or ends with `"/Recipe"`).
   - Extracts JSON-LD `name`, `description`, `author`, `publisher`, `image` (string, array, or `ImageObject.url`), `recipeYield`, ISO-8601 `prepTime`/`cookTime`/`totalTime` (supporting `PT15M`, `PT1H30M`, `P0DT45M`), `recipeIngredient` lines, and `recipeInstructions` (string, string array, `HowToStep`, and nested `HowToSection` items, prefixing section names as `"[Section: <name>] <text>"` so the LLM preserves step sections!).
   - Also extracts fallback `<title>`, `og:title`, `og:site_name`, `og:image`, and cleaned readable DOM text (stripping `<script>`, `<style>`, `<noscript>`, `<svg>`, `<nav>`, `<footer>`, `<header>`, `<aside>`, HTML comments, converting block closures to `\n`, HTML-decoding via `WebUtility.HtmlDecode`, collapsing whitespace, and truncating to `24,000` chars).
   - If a primary image URL (`PrimaryImageUrl` or `og:image`) is found, resolves relative URLs via `new Uri(uri, rawImageUrl)` and attempts bounded download (max `15 MB`, `15s` timeout) into a `MemoryStream`, passing it to `IMediaStorageService.SaveRecipeCoverAsync`. Any image download/format error is logged and ignored so recipe extraction still succeeds.

3. **`Infrastructure/Ingestion/LlmExtractionClient.cs`** (`ILlmExtractionClient`):
   - Injects `HttpClient`.
   - Helper `NormalizeBaseUrl(LlmProvider provider, string baseUrl)`:
     - Validates absolute `http://` or `https://` URI (throws `DomainValidationException` on invalid URI).
     - For `Ollama`: trims trailing `/`.
     - For `OpenAiCompatible`: trims trailing `/`; if path does not already end with `/v1` (case-insensitive), appends `/v1`.
   - **`ProbeAndListModelsAsync`**:
     - `Ollama`: `GET {normalizedBase}/api/tags` $\rightarrow$ reads `models[*].name`.
     - `OpenAiCompatible`: `GET {normalizedBase}/models` (with `Authorization: Bearer {ApiKey}` if set) $\rightarrow$ reads `data[*].id`.
     - Catches `HttpRequestException`, `TaskCanceledException`, `JsonException` and returns `IsReachable = false` with descriptive `ErrorMessage` (re-throwing only if `cancellationToken.IsCancellationRequested`).
   - **`ExtractRecipeFromTextAsync`**:
     - If `string.IsNullOrWhiteSpace(config.TextModel)`, throws `new LlmHostUnavailableException("No Text LLM model is selected. Configure a Text Model in LLM Settings and retry.")`.
     - Builds a strict culinary extraction system prompt instructing the model to extract structured JSON matching `LlmExtractedRecipePayload` (listing available `StoreAisle` names and household `Tag` names, requiring `canonicalFoodName` in singular form, `originalRawText` verbatim, `ingredientIndices` 0-based per step, `timers` in `durationSeconds`, and `confidenceScore` between `0.0` and `1.0`).
     - `Ollama`: `POST {normalizedBase}/api/chat` with `model`, `stream = false`, `keep_alive = config.OllamaKeepAlive`, `options = { temperature = 0.1 }`, and `format` set to the JSON Schema object for `LlmExtractedRecipePayload`.
     - `OpenAiCompatible`: `POST {normalizedBase}/chat/completions` with `model`, `stream = false`, `temperature = 0.1`, `response_format = { type = "json_object" }`, and `Authorization: Bearer {ApiKey}` when configured.
     - Network failures (`HttpRequestException`), HTTP timeouts (`TaskCanceledException` when `!cancellationToken.IsCancellationRequested`), and transient host status codes (`408`, `429`, `502`, `503`, `504`) throw `LlmHostUnavailableException`.
     - Strips optional markdown code fences (` ```json ... ``` `) and deserializes `LlmExtractedRecipePayload` with `PropertyNameCaseInsensitive = true` (throwing `DomainValidationException` if JSON is empty or lacks a `Title`).

4. **`Infrastructure/Ingestion/IngestionBackgroundWorker.cs`** (`BackgroundService`):
   - Injects `IServiceScopeFactory`, `IIngestionQueueSignal`, `IOptions<LlmDefaultsOptions>`, and `ILogger<IngestionBackgroundWorker>`.
   - Returns immediately from `ExecuteAsync` if `!options.Value.EnableBackgroundWorker`.
   - On startup, creates a scope and calls `processor.RecoverInterruptedJobsAsync(stoppingToken)`.
   - Loops until `stoppingToken.IsCancellationRequested`:
     - Drains all currently `Queued` jobs (`while (await processor.ProcessNextQueuedJobAsync(stoppingToken)) { }`).
     - Calls `await processor.ProbeAndPromoteWaitingJobsAsync(stoppingToken)` to check if any `WaitingForLlmHost` jobs have `NextRetryAt <= UtcNow` and whether the LLM host is back online.
     - Awaits `queueSignal.WaitForWorkAsync(TimeSpan.FromSeconds(options.Value.WorkerPollIntervalSeconds), stoppingToken)`.

### 2.6 Data Services Business Logic & Resilience Invariants (`Data/Services/`)

1. **`Data/Services/LlmSettingsService.cs`** (`ILlmSettingsService`):
   - Validates `BaseUrl` is a valid `http://` or `https://` URL (`DomainValidationException` otherwise).
   - `GetSettingsAsync`: Reads `HouseholdLlmSettings` (`AsNoTracking()`), falling back to `IOptions<LlmDefaultsOptions>` if not yet persisted. Masks `ApiKey` (`ApiKeyMasked = MaskSecret(apiKey)` e.g., `"••••" + last4` when length > 4, else `"••••••••"`).
   - `UpdateSettingsAsync`: Upserts the `SingletonKey = "Default"` row. Preserves existing `ApiKey` when `request.ApiKey` is null/empty and `!request.ClearApiKey`; clears when `request.ClearApiKey`; updates when `!string.IsNullOrWhiteSpace(request.ApiKey)`. If `TextModel` is non-empty, promotes any `WaitingForLlmHost` jobs `NextRetryAt` to `DateTimeOffset.UtcNow` and signals `IIngestionQueueSignal` so newly configured settings immediately wake the queue!
   - `TestConnectionAsync`: Merges optional `TestLlmConnectionRequest` overrides onto `GetEffectiveConfigAsync` (using saved `ApiKey` if `request.ApiKey` is null/empty and `!request.ClearApiKey`), validates `BaseUrl` syntax (`DomainValidationException` on malformed URL), and delegates to `ILlmExtractionClient.ProbeAndListModelsAsync`.

2. **`Data/Services/IngestionService.cs`** (`IIngestionService`):
   - `EnqueueWebUrlsAsync`: Validates 1–20 non-empty `http://` or `https://` URLs per batch (`DomainValidationException` if empty, > 20, or invalid URL scheme). Creates `Queued` `IngestionJob` rows (`Channel = IngestionChannel.WebUrl`, `DisplayTitle = uri.Host + uri.AbsolutePath` truncated to 200 chars), saves in `< 200ms`, calls `queueSignal.NotifyWorkAvailable()`, and returns `IReadOnlyList<IngestionJobSummaryDto>`.
   - `EnqueueRawTextAsync`: Validates non-whitespace `RawText` (`<= 100,000` chars). Infers initial `DisplayTitle` from `TitleHint` or first non-empty line of `RawText` (stripped of `#` markdown heading markers, truncated to 120 chars). Saves `Queued` `IngestionJob` (`Channel = IngestionChannel.RawText`), signals `queueSignal`, and returns `IngestionJobSummaryDto`.
   - `SaveDraftAsync`: Validates job exists (`null` $\rightarrow$ `404`) and is in `ReadyForReview` or `Failed` (`ConflictException` if already `Published` or `Discarded`), updates `ExtractedDraftJson` and `DisplayTitle = draft.Title.Trim()`, saves, and returns `IngestionJobDetailDto`.
   - `ApproveAndPublishAsync`:
     - Validates job exists (`NotFoundException` if missing; `ConflictException` if already `Published` or `Discarded`).
     - Resolves/creates new `FoodItem`s for any ingredient where `MatchedFoodItemId == null && CreateNewFoodItem && !string.IsNullOrWhiteSpace(ProposedFoodItemName)`:
       - Re-checks `dbContext.FoodItems` (`NormalizedName`) and `dbContext.FoodItemAliases` (`NormalizedAlias`) case-insensitively first so duplicate ingredients within the same recipe or concurrent jobs bind cleanly to the same `FoodItem` without throwing `ConflictException`!
       - Otherwise creates the new `FoodItem` with `DefaultStoreAisleId = ing.SuggestedStoreAisleId` (validated against `StoreAisles`) and `IsPantryStaple = ing.SuggestedIsPantryStaple`.
     - Maps `IngestionExtractedDraftDto` into `UpsertRecipeRequest` (`Status = RecipeStatus.Published`, preserving `OriginalRawText`, `SectionName`, `IngredientIndices`, `Timers`, `CoverImagePath`, `ThumbnailImagePath`, `SuggestedCollectionIds`, `SuggestedTagIds`) and calls `IRecipeService.CreateRecipeAsync`.
     - Updates `job.Status = IngestionJobStatus.Published`, `job.ResultingRecipeId = createdRecipe.Id`, `job.ExtractedDraftJson = serializedDraft`, `job.StatusDetail = "Approved and published to cookbook."`, saves, and returns `RecipeDetailDto`.
   - `RetryJobAsync` / `RetryAllWaitingJobsAsync`:
     - `RetryJobAsync`: Transitions a `WaitingForLlmHost` or `Failed` job back to `Queued` (`NextRetryAt = null`, `StatusDetail = "Queued for retry."`), signals `queueSignal`. Throws `ConflictException` if job is already `Published` or `Discarded`.
     - `RetryAllWaitingJobsAsync`: Transitions all `WaitingForLlmHost` jobs to `Queued` (`NextRetryAt = null`), saves, signals `queueSignal`, and returns `new RetryWaitingJobsResultDto(count)`.
   - `DiscardJobAsync`:
     - Marks job `Discarded` (cleaning up `DownloadedCoverImagePath` / `DownloadedThumbnailImagePath` via `IMediaStorageService.DeleteMediaFileAsync` ONLY if `ResultingRecipeId == null`). Throws `ConflictException` if job is `Published`.

3. **`Data/Services/IngestionProcessor.cs`** (`IIngestionProcessor`):
   - **Step 1 (`Preprocessing`):**
     - For `WebUrl`: If `ScrapedPageJson` is not already populated from an earlier attempt, calls `IWebRecipeScraper.ScrapeRecipePageAsync(job.SourceUrl!)`, stores `ScrapedPageJson`, `NormalizedSourceText` (combining JSON-LD metadata, `RawIngredientLines`, `RawInstructionLines`, and `CleanedBodyText`), `DownloadedCoverImagePath`, `DownloadedThumbnailImagePath`, and updates `DisplayTitle` if `PageTitle` was discovered.
     - For `RawText`: Populates `NormalizedSourceText = job.RawText`.
     - Note: Because preprocessing commits to `AppDbContext` *before* calling the LLM, if the LAN LLM host is asleep/offline, the scraped web page and downloaded WebP images are already safely stored locally and never re-fetched on retry!
   - **Step 2 (`Extracting`):**
     - Loads `EffectiveLlmConnectionConfig` plus household `StoreAisles`, `FoodItems` (with `Aliases`), and `Tags`.
     - Calls `ILlmExtractionClient.ExtractRecipeFromTextAsync(...)`.
     - Normalizes output via `RecipeDraftNormalizer.Normalize(...)`.
     - **If `config.AutoPublishHighConfidenceText` is `true`**, channel is `WebUrl` or `RawText`, `!string.IsNullOrWhiteSpace(draft.Title)`, `(draft.Ingredients?.Count ?? 0) >= 2`, `(draft.Steps?.Count ?? 0) >= 1`, and `draft.ConfidenceScore >= 0.85m`:
       - Automatically creates any new `FoodItem`s, publishes via `IRecipeService.CreateRecipeAsync`, sets `job.Status = IngestionJobStatus.Published`, `job.ResultingRecipeId = recipe.Id`, `job.ProcessedAt = UtcNow`, and `job.StatusDetail = "Auto-published high-confidence extraction."`.
     - **Otherwise:** sets `job.Status = IngestionJobStatus.ReadyForReview`, `job.ExtractedDraftJson`, `job.ConfidenceScore = draft.ConfidenceScore`, `job.DisplayTitle = draft.Title`, `job.ProcessedAt = UtcNow`, and `job.StatusDetail = "Ready for side-by-side review."`.
   - **Error Handling:**
     - `catch (LlmHostUnavailableException ex)`: Sets `job.Status = IngestionJobStatus.WaitingForLlmHost`, increments `AttemptCount`, computes backoff `delaySeconds = Math.Min(maxBackoff, 15 * (1 << Math.Min(job.AttemptCount - 1, 5)))`, sets `job.NextRetryAt = UtcNow.AddSeconds(delaySeconds)`, `job.StatusDetail = ex.Message`, and saves cleanly.
     - `catch (Exception ex) when (ex is not OperationCanceledException)`: Sets `job.Status = IngestionJobStatus.Failed`, increments `AttemptCount`, sets `job.StatusDetail = ex.Message`, and saves cleanly.

---

## 3. API Endpoints (`API/Controllers/`)

### 3.1 `API/Controllers/LlmSettingsController.cs` (`[Route("llm-settings")]`, `[Authorize]`)

| Verb | Route (`/api/...`) | Request Body / Query | Response (`200`) | Error Cases (`400`/`401`) |
| :--- | :--- | :--- | :--- | :--- |
| `GET` | `/llm-settings` | — | `200 OK` (`LlmSettingsDto`) | `401 Unauthorized` |
| `PUT` | `/llm-settings` | `[FromBody] UpdateLlmSettingsRequest` | `200 OK` (`LlmSettingsDto`) | `400 Bad Request` (`ProblemDetails` on invalid URL/range), `401` |
| `POST` | `/llm-settings/test` | `[FromBody] TestLlmConnectionRequest` | `200 OK` (`LlmConnectionTestResultDto`) | `400 Bad Request` (`ProblemDetails` on malformed URL), `401` |

### 3.2 `API/Controllers/IngestionJobsController.cs` (`[Route("ingestion/jobs")]`, `[Authorize]`)

| Verb | Route (`/api/...`) | Request Body / Query | Response (`200`/`201`/`202`/`204`) | Error Cases (`400`/`401`/`404`/`409`) |
| :--- | :--- | :--- | :--- | :--- |
| `GET` | `/ingestion/jobs/summary` | — | `200 OK` (`IngestionQueueSummaryDto`) | `401 Unauthorized` |
| `GET` | `/ingestion/jobs` | `[FromQuery] IngestionJobStatus? status` | `200 OK` (`IReadOnlyList<IngestionJobSummaryDto>`) | `401 Unauthorized` |
| `GET` | `/ingestion/jobs/{id:guid}` | — | `200 OK` (`IngestionJobDetailDto`) | `401`, `404 Not Found` |
| `POST` | `/ingestion/jobs/urls` | `[FromBody] EnqueueWebUrlIngestionRequest` | `202 Accepted` (`IReadOnlyList<IngestionJobSummaryDto>`) | `400 Bad Request` (`ProblemDetails`), `401` |
| `POST` | `/ingestion/jobs/text` | `[FromBody] EnqueueRawTextIngestionRequest` | `202 Accepted` (`IngestionJobSummaryDto`) | `400 Bad Request` (`ProblemDetails`), `401` |
| `PUT` | `/ingestion/jobs/{id:guid}/draft` | `[FromBody] IngestionExtractedDraftDto` | `200 OK` (`IngestionJobDetailDto`) | `400`, `401`, `404`, `409 Conflict` |
| `POST` | `/ingestion/jobs/{id:guid}/approve` | `[FromBody] IngestionExtractedDraftDto` | `201 Created` (`RecipeDetailDto`) | `400`, `401`, `404`, `409 Conflict` |
| `POST` | `/ingestion/jobs/{id:guid}/retry` | — | `202 Accepted` (`IngestionJobSummaryDto`) | `401`, `404`, `409 Conflict` |
| `POST` | `/ingestion/jobs/retry-waiting` | — | `200 OK` (`RetryWaitingJobsResultDto`) | `401 Unauthorized` |
| `DELETE` | `/ingestion/jobs/{id:guid}` | — | `204 No Content` | `401`, `404`, `409 Conflict` |

> **Lean Controller Rule:** Both controllers must strictly bind HTTP payloads, invoke `ILlmSettingsService` / `IIngestionService`, and translate `DomainValidationException` (`400`), `NotFoundException` (`404`), and `ConflictException` (`409`) into `ProblemDetails`—with zero `AppDbContext` or scraping/LLM logic in `API/`.

---

## 4. Angular Frontend Design (`Client/`)

### 4.1 Models & Services (`src/app/core/`)

1. **`Client/src/app/core/models/ingestion.models.ts`**:
   - Export TypeScript types matching all backend DTOs:
     - `LlmProvider = 'Ollama' | 'OpenAiCompatible'`
     - `IngestionChannel = 'WebUrl' | 'RawText' | 'Images' | 'Pdf'`
     - `IngestionJobStatus = 'Queued' | 'Preprocessing' | 'Extracting' | 'WaitingForLlmHost' | 'ReadyForReview' | 'Published' | 'Failed' | 'Discarded'`
     - `LlmSettingsDto`, `UpdateLlmSettingsRequest`, `TestLlmConnectionRequest`, `LlmConnectionTestResultDto`
     - `ScrapedWebRecipePayload`, `IngestionDraftIngredientDto`, `IngestionDraftStepDto`, `IngestionExtractedDraftDto`
     - `EnqueueWebUrlIngestionRequest`, `EnqueueRawTextIngestionRequest`, `IngestionQueueSummaryDto`, `IngestionJobSummaryDto`, `IngestionJobDetailDto`, `RetryWaitingJobsResultDto`

2. **`Client/src/app/core/services/llm-settings/llm-settings.service.ts`** (+ `.spec.ts`):
   - Decorated with `@Service()`, injecting `HttpClient` and `MatDialog`.
   - Methods using `apiUrl('/llm-settings...')`:
     - `getSettings(): Observable<LlmSettingsDto>`
     - `updateSettings(request: UpdateLlmSettingsRequest): Observable<LlmSettingsDto>`
     - `testConnection(request: TestLlmConnectionRequest): Observable<LlmConnectionTestResultDto>`
     - `openLlmSettingsDialog(): void` (dynamically opens `LlmSettingsDialogComponent` at `width: '620px', maxWidth: '95vw'`).

3. **`Client/src/app/core/services/ingestion/ingestion.service.ts`** (+ `.spec.ts`):
   - Decorated with `@Service()`, injecting `HttpClient`.
   - Reactive signals exposed for shell badge & live views:
     - `readonly summary = signal<IngestionQueueSummaryDto>({ readyForReviewCount: 0, activeProcessingCount: 0, waitingForLlmHostCount: 0, failedCount: 0 });`
     - `readonly reviewInboxCount = computed(() => this.summary().readyForReviewCount);`
     - `readonly waitingForHostCount = computed(() => this.summary().waitingForLlmHostCount);`
   - Methods using `apiUrl('/ingestion/jobs...')` (automatically calling `refreshSummary()` via `tap(...)` after mutating calls):
     - `refreshSummary(): void`
     - `getSummary(): Observable<IngestionQueueSummaryDto>`
     - `getJobs(status?: IngestionJobStatus): Observable<IngestionJobSummaryDto[]>`
     - `getJobById(id: string): Observable<IngestionJobDetailDto>`
     - `enqueueUrls(request: EnqueueWebUrlIngestionRequest): Observable<IngestionJobSummaryDto[]>`
     - `enqueueRawText(request: EnqueueRawTextIngestionRequest): Observable<IngestionJobSummaryDto>`
     - `saveDraft(id: string, draft: IngestionExtractedDraftDto): Observable<IngestionJobDetailDto>`
     - `approveAndPublish(id: string, draft: IngestionExtractedDraftDto): Observable<RecipeDetailDto>`
     - `retryJob(id: string): Observable<IngestionJobSummaryDto>`
     - `retryAllWaitingJobs(): Observable<RetryWaitingJobsResultDto>`
     - `discardJob(id: string): Observable<void>`

### 4.2 Feature Components, Navigation Badge & Routes (`src/app/features/`)

> **Strict Per-Component Directory Rule:** Every component below resides in its own dedicated kebab-case directory with external `.component.ts`, `.component.html`, `.component.scss`, and `.component.spec.ts` files. Never inline `template` or `styles`.

1. **Shell Navigation & Badge (`features/shell/app-shell/`)**:
   - Inject `IngestionService` and `LlmSettingsService` in `AppShellComponent`; call `this.ingestionService.refreshSummary()` in `ngOnInit()`.
   - Add `Ingest` navigation link (`routerLink="/ingest"`, `<mat-icon>move_to_inbox</mat-icon>`, label `Ingest`) to both `nav.desktop-nav` and `nav.mobile-bottom-nav`, displaying a pill/circle `.inbox-count-badge` whenever `ingestionService.reviewInboxCount() > 0` (e.g., `aria-label="Ingest and Review Inbox"`).
   - Add `<button mat-menu-item (click)="llmSettingsService.openLlmSettingsDialog()"><mat-icon>smart_toy</mat-icon><span>LLM Provider Settings</span></button>` inside `userMenu` above `Account Settings`.
   - In `app-shell.component.scss`, refine `.mobile-bottom-nav .mobile-tab` (`box-sizing: border-box; padding: 0.35rem 0.5rem; min-width: 58px; position: relative;`) so all 4 tabs (`Cookbook`, `Ingest`, `Matcher`, `Catalog`) fit cleanly within a `360px` mobile viewport with zero horizontal overflow.
   - Update `app-shell.component.spec.ts` with mock `IngestionService` and `LlmSettingsService` providers.

2. **`LlmSettingsDialogComponent` (`features/ingestion/llm-settings-dialog/`)**:
   - Typed Reactive Form (`FormBuilder.nonNullable`): `provider` (`'Ollama'` | `'OpenAiCompatible'`), `baseUrl`, `apiKey`, `clearApiKey`, `textModel`, `visionModel`, `ollamaKeepAlive`, `requestTimeoutSeconds`, `autoPublishHighConfidenceText`.
   - **"Test Connection & Refresh Models"** button (`aria-label="Test LLM connection and refresh models"`):
     - Calls `llmSettingsService.testConnection(...)`, displays `.connection-status-banner` (`role="status"`): green success state showing latency + model count (`Connected in 18 ms • 3 models available`) or warning state showing the offline/error detail.
     - Populates `discoveredModels = signal<string[]>([])` feeding `<mat-autocomplete>` options on both the **Text Model** and **Vision Model** `<input matInput>` fields so users can either pick a discovered model or type any model identifier manually when the GPU host is asleep.
   - Saves via `llmSettingsService.updateSettings(...)`, closes dialog, and triggers `ingestionService.refreshSummary()`.

3. **`IngestionHubComponent` (`features/ingestion/ingestion-hub/`, route `/ingest`)**:
   - **Header Bar:** Page title `"Recipe Ingestion & Review Inbox"`, subtitle, and **"LLM Settings"** button (`(click)="openLlmSettings()"`).
   - **Quick Capture Card (`.capture-card`):**
     - Segmented channel toggle (`Web Recipe URLs` | `Paste Raw Text / Markdown`).
     - **Web URL Mode:** `<textarea formControlName="urlsText" rows="3">` accepting 1–20 URLs (one per line) + `"Queue URL(s)"` submit button (`aria-label="Queue recipe URLs"`). Clears immediately on `202 Accepted` so rapid batch pasting takes `< 1s`.
     - **Raw Text Mode:** Optional `titleHint` input + `<textarea formControlName="rawText" rows="6">` for pasted recipes/markdown/chat transcripts + `"Extract Recipe"` submit button (`aria-label="Queue raw text recipe"`).
   - **Offline Host Resilience Banner (`.waiting-host-banner`):**
     - Shown whenever `waitingJobs().length > 0` (`role="status"`): explains that jobs are safely persisted waiting for the LAN LLM host (with all scraped HTML & photos preserved), with **"LLM Settings"** and **"Retry All Waiting Jobs"** (`aria-label="Retry all waiting jobs"`) buttons.
   - **Inbox & Queue Filter Strip + Job List:**
     - Filter pills: `All`, `Review Inbox (N)`, `Active & Waiting (N)`, `Published`, `Failed`.
     - Auto-polls `loadJobs()` + `ingestionService.refreshSummary()` every `2000ms` (cleaned up via `DestroyRef`) **only** while at least one job is in `Queued`, `Preprocessing`, or `Extracting`.
     - Each `.job-card` shows channel icon, `displayTitle`, `sourceUrl` (if URL job), status badge, confidence chip (when extracted), `statusDetail`, thumbnail (if present), and context actions:
       - `ReadyForReview` $\rightarrow$ primary `a[mat-flat-button]` **"Review Side-by-Side"** (`[routerLink]="['/ingest/review', job.id]"`).
       - `Published` $\rightarrow$ `a[mat-stroked-button]` **"Open Recipe"** (`[routerLink]="['/recipes', job.resultingRecipeId]"`).
       - `WaitingForLlmHost` / `Failed` $\rightarrow$ **"Retry"** button + **"Discard"** button.

4. **`IngestionReviewComponent` (`features/ingestion/ingestion-review/`, route `/ingest/review/:id`)**:
   - **Responsive Side-by-Side Split Layout:**
     - Desktop (`> 960px`): 2-column grid (`.review-split-grid`: left sticky source inspector pane `42%`, right structured draft editor `58%`).
     - Mobile (`<= 960px`, tested at `360px`): Segmented pane switcher (`.mobile-pane-switcher`: `Original Source` | `Structured Draft`) toggling `.pane-hidden-mobile` so `360px` screens never horizontally scroll or stack awkwardly long panes.
   - **Left Pane — Original Source Inspector (`.source-inspector-pane`):**
     - Source metadata card (`SourceUrl` external link, `SourceName`, `SourceAuthor`, `ConfidenceScore` percentage badge, downloaded cover image preview).
     - Collapsible/scrollable **Extracted Raw Ingredient Lines** list (when `scrapedPage?.rawIngredientLines?.length`) and **Source Text / Cleaned Page Text** (`normalizedSourceText || rawText`) in a readable container for line-by-line verification.
   - **Right Pane — Structured Recipe Draft Editor (`.draft-editor-pane`):**
     - Loads `StoreAisleDto[]`, `FoodItemDto[]`, `CollectionDto[]`, and `TagDto[]` on init.
     - Editable Reactive Form pre-populated from `job.extractedDraft`:
       - Core fields: `title`, `description`, `baseServings`, `yieldDisplay`, `prepTimeMinutes`, `cookTimeMinutes`, `totalTimeMinutes`, `difficulty`, `sourceName`, `sourceAuthor`, `sourceUrl`, cover image preview + optional replace upload via `MediaService`.
       - Taxonomy chips/selectors: `Collections` and `Suggested Tags` (pre-toggled from `suggestedTagIds`).
       - **Ingredients FormArray with Canonical `FoodItem` & New-Item Aisle Highlighting:**
         - Displays `originalRawText` reference caption above each ingredient row.
         - If `matchedFoodItemId` is set: displays `.matched-food-pill` (`Linked: <FoodItemName>`) with option to change existing `FoodItem`.
         - If `isNewFoodItem` (`!matchedFoodItemId`): highlights row with `.new-food-item-banner` badge (`New Food Item`), editable `proposedFoodItemName` input, AI-suggested **Grocery Aisle** `<mat-select formControlName="suggestedStoreAisleId">`, and `Pantry Staple` checkbox (`suggestedIsPantryStaple`).
         - Standard fields: `sectionName`, `quantity`, `maxQuantity`, `unit` (`MEASUREMENT_UNITS`), `customUnitName`, `displayName`, `preparationNote`, `isOptional`, plus Add/Remove ingredient buttons.
       - **Steps FormArray with Timers & Ingredient Callouts:**
         - Editable `sectionName`, `instruction`, `ingredientIndices` `<mat-select multiple>` referencing draft ingredient labels, and nested `timers` (`minutes`, `seconds`, `label`) pre-populated from extraction + `StepTimerExtractor`.
   - **Review Action Bar:**
     - **"Approve & Publish"** (`button[aria-label='Approve and publish recipe']`) $\rightarrow$ calls `ingestionService.approveAndPublish(job.id, draft)` and navigates to `['/recipes', createdRecipe.id]`.
     - **"Save Draft"** (`button[aria-label='Save review draft']`) $\rightarrow$ calls `ingestionService.saveDraft(job.id, draft)`.
     - **"Discard"** (`button[aria-label='Discard ingestion job']`) $\rightarrow$ confirms via `ConfirmDialogComponent`, calls `ingestionService.discardJob(job.id)`, and returns to `/ingest`.

5. **Routes (`Client/src/app/app.routes.ts`)**:
   - Add under authenticated `AppShellComponent` `children`:
     ```ts
     {
       path: 'ingest',
       loadComponent: () =>
         import('./features/ingestion/ingestion-hub/ingestion-hub.component').then(
           (m) => m.IngestionHubComponent,
         ),
     },
     {
       path: 'ingest/review/:id',
       loadComponent: () =>
         import('./features/ingestion/ingestion-review/ingestion-review.component').then(
           (m) => m.IngestionReviewComponent,
         ),
     },
     ```

---

## 5. Playwright E2E Test Scenarios (`Tests/Client.E2ETests/LlmIngestionE2ETests.cs`)

Create `public sealed class LlmIngestionE2ETests : E2ETestBase` accompanied by an in-process lightweight loopback mock HTTP server (`MockLanLlmAndBlogServer : IAsyncDisposable` inside `LlmIngestionE2ETests.cs` listening on a free `127.0.0.1:<port>` via `WebApplication.CreateSlimBuilder` or `HttpListener`) that serves:
- Mock Ollama endpoints (`GET /api/tags` returning `["qwen2.5:14b", "qwen2.5vl:7b"]` and `POST /api/chat` returning structured `LlmExtractedRecipePayload` JSON, plus a toggleable `SimulateOffline` boolean returning `503 Service Unavailable` to test `WaitingForLlmHost` resilience!).
- Mock Recipe Blog page (`GET /blog/crispy-sesame-tofu` with `schema.org/Recipe` JSON-LD + valid `1x1` PNG at `GET /blog/tofu.png`).

Implement these required `[TestMethod]` scenarios:

1. `[TestMethod] LlmSettingsAndUrlIngestion_ConfigureOllamaScrapeUrlReviewSideBySideAndPublish_CreatesRecipeAndNewFoodItem()`:
   - Registers & logs in; opens **LLM Provider Settings** dialog, enters the mock Ollama server URL, clicks **"Test Connection & Refresh Models"**, verifies `"Connected"` banner and discovered model options (`qwen2.5:14b`), selects `qwen2.5:14b` for Text Model and `qwen2.5vl:7b` for Vision Model, and saves.
   - Navigates to `/ingest`, submits the mock recipe blog URL (`/blog/crispy-sesame-tofu`), verifies the job transitions to `ReadyForReview` and increments the shell `Ingest` `.inbox-count-badge`.
   - Clicks **"Review Side-by-Side"** (`/ingest/review/:id`), verifies the left source inspector displays the scraped URL + raw ingredient lines, verifies the right draft editor highlights a **New Food Item** (`"Toasted Sesame Oil"`) with its AI-suggested grocery aisle (`Spices & Baking`) alongside a matched seeded `FoodItem` (`"Garlic"`), edits the title or note, and clicks **"Approve & Publish"**.
   - Verifies navigation to `/recipes/:id` with downloaded WebP cover photo, scaled ingredients, detected step timer badge, decremented inbox badge, and verifies `"Toasted Sesame Oil"` now appears in `/catalog`.

2. `[TestMethod] OfflineLlmHostResilience_RawTextQueuesAsWaitingForHost_ThenRetryAllWaitingCompletesWhenHostOnline()`:
   - Configures LLM Settings pointing to the mock server, then flips `mockServer.SimulateOffline = true`.
   - Submits a Raw Text recipe on `/ingest`; verifies the job transitions to `WaitingForLlmHost` and renders `.waiting-host-banner` without failing or losing the pasted text.
   - Flips `mockServer.SimulateOffline = false`, clicks **"Retry All Waiting Jobs"**, verifies the job transitions to `ReadyForReview`, opens review, and approves/discards cleanly.

3. `[TestMethod] IngestionAndReviewInbox_MobileViewport_360px_SwitchesSourceAndDraftPanesCleanly()`:
   - Resizes viewport to `360x640`, verifies the 4-tab `.mobile-bottom-nav` (`Cookbook`, `Ingest` + badge, `Matcher`, `Catalog`) fits within `360px` (`scrollWidth <= 360`), navigates to `/ingest/review/:id` on a ready review job, and verifies the mobile `Original Source` | `Structured Draft` pane toggle switches cleanly between source text and draft form without horizontal overflow.

---

## 6. Step-by-Step Execution Checklist (For Implementation Agent)

- [x] **Task 1: Backend Core, Infrastructure & Data Layer + EF Migration**
  - Create `Core/Ingestion/` enums, `LlmHostUnavailableException`, options/DTOs, `StepTimerExtractor`, `RecipeDraftNormalizer`, and service interfaces (`ILlmSettingsService`, `ILlmExtractionClient`, `IWebRecipeScraper`, `IIngestionQueueSignal`, `IIngestionProcessor`, `IIngestionService`).
  - Create `Data/Entities/HouseholdLlmSettings.cs` and `Data/Entities/IngestionJob.cs`, update `Data/Entities/Recipe.cs`, `Data/AppDbContext.cs`, `Data/Configurations/`, `Data/Seeding/HouseholdCatalogSeeder.cs`, `Data/Services/RecipeService.cs`, and implement `LlmSettingsService`, `IngestionService`, and `IngestionProcessor` in `Data/Services/`. Register in `DataServiceCollectionExtensions.cs`.
  - Implement `IngestionQueueSignal`, `WebRecipeScraper`, `LlmExtractionClient`, and `IngestionBackgroundWorker` in `Infrastructure/Ingestion/`, register in `InfrastructureServiceCollectionExtensions.cs`, and add `"Llm"` defaults to `API/appsettings.json` and `API/appsettings.Development.json`.
  - Ensure local SQL Server is running, then generate, apply, and format the EF Core migration:
    ```powershell
    .\scripts\ensure-local-infra.ps1
    dotnet ef migrations add AddLlmSettingsAndIngestionQueue --project Data/App.Data.csproj
    dotnet ef database update --project Data/App.Data.csproj
    dotnet csharpier format Data/Migrations
    ```
  - Write and run unit tests in `Tests/Core.UnitTests` and `Tests/Infrastructure.UnitTests`:
    ```powershell
    dotnet test --project Tests/Core.UnitTests/App.Core.UnitTests.csproj
    dotnet test --project Tests/Infrastructure.UnitTests/App.Infrastructure.UnitTests.csproj
    ```

- [x] **Task 2: API Controllers & Integration Tests**
  - Create thin HTTP controllers `API/Controllers/LlmSettingsController.cs` and `API/Controllers/IngestionJobsController.cs`.
  - Update `Tests/API.IntegrationTests/AppWebApplicationFactory.cs` and `Tests/API.IntegrationTests/OpenApiIntegrationTests.cs`.
  - Write comprehensive integration tests in `Tests/API.IntegrationTests/LlmSettingsIntegrationTests.cs` (API key masking, preservation, clear, Ollama + OpenAI `/v1` connection probe, validation errors) and `Tests/API.IntegrationTests/IngestionIntegrationTests.cs` (URL batch + raw text enqueue, `WaitingForLlmHost` offline transition & retry, `ReadyForReview` draft save & approve creating new `FoodItem`s + `Recipe`, `AutoPublishHighConfidenceText` flow, discard cleanup, and auth boundaries).
  - Run integration tests until 100% green:
    ```powershell
    dotnet test --project Tests/API.IntegrationTests/App.API.IntegrationTests.csproj
    ```

- [x] **Task 3: Angular Client & Unit Tests**
  - Create `Client/src/app/core/models/ingestion.models.ts`, `LlmSettingsService` (+ `.spec.ts`), and `IngestionService` (+ `.spec.ts`).
  - Create dedicated per-component directories (`llm-settings-dialog/`, `ingestion-hub/`, `ingestion-review/`) under `Client/src/app/features/ingestion/` with strictly separate `.ts`, `.html`, `.scss`, and `.spec.ts` files.
  - Update `AppShellComponent` (+ `.html`, `.scss`, `.spec.ts`), `RecipeListComponent` (+ `.html`, `.scss`), and `app.routes.ts`.
  - Run frontend lint, production build, and unit tests in `Client/` until 100% green:
    ```powershell
    # In Client/:
    npm run lint
    npm run build:production
    npm test -- --watch=false
    ```

- [x] **Task 4: Playwright E2E Verification & Final Formatting**
  - Implement `Tests/Client.E2ETests/LlmIngestionE2ETests.cs` covering all 3 scenarios in Section 5 using an in-process loopback mock LLM + Recipe Blog server.
  - Build `Client/` (`npm run build:production`) and `App.slnx` (`dotnet build App.slnx`), then run Playwright E2E tests:
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
  - Run full solution test suite (`dotnet test App.slnx`) and verify 100% green.
  - Mark this plan's status header as `[x] Completed`.
