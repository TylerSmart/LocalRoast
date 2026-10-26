using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.API.IntegrationTests;

[TestClass]
public sealed class OpenApiIntegrationTests
{
    private AppWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    [TestInitialize]
    public void Setup()
    {
        _factory = new AppWebApplicationFactory();
        _client = _factory.CreateClient();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [TestMethod]
    public async Task GetOpenApiDocument_ReturnsValidJson()
    {
        var response = await _client.GetAsync("/api/openapi/v1.json");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var expectedTitle =
            _factory.Services.GetRequiredService<IConfiguration>()["App:Name"] ?? "App";
        var content = await response.Content.ReadAsStringAsync();
        Assert.IsFalse(string.IsNullOrWhiteSpace(content));
        StringAssert.Contains(content, expectedTitle);
        StringAssert.Contains(content, "/recipes");
        StringAssert.Contains(content, "/food-items");
        StringAssert.Contains(content, "/store-aisles");
        StringAssert.Contains(content, "/collections");
        StringAssert.Contains(content, "/tags");
        StringAssert.Contains(content, "/media");
        StringAssert.Contains(content, "/public/shares");
        StringAssert.Contains(content, "/shares");
        StringAssert.Contains(content, "/auth");
        StringAssert.Contains(content, "/llm-settings");
        StringAssert.Contains(content, "/ingestion/jobs");
        StringAssert.Contains(content, "securitySchemes");
        StringAssert.Contains(content, "Bearer");
    }
}
