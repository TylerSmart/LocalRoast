namespace App.API.Http;

using System.Reflection;
using System.Security.Cryptography;
using App.Core.System;

public static class ApiBuildVersion
{
    private static readonly DateTimeOffset ProcessStartedAt = DateTimeOffset.UtcNow;
    private static readonly object SyncLock = new();
    private static ApplicationVersionDto? _cachedInfo;

    public static ApplicationVersionDto Info
    {
        get
        {
            if (_cachedInfo is not null)
            {
                return _cachedInfo;
            }

            lock (SyncLock)
            {
                _cachedInfo ??= ResolveVersionInfo(null);
                return _cachedInfo;
            }
        }
    }

    public static string Value => Info.Version;

    public static void Initialize(string? explicitVersion = null)
    {
        lock (SyncLock)
        {
            _cachedInfo = ResolveVersionInfo(explicitVersion);
        }
    }

    public static ApplicationVersionDto ResolveVersionInfo(
        string? explicitVersion,
        string? informationalVersionOverride = null
    )
    {
        // 1. Explicitly configured version or environment variables
        var envVersion = !string.IsNullOrWhiteSpace(explicitVersion)
            ? explicitVersion
            : Environment.GetEnvironmentVariable("APP_VERSION")
                ?? Environment.GetEnvironmentVariable("DOCKER_SHA")
                ?? Environment.GetEnvironmentVariable("BUILD_SHA")
                ?? Environment.GetEnvironmentVariable("GIT_SHA");

        if (!string.IsNullOrWhiteSpace(envVersion))
        {
            var trimmed = envVersion.Trim();
            var isSha = IsHexString(trimmed);
            var shortSha = isSha && trimmed.Length > 12 ? trimmed[..12] : trimmed;
            var source =
                !string.IsNullOrWhiteSpace(explicitVersion) ? "Configured"
                : Environment.GetEnvironmentVariable("DOCKER_SHA") is not null ? "Docker"
                : "Environment";

            return new ApplicationVersionDto(
                Version: shortSha,
                CommitSha: isSha ? trimmed : null,
                Source: source,
                StartedAt: ProcessStartedAt,
                InformationalVersion: trimmed
            );
        }

        // 2. Check AssemblyInformationalVersion (injected automatically by .NET SDK when building in git)
        var informationalVersion =
            informationalVersionOverride
            ?? typeof(ApiBuildVersion)
                .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var plusIndex = informationalVersion.IndexOf('+');
            if (plusIndex >= 0 && plusIndex < informationalVersion.Length - 1)
            {
                var commitSha = informationalVersion[(plusIndex + 1)..].Trim();
                if (IsHexString(commitSha))
                {
                    var shortSha = commitSha.Length > 12 ? commitSha[..12] : commitSha;
                    return new ApplicationVersionDto(
                        Version: shortSha,
                        CommitSha: commitSha,
                        Source: "Git",
                        StartedAt: ProcessStartedAt,
                        InformationalVersion: informationalVersion
                    );
                }
            }
        }

        // 3. Fallback: generate a random unique identifier for this instance/deployment
        var randomToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var fallbackBaseVersion =
            typeof(ApiBuildVersion).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

        return new ApplicationVersionDto(
            Version: randomToken,
            CommitSha: null,
            Source: "Generated",
            StartedAt: ProcessStartedAt,
            InformationalVersion: $"{fallbackBaseVersion}-{randomToken}"
        );
    }

    private static bool IsHexString(string value)
    {
        if (value.Length < 7)
        {
            return false;
        }

        foreach (var c in value)
        {
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }
}
