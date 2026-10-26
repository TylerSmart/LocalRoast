using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace App.API.IntegrationTests;

public class AppWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = "TestDb_" + Guid.NewGuid();
    private readonly string _uploadsPath = Path.Combine(
        Path.GetTempPath(),
        "LocalRoast_ApiTests_" + Guid.NewGuid().ToString("N")
    );

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("UseInMemoryDatabase", "true");
        builder.UseSetting("InMemoryDatabaseName", _databaseName);
        builder.UseSetting("MediaStorage:UploadsPath", _uploadsPath);
        builder.UseSetting(
            "Authentication:Jwt:Secret",
            "Test_Secret_Key_For_App_Integration_Tests_12345!"
        );
        builder.UseSetting("Authentication:Jwt:Issuer", "App");
        builder.UseSetting("Authentication:Jwt:Audience", "App");
        builder.UseSetting("Authentication:Oidc:Enabled", "true");
        builder.UseSetting("Authentication:Oidc:ProviderName", "TestOIDC");
        builder.UseSetting("Llm:EnableBackgroundWorker", "false");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            try
            {
                if (Directory.Exists(_uploadsPath))
                {
                    Directory.Delete(_uploadsPath, true);
                }
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }
}
