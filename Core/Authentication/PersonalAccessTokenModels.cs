using System.ComponentModel.DataAnnotations;

namespace App.Core.Authentication;

public sealed record PersonalAccessTokenDto(
    Guid Id,
    string Name,
    string TokenPrefix,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? RevokedAt,
    bool IsActive
)
{
    public bool IsRevoked => RevokedAt is not null;
    public bool IsExpired => ExpiresAt is not null && ExpiresAt <= DateTimeOffset.UtcNow;
}

public sealed record CreatedPersonalAccessTokenDto(
    PersonalAccessTokenDto Token,
    string PlaintextToken
);

public sealed record CreatePersonalAccessTokenRequest(
    [Required, MinLength(2), MaxLength(100)] string Name,
    DateTimeOffset? ExpiresAt = null
);

public sealed record ValidatedPersonalAccessTokenUser(
    Guid TokenId,
    Guid UserId,
    string Email,
    string Username
);
