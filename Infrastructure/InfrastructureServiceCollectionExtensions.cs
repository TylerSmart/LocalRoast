using App.Core.Authentication;
using App.Core.Media;
using App.Infrastructure.Authentication;
using App.Infrastructure.Media;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace App.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.AddSingleton<IJwtTokenService, JwtTokenService>();

        services.Configure<OidcOptions>(configuration.GetSection(OidcOptions.SectionName));
        services.AddHttpClient<IOidcService, OidcService>();

        services.Configure<MediaStorageOptions>(
            configuration.GetSection(MediaStorageOptions.SectionName)
        );
        services.AddScoped<IMediaStorageService, LocalFileMediaStorageService>();

        return services;
    }
}
