using App.Core.Authentication;
using App.Core.Exceptions;
using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace App.Data.Services;

public sealed class AuthService(
    AppDbContext dbContext,
    IPasswordHasher passwordHasher,
    IJwtTokenService jwtTokenService,
    IOidcService oidcService,
    IOptions<JwtOptions> jwtOptions
) : IAuthService
{
    private readonly JwtOptions _jwtOptions = jwtOptions.Value;

    public IReadOnlyList<AuthProviderDto> GetAuthProviders()
    {
        var providers = new List<AuthProviderDto>
        {
            new(Id: "local", Name: "Local Account", IsExternal: false),
        };

        var oidcConfig = oidcService.GetConfiguration();
        if (oidcConfig.Enabled)
        {
            providers.Add(
                new(
                    Id: "oidc",
                    Name: oidcConfig.ProviderName,
                    IsExternal: true,
                    OidcConfig: oidcConfig
                )
            );
        }

        return providers;
    }

    public async Task<AuthResponse> RegisterAsync(
        RegisterRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var normalizedEmail = request.Email.Trim().ToUpperInvariant();
        var normalizedUsername = request.Username.Trim().ToUpperInvariant();

        var emailExists = await dbContext.Users.AnyAsync(
            u => u.NormalizedEmail == normalizedEmail,
            cancellationToken
        );
        if (emailExists)
        {
            throw new ConflictException(
                "An account with this email address already exists.",
                "Email already registered"
            );
        }

        var usernameExists = await dbContext.Users.AnyAsync(
            u => u.NormalizedUsername == normalizedUsername,
            cancellationToken
        );
        if (usernameExists)
        {
            throw new ConflictException(
                "The specified username is already in use.",
                "Username taken"
            );
        }

        var passwordHash = passwordHasher.HashPassword(request.Password);
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = request.Email.Trim(),
            NormalizedEmail = normalizedEmail,
            Username = request.Username.Trim(),
            NormalizedUsername = normalizedUsername,
            PasswordHash = passwordHash,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "System",
            ModifiedAt = DateTimeOffset.UtcNow,
            ModifiedBy = "System",
        };

        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync(cancellationToken);

        var userSummary = new UserSummary(user.Id, user.Email, user.Username, user.ThemeSettings);
        var token = jwtTokenService.GenerateToken(userSummary);

        return new AuthResponse(
            Token: token,
            Id: user.Id,
            Email: user.Email,
            Username: user.Username,
            ExpiresAt: GetExpiresAt(),
            ThemeSettings: user.ThemeSettings
        );
    }

    public async Task<AuthResponse?> LoginAsync(
        LoginRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var normalizedEmail = request.Email.Trim().ToUpperInvariant();

        var user = await dbContext.Users.FirstOrDefaultAsync(
            u => u.NormalizedEmail == normalizedEmail,
            cancellationToken
        );

        if (
            user is null
            || string.IsNullOrEmpty(user.PasswordHash)
            || !passwordHasher.VerifyPassword(request.Password, user.PasswordHash)
        )
        {
            return null;
        }

        var userSummary = new UserSummary(user.Id, user.Email, user.Username, user.ThemeSettings);
        var token = jwtTokenService.GenerateToken(userSummary);

        return new AuthResponse(
            Token: token,
            Id: user.Id,
            Email: user.Email,
            Username: user.Username,
            ExpiresAt: GetExpiresAt(),
            ThemeSettings: user.ThemeSettings
        );
    }

    public async Task<CurrentUserDto?> GetCurrentUserAsync(
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

        return new CurrentUserDto(user.Id, user.Email, user.Username, user.ThemeSettings);
    }

    public async Task<AuthResponse?> ExternalCallbackAsync(
        ExternalAuthRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ExternalUserPrincipal? principal = null;

        var externalToken = !string.IsNullOrWhiteSpace(request.IdToken)
            ? request.IdToken
            : request.AccessToken;

        if (!string.IsNullOrWhiteSpace(externalToken))
        {
            principal = await oidcService.ValidateIdTokenAsync(externalToken, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(request.Code))
        {
            principal = await oidcService.ExchangeCodeAsync(
                request.Code,
                request.RedirectUri ?? string.Empty,
                cancellationToken
            );
        }

        if (principal is null)
        {
            return null;
        }

        var userSummary = await ProvisionOrGetExternalUserAsync(principal, cancellationToken);
        var token = jwtTokenService.GenerateToken(userSummary);

        return new AuthResponse(
            Token: token,
            Id: userSummary.Id,
            Email: userSummary.Email,
            Username: userSummary.Username,
            ExpiresAt: GetExpiresAt(),
            ThemeSettings: userSummary.ThemeSettings
        );
    }

    public async Task<UserSummary> ProvisionOrGetExternalUserAsync(
        ExternalUserPrincipal principal,
        CancellationToken cancellationToken = default
    )
    {
        var normalizedEmail = principal.Email.Trim().ToUpperInvariant();

        // 1. Find by external subject
        var user = await dbContext.Users.FirstOrDefaultAsync(
            u =>
                u.ExternalProvider == principal.Provider
                && u.ExternalSubjectId == principal.SubjectId,
            cancellationToken
        );

        // 2. If not found by external subject, find by email to link account
        if (user is null)
        {
            user = await dbContext.Users.FirstOrDefaultAsync(
                u => u.NormalizedEmail == normalizedEmail,
                cancellationToken
            );

            if (user is not null)
            {
                user.ExternalProvider = principal.Provider;
                user.ExternalSubjectId = principal.SubjectId;
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        // 3. If still not found, create new user
        if (user is null)
        {
            var baseUsername = !string.IsNullOrWhiteSpace(principal.Name)
                ? principal.Name.Trim()
                : principal.Email.Split('@')[0];

            var username = baseUsername;
            var suffix = 1;
            while (
                await dbContext.Users.AnyAsync(
                    u => u.NormalizedUsername == username.ToUpperInvariant(),
                    cancellationToken
                )
            )
            {
                username = $"{baseUsername}{suffix++}";
            }

            user = new User
            {
                Id = Guid.NewGuid(),
                Email = principal.Email.Trim(),
                NormalizedEmail = normalizedEmail,
                Username = username,
                NormalizedUsername = username.ToUpperInvariant(),
                ExternalProvider = principal.Provider,
                ExternalSubjectId = principal.SubjectId,
                PasswordHash = string.Empty,
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedBy = principal.Provider,
                ModifiedAt = DateTimeOffset.UtcNow,
                ModifiedBy = principal.Provider,
            };

            dbContext.Users.Add(user);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new UserSummary(user.Id, user.Email, user.Username, user.ThemeSettings);
    }

    private DateTimeOffset? GetExpiresAt() =>
        _jwtOptions.ExpirationMinutes > 0
            ? DateTimeOffset.UtcNow.AddMinutes(_jwtOptions.ExpirationMinutes)
            : null;
}
