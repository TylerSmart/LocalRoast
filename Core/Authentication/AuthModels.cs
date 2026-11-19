using System.ComponentModel.DataAnnotations;

namespace App.Core.Authentication;

public sealed record RegisterRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MinLength(3), MaxLength(100)] string Username,
    [Required, MinLength(6), MaxLength(100)] string Password
);

public sealed record LoginRequest(
    [Required, EmailAddress, MaxLength(256)] string Email,
    [Required, MinLength(1), MaxLength(100)] string Password
);

public sealed record AuthResponse(
    string Token,
    Guid Id,
    string Email,
    string Username,
    DateTimeOffset? ExpiresAt,
    string? ThemeSettings = null
);

public sealed record CurrentUserDto(
    Guid Id,
    string Email,
    string Username,
    string? ThemeSettings = null
);

public sealed record ExternalAuthRequest(
    string? Code = null,
    string? IdToken = null,
    string? AccessToken = null,
    string? RedirectUri = null
);
