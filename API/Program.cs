using App.API.Authentication;
using App.API.Hosting;
using App.API.Http;
using App.API.Mcp;
using App.Core;
using App.Core.Authentication;
using App.Data;
using App.Data.Seeding;
using App.Infrastructure;
using App.Infrastructure.Media;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.OpenApi;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog(
    (context, services, configuration) =>
        configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
);

builder.Services.AddProblemDetails();

builder.Services.AddControllers();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .SetIsOriginAllowed(origin =>
            {
                if (Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                {
                    return uri.IsLoopback;
                }
                return false;
            })
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

builder.Services.AddOpenApi(
    "v1",
    options =>
    {
        options.AddDocumentTransformer(
            (document, context, cancellationToken) =>
            {
                document.Info.Title = builder.Configuration["App:Name"] ?? "App";
                document.Info.Version = "v1";
                document.Servers = [new OpenApiServer { Url = "/api" }];

                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
                {
                    ["Bearer"] = new OpenApiSecurityScheme
                    {
                        Type = SecuritySchemeType.Http,
                        Scheme = "bearer",
                        BearerFormat = "JWT",
                        Description =
                            "JWT Authorization header using the Bearer scheme. Enter your JWT token to authorize requests across all endpoints.",
                    },
                };

                var oidcOptions = builder
                    .Configuration.GetSection(OidcOptions.SectionName)
                    .Get<OidcOptions>();
                if (oidcOptions?.Enabled == true)
                {
                    var authEndpoint = !string.IsNullOrWhiteSpace(oidcOptions.AuthorizationEndpoint)
                        ? oidcOptions.AuthorizationEndpoint
                        : (
                            !string.IsNullOrWhiteSpace(oidcOptions.Authority)
                                ? $"{oidcOptions.Authority}/o/oauth2/v2/auth"
                                : null
                        );
                    var tokenEndpoint = !string.IsNullOrWhiteSpace(oidcOptions.TokenEndpoint)
                        ? oidcOptions.TokenEndpoint
                        : (
                            !string.IsNullOrWhiteSpace(oidcOptions.Authority)
                                ? $"{oidcOptions.Authority}/token"
                                : null
                        );

                    if (
                        Uri.TryCreate(authEndpoint, UriKind.Absolute, out var authUri)
                        && Uri.TryCreate(tokenEndpoint, UriKind.Absolute, out var tokenUri)
                    )
                    {
                        document.Components.SecuritySchemes["OAuth2"] = new OpenApiSecurityScheme
                        {
                            Type = SecuritySchemeType.OAuth2,
                            Description =
                                $"{oidcOptions.ProviderName} OAuth2 / OIDC authentication flow.",
                            Flows = new OpenApiOAuthFlows
                            {
                                AuthorizationCode = new OpenApiOAuthFlow
                                {
                                    AuthorizationUrl = authUri,
                                    TokenUrl = tokenUri,
                                    Scopes = new Dictionary<string, string>
                                    {
                                        ["openid"] = "OpenID Connect",
                                        ["profile"] = "User Profile",
                                        ["email"] = "User Email",
                                    },
                                },
                            },
                        };
                    }
                }

                return Task.CompletedTask;
            }
        );

        options.AddOperationTransformer(
            (operation, context, cancellationToken) =>
            {
                var metadata = context.Description.ActionDescriptor.EndpointMetadata;
                var hasAuthorize = metadata.OfType<AuthorizeAttribute>().Any();
                var allowAnonymous = metadata.OfType<AllowAnonymousAttribute>().Any();

                if (hasAuthorize && !allowAnonymous)
                {
                    operation.Security ??= [];
                    operation.Security.Add(
                        new OpenApiSecurityRequirement
                        {
                            [new OpenApiSecuritySchemeReference("Bearer", null, null)] = [],
                        }
                    );
                }

                return Task.CompletedTask;
            }
        );
    }
);

builder.Services.AddCoreServices();

builder.Services.AddInfrastructureServices(builder.Configuration);

builder.Services.AddDataServices(builder.Configuration);

builder.Services.AddAppAuthentication(builder.Configuration);

builder.Services.AddAppMcpServer();

// Every other Angular asset is content-hashed and safe to cache forever, but index.html keeps its
// name across deploys. Without this it can be served from cache and reference a previous deploy's
// bundle. Applies to UseStaticFiles and MapFallbackToFile, which both read these options.
builder.Services.Configure<StaticFileOptions>(options =>
{
    options.OnPrepareResponse = context =>
    {
        if (string.Equals(context.File.Name, "index.html", StringComparison.OrdinalIgnoreCase))
        {
            context.Context.Response.Headers.CacheControl = "no-cache, must-revalidate";
        }
    };
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (db.Database.IsRelational())
    {
        db.Database.Migrate();
    }

    await HouseholdCatalogSeeder.SeedAsync(db);
}

app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate =
        "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
    options.GetLevel = (httpContext, _, exception) =>
        exception is not null
        || httpContext.Response.StatusCode >= StatusCodes.Status500InternalServerError
            ? LogEventLevel.Error
        : httpContext.Response.StatusCode >= StatusCodes.Status400BadRequest ? LogEventLevel.Warning
        : LogEventLevel.Information;
    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        diagnosticContext.Set("TraceIdentifier", httpContext.TraceIdentifier);
        diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
        diagnosticContext.Set("RequestScheme", httpContext.Request.Scheme);
    };
});

app.UseExceptionHandler();

app.UseMiddleware<ApiVersionHeaderMiddleware>();

var hasHttpsUrl =
    builder
        .Configuration["ASPNETCORE_URLS"]
        ?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Any(url => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) == true;

if (hasHttpsUrl)
{
    app.UseHttpsRedirection();
}

var uploadsRoot = MediaStorageOptions.ResolveUploadsRoot(
    builder.Configuration["MediaStorage:UploadsPath"],
    app.Environment.ContentRootPath
);
Directory.CreateDirectory(uploadsRoot);

var uploadsContentTypeProvider = new FileExtensionContentTypeProvider();
uploadsContentTypeProvider.Mappings[".webp"] = "image/webp";
uploadsContentTypeProvider.Mappings[".jpg"] = "image/jpeg";
uploadsContentTypeProvider.Mappings[".jpeg"] = "image/jpeg";
uploadsContentTypeProvider.Mappings[".png"] = "image/png";
uploadsContentTypeProvider.Mappings[".gif"] = "image/gif";
uploadsContentTypeProvider.Mappings[".pdf"] = "application/pdf";

app.UseStaticFiles(
    new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(uploadsRoot),
        RequestPath = "/api/uploads",
        ContentTypeProvider = uploadsContentTypeProvider,
        OnPrepareResponse = ctx =>
        {
            ctx.Context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        },
    }
);

var serveSpa = SpaHosting.HasIndexHtml(app.Environment.WebRootPath);
if (serveSpa)
{
    app.UseDefaultFiles();

    app.UseStaticFiles();
}

app.MapOpenApi("/api/openapi/{documentName}.json").AllowAnonymous();

app.MapScalarApiReference(
        "/api/scalar",
        options =>
        {
            options.WithOpenApiRoutePattern("/api/openapi/{documentName}.json");
            options.WithTitle(builder.Configuration["App:Name"] ?? "App");
            options.PersistentAuthentication = true;
            options.Authentication = new ScalarAuthenticationOptions
            {
                PreferredSecuritySchemes = ["Bearer"],
            };

            var oidcConfig = builder
                .Configuration.GetSection(OidcOptions.SectionName)
                .Get<OidcOptions>();
            if (oidcConfig?.Enabled == true)
            {
                options.AddAuthorizationCodeFlow(
                    "OAuth2",
                    flow =>
                    {
                        flow.ClientId = oidcConfig.ClientId;
                        flow.ClientSecret = oidcConfig.ClientSecret;
                        flow.SelectedScopes = ["openid", "profile", "email"];
                        flow.Pkce = Pkce.Sha256;
                    }
                );
            }
        }
    )
    .AllowAnonymous();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapGroup("/api").MapControllers();

app.MapMcp("/api/mcp")
    .RequireAuthorization(
        App.API.Authentication.AuthenticationServiceCollectionExtensions.McpAuthorizationPolicy
    )
    .ExcludeFromDescription();

if (serveSpa)
{
    app.MapFallbackToFile("index.html");
}

app.Run();

public partial class Program;
