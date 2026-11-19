using System.Text.Json;
using App.Core.Account;
using App.Core.Authentication;
using App.Core.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace App.Data.Services;

public sealed class AccountService(
    AppDbContext dbContext,
    IPasswordHasher passwordHasher,
    IJwtTokenService jwtTokenService,
    IOptions<JwtOptions> jwtOptions
) : IAccountService
{
    private const int MaxThemeSettingsLength = 4000;

    private readonly JwtOptions _jwtOptions = jwtOptions.Value;

    public async Task<AccountDto?> GetAccountAsync(
        Guid userId,
        CancellationToken cancellationToken = default
    )
    {
        var user = await dbContext
            .Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null)
        {
            return null;
        }

        return new AccountDto(
            Id: user.Id,
            Email: user.Email,
            Username: user.Username,
            HasPassword: !string.IsNullOrEmpty(user.PasswordHash),
            ExternalProvider: user.ExternalProvider,
            ThemeSettings: user.ThemeSettings
        );
    }

    public async Task<AuthResponse> UpdateProfileAsync(
        Guid userId,
        UpdateProfileRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var user =
            await dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new NotFoundException("The authenticated user could not be found.");

        var trimmedUsername = request.Username.Trim();
        var trimmedEmail = request.Email.Trim();

        if (trimmedUsername.Length < 3)
        {
            throw new DomainValidationException(
                "Username must be at least 3 characters long.",
                "Invalid username"
            );
        }

        if (string.IsNullOrWhiteSpace(trimmedEmail))
        {
            throw new DomainValidationException(
                "A valid email address is required.",
                "Invalid email"
            );
        }

        var normalizedUsername = trimmedUsername.ToUpperInvariant();
        var normalizedEmail = trimmedEmail.ToUpperInvariant();

        var emailChanging = !string.Equals(
            user.NormalizedEmail,
            normalizedEmail,
            StringComparison.Ordinal
        );
        var usernameLookupChanging = !string.Equals(
            user.NormalizedUsername,
            normalizedUsername,
            StringComparison.Ordinal
        );

        if (emailChanging)
        {
            var hasPassword = !string.IsNullOrEmpty(user.PasswordHash);
            if (!hasPassword)
            {
                throw new DomainValidationException(
                    "Accounts signed in exclusively through an external provider cannot change their email address.",
                    "Email managed by external provider"
                );
            }

            if (
                string.IsNullOrEmpty(request.CurrentPassword)
                || !passwordHasher.VerifyPassword(request.CurrentPassword, user.PasswordHash)
            )
            {
                throw new DomainValidationException(
                    "Your current password is required and must match to change your email address.",
                    "Invalid current password"
                );
            }

            var emailExists = await dbContext.Users.AnyAsync(
                u => u.Id != userId && u.NormalizedEmail == normalizedEmail,
                cancellationToken
            );
            if (emailExists)
            {
                throw new ConflictException(
                    "An account with this email address already exists.",
                    "Email already registered"
                );
            }
        }

        if (usernameLookupChanging)
        {
            var usernameExists = await dbContext.Users.AnyAsync(
                u => u.Id != userId && u.NormalizedUsername == normalizedUsername,
                cancellationToken
            );
            if (usernameExists)
            {
                throw new ConflictException(
                    "The specified username is already in use.",
                    "Username taken"
                );
            }
        }

        user.Username = trimmedUsername;
        user.NormalizedUsername = normalizedUsername;
        user.Email = trimmedEmail;
        user.NormalizedEmail = normalizedEmail;

        await dbContext.SaveChangesAsync(cancellationToken);

        return CreateAuthResponse(user.Id, user.Email, user.Username, user.ThemeSettings);
    }

    public async Task<AuthResponse> ChangePasswordAsync(
        Guid userId,
        ChangePasswordRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var user =
            await dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new NotFoundException("The authenticated user could not be found.");

        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 6)
        {
            throw new DomainValidationException(
                "New password must be at least 6 characters long.",
                "Invalid new password"
            );
        }

        var hasPassword = !string.IsNullOrEmpty(user.PasswordHash);
        if (hasPassword)
        {
            if (
                string.IsNullOrEmpty(request.CurrentPassword)
                || !passwordHasher.VerifyPassword(request.CurrentPassword, user.PasswordHash)
            )
            {
                throw new DomainValidationException(
                    "The current password you entered is incorrect.",
                    "Invalid current password"
                );
            }
        }

        user.PasswordHash = passwordHasher.HashPassword(request.NewPassword);
        await dbContext.SaveChangesAsync(cancellationToken);

        return CreateAuthResponse(user.Id, user.Email, user.Username, user.ThemeSettings);
    }

    public async Task<string> UpdateThemeSettingsAsync(
        Guid userId,
        UpdateThemeSettingsRequest request,
        CancellationToken cancellationToken = default
    )
    {
        if (request.Settings.ValueKind != JsonValueKind.Object)
        {
            throw new DomainValidationException(
                "Theme settings must be a valid JSON object.",
                "Invalid theme settings"
            );
        }

        var serialized = JsonSerializer.Serialize(request.Settings);
        if (serialized.Length > MaxThemeSettingsLength)
        {
            throw new DomainValidationException(
                $"Theme settings cannot exceed {MaxThemeSettingsLength} characters.",
                "Theme settings too large"
            );
        }

        var user =
            await dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new NotFoundException("The authenticated user could not be found.");

        user.ThemeSettings = serialized;
        await dbContext.SaveChangesAsync(cancellationToken);

        return serialized;
    }

    private AuthResponse CreateAuthResponse(
        Guid id,
        string email,
        string username,
        string? themeSettings
    )
    {
        var summary = new UserSummary(id, email, username, themeSettings);
        var token = jwtTokenService.GenerateToken(summary);
        DateTimeOffset? expiresAt =
            _jwtOptions.ExpirationMinutes > 0
                ? DateTimeOffset.UtcNow.AddMinutes(_jwtOptions.ExpirationMinutes)
                : null;

        return new AuthResponse(
            Token: token,
            Id: id,
            Email: email,
            Username: username,
            ExpiresAt: expiresAt,
            ThemeSettings: themeSettings
        );
    }
}
