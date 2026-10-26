using App.Core.Exceptions;
using App.Core.Recipes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace App.API.Controllers;

[ApiController]
[Route("tags")]
[Authorize]
public sealed class TagsController(ITaxonomyService taxonomyService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<TagDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTags(
        [FromQuery] TagCategory? category,
        CancellationToken cancellationToken
    )
    {
        var tags = await taxonomyService.GetTagsAsync(category, cancellationToken);
        return Ok(tags);
    }

    [HttpPost]
    [ProducesResponseType(typeof(TagDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateTag(
        [FromBody] UpsertTagRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var created = await taxonomyService.CreateTagAsync(request, cancellationToken);
            return CreatedAtAction(nameof(GetTags), new { id = created.Id }, created);
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

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(TagDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateTag(
        Guid id,
        [FromBody] UpsertTagRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var updated = await taxonomyService.UpdateTagAsync(id, request, cancellationToken);
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

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteTag(Guid id, CancellationToken cancellationToken)
    {
        var deleted = await taxonomyService.DeleteTagAsync(id, cancellationToken);
        return deleted ? NoContent() : NotFound();
    }
}
