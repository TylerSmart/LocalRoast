namespace App.Infrastructure.Media;

public sealed class MediaStorageOptions
{
    public const string SectionName = "MediaStorage";

    public string UploadsPath { get; set; } = "App_Data/uploads";

    public long MaxUploadBytes { get; set; } = 15 * 1024 * 1024; // 15 MB

    public long MaxPdfUploadBytes { get; set; } = 25 * 1024 * 1024; // 25 MB

    public static string ResolveUploadsRoot(string? configuredPath, string contentRootPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && Path.IsPathRooted(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var relative = string.IsNullOrWhiteSpace(configuredPath)
            ? "App_Data/uploads"
            : configuredPath;
        return Path.GetFullPath(Path.Combine(contentRootPath, relative));
    }
}
