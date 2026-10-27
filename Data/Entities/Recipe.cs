using App.Core.Recipes;

namespace App.Data.Entities;

public sealed class Recipe : IAuditable
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string? Description { get; set; }

    public RecipeStatus Status { get; set; } = RecipeStatus.Published;

    public decimal BaseServings { get; set; } = 4m;

    public string? YieldDisplay { get; set; }

    public int? PrepTimeMinutes { get; set; }

    public int? CookTimeMinutes { get; set; }

    public int? TotalTimeMinutes { get; set; }

    public RecipeDifficulty? Difficulty { get; set; }

    public string? SourceUrl { get; set; }

    public string? SourceName { get; set; }

    public string? SourceAuthor { get; set; }

    public string? SourceBookTitle { get; set; }

    public int? SourceBookPage { get; set; }

    public string? CoverImagePath { get; set; }

    public string? ThumbnailImagePath { get; set; }

    public int TimesCooked { get; set; }

    public DateTimeOffset? LastCookedAt { get; set; }

    public decimal? AverageRating { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string CreatedBy { get; set; } = "System";

    public DateTimeOffset ModifiedAt { get; set; }

    public string ModifiedBy { get; set; } = "System";

    public ICollection<RecipeIngredient> Ingredients { get; set; } = new List<RecipeIngredient>();

    public ICollection<RecipeIngredient> ReferencedAsSubRecipeIn { get; set; } =
        new List<RecipeIngredient>();

    public ICollection<RecipeStep> Steps { get; set; } = new List<RecipeStep>();

    public ICollection<RecipeCollection> RecipeCollections { get; set; } =
        new List<RecipeCollection>();

    public ICollection<RecipeTag> RecipeTags { get; set; } = new List<RecipeTag>();

    public ICollection<ShareLink> ShareLinks { get; set; } = new List<ShareLink>();

    public ICollection<IngestionJob> IngestionJobs { get; set; } = new List<IngestionJob>();

    public ICollection<RecipeAttachment> Attachments { get; set; } = new List<RecipeAttachment>();
}
