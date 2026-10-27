using App.Core.Exceptions;
using App.Core.Ingestion;
using App.Core.Recipes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace App.API.Controllers;

[ApiController]
[Route("ingestion/jobs")]
[Authorize]
public sealed class IngestionJobsController(IIngestionService ingestionService) : ControllerBase
{
    [HttpGet("summary")]
    [ProducesResponseType(typeof(IngestionQueueSummaryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSummary(CancellationToken cancellationToken)
    {
        var summary = await ingestionService.GetSummaryAsync(cancellationToken);
        return Ok(summary);
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<IngestionJobSummaryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetJobs(
        [FromQuery] IngestionJobStatus? status,
        CancellationToken cancellationToken
    )
    {
        var jobs = await ingestionService.GetJobsAsync(status, cancellationToken);
        return Ok(jobs);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(IngestionJobDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetJobById(Guid id, CancellationToken cancellationToken)
    {
        var job = await ingestionService.GetJobByIdAsync(id, cancellationToken);
        return job is not null ? Ok(job) : NotFound();
    }

    [HttpPost("urls")]
    [ProducesResponseType(
        typeof(IReadOnlyList<IngestionJobSummaryDto>),
        StatusCodes.Status202Accepted
    )]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> EnqueueWebUrls(
        [FromBody] EnqueueWebUrlIngestionRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var jobs = await ingestionService.EnqueueWebUrlsAsync(request, cancellationToken);
            return Accepted(jobs);
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
    }

    [HttpPost("text")]
    [ProducesResponseType(typeof(IngestionJobSummaryDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> EnqueueRawText(
        [FromBody] EnqueueRawTextIngestionRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var job = await ingestionService.EnqueueRawTextAsync(request, cancellationToken);
            return Accepted(job);
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
    }

    [HttpPost("images")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(67_108_864)]
    [ProducesResponseType(typeof(IngestionJobSummaryDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> EnqueueImages(
        [FromForm] IFormFileCollection files,
        [FromForm] string? titleHint,
        [FromForm] string? sourceBookTitle,
        [FromForm] int? sourceBookPage,
        CancellationToken cancellationToken
    )
    {
        if (files is null || files.Count == 0)
        {
            return BadRequest(
                new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Validation Failed",
                    Detail = "At least one image file is required.",
                }
            );
        }

        try
        {
            var uploadStreams = files
                .Select(f => new IngestionUploadFileStream(
                    f.OpenReadStream(),
                    f.FileName,
                    f.ContentType,
                    f.Length
                ))
                .ToList();

            var metadata = new EnqueueImageIngestionMetadata(
                TitleHint: titleHint,
                SourceBookTitle: sourceBookTitle,
                SourceBookPage: sourceBookPage
            );

            var job = await ingestionService.EnqueueImagesAsync(
                uploadStreams,
                metadata,
                cancellationToken
            );
            return AcceptedAtAction(nameof(GetJobById), new { id = job.Id }, job);
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
    }

    [HttpPost("pdf")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(28_311_552)]
    [ProducesResponseType(typeof(IngestionJobSummaryDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> EnqueuePdf(
        [FromForm] IFormFile file,
        [FromForm] string? titleHint,
        [FromForm] string? sourceBookTitle,
        [FromForm] int? sourceBookPage,
        CancellationToken cancellationToken
    )
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(
                new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Validation Failed",
                    Detail = "A valid PDF file is required.",
                }
            );
        }

        try
        {
            var uploadStream = new IngestionUploadFileStream(
                file.OpenReadStream(),
                file.FileName,
                file.ContentType,
                file.Length
            );

            var metadata = new EnqueuePdfIngestionMetadata(
                TitleHint: titleHint,
                SourceBookTitle: sourceBookTitle,
                SourceBookPage: sourceBookPage
            );

            var job = await ingestionService.EnqueuePdfAsync(
                uploadStream,
                metadata,
                cancellationToken
            );
            return AcceptedAtAction(nameof(GetJobById), new { id = job.Id }, job);
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
    }

    [HttpPost("{id:guid}/cover-from-page")]
    [ProducesResponseType(typeof(IngestionJobDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> PromoteSourcePageToCover(
        Guid id,
        [FromBody] PromotePageToCoverRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var updated = await ingestionService.PromoteSourcePageToCoverAsync(
                id,
                request.PageNumber,
                cancellationToken
            );
            return updated is not null ? Ok(updated) : NotFound();
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

    [HttpPut("{id:guid}/draft")]
    [ProducesResponseType(typeof(IngestionJobDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SaveDraft(
        Guid id,
        [FromBody] IngestionExtractedDraftDto draft,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var updated = await ingestionService.SaveDraftAsync(id, draft, cancellationToken);
            return updated is not null ? Ok(updated) : NotFound();
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
    }

    [HttpPost("{id:guid}/approve")]
    [ProducesResponseType(typeof(RecipeDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ApproveAndPublish(
        Guid id,
        [FromBody] IngestionExtractedDraftDto draft,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var recipe = await ingestionService.ApproveAndPublishAsync(
                id,
                draft,
                cancellationToken
            );
            return CreatedAtAction("GetRecipeById", "Recipes", new { id = recipe.Id }, recipe);
        }
        catch (NotFoundException)
        {
            return NotFound();
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
    }

    [HttpPost("{id:guid}/retry")]
    [ProducesResponseType(typeof(IngestionJobSummaryDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RetryJob(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var retried = await ingestionService.RetryJobAsync(id, cancellationToken);
            return retried is not null ? Accepted(retried) : NotFound();
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

    [HttpPost("retry-waiting")]
    [ProducesResponseType(typeof(RetryWaitingJobsResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> RetryAllWaitingJobs(CancellationToken cancellationToken)
    {
        var result = await ingestionService.RetryAllWaitingJobsAsync(cancellationToken);
        return Ok(result);
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DiscardJob(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await ingestionService.DiscardJobAsync(id, cancellationToken);
            return deleted ? NoContent() : NotFound();
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
