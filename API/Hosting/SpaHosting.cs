using Microsoft.AspNetCore.StaticFiles;

namespace App.API.Hosting;

public static class SpaHosting
{
    private static readonly HashSet<string> NoCacheFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "index.html",
        "ngsw.json",
        "ngsw-worker.js",
        "safety-worker.js",
        "worker-basic.min.js",
        "site.webmanifest",
    };

    public static bool HasIndexHtml(string? webRootPath) =>
        !string.IsNullOrWhiteSpace(webRootPath)
        && File.Exists(Path.Combine(webRootPath, "index.html"));

    public static bool ShouldDisableCache(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) && NoCacheFileNames.Contains(fileName);

    public static FileExtensionContentTypeProvider CreateContentTypeProvider()
    {
        var provider = new FileExtensionContentTypeProvider();
        provider.Mappings[".webmanifest"] = "application/manifest+json";
        return provider;
    }
}
