using System.Security.Claims;
using App.Core.Account;
using App.Core.Authentication;
using App.Core.Exceptions;
using App.Core.Maintenance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace App.API.Controllers;

[ApiController]
[Route("account")]
[Authorize]
public sealed class AccountController(IAccountService accountService) : ControllerBase
{
    private Guid? TryGetCurrentUserId()
    {
        var raw = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    [HttpGet]
    [ProducesResponseType(typeof(AccountDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAccount(CancellationToken cancellationToken)
    {
        var userId = TryGetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var account = await accountService.GetAccountAsync(userId.Value, cancellationToken);
        if (account is null)
        {
            return NotFound(
                new ProblemDetails
                {
                    Status = StatusCodes.Status404NotFound,
                    Title = "User not found",
                    Detail = "The authenticated user could not be found.",
                }
            );
        }

        return Ok(account);
    }

    [HttpPut("profile")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateProfile(
        [FromBody] UpdateProfileRequest request,
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
            var response = await accountService.UpdateProfileAsync(
                userId.Value,
                request,
                cancellationToken
            );
            return Ok(response);
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

    [HttpPut("password")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ChangePassword(
        [FromBody] ChangePasswordRequest request,
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
            var response = await accountService.ChangePasswordAsync(
                userId.Value,
                request,
                cancellationToken
            );
            return Ok(response);
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

    [HttpPut("theme")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateThemeSettings(
        [FromBody] UpdateThemeSettingsRequest request,
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
            var saved = await accountService.UpdateThemeSettingsAsync(
                userId.Value,
                request,
                cancellationToken
            );
            return Ok(new { themeSettings = saved });
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

    [HttpPost("reset-data")]
    [ProducesResponseType(typeof(ResetAppDataResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ResetAppData(
        [FromServices] IAppDataMaintenanceService maintenanceService,
        CancellationToken cancellationToken
    )
    {
        var userId = TryGetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await maintenanceService.ResetAppDataAsync(cancellationToken);
        return Ok(result);
    }
}
