namespace App.Core.System;

public sealed record ApplicationVersionDto(
    string Version,
    string? CommitSha,
    string Source,
    DateTimeOffset StartedAt,
    string? InformationalVersion = null
);
