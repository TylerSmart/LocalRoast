using App.Core.Account;
using App.Core.Authentication;
using App.Core.FoodCatalog;
using App.Core.Ingestion;
using App.Core.Mcp;
using App.Core.Recipes;
using App.Data.Auditing;
using App.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace App.Data;

public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddDataServices(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddScoped<AuditSaveChangesInterceptor>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IPersonalAccessTokenService, PersonalAccessTokenService>();
        services.AddScoped<IFoodCatalogService, FoodCatalogService>();
        services.AddScoped<IRecipeService, RecipeService>();
        services.AddScoped<ITaxonomyService, TaxonomyService>();
        services.AddScoped<IShareLinkService, ShareLinkService>();
        services.AddScoped<ILlmSettingsService, LlmSettingsService>();
        services.AddScoped<IIngestionProcessor, IngestionProcessor>();
        services.AddScoped<IIngestionService, IngestionService>();
        services.AddScoped<IMcpCookbookService, McpCookbookService>();

        if (
            string.Equals(
                configuration["UseInMemoryDatabase"],
                "true",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            var inMemoryDbName = configuration["InMemoryDatabaseName"] ?? "App_Testing";
            services.AddDbContext<AppDbContext>(
                (provider, options) =>
                {
                    options.UseInMemoryDatabase(inMemoryDbName);
                    options.AddInterceptors(
                        provider.GetRequiredService<AuditSaveChangesInterceptor>()
                    );
                }
            );

            return services;
        }

        var connectionString =
            ResolveConfiguredConnectionString(configuration)
            ?? throw new InvalidOperationException(
                "ConnectionStrings:DefaultConnection is not configured."
            );

        services.AddDbContext<AppDbContext>(
            (provider, options) =>
            {
                options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure());
                options.AddInterceptors(provider.GetRequiredService<AuditSaveChangesInterceptor>());
            }
        );

        return services;
    }

    internal static string? ResolveConfiguredConnectionString(IConfiguration configuration)
    {
        var appName = configuration["App:Name"];
        return FirstConfigured(
            configuration.GetConnectionString("DefaultConnection"),
            configuration["ConnectionString:DefaultConnection"],
            !string.IsNullOrWhiteSpace(appName) ? configuration.GetConnectionString(appName) : null,
            !string.IsNullOrWhiteSpace(appName)
                ? configuration[$"ConnectionString:{appName}"]
                : null
        );
    }

    private static string? FirstConfigured(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (
                !string.IsNullOrWhiteSpace(candidate)
                && !string.Equals(candidate, "REPLACE_ME", StringComparison.OrdinalIgnoreCase)
            )
            {
                return candidate;
            }
        }

        return null;
    }
}
