using App.Core.Authentication;
using App.Core.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace App.API.Authentication;

public static class AuthenticationServiceCollectionExtensions
{
    public const string LocalJwtScheme = JwtBearerDefaults.AuthenticationScheme;
    public const string McpPatScheme = "McpPat";
    public const string McpAuthorizationPolicy = "McpPersonalAccessTokenPolicy";

    public static IServiceCollection AddAppAuthentication(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddHttpContextAccessor();
        services.AddScoped<IUserContext, HttpContextUserContext>();
        services.AddMemoryCache();

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));

        services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = LocalJwtScheme;
                options.DefaultChallengeScheme = LocalJwtScheme;
            })
            .AddScheme<AuthenticationSchemeOptions, DualBearerAuthenticationHandler>(
                LocalJwtScheme,
                _ => { }
            )
            .AddScheme<AuthenticationSchemeOptions, PersonalAccessTokenAuthenticationHandler>(
                McpPatScheme,
                _ => { }
            );

        services.AddAuthorization(options =>
        {
            options.AddPolicy(
                McpAuthorizationPolicy,
                policy => policy.AddAuthenticationSchemes(McpPatScheme).RequireAuthenticatedUser()
            );
        });

        return services;
    }
}
