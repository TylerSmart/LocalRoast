using System.Security.Claims;
using App.Core.Authentication;
using App.Core.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace App.API.Controllers;

[ApiController]
[Route("auth")]
public sealed class AuthController(IAuthService authService) : ControllerBase
{
    [HttpGet("providers")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(IReadOnlyList<AuthProviderDto>), StatusCodes.Status200OK)]
    public IActionResult GetAuthProviders()
    {
        return Ok(authService.GetAuthProviders());
    }

    [HttpPost("external-callback")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ExternalCallback(
        [FromBody] ExternalAuthRequest request,
        CancellationToken cancellationToken
    )
    {
        var response = await authService.ExternalCallbackAsync(request, cancellationToken);
        if (response is null)
        {
            return Unauthorized(
                new ProblemDetails
                {
                    Status = StatusCodes.Status401Unauthorized,
                    Title = "External authentication failed",
                    Detail = "Could not validate credentials with the external provider.",
                }
            );
        }

        return Ok(response);
    }

    [HttpPost("register")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register(
        [FromBody] RegisterRequest request,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var response = await authService.RegisterAsync(request, cancellationToken);
            return Ok(response);
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

    [HttpPost("login")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login(
        [FromBody] LoginRequest request,
        CancellationToken cancellationToken
    )
    {
        var response = await authService.LoginAsync(request, cancellationToken);
        if (response is null)
        {
            return Unauthorized(
                new ProblemDetails
                {
                    Status = StatusCodes.Status401Unauthorized,
                    Title = "Invalid credentials",
                    Detail = "The provided email or password is incorrect.",
                }
            );
        }

        return Ok(response);
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Logout()
    {
        return Ok(new { message = "Logged out successfully" });
    }

    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType(typeof(CurrentUserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetCurrentUser(CancellationToken cancellationToken)
    {
        var userIdStr =
            User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;

        if (!Guid.TryParse(userIdStr, out var userId))
        {
            return Unauthorized();
        }

        var user = await authService.GetCurrentUserAsync(userId, cancellationToken);
        if (user is null)
        {
            return Unauthorized(
                new ProblemDetails
                {
                    Status = StatusCodes.Status401Unauthorized,
                    Title = "Invalid session",
                    Detail = "The authenticated user account no longer exists.",
                }
            );
        }

        return Ok(user);
    }
}
