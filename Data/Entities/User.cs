namespace App.Data.Entities;

public sealed class User : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Email { get; set; } = string.Empty;

    public string NormalizedEmail { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string NormalizedUsername { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public string? ExternalProvider { get; set; }

    public string? ExternalSubjectId { get; set; }

    public string? ThemeSettings { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "System";

    public DateTimeOffset ModifiedAt { get; set; }

    public string ModifiedBy { get; set; } = "System";

    public ICollection<PersonalAccessToken> PersonalAccessTokens { get; set; } =
        new List<PersonalAccessToken>();
}
