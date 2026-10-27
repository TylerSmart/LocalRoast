namespace App.API.Authentication;

public static class McpUserContextAccessor
{
    private static readonly AsyncLocal<string?> CurrentUserIdHolder = new();

    public static string? CurrentUserId => CurrentUserIdHolder.Value;

    public static IDisposable BeginScope(Guid userId)
    {
        CurrentUserIdHolder.Value = userId.ToString();
        return new ScopeDisposable();
    }

    public static IDisposable BeginScope(string userId)
    {
        CurrentUserIdHolder.Value = userId;
        return new ScopeDisposable();
    }

    private sealed class ScopeDisposable : IDisposable
    {
        public void Dispose()
        {
            CurrentUserIdHolder.Value = null;
        }
    }
}
