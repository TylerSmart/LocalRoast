using App.Core.Exceptions;
using App.Core.Recipes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace App.API.Controllers;

[ApiController]
[Route("recipes/{recipeId:guid}/shares")]
[Authorize]
public sealed class RecipeSharesController(IShareLinkService shareLinkService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ShareLinkDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetShareLinks(
        [FromRoute] Guid recipeId,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var links = await shareLinkService.GetShareLinksForRecipeAsync(
                recipeId,
                cancellationToken
            );
            return Ok(links);
        }
        catch (NotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost]
    [ProducesResponseType(typeof(ShareLinkDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateShareLink(
        [FromRoute] Guid recipeId,
        [FromBody] CreateShareLinkRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var created = await shareLinkService.CreateShareLinkAsync(
                recipeId,
                request,
                cancellationToken
            );
            return StatusCode(StatusCodes.Status201Created, created);
        }
        catch (NotFoundException)
        {
            return NotFound();
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

    [HttpPost("{shareId:guid}/revoke")]
    [ProducesResponseType(typeof(ShareLinkDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeShareLink(
        [FromRoute] Guid recipeId,
        [FromRoute] Guid shareId,
        CancellationToken cancellationToken
    )
    {
        var result = await shareLinkService.RevokeShareLinkAsync(
            recipeId,
            shareId,
            cancellationToken
        );
        return result is not null ? Ok(result) : NotFound();
    }

    [HttpDelete("{shareId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteShareLink(
        [FromRoute] Guid recipeId,
        [FromRoute] Guid shareId,
        CancellationToken cancellationToken
    )
    {
        var deleted = await shareLinkService.DeleteShareLinkAsync(
            recipeId,
            shareId,
            cancellationToken
        );
        return deleted ? NoContent() : NotFound();
    }
}
