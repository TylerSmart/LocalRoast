# Phase 16: LLM Settings Modal Tabs & Database-Backed Model Profile Management

> **Branch:** `feature/phase-16-llm-settings-tabs-and-model-profiles`
> **Status:** `[ ]` Not Started
> **PRD Reference:** `docs/prd.md` — Section 4.2.4; `docs/todo.md` — Various items 5, 6
> **Target Execution:** Run `/execute-phase` to execute this plan step-by-step.
> **Live Environment Note:**
> - The local app (`http://localhost:4200`) and SQL Server (`localhost,14333` / container `local-roast-sqlserver`, DB `LocalRoast`) already contain the 1,562 imported recipes. Do not wipe or drop the database.
> - The Ollama server runs at `http://ThinkStationP3:11434`. **Hardware Constraint:** Only ever send **1 LLM request at a time**.

---

## 1. Scope & Objectives

1. **Organize the LLM Settings Modal into Tabs:**
   - Refactor `Client/src/app/features/ingestion/llm-settings-dialog/` from a single 22-control scrolling form into a structured `<mat-tab-group>` with 4 focused tabs:
     1. **Connection & Models:** Provider, Base URL, API Key, Test Connection banner, Text Model, Vision Model, and Quick-Apply Profile selector.
     2. **Sampling & Runtime:** Context Window (`contextWindowTokens`), Enable Thinking toggle, Temperature, Top P, Top K, Request Timeout, Ollama Keep-Alive, and CPU-Only toggle.
     3. **Refinement & Queue:** Enable Ingestion toggle, Auto-Publish High-Confidence toggle, Max Retries, Refine Model selector + Quick-Apply Profile, Refine Thinking/Temperature/TopP/TopK/Timeout.
     4. **Model Profiles:** Full CRUD interface to view, add, edit, delete, and restore default model profiles, plus a **"Save Current Settings as Profile"** shortcut.
2. **Database-Backed Model Profiles (Seeded Defaults + CRUD + Restore Defaults):**
   - Persist model profiles in a new `LlmModelProfiles` database table (`LlmModelProfile` entity implementing `IAuditable`).
   - Automatically seed the 5 built-in Gemma 4 profiles from `LlmModelProfiles.All` when the table is empty so all profiles (including defaults) can be edited or deleted, and provide a **"Restore Default Profiles"** action (`POST /api/llm-settings/model-profiles/restore-defaults`) that re-seeds/resets the built-in profiles without deleting custom user-added profiles.

---

## 2. Domain & Database Design (`Core/` & `Data/`)

### 2.1 Entities (`Data/Entities/LlmModelProfile.cs`)

```csharp
using App.Core.Common;

namespace App.Data.Entities;

public sealed class LlmModelProfile : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Slug { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ModelTag { get; set; } = string.Empty;
    public string MatchPrefixesJson { get; set; } = "[]";
    public string Summary { get; set; } = string.Empty;
    public bool EnableThinking { get; set; }
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public int? TopK { get; set; }
    public int ContextWindowTokens { get; set; } = 32768;
    public int RequestTimeoutSeconds { get; set; } = 600;
    public bool RecommendedAsRefineModel { get; set; }
    public string NotesJson { get; set; } = "[]";
    public int SortOrder { get; set; }
    public bool IsBuiltInDefault { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTimeOffset ModifiedAt { get; set; }
    public string ModifiedBy { get; set; } = string.Empty;
}
```

### 2.2 EF Core Fluent Configuration (`Data/Configurations/LlmModelProfileConfiguration.cs`)

- Table `LlmModelProfiles`, primary key `Id`.
- Unique index on `Slug` (`HasMaxLength(100)`).
- `DisplayName`: required, `HasMaxLength(150)`.
- `ModelTag`: required, `HasMaxLength(200)`.
- `Summary`: required, `HasMaxLength(500)`.
- `MatchPrefixesJson`: required, `HasMaxLength(1000)`.
- `NotesJson`: required, `HasMaxLength(4000)`.
- `CreatedBy` / `ModifiedBy`: required, `HasMaxLength(256)`.
- Register `DbSet<LlmModelProfile> LlmModelProfiles` in `AppDbContext`.
- Migration:
  ```powershell
  dotnet ef migrations add AddLlmModelProfilesTable --project Data/App.Data.csproj
  ```

### 2.3 DTOs, Requests & Service Contracts (`Core/Ingestion/LlmModelProfiles.cs`, `LlmSettingsModels.cs`, `ILlmSettingsService.cs`)

- Keep `LlmModelProfileDto` in `Core/Ingestion/LlmModelProfiles.cs` (`Id` remains `string` — storing the profile's `Slug` or `Guid.ToString()` so existing frontend profile selectors work seamlessly).
- Add `UpsertLlmModelProfileRequest` in `Core/Ingestion/LlmSettingsModels.cs`:
  ```csharp
  public record UpsertLlmModelProfileRequest(
      [Required, MaxLength(150)] string DisplayName,
      [Required, MaxLength(200)] string ModelTag,
      IReadOnlyList<string>? MatchPrefixes = null,
      [MaxLength(500)] string? Summary = null,
      bool EnableThinking = false,
      [Range(0.0, 2.0)] double? Temperature = null,
      [Range(0.0, 1.0)] double? TopP = null,
      [Range(1, 1000)] int? TopK = null,
      [Range(2048, 65536)] int ContextWindowTokens = 32768,
      [Range(5, 1800)] int RequestTimeoutSeconds = 600,
      bool RecommendedAsRefineModel = false,
      IReadOnlyList<string>? Notes = null
  );
  ```
- Add methods to `ILlmSettingsService` (`Core/Ingestion/ILlmSettingsService.cs`):
  ```csharp
  Task<IReadOnlyList<LlmModelProfileDto>> GetModelProfilesAsync(CancellationToken cancellationToken = default);
  Task<LlmModelProfileDto> CreateModelProfileAsync(UpsertLlmModelProfileRequest request, CancellationToken cancellationToken = default);
  Task<LlmModelProfileDto> UpdateModelProfileAsync(string id, UpsertLlmModelProfileRequest request, CancellationToken cancellationToken = default);
  Task DeleteModelProfileAsync(string id, CancellationToken cancellationToken = default);
  Task<IReadOnlyList<LlmModelProfileDto>> RestoreDefaultModelProfilesAsync(CancellationToken cancellationToken = default);
  ```

### 2.4 Service Business Logic (`Data/Services/LlmSettingsService.cs`)

- **Seeding:** Add `EnsureModelProfilesSeededAsync(CancellationToken)`:
  - Use a boolean flag on `HouseholdLlmSettings` (or seed whenever `LlmModelProfiles` is empty on first startup / via migration data seed) so if all profiles are deleted intentionally by the user, they are not unwantedly re-added until `RestoreDefaultModelProfilesAsync` is called; on initial migration or when no profiles have ever been seeded, insert the 5 built-in profiles from `LlmModelProfiles.All` with `Slug = profile.Id`, `IsBuiltInDefault = true`, and `SortOrder = 0..4`.
- **CRUD Operations:**
  - `GetModelProfilesAsync`: returns all rows ordered by `SortOrder`, then `DisplayName`, mapped to `LlmModelProfileDto` (using `Slug` as `Id`).
  - `CreateModelProfileAsync`: generates a unique kebab-case `Slug` from `DisplayName` (appending `-2`, `-3` if taken), validates inputs, saves to `LlmModelProfiles`, and returns `LlmModelProfileDto`.
  - `UpdateModelProfileAsync(string id, ...)`: finds profile by `Slug == id` (or `Guid.TryParse(id, out var g) && Id == g`), updates all fields, and returns `LlmModelProfileDto` (throws `NotFoundException` if not found).
  - `DeleteModelProfileAsync(string id, ...)`: finds profile by `Slug` or `Id` and removes it (throws `NotFoundException` if not found).
  - `RestoreDefaultModelProfilesAsync`: for each built-in profile in `LlmModelProfiles.All`, upserts by `Slug` (restoring default values and `SortOrder`), keeping any custom user-created profiles intact, and returns the updated list.

---

## 3. API Endpoints (`API/Controllers/LlmSettingsController.cs`)

| Verb | Route (`/api/llm-settings/...`) | Request Body | Response | Error Cases |
| :--- | :--- | :--- | :--- | :--- |
| `GET` | `/model-profiles` | None | `200 IReadOnlyList<LlmModelProfileDto>` | `401` |
| `POST` | `/model-profiles` | `UpsertLlmModelProfileRequest` | `201 LlmModelProfileDto` | `400`, `401` |
| `PUT` | `/model-profiles/{id}` | `UpsertLlmModelProfileRequest` | `200 LlmModelProfileDto` | `400`, `401`, `404` |
| `DELETE` | `/model-profiles/{id}` | None | `204 NoContent` | `401`, `404` |
| `POST` | `/model-profiles/restore-defaults` | None | `200 IReadOnlyList<LlmModelProfileDto>` | `401` |

---

## 4. Angular Frontend Design (`Client/`)

### 4.1 Models & Services (`src/app/core/models/ingestion.models.ts` & `src/app/core/services/ingestion.service.ts`)

- Add `UpsertLlmModelProfileRequest` interface in `ingestion.models.ts`.
- Add `createModelProfile`, `updateModelProfile`, `deleteModelProfile`, and `restoreDefaultModelProfiles` methods in `IngestionService`.

### 4.2 Tabbed LLM Settings Modal (`src/app/features/ingestion/llm-settings-dialog/`)

- Import `MatTabsModule` in `llm-settings-dialog.component.ts`.
- Structure `llm-settings-dialog.component.html` inside `<mat-dialog-content>` using `<mat-tab-group animationDuration="150ms">`:
  - **Tab 1: `Connection & Models`**
    - Provider (`provider`), Base URL (`baseUrl`), API Key (`apiKey`, `clearApiKey`), `Test Connection` button & result alert.
    - Text Model (`textModel`), Vision Model (`visionModel`), and Primary Model Profile quick-apply bar (`selectedProfileId` + `Apply`).
    - **"Save Current Settings as Profile"** button that switches to the `Model Profiles` tab with a pre-filled new profile draft from the active form values.
  - **Tab 2: `Sampling & Runtime`**
    - Context Window (`contextWindowTokens` slider + input), `enableThinking` toggle, `temperature`, `topP`, `topK`, `requestTimeoutSeconds`, `ollamaKeepAlive`, and `ollamaCpuOnly` toggle.
  - **Tab 3: `Refinement & Queue`**
    - `enableIngestion` toggle, `autoPublishHighConfidenceText` toggle, `maxIngestionRetries`.
    - Refine Model (`refineModel`), Refine Profile quick-apply bar (`selectedRefineProfileId` + `Apply`), `refineEnableThinking`, `refineTemperature`, `refineTopP`, `refineTopK`, `refineRequestTimeoutSeconds`.
  - **Tab 4: `Model Profiles`**
    - Header bar with **Add Profile** (`add` icon) and **Restore Defaults** (`restore` icon) buttons.
    - Profile cards list showing each profile's `displayName`, `modelTag`, `summary`, parameter chips (`Context`, `Timeout`, `Thinking`, `Temp`/`TopP`/`TopK`, `Refine`), and **Apply to Primary**, **Edit**, and **Delete** action buttons.
    - Inline profile editor form (shown when adding or editing a profile) with fields for `displayName`, `modelTag`, `matchPrefixes` (comma-separated), `summary`, `contextWindowTokens`, `requestTimeoutSeconds`, `enableThinking`, `recommendedAsRefineModel`, `temperature`, `topP`, `topK`, and `notes` (one per line), plus **Save Profile** and **Cancel** buttons.
- Responsive behavior at `360px`:
  - Tab labels scroll horizontally if needed (`mat-stretch-tabs="false"`), and profile cards / form rows stack into a single column with zero horizontal overflow.

---

## 5. Playwright E2E Test Scenarios (`Tests/Client.E2ETests/LlmIngestionE2ETests.cs`)

Add E2E test methods in `LlmIngestionE2ETests.cs`:
- `[TestMethod] LlmSettingsDialog_TabsAndModelProfileCrud_WorksEndToEnd()`:
  - Open LLM Settings dialog, verify all 4 tabs (`Connection & Models`, `Sampling & Runtime`, `Refinement & Queue`, `Model Profiles`) render and switch cleanly.
  - Navigate to `Model Profiles` tab, create a new custom profile (`"Qwen 2.5 14B Custom"`, tag `"qwen2.5:14b"`), verify it appears in the list and in the `Connection & Models` profile dropdown.
  - Edit the custom profile's summary, verify the update persists, then delete a profile and click `Restore Defaults` to verify defaults return.
- `[TestMethod] LlmSettingsDialog_MobileViewport_360px_RendersTabsWithoutOverflow()`:
  - Resize to `360x640`, open LLM Settings dialog, switch across all 4 tabs, and verify no horizontal overflow.

---

## 6. Step-by-Step Execution Checklist (For Implementation Agent)

- [ ] **Task 1: Backend Core, Entity, Migration & Service**
  - Create `Data/Entities/LlmModelProfile.cs` and `Data/Configurations/LlmModelProfileConfiguration.cs`.
  - Register `DbSet<LlmModelProfile>` in `Data/AppDbContext.cs`.
  - Add `UpsertLlmModelProfileRequest` and `ILlmSettingsService` CRUD methods in `Core/Ingestion/`.
  - Implement seeding + CRUD + restore-defaults in `Data/Services/LlmSettingsService.cs` and expose endpoints in `API/Controllers/LlmSettingsController.cs`.
  - Generate, apply, and format EF migration:
    ```powershell
    dotnet ef migrations add AddLlmModelProfilesTable --project Data/App.Data.csproj
    dotnet ef database update --project Data/App.Data.csproj
    dotnet csharpier format Data/Migrations
    ```
  - Write unit and integration tests in `Tests/API.IntegrationTests/LlmSettingsIntegrationTests.cs` covering profile seeding, create, update, delete, and restore-defaults.
  - Run backend tests:
    ```powershell
    dotnet test --project Tests/Core.UnitTests/App.Core.UnitTests.csproj
    dotnet test --project Tests/API.IntegrationTests/App.API.IntegrationTests.csproj
    ```

- [ ] **Task 2: Angular Tabbed Modal & Profile Management UI**
  - Update `ingestion.models.ts` and `ingestion.service.ts`.
  - Refactor `llm-settings-dialog.component.{ts,html,scss}` into the 4-tab layout with inline Model Profile CRUD.
  - Update `llm-settings-dialog.component.spec.ts` to test tab organization and profile CRUD operations.
  - Run frontend lint, build, and unit tests in `Client/`:
    ```powershell
    npm run lint
    npm run build:production
    npm test -- --watch=false
    ```

- [ ] **Task 3: Playwright E2E Verification & Final Formatting**
  - Add E2E tests in `Tests/Client.E2ETests/LlmIngestionE2ETests.cs` and run:
    ```powershell
    dotnet test --project Tests/Client.E2ETests/App.Client.E2ETests.csproj
    ```
  - Format and verify full solution:
    ```powershell
    dotnet csharpier format .
    dotnet test App.slnx
    ```
  - Mark this plan's status as `[x] Completed`.
