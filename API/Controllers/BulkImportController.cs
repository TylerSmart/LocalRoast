using App.Core.Common;
using App.Core.Exceptions;
using App.Core.FoodCatalog;
using App.Core.Ingestion;
using App.Core.Media;
using App.Core.Recipes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace App.API.Controllers;

/// <summary>
/// Scoped bulk-import endpoints accepting either a standard JWT Bearer token or an MCP
/// Personal Access Token (lr_pat_...) without exposing general /api/account or LLM secret endpoints.
/// </summary>
[ApiController]
[Route("bulk-import")]
[Authorize(
    Policy = App.API
        .Authentication
        .AuthenticationServiceCollectionExtensions
        .BulkImportAuthorizationPolicy
)]
public sealed class BulkImportController(
    IIngestionService ingestionService,
    ILlmSettingsService settingsService,
    ITaxonomyService taxonomyService,
    IMediaStorageService mediaStorageService,
    IRecipeService recipeService,
    IFoodCatalogService foodCatalogService
) : ControllerBase
{
    [HttpGet("ping")]
    [ProducesResponseType(typeof(BulkImportWorkerStateDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Ping(CancellationToken cancellationToken)
    {
        var settings = await settingsService.GetSettingsAsync(cancellationToken);
        return Ok(new BulkImportWorkerStateDto(true, settings.EnableIngestion));
    }

    [HttpPut("worker")]
    [ProducesResponseType(typeof(BulkImportWorkerStateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> SetWorkerState(
        [FromBody] SetBulkImportWorkerRequest request,
        CancellationToken cancellationToken
    ) =>
        await ExecuteSafeAsync(async () =>
        {
            var current = await settingsService.GetSettingsAsync(cancellationToken);
            if (current.EnableIngestion == request.EnableIngestion)
            {
                return Ok(new BulkImportWorkerStateDto(true, current.EnableIngestion));
            }

            var updated = await settingsService.UpdateWorkflowsAsync(
                new UpdateLlmWorkflowsRequest(
                    TextModelProfileId: current.TextModelProfileId,
                    VisionModelProfileId: current.VisionModelProfileId,
                    RefineModelProfileId: current.RefineModelProfileId,
                    AutoPublishHighConfidenceText: current.AutoPublishHighConfidenceText,
                    MaxIngestionRetries: current.MaxIngestionRetries,
                    EnableIngestion: request.EnableIngestion,
                    AutoResumeQueue: current.AutoResumeQueue,
                    EmbeddingModelProfileId: current.EmbeddingModelProfileId,
                    RefineDuringIngestion: current.RefineDuringIngestion,
                    RefineDuringIdle: current.RefineDuringIdle
                ),
                cancellationToken
            );

            return Ok(new BulkImportWorkerStateDto(true, updated.EnableIngestion));
        });

    [HttpGet("collections")]
    [ProducesResponseType(typeof(IReadOnlyList<CollectionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCollections(CancellationToken cancellationToken)
    {
        var collections = await taxonomyService.GetCollectionsAsync(cancellationToken);
        return Ok(collections);
    }

    [HttpPost("collections")]
    [ProducesResponseType(typeof(CollectionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateCollection(
        [FromBody] UpsertCollectionRequest request,
        CancellationToken cancellationToken
    ) =>
        await ExecuteSafeAsync(async () =>
        {
            var created = await taxonomyService.CreateCollectionAsync(request, cancellationToken);
            return CreatedAtAction(nameof(GetCollections), new { id = created.Id }, created);
        });

    [HttpPost("media/recipe-cover")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(16_777_216)]
    [ProducesResponseType(typeof(UploadedRecipeCoverDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadRecipeCover(
        IFormFile? file,
        CancellationToken cancellationToken
    )
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(
                new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Validation Error",
                    Detail = "A valid image file is required.",
                }
            );
        }

        return await ExecuteSafeAsync(async () =>
        {
            await using var stream = file.OpenReadStream();
            var result = await mediaStorageService.SaveRecipeCoverAsync(
                stream,
                file.FileName,
                file.Length,
                cancellationToken
            );
            return Ok(result);
        });
    }

    [HttpGet("jobs/summary")]
    [ProducesResponseType(typeof(IngestionQueueSummaryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetJobSummary(CancellationToken cancellationToken)
    {
        var summary = await ingestionService.GetSummaryAsync(cancellationToken);
        return Ok(summary);
    }

    [HttpGet("jobs")]
    [ProducesResponseType(typeof(PagedResultDto<IngestionJobSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(IReadOnlyList<IngestionJobSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetJobs(
        [FromQuery] IngestionJobStatus? status,
        [FromQuery] string? search,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromQuery] bool activeWaiting = false,
        [FromQuery] IngestionJobSortBy sortBy = IngestionJobSortBy.CreatedAt,
        [FromQuery] IngestionJobSortDirection sortDirection = IngestionJobSortDirection.Desc,
        CancellationToken cancellationToken = default
    )
    {
        if (page.HasValue || pageSize.HasValue)
        {
            var paged = await ingestionService.GetJobsPagedAsync(
                new IngestionJobQueryParameters(
                    status,
                    search,
                    page ?? 1,
                    pageSize ?? 25,
                    activeWaiting,
                    sortBy,
                    sortDirection
                ),
                cancellationToken
            );
            return Ok(paged);
        }

        var jobs = await ingestionService.GetJobsAsync(status, cancellationToken);
        return Ok(jobs);
    }

    [HttpGet("jobs/{id:guid}")]
    [ProducesResponseType(typeof(IngestionJobDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetJobById(Guid id, CancellationToken cancellationToken)
    {
        var job = await ingestionService.GetJobByIdAsync(id, cancellationToken);
        return job is not null ? Ok(job) : NotFound();
    }

    [HttpPost("jobs/text")]
    [ProducesResponseType(typeof(IngestionJobSummaryDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> EnqueueRawText(
        [FromBody] EnqueueRawTextIngestionRequest request,
        CancellationToken cancellationToken
    ) =>
        await ExecuteSafeAsync(async () =>
        {
            var job = await ingestionService.EnqueueRawTextAsync(request, cancellationToken);
            return Accepted(job);
        });

    [HttpPost("jobs/batch-source")]
    [ProducesResponseType(
        typeof(IReadOnlyList<IngestionJobSourceItemDto>),
        StatusCodes.Status200OK
    )]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetBatchJobSources(
        [FromBody] BatchJobSourceRequest request,
        CancellationToken cancellationToken
    ) =>
        await ExecuteSafeAsync(async () =>
        {
            var sources = await ingestionService.GetBatchJobSourcesAsync(
                request,
                cancellationToken
            );
            return Ok(sources);
        });

    [HttpPost("jobs/{id:guid}/submit-extraction")]
    [ProducesResponseType(typeof(SubmitExternalExtractionResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SubmitExternalExtraction(
        Guid id,
        [FromBody] SubmitExternalExtractionRequest request,
        CancellationToken cancellationToken
    ) =>
        await ExecuteSafeAsync(async () =>
        {
            var result = await ingestionService.SubmitExternalExtractionAsync(
                id,
                request,
                cancellationToken
            );
            return result is not null ? Ok(result) : NotFound();
        });

    [HttpGet("recipes")]
    [ProducesResponseType(typeof(RecipePagedResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(IReadOnlyList<RecipeSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRecipes(
        [FromQuery] RecipeQueryParameters query,
        CancellationToken cancellationToken
    )
    {
        if (query.Page.HasValue || query.PageSize.HasValue)
        {
            var paged = await recipeService.GetRecipesPagedAsync(query, cancellationToken);
            return Ok(paged);
        }

        var recipes = await recipeService.GetRecipesAsync(query, cancellationToken);
        return Ok(recipes);
    }

    [HttpGet("recipes/{id:guid}")]
    [ProducesResponseType(typeof(RecipeDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRecipeById(Guid id, CancellationToken cancellationToken)
    {
        var recipe = await recipeService.GetRecipeByIdAsync(id, cancellationToken);
        return recipe is not null ? Ok(recipe) : NotFound();
    }

    [HttpGet("food-items")]
    [ProducesResponseType(typeof(PagedResultDto<FoodItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(IReadOnlyList<FoodItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFoodItems(
        [FromQuery] FoodItemQueryParameters query,
        CancellationToken cancellationToken
    )
    {
        if (query.Page.HasValue || query.PageSize.HasValue)
        {
            var paged = await foodCatalogService.GetFoodItemsPagedAsync(query, cancellationToken);
            return Ok(paged);
        }

        var items = await foodCatalogService.GetFoodItemsAsync(query, cancellationToken);
        return Ok(items);
    }

    private async Task<IActionResult> ExecuteSafeAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (DomainValidationException ex)
        {
            return BadRequest(
                new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = ex.Title,
                    Detail = ex.Message,
                }
            );
        }
        catch (NotFoundException ex)
        {
            return NotFound(
                new ProblemDetails
                {
                    Status = StatusCodes.Status404NotFound,
                    Title = "Not Found",
                    Detail = ex.Message,
                }
            );
        }
        catch (ConflictException ex)
        {
            return Conflict(
                new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = ex.Title,
                    Detail = ex.Message,
                }
            );
        }
    }
}
