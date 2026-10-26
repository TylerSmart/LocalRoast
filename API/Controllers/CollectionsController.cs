using App.Core.Exceptions;
using App.Core.Recipes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace App.API.Controllers;

[ApiController]
[Route("collections")]
[Authorize]
public sealed class CollectionsController(ITaxonomyService taxonomyService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<CollectionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCollections(CancellationToken cancellationToken)
    {
        var collections = await taxonomyService.GetCollectionsAsync(cancellationToken);
        return Ok(collections);
    }

    [HttpPost]
    [ProducesResponseType(typeof(CollectionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateCollection(
        [FromBody] UpsertCollectionRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var created = await taxonomyService.CreateCollectionAsync(request, cancellationToken);
            return CreatedAtAction(nameof(GetCollections), new { id = created.Id }, created);
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
    [ProducesResponseType(typeof(CollectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateCollection(
        Guid id,
        [FromBody] UpsertCollectionRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var updated = await taxonomyService.UpdateCollectionAsync(
                id,
                request,
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

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteCollection(Guid id, CancellationToken cancellationToken)
    {
        var deleted = await taxonomyService.DeleteCollectionAsync(id, cancellationToken);
        return deleted ? NoContent() : NotFound();
    }
}
