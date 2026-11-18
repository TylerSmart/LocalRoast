using System.Collections.Concurrent;
using System.Reflection;

namespace App.Core.Ingestion;

/// <summary>
/// Loads embedded multi-stage LLM recipe ingestion prompt templates from App.Core resources.
/// </summary>
public static class IngestionPromptLoader
{
    public const string Stage1ExtractRecipe = "01-extract-recipe.md";
    public const string Stage2ShortenDescription = "02-shorten-description.md";
    public const string Stage3SearchTerms = "03a-ingredient-search-terms.md";
    public const string Stage3MatchIngredient = "03b-ingredient-match.md";
    public const string Stage3MatchBatch = "03c-ingredient-match-batch.md";
    public const string Stage4TokenizeStep = "04-tokenize-step.md";
    public const string Stage4TokenizeStepsBatch = "04b-tokenize-steps-batch.md";
    public const string Stage5PolishTargeted = "05b-polish-targeted.md";

    private static readonly ConcurrentDictionary<string, string> Cache = new(
        StringComparer.OrdinalIgnoreCase
    );

    public static string LoadPrompt(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        return Cache.GetOrAdd(
            fileName,
            static name =>
            {
                var assembly = typeof(IngestionPromptLoader).Assembly;
                var expectedSuffix = $".Ingestion.Prompts.{name}";
                var resourceName = assembly
                    .GetManifestResourceNames()
                    .FirstOrDefault(r =>
                        r.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase)
                    );

                if (resourceName is null)
                {
                    throw new InvalidOperationException(
                        $"Embedded ingestion prompt '{name}' was not found in assembly '{assembly.FullName}'."
                    );
                }

                using var stream =
                    assembly.GetManifestResourceStream(resourceName)
                    ?? throw new InvalidOperationException(
                        $"Unable to open embedded prompt stream '{resourceName}'."
                    );
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd().Trim();
            }
        );
    }

    public static string LoadShortenDescriptionPrompt(int maxLength)
    {
        var targetLength = Math.Max(1, (int)Math.Floor(maxLength * 0.75));
        return LoadPrompt(Stage2ShortenDescription)
            .Replace("{{MAX_LENGTH}}", maxLength.ToString(), StringComparison.Ordinal)
            .Replace("{{TARGET_LENGTH}}", targetLength.ToString(), StringComparison.Ordinal);
    }
}
