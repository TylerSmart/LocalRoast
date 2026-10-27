using System.Security.Claims;
using App.Core.Interfaces;

namespace App.API.Authentication;

public sealed class HttpContextUserContext(IHttpContextAccessor httpContextAccessor) : IUserContext
{
    public string UserId
    {
        get
        {
            var user = httpContextAccessor.HttpContext?.User;
            var userId =
                user?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user?.FindFirst("sub")?.Value;

            return userId
                ?? McpUserContextAccessor.CurrentUserId
                ?? throw new InvalidOperationException("User is not authenticated.");
        }
    }
}
