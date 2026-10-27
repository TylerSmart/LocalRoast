using System.Security.Claims;
using App.Core.Authentication;
using App.Core.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace App.API.Controllers;

[ApiController]
[Route("account/tokens")]
[Authorize]
public sealed class PersonalAccessTokensController(
    IPersonalAccessTokenService personalAccessTokenService
) : ControllerBase
{
    private Guid? TryGetCurrentUserId()
    {
        var raw = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PersonalAccessTokenDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetTokens(CancellationToken cancellationToken)
    {
        var userId = TryGetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var tokens = await personalAccessTokenService.GetTokensForUserAsync(
            userId.Value,
            cancellationToken
        );
        return Ok(tokens);
    }

    [HttpPost]
    [ProducesResponseType(typeof(CreatedPersonalAccessTokenDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateToken(
        [FromBody] CreatePersonalAccessTokenRequest request,
        CancellationToken cancellationToken
    )
    {
        var userId = TryGetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            var created = await personalAccessTokenService.CreateTokenAsync(
                userId.Value,
                request,
                cancellationToken
            );
            return StatusCode(StatusCodes.Status201Created, created);
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
                    Title = "User not found",
                    Detail = ex.Message,
                }
            );
        }
    }

    [HttpPost("{id:guid}/revoke")]
    [ProducesResponseType(typeof(PersonalAccessTokenDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RevokeToken(Guid id, CancellationToken cancellationToken)
    {
        var userId = TryGetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            var revoked = await personalAccessTokenService.RevokeTokenAsync(
                userId.Value,
                id,
                cancellationToken
            );
            if (revoked is null)
            {
                return NotFound(
                    new ProblemDetails
                    {
                        Status = StatusCodes.Status404NotFound,
                        Title = "Token not found",
                        Detail = "Personal access token was not found.",
                    }
                );
            }

            return Ok(revoked);
        }
        catch (ForbiddenException ex)
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new ProblemDetails
                {
                    Status = StatusCodes.Status403Forbidden,
                    Title = "Forbidden",
                    Detail = ex.Message,
                }
            );
        }
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteToken(Guid id, CancellationToken cancellationToken)
    {
        var userId = TryGetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            var deleted = await personalAccessTokenService.DeleteTokenAsync(
                userId.Value,
                id,
                cancellationToken
            );
            if (!deleted)
            {
                return NotFound(
                    new ProblemDetails
                    {
                        Status = StatusCodes.Status404NotFound,
                        Title = "Token not found",
                        Detail = "Personal access token was not found.",
                    }
                );
            }

            return NoContent();
        }
        catch (ForbiddenException ex)
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new ProblemDetails
                {
                    Status = StatusCodes.Status403Forbidden,
                    Title = "Forbidden",
                    Detail = ex.Message,
                }
            );
        }
    }
}
