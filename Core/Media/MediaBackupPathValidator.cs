namespace App.Core.Media;

public static class MediaBackupPathValidator
{
    public static readonly IReadOnlyList<string> AllowedSubdirectories =
    [
        "recipes",
        "steps",
        "ingestion",
        "attachments",
        "cook-logs",
    ];

    private static readonly HashSet<string> AllowedSubdirectorySet = new(
        AllowedSubdirectories,
        StringComparer.OrdinalIgnoreCase
    );

    public static bool TryNormalizeRelativePath(
        string? rawRelativePath,
        out string normalizedRelativePath
    )
    {
        normalizedRelativePath = string.Empty;
        if (string.IsNullOrWhiteSpace(rawRelativePath))
        {
            return false;
        }

        var trimmed = rawRelativePath.Trim();
        if (trimmed.Contains('\0', StringComparison.Ordinal) || trimmed.Contains(':'))
        {
            return false;
        }

        var slashNormalized = trimmed.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(slashNormalized))
        {
            return false;
        }

        var segments = slashNormalized.Split('/');
        if (segments.Length < 2)
        {
            return false;
        }

        var topDir = AllowedSubdirectories.FirstOrDefault(dir =>
            string.Equals(dir, segments[0], StringComparison.OrdinalIgnoreCase)
        );
        if (topDir is null || !AllowedSubdirectorySet.Contains(topDir))
        {
            return false;
        }

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (
                string.IsNullOrWhiteSpace(segment)
                || segment is "." or ".."
                || segment.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0
            )
            {
                return false;
            }
        }

        segments[0] = topDir;
        normalizedRelativePath = string.Join('/', segments);
        return true;
    }
}
