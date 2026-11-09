# Phase 15: Ingestion Review UX, Inline Issue Placement, Published Recipe Review & Paused-Queue Actions

> **Branch:** `feature/phase-15-review-ux-published-review-and-queue-actions`
> **Status:** `[x]` Completed
> **PRD Reference:** `docs/prd.md` — Sections 4.2.2, 4.2.3, 4.2.4; `docs/todo.md` — Ingestion items 4, 5 & Various items 1, 7
> **Target Execution:** Run `/execute-phase` to execute this plan step-by-step.
> **Live Environment Note:**
> - The local app (`http://localhost:4200`) and SQL Server (`localhost,14333` / container `local-roast-sqlserver`, DB `LocalRoast`) already contain the 1,562 imported recipes (`761` Published, `801` ReadyForReview). Do not wipe or drop the database.
> - The Ollama server runs at `http://ThinkStationP3:11434`. **Hardware Constraint:** Only ever send **1 LLM request at a time** (no parallel requests, and do not trigger a live batch retry of all 1,562 recipes against `ThinkStationP3` unless the queue is paused or you are testing in the isolated InMemory integration/E2E test host).

---

## 1. Scope & Objectives

1. **Inline Placement of Review Issues on Ingredient & Step Cards:**
   - Map each validation issue, quality warning, and measurement warning to its target `IngredientIndex` (`0..N-1`) or `StepNumber` (`1..M`) and display it **directly inside the corresponding Ingredient row card or Step card** in `ingestion-review`, eliminating back-and-forth scrolling between the top banner and the form.
   - Keep the top review diagnostics card as a compact control bar (`Recheck issues`, `Re-extract with AI`, `Refine with ...`, jump links, and recipe-level issues not tied to a single ingredient/step).
2. **Run Review AI Actions Even When the Ingestion Queue Is Paused:**
   - Allow `"Refine with ..."` (`RequestRefinementAsync` / `RefineNextPendingDraftAsync`) and single-job `"Rerun"` (`RetryJobAsync` with `runNow: true`) to execute immediately even when `EnableIngestion == false` (queue paused) and even when paused `Queued` jobs are waiting in the inbox.
3. **Open and Use the Ingestion Review Page for Published Recipes:**
   - Allow `IngestionJobStatus.Published` jobs to open `/ingest/review/:id`, edit the draft, run `"Recheck issues"`, `"Re-extract with AI"`, and `"Refine with ..."`, promote source pages to cover, and click **"Update Published Recipe"** to update the linked `ResultingRecipeId` in place.
   - Show the `"Review Side-by-Side"` button on `Published` jobs in `ingestion-hub` alongside `"Open Recipe"`.
4. **Fix `"Retry All"` in Ingestion Hub Across All Pages (`1562` Items):**
   - Pass `{ jobIds: [], filter: activeFilter(), search: searchQuery() }` from `ingestion-hub` and apply the full `Filter` + `Search` query predicate in `IngestionService.RetryBatchJobsAsync` across all matching rows in the database instead of only the 25 items on the current page.

---

## 2. Domain & Database Design (`Core/`, `Data/` & `Infrastructure/`)

### 2.1 DTOs & Contracts (`Core/Ingestion/IngestionModels.cs` & `IIngestionService.cs`)

- **Update `RetryBatchJobsRequest` (`Core/Ingestion/IngestionModels.cs`):**
  ```csharp
  public record RetryBatchJobsRequest(
      IReadOnlyList<Guid>? JobIds = null,
      [MaxLength(50)] string? Filter = null,
      [MaxLength(200)] string? Search = null
  );
  ```
- **Add `IngestionDiagnosticTargetDto` & update `IngestionReviewDiagnosticsDto` (`Core/Ingestion/IngestionModels.cs`):**
  ```csharp
  public record IngestionDiagnosticTargetDto(
      string Message,
      string Severity, // "error" | "warning"
      int? IngredientIndex = null,
      int? StepNumber = null
  );

  public record IngestionReviewDiagnosticsDto(
      IReadOnlyList<string> ValidationIssues,
      IReadOnlyList<string> QualityWarnings,
      IReadOnlyList<IngestionDiagnosticTargetDto>? TargetedDiagnostics = null
  );
  ```

### 2.2 Service Business Logic

1. **Structured Target Mapping (`Data/Services/IngestionReviewService.cs` & `Core/Ingestion/RecipeExtractionDiagnostics.cs`):**
   - In `IngestionReviewService.Diagnose(IngestionExtractedDraftDto draft, string? source)`:
     - Build `TargetedDiagnostics` for every item in `ValidationIssues` (`Severity = "error"`) and `QualityWarnings` (`Severity = "warning"`):
       - Match `^Step\s+(?<step>\d+)\b` $\rightarrow$ `StepNumber = int.Parse(...)`.
       - Match `^Ingredient\s+(?<ing>\d+)\b` $\rightarrow$ `IngredientIndex = int.Parse(...)`.
       - Otherwise, if the message starts with `^(?<food>[^:]+):`, match `food` against `draft.Ingredients` (`DisplayName` or `ProposedFoodItemName` or `MatchedFoodItemName`, case-insensitive) $\rightarrow$ `IngredientIndex = matchedIndex`.

2. **Paused-Queue Execution (`Infrastructure/Ingestion/IngestionBackgroundWorker.cs`, `Data/Services/IngestionReviewService.cs`, `Data/Services/IngestionService.cs`):**
   - **`IngestionBackgroundWorker.cs` (`lines 70–102`):**
     - Remove the early `if (!ingestionEnabled && !hasRunNow) { await queueSignal.WaitForWorkAsync(...); continue; }` block before `reviewService.RefineNextPendingDraftAsync(stoppingToken)` so pending refinements are checked on every worker cycle even when `ingestionEnabled == false`.
   - **`IngestionReviewService.cs` (`RefineNextPendingDraftAsync` & `HasRunnableImportAsync`, `lines 226–239, 390–399`):**
     - Remove `!config.EnableIngestion` from the guard in `RefineNextPendingDraftAsync` (only check `string.IsNullOrWhiteSpace(config.RefineModel) || await HasRunnableImportAsync(config.EnableIngestion, cancellationToken)`).
     - Update `HasRunnableImportAsync(bool ingestionEnabled, CancellationToken cancellationToken)`:
       ```csharp
       var now = DateTimeOffset.UtcNow;
       return await dbContext.IngestionJobs.AnyAsync(
           item =>
               item.Status == IngestionJobStatus.Queued
               && (item.NextRetryAt == null || item.NextRetryAt <= now)
               && (ingestionEnabled || item.RunNowRequested),
           cancellationToken
       );
       ```
       This ensures paused `Queued` jobs never block background refinements when `ingestionEnabled == false`.
   - **Single-Job Rerun When Paused (`Data/Services/IngestionService.cs` `RetryJobAsync`):**
     - Add optional `bool runNow = false` parameter to `IIngestionService.RetryJobAsync(Guid id, bool runNow = false, CancellationToken cancellationToken = default)` (or set `job.RunNowRequested = true` in `RetryJobAsync` whenever a single job is retried via `POST /api/ingestion/jobs/{id}/retry`!). Setting `job.RunNowRequested = true` in `RetryJobAsync` ensures any explicit single-job retry/rerun by the user runs immediately even when bulk queue processing is paused.

3. **Published Recipe Support in Ingestion Review (`Data/Services/IngestionService.cs` & `Data/Services/IngestionReviewService.cs`):**
   - Create helper `private static bool CanEditOrReview(IngestionJobStatus status) => status is IngestionJobStatus.ReadyForReview or IngestionJobStatus.Published;`.
   - **`IngestionService.SaveDraftAsync` (`line 1151`):** Allow `CanEditOrReview(job.Status)` (only throw `ConflictException` if `job.Status is not (IngestionJobStatus.ReadyForReview or IngestionJobStatus.Published)`).
   - **`IngestionService.ApproveAndPublishAsync` (`line 1197`):** Allow `CanEditOrReview(job.Status)`. (Lines 1358–1383 already update `job.ResultingRecipeId` in place when `job.ResultingRecipeId.HasValue`!)
   - **`IngestionService.PromoteSourcePageToCoverAsync` (`line 852`):** Allow `CanEditOrReview(job.Status)`. If `job.ResultingRecipeId.HasValue`, also update `CoverImagePath` and `ThumbnailImagePath` on the linked `Recipe` entity.
   - **`IngestionReviewService.cs` (`PreviewRepairAsync`, `RequestRefinementAsync`, `PreviewRefinementAsync`, `RefineNextPendingDraftAsync`):** Replace `job.Status != IngestionJobStatus.ReadyForReview` checks with `!CanEditOrReview(job.Status)`.

4. **Batch `"Retry All"` Across All Pages (`Data/Services/IngestionService.cs` `RetryBatchJobsAsync`, `lines 1563–1624`):**
   - Update `IIngestionService.RetryBatchJobsAsync(RetryBatchJobsRequest request, CancellationToken cancellationToken = default)`:
     - Exclude the currently active job (`var activeId = queueSignal.ActiveJobId; if (activeId.HasValue) query = query.Where(j => j.Id != activeId.Value);`).
     - If `request.JobIds is { Count: > 0 }`, filter by `request.JobIds`.
     - Otherwise, apply the `request.Filter` category predicate (`Review` $\rightarrow$ `ReadyForReview`; `Active` $\rightarrow$ `Queued | Preprocessing | Extracting`; `Waiting` $\rightarrow$ `WaitingForLlmHost`; `Failed` $\rightarrow$ `Failed`; `Published` $\rightarrow$ `Published`; `All` $\rightarrow$ all non-`Discarded`) **AND** the `request.Search` predicate (matching `DisplayTitle`, `SourceUrl`, `StatusDetail`, `ExtractedModel`, or `ResultingRecipe.Title`, identical to `GetJobsPagedAsync` lines 97–107).

---

## 3. API Endpoints (`API/Controllers/IngestionJobsController.cs`)

| Verb | Route (`/api/ingestion/jobs/...`) | Request Body / Query | Response | Behavior Change |
| :--- | :--- | :--- | :--- | :--- |
| `POST` | `/retry-batch` | `RetryBatchJobsRequest` (`jobIds`, `filter`, `search`) | `200 { retriedCount }` | Retries all jobs matching `filter` + `search` across all pages when `jobIds` is empty |
| `POST` | `/{id:guid}/retry` | None | `200 IngestionJobSummaryDto` | Sets `RunNowRequested = true` so single-job Rerun works when paused |
| `PUT` | `/{id:guid}/draft` | `IngestionExtractedDraftDto` | `200 IngestionJobDetailDto` | Accepts both `ReadyForReview` and `Published` jobs |
| `POST` | `/{id:guid}/publish` | `ApproveIngestionJobRequest` | `200 RecipeDetailDto` | Updates linked recipe in place when job is already `Published` |
| `POST` | `/{id:guid}/repair-preview` | `IngestionRepairPreviewRequest` | `200 IngestionRepairPreviewDto` | Accepts both `ReadyForReview` and `Published` jobs |
| `POST` | `/{id:guid}/refinement` | None | `200 IngestionRefinementDto` | Accepts both `ReadyForReview` and `Published` jobs; runs even when queue is paused |

---

## 4. Angular Frontend Design (`Client/`)

### 4.1 Models & Services (`src/app/core/models/ingestion.models.ts` & `src/app/core/services/ingestion.service.ts`)

- Add `IngestionDiagnosticTargetDto` interface and `targetedDiagnostics?: IngestionDiagnosticTargetDto[]` to `IngestionReviewDiagnosticsDto`.
- Update `IngestionService.retryBatchJobs(jobIds: string[], filter?: string, search?: string)` to include `search` in the POST body.

### 4.2 Feature Components (`src/app/features/ingestion/`)

1. **`ingestion-review/ingestion-review.component.{ts,html,scss}`:**
   - **Inline Diagnostics per Ingredient and Step:**
     - Add computed signals `ingredientIssuesMap = computed(() => ...)` (`Map<number, IngestionDiagnosticTargetDto[]>`) and `stepIssuesMap = computed(() => ...)` (`Map<number, IngestionDiagnosticTargetDto[]>`) combining `reviewDiagnostics()?.targetedDiagnostics` (or client-side fallback regex matching `Step (\d+)` / `Ingredient (\d+)` / ingredient `displayName`) AND `measurementWarnings()`.
     - Add `generalDiagnostics = computed(() => ...)` for any issues without an `ingredientIndex` or `stepNumber`.
     - Inside each ingredient row card in `ingestion-review.component.html` (`@for (ing of ingredients.controls; track $index; let i = $index)`), render `@if (ingredientIssuesMap().get(i); as issues)` as an inline `.inline-diagnostic-banner` (error/warning icon + message) at the top of that ingredient card.
     - Inside each step card in `ingestion-review.component.html` (`@for (step of steps.controls; track $index; let s = $index)`), render `@if (stepIssuesMap().get(s + 1); as issues)` as an inline `.inline-diagnostic-banner` right above the step instruction editor.
     - Keep the top `.review-diagnostics-card` as a summary/action bar showing total issue counts, clickable jump chips (`Ingredient #3`, `Step 2`) that smooth-scroll to the card, and the AI buttons (`Recheck issues`, `Re-extract with AI`, `Refine with ...`).
   - **Published Job Support:**
     - Add helper `canEditReview = computed(() => { const s = this.job()?.status; return s === 'ReadyForReview' || s === 'Published'; })`.
     - Replace `job()?.status !== 'ReadyForReview'` checks on `fixWithAi()`, `requestRefinement()`, `openRefinementPreview()`, `saveDraft()`, and `approveAndPublish()` with `!this.canEditReview()`.
     - When `job()?.status === 'Published'`, change the primary publish button label to `"Update Published Recipe"` and show an `"Open Published Recipe"` link in the header.
2. **`ingestion-hub/ingestion-hub.component.{ts,html}`:**
   - In `ingestion-hub.component.html`:
     - Change the Retry All button label from `'Retry All (' + filteredJobs().length + ')'` to `'Retry All (' + totalCount() + ')'`.
     - For `job.status === 'Published'`, render the `"Review Side-by-Side"` button (`[routerLink]="['/ingest/review', job.id]"`) next to `"Open Recipe"`.
   - In `ingestion-hub.component.ts` (`retryAllInCurrentTab()`):
     - Call `this.ingestionService.retryBatchJobs([], currentFilter, this.searchQuery().trim() || undefined)` and update the confirmation prompt to use `this.totalCount()`.

---

## 5. Playwright E2E Test Scenarios (`Tests/Client.E2ETests/LlmIngestionE2ETests.cs`)

Add/update E2E test methods in `LlmIngestionE2ETests.cs`:
- `[TestMethod] IngestionReview_DisplaysInlineIssuesOnIngredientAndStepCards()`:
  - Open review for a job with an ingredient issue and a step issue; verify `.inline-diagnostic-banner` renders directly inside the respective ingredient and step cards.
- `[TestMethod] IngestionReview_PublishedJob_CanOpenReviewAndUpdatePublishedRecipe()`:
  - Publish an ingestion job, navigate to `/ingest/review/{id}` (and verify the `"Review Side-by-Side"` button appears on the Published tab in `/ingest`), edit the title/ingredient, click `"Update Published Recipe"`, and verify the published recipe reflects the update.
- `[TestMethod] IngestionHub_RetryAll_RetriesAllMatchingFilterAcrossPages()`:
  - Verify `"Retry All (N)"` shows total count across pages and retries all matching jobs.

---

## 6. Step-by-Step Execution Checklist (For Implementation Agent)

- [x] **Task 1: Backend Core, Data & Background Worker**
  - Update `Core/Ingestion/IngestionModels.cs` and `Core/Ingestion/IIngestionService.cs`.
  - Update `Data/Services/IngestionReviewService.cs` (targeted diagnostics mapping, allow `Published` status, remove pause blocker in `RefineNextPendingDraftAsync` and `HasRunnableImportAsync`).
  - Update `Infrastructure/Ingestion/IngestionBackgroundWorker.cs` (run pending refinements even when bulk ingestion is paused).
  - Update `Data/Services/IngestionService.cs` (allow `Published` status in `SaveDraftAsync`, `ApproveAndPublishAsync`, and `PromoteSourcePageToCoverAsync`; set `RunNowRequested = true` in `RetryJobAsync`; support full-table `Filter` + `Search` in `RetryBatchJobsAsync`).
  - Update `API/Controllers/IngestionJobsController.cs`.
  - Write unit & integration tests in `Tests/API.IntegrationTests/IngestionIntegrationTests.cs` and `IngestionRefinementIntegrationTests.cs` covering:
    - Paused queue + `"Refine with ..."` execution.
    - Paused queue + single-job `"Rerun"` (`RetryJobAsync` sets `RunNowRequested = true`).
    - Editing, repairing, and republishing an already-`Published` ingestion job.
    - `RetryBatchJobsAsync` with `PageSize = 25` and `> 25` jobs matching `Filter` + `Search`.
  - Run backend tests:
    ```powershell
    dotnet test --project Tests/Core.UnitTests/App.Core.UnitTests.csproj
    dotnet test --project Tests/API.IntegrationTests/App.API.IntegrationTests.csproj
    ```

- [x] **Task 2: Angular Client & Spec Tests**
  - Update `ingestion.models.ts`, `ingestion.service.ts`, `ingestion-review.component.{ts,html,scss}`, and `ingestion-hub.component.{ts,html}`.
  - Update `ingestion-review.component.spec.ts` and `ingestion-hub.component.spec.ts`.
  - Run frontend lint, build, and unit tests in `Client/`:
    ```powershell
    npm run lint
    npm run build:production
    npm test -- --watch=false
    ```

- [x] **Task 3: Playwright E2E Verification & Final Formatting**
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
