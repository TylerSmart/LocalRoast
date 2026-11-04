using System.Net;
using System.Net.Http.Json;
using App.API.Http;
using App.Core.System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace App.API.IntegrationTests;

[TestClass]
public sealed class VersionIntegrationTests
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
    public async Task GetApiVersion_ReturnsOk_WithValidVersionInfoAndHeader()
    {
        var response = await _client.GetAsync("/api/version");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        // Header check
        Assert.IsTrue(response.Headers.Contains("X-Api-Version"));
        var headerValue = response.Headers.GetValues("X-Api-Version").FirstOrDefault();
        Assert.IsFalse(string.IsNullOrWhiteSpace(headerValue));

        // Body check
        var versionInfo = await response.Content.ReadFromJsonAsync<ApplicationVersionDto>();
        Assert.IsNotNull(versionInfo);
        Assert.IsFalse(string.IsNullOrWhiteSpace(versionInfo.Version));
        Assert.AreEqual(headerValue, versionInfo.Version);
        Assert.IsFalse(string.IsNullOrWhiteSpace(versionInfo.Source));
        Assert.IsTrue(
            versionInfo.StartedAt > DateTimeOffset.UtcNow.AddMinutes(-5)
                && versionInfo.StartedAt <= DateTimeOffset.UtcNow.AddMinutes(1),
            "StartedAt should be a recent UTC timestamp."
        );
    }

    [TestMethod]
    public async Task GetRootVersion_ReturnsOk_MatchingApiVersion()
    {
        var response = await _client.GetAsync("/version");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var versionInfo = await response.Content.ReadFromJsonAsync<ApplicationVersionDto>();
        Assert.IsNotNull(versionInfo);
        Assert.IsFalse(string.IsNullOrWhiteSpace(versionInfo.Version));
        Assert.AreEqual(ApiBuildVersion.Value, versionInfo.Version);
    }

    [TestMethod]
    public void ResolveVersionInfo_WithExplicitVersion_ReturnsConfiguredSource()
    {
        var info = ApiBuildVersion.ResolveVersionInfo("v2.5.0");

        Assert.AreEqual("v2.5.0", info.Version);
        Assert.IsNull(info.CommitSha);
        Assert.AreEqual("Configured", info.Source);
    }

    [TestMethod]
    public void ResolveVersionInfo_WithExplicitSha_SetsCommitShaAndShortVersion()
    {
        const string fullSha = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
        var info = ApiBuildVersion.ResolveVersionInfo(fullSha);

        Assert.AreEqual("4b825dc642cb", info.Version);
        Assert.AreEqual(fullSha, info.CommitSha);
        Assert.AreEqual("Configured", info.Source);
    }

    [TestMethod]
    public void ResolveVersionInfo_WithInformationalGitSha_ExtractsSha()
    {
        const string fullSha = "a1b2c3d4e5f67890abcdef1234567890abcdef12";
        var informational = $"1.0.0+{fullSha}";
        var info = ApiBuildVersion.ResolveVersionInfo(
            null,
            informationalVersionOverride: informational
        );

        Assert.AreEqual("a1b2c3d4e5f6", info.Version);
        Assert.AreEqual(fullSha, info.CommitSha);
        Assert.AreEqual("Git", info.Source);
        Assert.AreEqual(informational, info.InformationalVersion);
    }

    [TestMethod]
    public void ResolveVersionInfo_WithoutGitOrEnv_GeneratesRandomTwelveCharHex()
    {
        var info = ApiBuildVersion.ResolveVersionInfo(null, informationalVersionOverride: "1.0.0");

        Assert.IsNotNull(info.Version);
        Assert.AreEqual(12, info.Version.Length);
        Assert.IsTrue(info.Version.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')));
        Assert.IsNull(info.CommitSha);
        Assert.AreEqual("Generated", info.Source);
    }
}
