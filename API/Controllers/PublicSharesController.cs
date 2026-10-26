using App.Core.Recipes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace App.API.Controllers;

[ApiController]
[Route("public/shares")]
[AllowAnonymous]
public sealed class PublicSharesController(IShareLinkService shareLinkService) : ControllerBase
{
    [HttpGet("{token}")]
    [ProducesResponseType(typeof(PublicSharedRecipeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSharedRecipe(
        [FromRoute] string token,
        CancellationToken cancellationToken
    )
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";

        var recipe = await shareLinkService.GetPublicSharedRecipeByTokenAsync(
            token,
            cancellationToken
        );

        if (recipe is null)
        {
            return NotFound(
                new ProblemDetails
                {
                    Status = StatusCodes.Status404NotFound,
                    Title = "Shared Recipe Not Found",
                    Detail = "This shared recipe link is invalid, expired, or has been revoked.",
                }
            );
        }

        return Ok(recipe);
    }
}
