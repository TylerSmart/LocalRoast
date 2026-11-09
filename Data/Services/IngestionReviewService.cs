using System.Text.Json;
using App.Core.Exceptions;
using App.Core.Ingestion;
using App.Core.Media;
using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace App.Data.Services;

public sealed class IngestionReviewService(
    AppDbContext dbContext,
    ILlmSettingsService llmSettingsService,
    ILlmExtractionClient llmClient,
    IIngestionQueueSignal queueSignal,
    ILogger<IngestionReviewService> logger
) : IIngestionReviewService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static bool CanEditOrReview(IngestionJobStatus status) =>
        status is IngestionJobStatus.ReadyForReview or IngestionJobStatus.Published;

    public async Task<IngestionReviewDiagnosticsDto> GetDiagnosticsAsync(
        Guid id,
        IngestionExtractedDraftDto draft,
        CancellationToken cancellationToken = default
    )
    {
        var job = await FindJobAsync(id, cancellationToken);
        return Diagnose(draft, GetSource(job));
    }

    public async Task<IngestionRepairPreviewDto> PreviewRepairAsync(
        Guid id,
        IngestionRepairPreviewRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var job = await FindJobAsync(id, cancellationToken);
        if (!CanEditOrReview(job.Status))
        {
            throw new ConflictException("Only a recipe awaiting review can be repaired.");
        }

        var source = RequireSource(job);
        var before = Diagnose(request.Draft, source);
        var config = await llmSettingsService.GetEffectiveConfigAsync(cancellationToken);
        queueSignal.PreemptBackgroundWork();
        var result = await llmClient.RepairRecipeDraftAsync(
            config,
            new LlmRecipeRepairContext(
                source,
                IngestionDraftRepairMapper.ToPayload(request.Draft),
                request.Hint
            ),
            cancellationToken
        );

        var currentJob = await dbContext
            .IngestionJobs.AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new { item.Status, item.ModifiedAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (
            currentJob is null
            || !CanEditOrReview(currentJob.Status)
            || currentJob.ModifiedAt != job.ModifiedAt
        )
        {
            throw new ConflictException(
                "The ingestion job changed while the AI was working. Reload it before repairing."
            );
        }

        var foods = await LoadCatalogFoodsAsync(cancellationToken);
        var proposed = IngestionDraftRepairMapper.Merge(request.Draft, result.Draft, foods, source);
        return new IngestionRepairPreviewDto(
            proposed,
            before,
            Diagnose(proposed, source),
            result.Notices
        );
    }

    public async Task<IngestionRefinementDto?> GetRefinementAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        var job = await FindJobAsync(id, cancellationToken);
        var refinement = await dbContext
            .IngestionDraftRefinements.AsNoTracking()
            .FirstOrDefaultAsync(item => item.IngestionJobId == id, cancellationToken);
        return refinement is null ? null : ToDto(refinement, job);
    }

    public async Task<IngestionRefinementDto> RequestRefinementAsync(
        Guid id,
        CancellationToken cancellationToken = default
    )
    {
        var job = await FindJobAsync(id, cancellationToken);
        if (!CanEditOrReview(job.Status))
        {
            throw new ConflictException("Only a recipe awaiting review can be refined.");
        }
        if (string.IsNullOrWhiteSpace(job.ExtractedDraftJson))
        {
            throw new DomainValidationException("This job has no draft to refine.");
        }

        var config = await llmSettingsService.GetEffectiveConfigAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(config.RefineModel))
        {
            throw new DomainValidationException(
                "No Refine Model is configured in LLM Settings. Select one before refining drafts."
            );
        }

        var refinement = await dbContext.IngestionDraftRefinements.FirstOrDefaultAsync(
            item => item.IngestionJobId == id,
            cancellationToken
        );
        if (refinement?.Status == IngestionRefinementStatus.Running)
        {
            throw new ConflictException("This draft is already being refined.");
        }
        if (refinement is null)
        {
            refinement = new IngestionDraftRefinement { IngestionJobId = id };
            dbContext.IngestionDraftRefinements.Add(refinement);
        }
        Requeue(refinement, config.RefineModel.Trim(), DateTimeOffset.UtcNow);
        await dbContext.SaveChangesAsync(cancellationToken);
        queueSignal.NotifyBackgroundWorkAvailable();
        return ToDto(refinement, job);
    }

    public async Task DismissRefinementAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var refinement = await dbContext.IngestionDraftRefinements.FirstOrDefaultAsync(
            item => item.IngestionJobId == id,
            cancellationToken
        );
        if (refinement is null)
        {
            return;
        }
        if (refinement.Status == IngestionRefinementStatus.Running && queueSignal.ActiveJobId == id)
        {
            queueSignal.CancelActiveExecution();
        }
        dbContext.IngestionDraftRefinements.Remove(refinement);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The background pass removed or replaced it first.
        }
    }

    public async Task<IngestionRepairPreviewDto> PreviewRefinementAsync(
        Guid id,
        IngestionExtractedDraftDto draft,
        CancellationToken cancellationToken = default
    )
    {
        var job = await FindJobAsync(id, cancellationToken);
        if (!CanEditOrReview(job.Status))
        {
            throw new ConflictException("Only a recipe awaiting review can be refined.");
        }
        var refinement =
            await dbContext
                .IngestionDraftRefinements.AsNoTracking()
                .FirstOrDefaultAsync(item => item.IngestionJobId == id, cancellationToken)
            ?? throw new ConflictException("No refined draft is available for this job.");
        if (
            refinement.Status != IngestionRefinementStatus.Improved
            || string.IsNullOrWhiteSpace(refinement.ProposalJson)
        )
        {
            throw new ConflictException("No refined draft is available for this job.");
        }

        LlmExtractedRecipePayload proposal;
        try
        {
            proposal =
                JsonSerializer.Deserialize<LlmExtractedRecipePayload>(
                    refinement.ProposalJson,
                    JsonOptions
                ) ?? throw new JsonException("The refined draft is empty.");
        }
        catch (JsonException)
        {
            throw new DomainValidationException(
                "The refined draft could not be read. Dismiss it and refine again."
            );
        }

        var source = GetSource(job);
        var foods = await LoadCatalogFoodsAsync(cancellationToken);
        var proposed = IngestionDraftRepairMapper.Merge(draft, proposal, foods, source);
        var notices = new List<string>
        {
            $"Refined by {refinement.Model} in the background. Applying it replaces the editor contents.",
        };
        if (refinement.BaseDraftJson != job.ExtractedDraftJson)
        {
            notices.Add(
                "The saved draft changed after this refinement started. Applying it can undo those later edits, so compare every change."
            );
        }
        return new IngestionRepairPreviewDto(
            proposed,
            Diagnose(draft, source),
            Diagnose(proposed, source),
            notices
        );
    }

    public async Task<bool> RefineNextPendingDraftAsync(
        CancellationToken cancellationToken = default
    )
    {
        var config = await llmSettingsService.GetEffectiveConfigAsync(cancellationToken);
        if (
            string.IsNullOrWhiteSpace(config.RefineModel)
            || await HasRunnableImportAsync(config.EnableIngestion, cancellationToken)
        )
        {
            return false;
        }

        var refinementId = await dbContext
            .IngestionDraftRefinements.Where(item =>
                item.Status == IngestionRefinementStatus.Pending
                && (
                    item.IngestionJob!.Status == IngestionJobStatus.ReadyForReview
                    || item.IngestionJob.Status == IngestionJobStatus.Published
                )
            )
            .OrderBy(item => item.RequestedAt)
            .Select(item => item.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (refinementId == Guid.Empty)
        {
            return false;
        }

        using var executionLock = await queueSignal.AcquireJobExecutionAsync(cancellationToken);
        var refinement = await dbContext.IngestionDraftRefinements.FirstOrDefaultAsync(
            item => item.Id == refinementId,
            cancellationToken
        );
        if (refinement?.Status != IngestionRefinementStatus.Pending)
        {
            return refinement is not null;
        }

        using var refineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var registration = queueSignal.RegisterPreemptibleExecution(
            refinement.IngestionJobId,
            refineCts
        );
        // An import queued while this pass waited for the execution lock goes first.
        if (await HasRunnableImportAsync(config.EnableIngestion, cancellationToken))
        {
            return false;
        }

        var job = await dbContext
            .IngestionJobs.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == refinement.IngestionJobId, cancellationToken);
        if (
            job is null
            || !CanEditOrReview(job.Status)
            || string.IsNullOrWhiteSpace(job.ExtractedDraftJson)
        )
        {
            return false;
        }

        var refineConfig = config.ForRefinement();
        var model = refineConfig.TextModel!;
        refinement.Model = model;
        refinement.Status = IngestionRefinementStatus.Running;
        refinement.Detail = $"Refining with {model}...";
        refinement.BaseDraftJson = job.ExtractedDraftJson;
        refinement.ProposalJson = null;
        refinement.IssuesBefore = null;
        refinement.IssuesAfter = null;
        refinement.CompletedAt = null;
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            var draft =
                JsonSerializer.Deserialize<IngestionExtractedDraftDto>(
                    job.ExtractedDraftJson,
                    JsonOptions
                ) ?? throw new DomainValidationException("The saved draft is empty.");
            var source = GetSource(job);
            var before = Diagnose(draft, source);
            refinement.IssuesBefore = before.ValidationIssues.Count + before.QualityWarnings.Count;
            if (before.ValidationIssues.Count == 0 && before.QualityWarnings.Count == 0)
            {
                Finish(
                    refinement,
                    IngestionRefinementStatus.NoImprovement,
                    "The saved draft has no issues for the Refine Model to fix."
                );
            }
            else
            {
                var result = await llmClient.RepairRecipeDraftAsync(
                    refineConfig,
                    new LlmRecipeRepairContext(
                        RequireSource(job),
                        IngestionDraftRepairMapper.ToPayload(draft)
                    ),
                    refineCts.Token
                );
                var foods = await LoadCatalogFoodsAsync(refineCts.Token);
                var after = Diagnose(
                    IngestionDraftRepairMapper.Merge(draft, result.Draft, foods, source),
                    source
                );
                refinement.IssuesAfter = after.ValidationIssues.Count + after.QualityWarnings.Count;
                var improved =
                    after.ValidationIssues.Count < before.ValidationIssues.Count
                    || (
                        after.ValidationIssues.Count == before.ValidationIssues.Count
                        && after.QualityWarnings.Count < before.QualityWarnings.Count
                    );
                if (improved)
                {
                    refinement.ProposalJson = JsonSerializer.Serialize(result.Draft, JsonOptions);
                    Finish(
                        refinement,
                        IngestionRefinementStatus.Improved,
                        $"{model} proposed a draft with {Describe(after)} (was {Describe(before)})."
                    );
                }
                else
                {
                    Finish(
                        refinement,
                        IngestionRefinementStatus.NoImprovement,
                        $"{model} did not improve the draft: {Describe(after)} (was {Describe(before)})."
                    );
                }
            }
        }
        catch (OperationCanceledException)
            when (refineCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            refinement.Status = IngestionRefinementStatus.Pending;
            refinement.Detail = "Paused so imports and other AI requests can run first.";
            refinement.IssuesBefore = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex,
                "Refining the draft of ingestion job {JobId} failed.",
                refinement.IngestionJobId
            );
            var message = $"Refining with {model} failed: {ex.Message}";
            Finish(
                refinement,
                IngestionRefinementStatus.Failed,
                message[..Math.Min(message.Length, 2000)]
            );
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The refinement was dismissed or the job deleted while the pass ran.
        }
        return true;
    }

    private async Task<bool> HasRunnableImportAsync(
        bool ingestionEnabled,
        CancellationToken cancellationToken
    )
    {
        var now = DateTimeOffset.UtcNow;
        return await dbContext.IngestionJobs.AnyAsync(
            item =>
                item.Status == IngestionJobStatus.Queued
                && (item.NextRetryAt == null || item.NextRetryAt <= now)
                && (ingestionEnabled || item.RunNowRequested),
            cancellationToken
        );
    }

    internal static void Requeue(
        IngestionDraftRefinement refinement,
        string model,
        DateTimeOffset requestedAt
    )
    {
        refinement.Status = IngestionRefinementStatus.Pending;
        refinement.Model = model;
        refinement.Detail = $"Waiting to refine with {model} when no imports are queued.";
        refinement.BaseDraftJson = null;
        refinement.ProposalJson = null;
        refinement.IssuesBefore = null;
        refinement.IssuesAfter = null;
        refinement.RequestedAt = requestedAt;
        refinement.CompletedAt = null;
    }

    private static void Finish(
        IngestionDraftRefinement refinement,
        IngestionRefinementStatus status,
        string detail
    )
    {
        refinement.Status = status;
        refinement.Detail = detail;
        refinement.CompletedAt = DateTimeOffset.UtcNow;
    }

    private static string Describe(IngestionReviewDiagnosticsDto diagnostics) =>
        $"{Count(diagnostics.ValidationIssues.Count, "validation issue")} and {Count(diagnostics.QualityWarnings.Count, "quality warning")}";

    private static string Count(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static IngestionRefinementDto ToDto(
        IngestionDraftRefinement refinement,
        IngestionJob job
    ) =>
        new(
            refinement.Status,
            refinement.Model,
            refinement.Detail,
            refinement.IssuesBefore,
            refinement.IssuesAfter,
            refinement.RequestedAt,
            refinement.CompletedAt,
            refinement.Status == IngestionRefinementStatus.Improved
                && !string.IsNullOrWhiteSpace(refinement.ProposalJson),
            refinement.BaseDraftJson is not null
                && refinement.BaseDraftJson != job.ExtractedDraftJson
        );

    private async Task<List<CatalogFoodLookupItem>> LoadCatalogFoodsAsync(
        CancellationToken cancellationToken
    ) =>
        await dbContext
            .FoodItems.AsNoTracking()
            .Select(food => new CatalogFoodLookupItem(
                food.Id,
                food.Name,
                food.NormalizedName,
                food.IsPantryStaple,
                food.Aliases.Select(alias => alias.NormalizedAlias).ToList(),
                food.Category
            ))
            .ToListAsync(cancellationToken);

    private async Task<IngestionJob> FindJobAsync(Guid id, CancellationToken cancellationToken) =>
        await dbContext
            .IngestionJobs.AsNoTracking()
            .FirstOrDefaultAsync(job => job.Id == id, cancellationToken)
        ?? throw new NotFoundException("Ingestion job not found.");

    internal static string? GetSource(IngestionJob job) =>
        !string.IsNullOrWhiteSpace(job.StructuredSourceText) ? job.StructuredSourceText
        : !string.IsNullOrWhiteSpace(job.NormalizedSourceText) ? job.NormalizedSourceText
        : job.RawText;

    private static readonly System.Text.RegularExpressions.Regex StepPattern = new(
        @"^Step\s+(?<step>\d+)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1)
    );
    private static readonly System.Text.RegularExpressions.Regex IngredientPattern = new(
        @"^Ingredient\s+#?(?<ing>\d+)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1)
    );
    private static readonly System.Text.RegularExpressions.Regex PrefixColonPattern = new(
        @"^(?<food>[^:]+):",
        System.Text.RegularExpressions.RegexOptions.None,
        TimeSpan.FromSeconds(1)
    );

    internal static IngestionReviewDiagnosticsDto Diagnose(
        IngestionExtractedDraftDto draft,
        string? source
    )
    {
        var payload = IngestionDraftRepairMapper.ToPayload(draft);
        var issues = RecipeExtractionDiagnostics.GetReviewIssues(payload, source);
        var warnings = RecipeExtractionDiagnostics.GetQualityWarnings(payload);
        var targeted = BuildTargetedDiagnostics(issues, warnings, draft);
        return new IngestionReviewDiagnosticsDto(issues, warnings, targeted);
    }

    internal static List<IngestionDiagnosticTargetDto> BuildTargetedDiagnostics(
        IReadOnlyList<string> issues,
        IReadOnlyList<string> warnings,
        IngestionExtractedDraftDto draft
    )
    {
        var result = new List<IngestionDiagnosticTargetDto>();
        foreach (var issue in issues)
        {
            var (ingIndex, stepNumber) = MatchTarget(issue, draft);
            result.Add(new IngestionDiagnosticTargetDto(issue, "error", ingIndex, stepNumber));
        }
        foreach (var warning in warnings)
        {
            var (ingIndex, stepNumber) = MatchTarget(warning, draft);
            result.Add(new IngestionDiagnosticTargetDto(warning, "warning", ingIndex, stepNumber));
        }
        return result;
    }

    private static (int? IngredientIndex, int? StepNumber) MatchTarget(
        string message,
        IngestionExtractedDraftDto draft
    )
    {
        var stepMatch = StepPattern.Match(message);
        if (stepMatch.Success && int.TryParse(stepMatch.Groups["step"].Value, out var stepNum))
        {
            return (null, stepNum);
        }

        var ingMatch = IngredientPattern.Match(message);
        if (ingMatch.Success && int.TryParse(ingMatch.Groups["ing"].Value, out var ingNum))
        {
            var count = draft.Ingredients?.Count ?? 0;
            if (count > 0)
            {
                if (ingNum >= 0 && ingNum < count)
                {
                    return (ingNum, null);
                }
                if (ingNum >= 1 && ingNum - 1 < count)
                {
                    return (ingNum - 1, null);
                }
            }
            return (ingNum, null);
        }

        var colonMatch = PrefixColonPattern.Match(message);
        if (colonMatch.Success && draft.Ingredients is { Count: > 0 })
        {
            var foodPrefix = colonMatch.Groups["food"].Value.Trim().Trim('"', '\'');
            var portionSplit = foodPrefix.IndexOf(" portion ", StringComparison.OrdinalIgnoreCase);
            var searchName = portionSplit >= 0 ? foodPrefix[..portionSplit].Trim() : foodPrefix;

            for (var i = 0; i < draft.Ingredients.Count; i++)
            {
                var ing = draft.Ingredients[i];
                if (
                    string.Equals(
                        ing.DisplayName?.Trim(),
                        searchName,
                        StringComparison.OrdinalIgnoreCase
                    )
                    || string.Equals(
                        ing.ProposedFoodItemName?.Trim(),
                        searchName,
                        StringComparison.OrdinalIgnoreCase
                    )
                    || string.Equals(
                        ing.MatchedFoodItemName?.Trim(),
                        searchName,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    return (i, null);
                }
            }
        }

        return (null, null);
    }

    private static string RequireSource(IngestionJob job) =>
        GetSource(job) is { } source && !string.IsNullOrWhiteSpace(source)
            ? source
            : throw new DomainValidationException(
                "This job has no source text to re-extract from."
            );
}
