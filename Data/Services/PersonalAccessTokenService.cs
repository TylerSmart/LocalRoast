using App.Core.Authentication;
using App.Core.Exceptions;
using App.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace App.Data.Services;

public sealed class PersonalAccessTokenService(
    AppDbContext dbContext,
    TimeProvider? timeProvider = null
) : IPersonalAccessTokenService
{
    private const int MaxActiveTokensPerUser = 25;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<PersonalAccessTokenDto>> GetTokensForUserAsync(
        Guid userId,
        CancellationToken cancellationToken = default
    )
    {
        var now = _timeProvider.GetUtcNow();
        var tokens = await dbContext
            .PersonalAccessTokens.AsNoTracking()
            .Where(t => t.UserId == userId)
            .ToListAsync(cancellationToken);

        return tokens
            .OrderByDescending(t => IsTokenActive(t, now))
            .ThenByDescending(t => t.CreatedAt)
            .Select(t => new PersonalAccessTokenDto(
                Id: t.Id,
                Name: t.Name,
                TokenPrefix: t.TokenPrefix,
                CreatedAt: t.CreatedAt,
                ExpiresAt: t.ExpiresAt,
                LastUsedAt: t.LastUsedAt,
                RevokedAt: t.RevokedAt,
                IsActive: IsTokenActive(t, now)
            ))
            .ToList();
    }

    public async Task<CreatedPersonalAccessTokenDto> CreateTokenAsync(
        Guid userId,
        CreatePersonalAccessTokenRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var userExists = await dbContext
            .Users.AsNoTracking()
            .AnyAsync(u => u.Id == userId, cancellationToken);
        if (!userExists)
        {
            throw new NotFoundException("The specified user could not be found.");
        }

        var trimmedName = request.Name?.Trim() ?? string.Empty;
        if (trimmedName.Length < 2 || trimmedName.Length > 100)
        {
            throw new DomainValidationException(
                "Token name must be between 2 and 100 characters long.",
                "Invalid token name"
            );
        }

        var now = _timeProvider.GetUtcNow();
        if (request.ExpiresAt.HasValue && request.ExpiresAt.Value <= now)
        {
            throw new DomainValidationException(
                "Token expiration date must be in the future.",
                "Invalid expiration"
            );
        }

        var activeCount = await dbContext.PersonalAccessTokens.CountAsync(
            t =>
                t.UserId == userId
                && t.RevokedAt == null
                && (!t.ExpiresAt.HasValue || t.ExpiresAt.Value > now),
            cancellationToken
        );

        if (activeCount >= MaxActiveTokensPerUser)
        {
            throw new DomainValidationException(
                $"Maximum of {MaxActiveTokensPerUser} active personal access tokens reached. Revoke unused tokens first.",
                "Token limit reached"
            );
        }

        GeneratedPersonalAccessToken generated = default;
        var unique = false;
        for (var i = 0; i < 5; i++)
        {
            generated = PersonalAccessTokenGenerator.GenerateToken();
            var exists = await dbContext.PersonalAccessTokens.AnyAsync(
                t => t.TokenHash == generated.TokenHash,
                cancellationToken
            );
            if (!exists)
            {
                unique = true;
                break;
            }
        }

        if (!unique)
        {
            throw new InvalidOperationException(
                "Failed to generate a unique personal access token."
            );
        }

        var token = new PersonalAccessToken
        {
            UserId = userId,
            Name = trimmedName,
            TokenPrefix = generated.TokenPrefix,
            TokenHash = generated.TokenHash,
            ExpiresAt = request.ExpiresAt,
        };

        dbContext.PersonalAccessTokens.Add(token);
        await dbContext.SaveChangesAsync(cancellationToken);

        var dto = new PersonalAccessTokenDto(
            Id: token.Id,
            Name: token.Name,
            TokenPrefix: token.TokenPrefix,
            CreatedAt: token.CreatedAt,
            ExpiresAt: token.ExpiresAt,
            LastUsedAt: token.LastUsedAt,
            RevokedAt: token.RevokedAt,
            IsActive: true
        );

        return new CreatedPersonalAccessTokenDto(dto, generated.PlaintextToken);
    }

    public async Task<PersonalAccessTokenDto?> RevokeTokenAsync(
        Guid userId,
        Guid tokenId,
        CancellationToken cancellationToken = default
    )
    {
        var token = await dbContext.PersonalAccessTokens.FirstOrDefaultAsync(
            t => t.Id == tokenId,
            cancellationToken
        );

        if (token is null)
        {
            return null;
        }

        if (token.UserId != userId)
        {
            throw new ForbiddenException(
                "You do not have permission to modify another user's personal access token."
            );
        }

        var now = _timeProvider.GetUtcNow();
        if (token.RevokedAt is null)
        {
            token.RevokedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new PersonalAccessTokenDto(
            Id: token.Id,
            Name: token.Name,
            TokenPrefix: token.TokenPrefix,
            CreatedAt: token.CreatedAt,
            ExpiresAt: token.ExpiresAt,
            LastUsedAt: token.LastUsedAt,
            RevokedAt: token.RevokedAt,
            IsActive: false
        );
    }

    public async Task<bool> DeleteTokenAsync(
        Guid userId,
        Guid tokenId,
        CancellationToken cancellationToken = default
    )
    {
        var token = await dbContext.PersonalAccessTokens.FirstOrDefaultAsync(
            t => t.Id == tokenId,
            cancellationToken
        );

        if (token is null)
        {
            return false;
        }

        if (token.UserId != userId)
        {
            throw new ForbiddenException(
                "You do not have permission to delete another user's personal access token."
            );
        }

        dbContext.PersonalAccessTokens.Remove(token);
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<ValidatedPersonalAccessTokenUser?> ValidateAndTouchTokenAsync(
        string plaintextToken,
        CancellationToken cancellationToken = default
    )
    {
        if (!PersonalAccessTokenGenerator.IsWellFormedToken(plaintextToken))
        {
            return null;
        }

        var hash = PersonalAccessTokenGenerator.ComputeSha256Hex(plaintextToken);
        var token = await dbContext
            .PersonalAccessTokens.Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (
            token is null
            || !PersonalAccessTokenGenerator.FixedTimeHashEquals(token.TokenHash, hash)
        )
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        if (
            token.RevokedAt.HasValue
            || (token.ExpiresAt.HasValue && token.ExpiresAt.Value <= now)
            || token.User is null
        )
        {
            return null;
        }

        if (token.LastUsedAt is null || (now - token.LastUsedAt.Value) >= TimeSpan.FromSeconds(60))
        {
            token.LastUsedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new ValidatedPersonalAccessTokenUser(
            TokenId: token.Id,
            UserId: token.User.Id,
            Email: token.User.Email,
            Username: token.User.Username
        );
    }

    private static bool IsTokenActive(PersonalAccessToken token, DateTimeOffset now) =>
        token.RevokedAt is null && (!token.ExpiresAt.HasValue || token.ExpiresAt.Value > now);
}
