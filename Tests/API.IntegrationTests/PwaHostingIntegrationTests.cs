using System.Net;
using System.Text.Json;
using App.API.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.API.IntegrationTests;

[TestClass]
public sealed class PwaHostingIntegrationTests
{
    private string _tempWebRoot = null!;
    private PwaWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempWebRoot = Path.Combine(
            Path.GetTempPath(),
            "LocalRoast_PwaWebRoot_" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(_tempWebRoot);

        File.WriteAllText(
            Path.Combine(_tempWebRoot, "index.html"),
            "<!doctype html><html><head><link rel=\"manifest\" href=\"site.webmanifest\" /></head><body><app-root></app-root></body></html>"
        );
        File.WriteAllText(
            Path.Combine(_tempWebRoot, "site.webmanifest"),
            """{"id":"/","name":"LocalRoast","short_name":"LocalRoast","start_url":"/","display":"standalone"}"""
        );
        File.WriteAllText(Path.Combine(_tempWebRoot, "ngsw.json"), """{"configVersion":1}""");
        File.WriteAllText(
            Path.Combine(_tempWebRoot, "ngsw-worker.js"),
            "self.addEventListener('fetch', () => {});"
        );
        File.WriteAllText(
            Path.Combine(_tempWebRoot, "safety-worker.js"),
            "self.addEventListener('install', () => {});"
        );
        File.WriteAllText(Path.Combine(_tempWebRoot, "main-ABC12345.js"), "console.log('ok');");

        _factory = new PwaWebApplicationFactory(_tempWebRoot);
        _client = _factory.CreateClient();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _client.Dispose();
        _factory.Dispose();

        if (Directory.Exists(_tempWebRoot))
        {
            try
            {
                Directory.Delete(_tempWebRoot, recursive: true);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    [TestMethod]
    public async Task SiteWebManifest_IsServedWithManifestContentType_AndNoCacheHeader()
    {
        var response = await _client.GetAsync("/site.webmanifest");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(
            "application/manifest+json",
            response.Content.Headers.ContentType?.MediaType
        );
        AssertNoCacheHeader(response);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.AreEqual("LocalRoast", doc.RootElement.GetProperty("name").GetString());
        Assert.AreEqual("standalone", doc.RootElement.GetProperty("display").GetString());
    }

    [TestMethod]
    [DataRow("/index.html")]
    [DataRow("/ngsw.json")]
    [DataRow("/ngsw-worker.js")]
    [DataRow("/safety-worker.js")]
    public async Task ServiceWorkerControlFiles_EmitNoCacheMustRevalidateHeader(string path)
    {
        var response = await _client.GetAsync(path);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        AssertNoCacheHeader(response);
    }

    [TestMethod]
    public async Task HashedClientBundle_DoesNotEmitNoCacheHeader()
    {
        var response = await _client.GetAsync("/main-ABC12345.js");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsNull(
            response.Headers.CacheControl,
            "Content-hashed Angular bundles should not be forced to no-cache."
        );
    }

    [TestMethod]
    public void SpaHosting_ShouldDisableCache_MatchesOnlyUncachedControlFiles()
    {
        Assert.IsTrue(SpaHosting.ShouldDisableCache("index.html"));
        Assert.IsTrue(SpaHosting.ShouldDisableCache("ngsw.json"));
        Assert.IsTrue(SpaHosting.ShouldDisableCache("ngsw-worker.js"));
        Assert.IsTrue(SpaHosting.ShouldDisableCache("safety-worker.js"));
        Assert.IsTrue(SpaHosting.ShouldDisableCache("worker-basic.min.js"));
        Assert.IsTrue(SpaHosting.ShouldDisableCache("site.webmanifest"));
        Assert.IsFalse(SpaHosting.ShouldDisableCache("main-ABC12345.js"));
        Assert.IsFalse(SpaHosting.ShouldDisableCache("icon-512.png"));
    }

    private static void AssertNoCacheHeader(HttpResponseMessage response)
    {
        var cacheControl = response.Headers.CacheControl;
        Assert.IsNotNull(cacheControl, "Expected Cache-Control header to be present.");
        Assert.IsTrue(cacheControl.NoCache, "Expected Cache-Control to include no-cache.");
        Assert.IsTrue(
            cacheControl.MustRevalidate,
            "Expected Cache-Control to include must-revalidate."
        );
    }

    private sealed class PwaWebApplicationFactory(string webRootPath) : AppWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseWebRoot(webRootPath);
        }
    }
}
