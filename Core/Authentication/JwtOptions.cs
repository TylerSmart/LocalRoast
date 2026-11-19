namespace App.Core.Authentication;

public sealed class JwtOptions
{
    public const string SectionName = "Authentication:Jwt";

    public string Secret { get; set; } = string.Empty;

    public string Issuer { get; set; } = "App";

    public string Audience { get; set; } = "App";

    /// <summary>
    /// Session lifetime in minutes. Values &lt;= 0 issue non-expiring tokens (default).
    /// </summary>
    public int ExpirationMinutes { get; set; } = 0;
}
