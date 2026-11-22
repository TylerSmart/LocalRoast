using App.Core.Interfaces;
using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace App.Data.Auditing;

public sealed class AuditSaveChangesInterceptor(IUserContext userContext) : SaveChangesInterceptor
{
    private static readonly AsyncLocal<int> RestoreScopeDepth = new();

    public static IDisposable BeginRestoreScope() => new RestoreScope();

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result
    )
    {
        ApplyAudit(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default
    )
    {
        ApplyAudit(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void ApplyAudit(DbContext? context)
    {
        if (context is null || RestoreScopeDepth.Value > 0)
            return;

        var userId = ResolveUserId();
        var now = DateTimeOffset.UtcNow;

        foreach (var entry in context.ChangeTracker.Entries<IAuditable>())
        {
            if (entry.State is EntityState.Added)
            {
                entry.Entity.CreatedAt = now;
                entry.Entity.CreatedBy = userId;
                entry.Entity.ModifiedAt = now;
                entry.Entity.ModifiedBy = userId;
            }
            else if (entry.State is EntityState.Modified)
            {
                entry.Entity.ModifiedAt = now;
                entry.Entity.ModifiedBy = userId;
            }
        }
    }

    private string ResolveUserId()
    {
        try
        {
            var userId = userContext.UserId;
            return string.IsNullOrWhiteSpace(userId) ? "System" : userId;
        }
        catch (InvalidOperationException)
        {
            return "System";
        }
    }

    private sealed class RestoreScope : IDisposable
    {
        private readonly int _previousDepth = RestoreScopeDepth.Value;
        private int _disposed;

        public RestoreScope()
        {
            RestoreScopeDepth.Value = _previousDepth + 1;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                RestoreScopeDepth.Value = _previousDepth;
            }
        }
    }
}
