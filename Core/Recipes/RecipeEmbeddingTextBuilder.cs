using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using App.Core.FoodCatalog;

namespace App.Core.Recipes;

public record RecipeEmbeddingIngredientInput(
    string? CanonicalFoodName,
    string? DisplayName,
    bool IsPantryStaple,
    bool IsOptional,
    string? LinkedRecipeTitle = null
);

public record RecipeEmbeddingDocument(
    string Title,
    string? Description,
    int? TotalTimeMinutes,
    RecipeDifficulty? Difficulty,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Collections,
    IReadOnlyList<RecipeEmbeddingIngredientInput> Ingredients
);

public record BuiltRecipeEmbeddingInput(string EmbeddingText, string ContentHash);

/// <summary>
/// Deterministically builds a compact semantic planning-card representation and SHA-256 content
/// hash for a recipe so recipe embeddings stay fresh on edits without redundant re-embedding.
/// </summary>
public static partial class RecipeEmbeddingTextBuilder
{
    public const int MaxEmbeddingTextLength = 2000;
    private const int MaxDescriptionLength = 300;
    private const int MaxIngredients = 24;

    public static BuiltRecipeEmbeddingInput Build(RecipeEmbeddingDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var lines = new List<string>(6);

        var title = CollapseWhitespace(document.Title);
        if (!string.IsNullOrEmpty(title))
        {
            lines.Add($"Title: {title}");
        }

        var summary = CollapseWhitespace(document.Description);
        if (!string.IsNullOrEmpty(summary))
        {
            if (summary.Length > MaxDescriptionLength)
            {
                summary = summary[..MaxDescriptionLength].TrimEnd();
            }
            lines.Add($"Summary: {summary}");
        }

        var metaParts = new List<string>(2);
        if (document.TotalTimeMinutes is int mins and > 0)
        {
            var quickCue = mins <= 35 ? " (quick weeknight)" : string.Empty;
            metaParts.Add($"{mins} min total{quickCue}");
        }
        if (document.Difficulty.HasValue)
        {
            metaParts.Add($"Difficulty: {document.Difficulty.Value}");
        }
        if (metaParts.Count > 0)
        {
            lines.Add($"Timing: {string.Join(" | ", metaParts)}");
        }

        var tags = (document.Tags ?? [])
            .Select(CollapseWhitespace)
            .Where(t => !string.IsNullOrEmpty(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (tags.Count > 0)
        {
            lines.Add($"Tags: {string.Join(", ", tags)}");
        }

        var collections = (document.Collections ?? [])
            .Select(CollapseWhitespace)
            .Where(c => !string.IsNullOrEmpty(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (collections.Count > 0)
        {
            lines.Add($"Collections: {string.Join(", ", collections)}");
        }

        var ingredients = ExtractCoreIngredients(document.Ingredients);
        if (ingredients.Count > 0)
        {
            lines.Add($"Ingredients: {string.Join(", ", ingredients)}");
        }

        var text = string.Join("\n", lines);
        if (text.Length > MaxEmbeddingTextLength)
        {
            text = text[..MaxEmbeddingTextLength].TrimEnd();
        }

        var hash = ComputeContentHash(text);
        return new BuiltRecipeEmbeddingInput(text, hash);
    }

    public static string ComputeContentHash(string embeddingText)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(embeddingText ?? string.Empty));
        return Convert.ToHexStringLower(bytes);
    }

    private static List<string> ExtractCoreIngredients(
        IReadOnlyList<RecipeEmbeddingIngredientInput>? ingredients
    )
    {
        if (ingredients is null || ingredients.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(Math.Min(MaxIngredients, ingredients.Count));

        // Non-optional core items first, then optional items, skipping pantry staples.
        foreach (var ing in ingredients.OrderBy(i => i.IsOptional ? 1 : 0))
        {
            if (result.Count >= MaxIngredients)
            {
                break;
            }

            if (ing.IsPantryStaple)
            {
                continue;
            }

            string? label = null;
            if (!string.IsNullOrWhiteSpace(ing.LinkedRecipeTitle))
            {
                label = CollapseWhitespace(ing.LinkedRecipeTitle);
            }
            else if (!string.IsNullOrWhiteSpace(ing.CanonicalFoodName))
            {
                label = CollapseWhitespace(ing.CanonicalFoodName);
            }
            else if (!string.IsNullOrWhiteSpace(ing.DisplayName))
            {
                var normalized = FoodNameSanitizer.NormalizeCanonicalFoodName(ing.DisplayName);
                label = CollapseWhitespace(
                    !string.IsNullOrWhiteSpace(normalized) ? normalized : ing.DisplayName
                );
            }

            if (!string.IsNullOrWhiteSpace(label) && seen.Add(label))
            {
                result.Add(label);
            }
        }

        return result;
    }

    private static string CollapseWhitespace(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return WhitespaceRegex().Replace(value.Trim(), " ");
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
