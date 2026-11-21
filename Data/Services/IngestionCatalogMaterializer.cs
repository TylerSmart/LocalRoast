using System.Text.Json;
using System.Text.RegularExpressions;
using App.Core.Exceptions;
using App.Core.FoodCatalog;
using App.Core.Ingestion;
using App.Core.Media;
using App.Core.Recipes;
using App.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace App.Data.Services;

public static partial class IngestionCatalogMaterializer
{
    [GeneratedRegex(
        @"\b(?:recipe\s+(?:follows|below|above|included|on\s+(?:this\s+|facing\s+)?page)|1\s+recipe\b|see\s+recipe\b)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex ExplicitRecipeReferenceCueRegex();

    [GeneratedRegex(
        @"\b(?:this\s+page|facing\s+page)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex PageReferenceCueRegex();

    public static string? ValidateOptionalMediaPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim();

        if (
            !trimmed.StartsWith("/api/uploads/", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("..")
            || trimmed.Contains('\\')
        )
        {
            throw new DomainValidationException(
                "Invalid media path. Uploaded media paths must start with '/api/uploads/'."
            );
        }

        return trimmed;
    }

    public static async Task<Guid> ResolveOrCreateFoodItemAsync(
        AppDbContext dbContext,
        string proposedFoodItemName,
        bool suggestedIsPantryStaple,
        FoodCategory? suggestedCategory,
        CancellationToken cancellationToken
    )
    {
        var cleanName = proposedFoodItemName.Trim();

        if (!FoodNameSanitizer.IsValidFoodName(cleanName))
        {
            throw new DomainValidationException(
                $"Cannot create or resolve a food item with invalid placeholder name '{cleanName}'."
            );
        }

        if (cleanName.Length > 150)
        {
            cleanName = cleanName[..150].Trim();
        }

        var normName = cleanName.ToLowerInvariant();

        var singularNorm = RecipeUnitAndSectionHelpers.Singularize(normName);

        var candidateNorms = new[]
        {
            normName,
            singularNorm,
            $"{singularNorm}s",
            $"{singularNorm}es",
        }
            .Distinct()
            .ToArray();

        var existing =
            await dbContext.FoodItems.FirstOrDefaultAsync(
                f => candidateNorms.Contains(f.NormalizedName),
                cancellationToken
            )
            ?? await dbContext
                .FoodItemAliases.Where(a => candidateNorms.Contains(a.NormalizedAlias))
                .Select(a => a.FoodItem)
                .FirstOrDefaultAsync(cancellationToken);

        if (existing is not null)
        {
            return existing.Id;
        }

        var newFoodItem = new FoodItem
        {
            Name = cleanName,

            NormalizedName = normName,

            IsPantryStaple = suggestedIsPantryStaple,

            Category = suggestedCategory ?? FoodCategoryClassifier.Classify(cleanName),
        };

        dbContext.FoodItems.Add(newFoodItem);

        await dbContext.SaveChangesAsync(cancellationToken);

        return newFoodItem.Id;
    }

    public sealed record MatchedRecipeReference(Guid Id, string Title, string Slug);

    public static async Task<MatchedRecipeReference?> FindMatchingRecipeAsync(
        AppDbContext dbContext,
        string title,
        Guid? excludedRecipeId,
        CancellationToken cancellationToken
    )
    {
        var cleanTitle =
            RecipeUnitAndSectionHelpers.NormalizeRecipeReferenceTitle(title) ?? title.Trim();
        if (string.IsNullOrWhiteSpace(cleanTitle))
        {
            return null;
        }

        var normalizedTitle = cleanTitle.ToUpperInvariant();
        var normalizedStraight = NormalizeTitleComparisonKey(cleanTitle).ToUpperInvariant();

        var exact = await dbContext
            .Recipes.AsNoTracking()
            .Where(r => !excludedRecipeId.HasValue || r.Id != excludedRecipeId.Value)
            .Where(r =>
                r.Title.ToUpper() == normalizedTitle
                || r.Title.Replace("\u2019", "'").Replace("\u2018", "'").ToUpper()
                    == normalizedStraight
            )
            .Select(r => new MatchedRecipeReference(r.Id, r.Title, r.Slug))
            .FirstOrDefaultAsync(cancellationToken);

        if (exact is not null)
        {
            return exact;
        }

        var prefixUpper = normalizedStraight + " (";
        var candidates = await dbContext
            .Recipes.AsNoTracking()
            .Where(r => !excludedRecipeId.HasValue || r.Id != excludedRecipeId.Value)
            .Where(r =>
                r.Title.Replace("\u2019", "'")
                    .Replace("\u2018", "'")
                    .ToUpper()
                    .StartsWith(prefixUpper)
                || r.Title.EndsWith(")")
                || (normalizedStraight.EndsWith("PIE DOUGH") && r.Title.ToUpper().Contains("PIE"))
            )
            .Select(r => new MatchedRecipeReference(r.Id, r.Title, r.Slug))
            .ToListAsync(cancellationToken);

        return candidates.FirstOrDefault(c => IsRecipeReferenceTitleMatch(c.Title, cleanTitle));
    }

    public static bool IsRecipeReferenceTitleMatch(
        string publishedTitle,
        string cleanReferenceTitle
    )
    {
        var normPublished = NormalizeTitleComparisonKey(publishedTitle);
        var normRef = NormalizeTitleComparisonKey(cleanReferenceTitle);
        if (string.IsNullOrWhiteSpace(normPublished) || string.IsNullOrWhiteSpace(normRef))
        {
            return false;
        }

        if (string.Equals(normPublished, normRef, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normPublished.StartsWith(normRef + " (", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var strippedPublished = Regex.Replace(normPublished, @"\s*\([^()]+\)\s*$", "").Trim();
        if (
            !string.IsNullOrWhiteSpace(strippedPublished)
            && string.Equals(strippedPublished, normRef, StringComparison.OrdinalIgnoreCase)
        )
        {
            return true;
        }

        if (normRef.EndsWith("pie dough", StringComparison.OrdinalIgnoreCase))
        {
            if (
                normRef.Contains("single-crust", StringComparison.OrdinalIgnoreCase)
                && normPublished.Contains("Single-Crust Pie", StringComparison.OrdinalIgnoreCase)
            )
            {
                return true;
            }
            if (
                normRef.Contains("double-crust", StringComparison.OrdinalIgnoreCase)
                && normPublished.Contains("Double-Crust Pie", StringComparison.OrdinalIgnoreCase)
            )
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeTitleComparisonKey(string title) =>
        title.Replace('\u2019', '\'').Replace('\u2018', '\'').Trim();

    public static async Task<Guid?> ResolveRecipeReferenceAsync(
        AppDbContext dbContext,
        string title,
        Guid? excludedRecipeId,
        CancellationToken cancellationToken
    ) => (await FindMatchingRecipeAsync(dbContext, title, excludedRecipeId, cancellationToken))?.Id;

    public static async Task<MatchedRecipeReference?> ResolveExplicitOrMatchedRecipeAsync(
        AppDbContext dbContext,
        IngestionDraftIngredientDto ing,
        string recipeTitle,
        Guid? excludedRecipeId,
        CancellationToken cancellationToken
    )
    {
        if (
            ing.MatchedRecipeId.HasValue
            && (!excludedRecipeId.HasValue || ing.MatchedRecipeId.Value != excludedRecipeId.Value)
        )
        {
            var titleMatchesSavedMatch =
                string.IsNullOrWhiteSpace(ing.MatchedRecipeTitle)
                || IsRecipeReferenceTitleMatch(ing.MatchedRecipeTitle, recipeTitle);

            if (titleMatchesSavedMatch)
            {
                var explicitMatch = await dbContext
                    .Recipes.AsNoTracking()
                    .Where(r =>
                        r.Id == ing.MatchedRecipeId.Value && r.Status != RecipeStatus.Archived
                    )
                    .Select(r => new MatchedRecipeReference(r.Id, r.Title, r.Slug))
                    .FirstOrDefaultAsync(cancellationToken);

                if (
                    explicitMatch is not null
                    && IsRecipeReferenceTitleMatch(explicitMatch.Title, recipeTitle)
                )
                {
                    return explicitMatch;
                }
            }
        }

        return await FindMatchingRecipeAsync(
            dbContext,
            recipeTitle,
            excludedRecipeId,
            cancellationToken
        );
    }

    public static async Task<int> BacklinkUnresolvedRecipeReferencesAsync(
        AppDbContext dbContext,
        Guid recipeId,
        string recipeTitle,
        CancellationToken cancellationToken = default
    )
    {
        var cleanTitle =
            RecipeUnitAndSectionHelpers.NormalizeRecipeReferenceTitle(recipeTitle)
            ?? recipeTitle.Trim();
        if (string.IsNullOrWhiteSpace(cleanTitle))
            return 0;

        var waitingIngredients = await dbContext
            .RecipeIngredients.Where(ri =>
                ri.LinkedRecipeId == null
                && ri.UnresolvedRecipeTitle != null
                && ri.RecipeId != recipeId
            )
            .ToListAsync(cancellationToken);

        var updatedCount = 0;
        foreach (var ri in waitingIngredients)
        {
            var candidateTitle =
                RecipeUnitAndSectionHelpers.NormalizeRecipeReferenceTitle(ri.UnresolvedRecipeTitle)
                ?? ri.UnresolvedRecipeTitle!.Trim();
            if (
                string.Equals(candidateTitle, cleanTitle, StringComparison.OrdinalIgnoreCase)
                || IsRecipeReferenceTitleMatch(recipeTitle, candidateTitle)
            )
            {
                ri.LinkedRecipeId = recipeId;
                ri.UnresolvedRecipeTitle = null;
                updatedCount++;
            }
        }

        if (updatedCount > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return updatedCount;
    }

    public static async Task<int> RepairAllUnresolvedRecipeReferencesAsync(
        AppDbContext dbContext,
        CancellationToken cancellationToken = default
    )
    {
        var waitingIngredients = await dbContext
            .RecipeIngredients.Where(ri =>
                ri.LinkedRecipeId == null && ri.UnresolvedRecipeTitle != null
            )
            .ToListAsync(cancellationToken);

        if (waitingIngredients.Count == 0)
            return 0;

        var allRecipes = await dbContext
            .Recipes.AsNoTracking()
            .Select(r => new { r.Id, r.Title })
            .ToListAsync(cancellationToken);

        var recipeMap = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in allRecipes)
        {
            var clean =
                RecipeUnitAndSectionHelpers.NormalizeRecipeReferenceTitle(r.Title)
                ?? r.Title.Trim();
            if (!recipeMap.ContainsKey(clean))
            {
                recipeMap[clean] = r.Id;
            }
            if (!recipeMap.ContainsKey(r.Title.Trim()))
            {
                recipeMap[r.Title.Trim()] = r.Id;
            }
        }

        var updatedCount = 0;
        foreach (var ri in waitingIngredients)
        {
            var candidateTitle =
                RecipeUnitAndSectionHelpers.NormalizeRecipeReferenceTitle(ri.UnresolvedRecipeTitle)
                ?? ri.UnresolvedRecipeTitle!.Trim();
            Guid matchedId = Guid.Empty;
            var found =
                recipeMap.TryGetValue(candidateTitle, out matchedId)
                || recipeMap.TryGetValue(ri.UnresolvedRecipeTitle!.Trim(), out matchedId);

            if (!found)
            {
                var fuzzy = allRecipes.FirstOrDefault(r =>
                    r.Id != ri.RecipeId && IsRecipeReferenceTitleMatch(r.Title, candidateTitle)
                );
                if (fuzzy is not null)
                {
                    matchedId = fuzzy.Id;
                    found = true;
                }
            }

            if (found && matchedId != ri.RecipeId)
            {
                ri.LinkedRecipeId = matchedId;
                ri.UnresolvedRecipeTitle = null;
                updatedCount++;
            }
        }

        if (updatedCount > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return updatedCount;
    }

    public static async Task<(
        IngestionExtractedDraftDto Draft,
        List<string> UnresolvedReferenceIssues
    )> ResolveDraftRecipeReferencesAsync(
        AppDbContext dbContext,
        IngestionExtractedDraftDto draft,
        Guid? excludedRecipeId,
        CancellationToken cancellationToken
    )
    {
        var unresolvedIssues = new List<string>();
        if (draft.Ingredients is not { Count: > 0 })
        {
            return (draft, unresolvedIssues);
        }

        var updatedIngredients = new List<IngestionDraftIngredientDto>(draft.Ingredients.Count);
        foreach (var ing in draft.Ingredients)
        {
            if (ing.IsRecipeReference && !string.IsNullOrWhiteSpace(ing.ReferencedRecipeTitle))
            {
                var candidates = BuildRecipeReferenceTitleCandidates(ing);
                MatchedRecipeReference? matchedRecipe = await ResolveExplicitOrMatchedRecipeAsync(
                    dbContext,
                    ing,
                    ing.ReferencedRecipeTitle.Trim(),
                    excludedRecipeId,
                    cancellationToken
                );
                string? matchedCandidate = matchedRecipe is not null
                    ? ing.ReferencedRecipeTitle.Trim()
                    : null;
                if (matchedRecipe is null)
                {
                    foreach (var candidate in candidates)
                    {
                        matchedRecipe = await FindMatchingRecipeAsync(
                            dbContext,
                            candidate,
                            excludedRecipeId,
                            cancellationToken
                        );
                        if (matchedRecipe is not null)
                        {
                            matchedCandidate = candidate;
                            break;
                        }
                    }
                }

                var cleanTitle = candidates.FirstOrDefault() ?? ing.ReferencedRecipeTitle.Trim();

                if (matchedRecipe is not null)
                {
                    var (updatedRefTitle, updatedPrepNote) = ReconcileMatchedReferenceFields(
                        ing,
                        cleanTitle,
                        matchedCandidate,
                        matchedRecipe.Title
                    );
                    updatedIngredients.Add(
                        ing with
                        {
                            ReferencedRecipeTitle = updatedRefTitle,
                            PreparationNote = updatedPrepNote,
                            MatchedRecipeId = matchedRecipe.Id,
                            MatchedRecipeTitle = matchedRecipe.Title,
                            MatchedRecipeSlug = matchedRecipe.Slug,
                        }
                    );
                    continue;
                }

                var unlinkedTitle = PickPreferredUnlinkedTitle(candidates, cleanTitle, ing);
                unresolvedIssues.Add(
                    $"Referenced recipe '{unlinkedTitle}' is not published yet and could not be linked."
                );
                if (
                    !string.Equals(
                        unlinkedTitle,
                        ing.ReferencedRecipeTitle.Trim(),
                        StringComparison.OrdinalIgnoreCase
                    )
                    || ing.MatchedRecipeId.HasValue
                    || !string.IsNullOrWhiteSpace(ing.MatchedRecipeTitle)
                    || !string.IsNullOrWhiteSpace(ing.MatchedRecipeSlug)
                )
                {
                    updatedIngredients.Add(
                        ing with
                        {
                            ReferencedRecipeTitle = unlinkedTitle,
                            PreparationNote = StripMatchedPrepSuffix(
                                ing.PreparationNote,
                                unlinkedTitle
                            ),
                            MatchedRecipeId = null,
                            MatchedRecipeTitle = null,
                            MatchedRecipeSlug = null,
                        }
                    );
                    continue;
                }
            }
            else if (ing.IsRecipeReference && !ing.MatchedRecipeId.HasValue)
            {
                var unlinkedTitle = !string.IsNullOrWhiteSpace(ing.DisplayName)
                    ? ing.DisplayName
                    : "sub-recipe";
                unresolvedIssues.Add(
                    $"Referenced recipe '{unlinkedTitle}' is missing a referenced recipe title."
                );
            }
            else if (!ing.IsRecipeReference)
            {
                var promoted = await TryPromoteUnflaggedRecipeReferenceAsync(
                    dbContext,
                    ing,
                    excludedRecipeId,
                    unresolvedIssues,
                    cancellationToken
                );
                if (promoted is not null)
                {
                    updatedIngredients.Add(promoted);
                    continue;
                }
            }

            updatedIngredients.Add(ing);
        }

        return (draft with { Ingredients = updatedIngredients }, unresolvedIssues);
    }

    private static List<string> BuildRecipeReferenceTitleCandidates(IngestionDraftIngredientDto ing)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            var normalized =
                RecipeUnitAndSectionHelpers.NormalizeRecipeReferenceTitle(raw) ?? raw.Trim();
            if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
            {
                candidates.Add(normalized);
            }
        }

        var rawBase = !string.IsNullOrWhiteSpace(ing.ReferencedRecipeTitle)
            ? ing.ReferencedRecipeTitle
            : ing.DisplayName;
        AddCandidate(rawBase);

        if (
            candidates.Count > 0
            && !string.IsNullOrWhiteSpace(ing.PreparationNote)
            && ing.PreparationNote.TrimStart()
                .StartsWith("for ", StringComparison.OrdinalIgnoreCase)
        )
        {
            AddCandidate($"{candidates[0]} {ing.PreparationNote.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(ing.OriginalRawText))
        {
            AddCandidate(ing.OriginalRawText);
        }

        return candidates;
    }

    private static string PickPreferredUnlinkedTitle(
        IReadOnlyList<string> candidates,
        string cleanTitle,
        IngestionDraftIngredientDto ing
    )
    {
        if (
            candidates.Count > 1
            && !string.IsNullOrWhiteSpace(ing.PreparationNote)
            && ing.PreparationNote.TrimStart()
                .StartsWith("for ", StringComparison.OrdinalIgnoreCase)
        )
        {
            var expanded = candidates.FirstOrDefault(c =>
                c.StartsWith(cleanTitle, StringComparison.OrdinalIgnoreCase)
                && c.Length > cleanTitle.Length
            );
            if (!string.IsNullOrWhiteSpace(expanded))
            {
                return expanded;
            }
        }

        return !string.IsNullOrWhiteSpace(cleanTitle)
            ? cleanTitle
            : (!string.IsNullOrWhiteSpace(ing.DisplayName) ? ing.DisplayName : "sub-recipe");
    }

    private static (
        string? ReferencedRecipeTitle,
        string? PreparationNote
    ) ReconcileMatchedReferenceFields(
        IngestionDraftIngredientDto ing,
        string baseCleanTitle,
        string? matchedCandidate,
        string matchedRecipeTitle
    )
    {
        var prepTrimmed = ing.PreparationNote?.Trim();
        var absorbedPrepNote =
            !string.IsNullOrWhiteSpace(prepTrimmed)
            && prepTrimmed.StartsWith("for ", StringComparison.OrdinalIgnoreCase)
            && (
                matchedRecipeTitle.EndsWith(prepTrimmed, StringComparison.OrdinalIgnoreCase)
                || (
                    matchedCandidate is not null
                    && !string.Equals(
                        matchedCandidate,
                        baseCleanTitle,
                        StringComparison.OrdinalIgnoreCase
                    )
                    && matchedCandidate.EndsWith(prepTrimmed, StringComparison.OrdinalIgnoreCase)
                )
            );

        if (absorbedPrepNote)
        {
            return (matchedRecipeTitle, null);
        }

        return (ing.ReferencedRecipeTitle, ing.PreparationNote);
    }

    private static string? StripMatchedPrepSuffix(string? preparationNote, string resolvedTitle)
    {
        if (string.IsNullOrWhiteSpace(preparationNote))
        {
            return null;
        }

        var trimmed = preparationNote.Trim();
        return
            trimmed.StartsWith("for ", StringComparison.OrdinalIgnoreCase)
            && resolvedTitle.EndsWith(trimmed, StringComparison.OrdinalIgnoreCase)
            ? null
            : preparationNote;
    }

    private static async Task<IngestionDraftIngredientDto?> TryPromoteUnflaggedRecipeReferenceAsync(
        AppDbContext dbContext,
        IngestionDraftIngredientDto ing,
        Guid? excludedRecipeId,
        List<string> unresolvedIssues,
        CancellationToken cancellationToken
    )
    {
        if (IngestionMetadataAndTagHelper.IsRawGrocerySeePageNote(ing.OriginalRawText))
        {
            return null;
        }

        var hasExplicitCue =
            (
                !string.IsNullOrWhiteSpace(ing.OriginalRawText)
                && ExplicitRecipeReferenceCueRegex().IsMatch(ing.OriginalRawText)
            )
            || (
                !string.IsNullOrWhiteSpace(ing.PreparationNote)
                && ExplicitRecipeReferenceCueRegex().IsMatch(ing.PreparationNote)
            )
            || (
                !string.IsNullOrWhiteSpace(ing.DisplayName)
                && ExplicitRecipeReferenceCueRegex().IsMatch(ing.DisplayName)
            );

        var hasPageNote =
            (
                !string.IsNullOrWhiteSpace(ing.OriginalRawText)
                && PageReferenceCueRegex().IsMatch(ing.OriginalRawText)
            )
            || (
                !string.IsNullOrWhiteSpace(ing.PreparationNote)
                && PageReferenceCueRegex().IsMatch(ing.PreparationNote)
            )
            || (
                !string.IsNullOrWhiteSpace(ing.DisplayName)
                && PageReferenceCueRegex().IsMatch(ing.DisplayName)
            );

        var candidates = BuildRecipeReferenceTitleCandidates(ing);
        if (candidates.Count == 0)
        {
            return null;
        }

        var cleanTitle = candidates[0];
        var wordCount = cleanTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var shouldLookupMatch =
            hasExplicitCue
            || hasPageNote
            || (
                !ing.MatchedFoodItemId.HasValue && wordCount >= 2 && ing.Unit != MeasurementUnit.Can
            );

        if (!shouldLookupMatch)
        {
            return null;
        }

        MatchedRecipeReference? matchedRecipe = null;
        string? matchedCandidate = null;
        foreach (var candidate in candidates)
        {
            matchedRecipe = await FindMatchingRecipeAsync(
                dbContext,
                candidate,
                excludedRecipeId,
                cancellationToken
            );
            if (matchedRecipe is not null)
            {
                matchedCandidate = candidate;
                break;
            }
        }

        if (matchedRecipe is not null)
        {
            var (_, updatedPrepNote) = ReconcileMatchedReferenceFields(
                ing,
                cleanTitle,
                matchedCandidate,
                matchedRecipe.Title
            );
            return ing with
            {
                IsRecipeReference = true,
                ReferencedRecipeTitle = matchedRecipe.Title,
                PreparationNote = updatedPrepNote,
                MatchedRecipeId = matchedRecipe.Id,
                MatchedRecipeTitle = matchedRecipe.Title,
                MatchedRecipeSlug = matchedRecipe.Slug,
                MatchedFoodItemId = null,
                MatchedFoodItemName = null,
                ProposedFoodItemName = null,
                CreateNewFoodItem = false,
            };
        }

        if (hasExplicitCue && !ing.IsOptional)
        {
            var preferredTitle = PickPreferredUnlinkedTitle(candidates, cleanTitle, ing);
            unresolvedIssues.Add(
                $"Referenced recipe '{preferredTitle}' is not published yet and could not be linked."
            );
            return ing with
            {
                IsRecipeReference = true,
                ReferencedRecipeTitle = preferredTitle,
                PreparationNote = StripMatchedPrepSuffix(ing.PreparationNote, preferredTitle),
                MatchedFoodItemId = null,
                MatchedFoodItemName = null,
                ProposedFoodItemName = null,
                CreateNewFoodItem = false,
            };
        }

        return null;
    }

    public static async Task<UpsertRecipeRequest> BuildUpsertRequestFromDraftAsync(
        AppDbContext dbContext,
        IngestionExtractedDraftDto draft,
        Guid? excludeRecipeId,
        RecipeStatus targetStatus,
        string? extractedModel,
        string? refinedModel,
        CancellationToken cancellationToken
    )
    {
        var ingredientsInput = new List<RecipeIngredientInput>();

        if (draft.Ingredients is not null)
        {
            foreach (var ing in draft.Ingredients)
            {
                var recipeTitle = ing.IsRecipeReference ? ing.ReferencedRecipeTitle?.Trim() : null;
                if (ing.IsRecipeReference && string.IsNullOrWhiteSpace(recipeTitle))
                {
                    throw new DomainValidationException(
                        "A title is required for a recipe component reference."
                    );
                }

                var foodItemId = ing.IsRecipeReference ? null : ing.MatchedFoodItemId;
                Guid? linkedRecipeId = null;

                if (!string.IsNullOrWhiteSpace(recipeTitle))
                {
                    linkedRecipeId = (
                        await ResolveExplicitOrMatchedRecipeAsync(
                            dbContext,
                            ing,
                            recipeTitle,
                            excludeRecipeId,
                            cancellationToken
                        )
                    )?.Id;
                }

                if (
                    foodItemId == null
                    && !ing.IsRecipeReference
                    && ing.CreateNewFoodItem
                    && !string.IsNullOrWhiteSpace(ing.ProposedFoodItemName)
                )
                {
                    foodItemId = await ResolveOrCreateFoodItemAsync(
                        dbContext,
                        ing.ProposedFoodItemName,
                        ing.SuggestedIsPantryStaple,
                        ing.SuggestedCategory,
                        cancellationToken
                    );
                }

                var (cleanQty, cleanMaxQty) = RecipeUnitAndSectionHelpers.NormalizeQuantities(
                    ing.Quantity,
                    ing.MaxQuantity
                );

                var cleanUnit =
                    ing.Unit == MeasurementUnit.Custom
                    && (
                        string.IsNullOrWhiteSpace(ing.CustomUnitName)
                        || RecipeUnitAndSectionHelpers.IsDisallowedUnitPlaceholder(
                            ing.CustomUnitName
                        )
                    )
                        ? null
                        : ing.Unit;

                if (
                    (cleanUnit is MeasurementUnit.Pinch or MeasurementUnit.Dash)
                    && (cleanQty is null or <= 0.1m)
                )
                {
                    cleanQty = 1m;
                }

                var replacementsInput = await MaterializeReplacementsAsync(
                    dbContext,
                    ing.Replacements,
                    cancellationToken
                );

                ingredientsInput.Add(
                    new RecipeIngredientInput(
                        SectionName: ing.SectionName,
                        Quantity: cleanQty,
                        MaxQuantity: cleanMaxQty,
                        Unit: cleanUnit,
                        CustomUnitName: cleanUnit == MeasurementUnit.Custom
                            ? ing.CustomUnitName
                            : null,
                        FoodItemId: foodItemId,
                        LinkedRecipeId: linkedRecipeId,
                        DisplayName: string.IsNullOrWhiteSpace(recipeTitle)
                            ? ing.DisplayName
                            : recipeTitle,
                        PreparationNote: ing.PreparationNote,
                        IsOptional: ing.IsOptional,
                        OriginalRawText: ing.OriginalRawText,
                        AlternativeMeasurement: ing.AlternativeMeasurement,
                        PreferAlternativeMeasurement: ing.PreferAlternativeMeasurement,
                        UnresolvedRecipeTitle: linkedRecipeId.HasValue ? null : recipeTitle,
                        Replacements: replacementsInput
                    )
                );
            }
        }

        var stepsInput = new List<RecipeStepInput>();
        if (draft.Steps is not null)
        {
            foreach (var s in draft.Steps)
            {
                stepsInput.Add(
                    new RecipeStepInput(
                        SectionName: s.SectionName,
                        Instruction: s.Instruction,
                        Timers: s.Timers,
                        ImagePath: s.ImagePath
                    )
                );
            }
        }

        var resolvedTagIds = await ResolveAndMaterializeTagsAsync(
            dbContext,
            draft.SuggestedTagIds,
            draft.ProposedTagNames,
            cancellationToken,
            allowNewCustomTags: false
        );

        return new UpsertRecipeRequest(
            Title: draft.Title,
            Description: draft.Description != null && draft.Description.Length > 2000
                ? draft.Description[..2000].TrimEnd()
                : draft.Description,
            Status: targetStatus,
            BaseServings: draft.BaseServings,
            YieldDisplay: draft.YieldDisplay,
            PrepTimeMinutes: draft.PrepTimeMinutes,
            CookTimeMinutes: draft.CookTimeMinutes,
            TotalTimeMinutes: draft.TotalTimeMinutes,
            Difficulty: draft.Difficulty,
            SourceUrl: draft.SourceUrl,
            SourceName: draft.SourceName,
            SourceAuthor: draft.SourceAuthor,
            SourceBookTitle: draft.SourceBookTitle,
            SourceBookPage: draft.SourceBookPage,
            Ingredients: ingredientsInput,
            Steps: stepsInput,
            CoverImagePath: draft.CoverImagePath,
            ThumbnailImagePath: draft.ThumbnailImagePath,
            CollectionIds: draft.SuggestedCollectionIds,
            TagIds: resolvedTagIds,
            ExtractedModel: extractedModel,
            RefinedModel: refinedModel
        );
    }

    public static async Task<RecipeDetailDto?> ApplyRefinedDraftToRecipeAsync(
        AppDbContext dbContext,
        IRecipeService recipeService,
        Guid recipeId,
        RecipeStatus preservedStatus,
        string? preservedExtractedModel,
        string refinedModel,
        IngestionExtractedDraftDto draft,
        CancellationToken cancellationToken
    )
    {
        var upsertRequest = await BuildUpsertRequestFromDraftAsync(
            dbContext,
            draft,
            excludeRecipeId: recipeId,
            targetStatus: preservedStatus,
            extractedModel: preservedExtractedModel,
            refinedModel: refinedModel,
            cancellationToken
        );

        return await recipeService.UpdateRecipeAsync(recipeId, upsertRequest, cancellationToken);
    }

    public static async Task<RecipeDetailDto> PublishDraftAsync(
        AppDbContext dbContext,
        IRecipeService recipeService,
        IMediaStorageService mediaStorageService,
        IngestionJob job,
        IngestionExtractedDraftDto draft,
        CancellationToken cancellationToken
    )
    {
        var upsertRequest = await BuildUpsertRequestFromDraftAsync(
            dbContext,
            draft,
            excludeRecipeId: job.ResultingRecipeId,
            targetStatus: RecipeStatus.Published,
            extractedModel: job.ExtractedModel,
            refinedModel: job.RefinedModel ?? draft.RefinedModel ?? string.Empty,
            cancellationToken
        );

        RecipeDetailDto createdRecipe;

        if (job.ResultingRecipeId.HasValue)
        {
            var existingRecipe = await dbContext.Recipes.FirstOrDefaultAsync(
                r => r.Id == job.ResultingRecipeId.Value,
                cancellationToken
            );

            if (existingRecipe is not null)
            {
                var updated = await recipeService.UpdateRecipeAsync(
                    existingRecipe.Id,
                    upsertRequest,
                    cancellationToken
                );

                createdRecipe =
                    updated
                    ?? await recipeService.CreateRecipeAsync(upsertRequest, cancellationToken);
            }
            else
            {
                createdRecipe = await recipeService.CreateRecipeAsync(
                    upsertRequest,
                    cancellationToken
                );
            }
        }
        else
        {
            createdRecipe = await recipeService.CreateRecipeAsync(upsertRequest, cancellationToken);
        }

        await PromoteOrAttachSourceFilesAsync(
            dbContext,
            mediaStorageService,
            job,
            draft,
            createdRecipe.Id,
            cancellationToken
        );

        return createdRecipe;
    }

    /// <summary>
    /// Removes food items that were detached from a published recipe during refinement when they
    /// are no longer referenced by any recipe ingredient, replacement, shopping list item, or
    /// pending review draft.
    /// </summary>
    public static async Task<int> CleanupOrphanedFoodItemsAsync(
        AppDbContext dbContext,
        IReadOnlyCollection<Guid> candidateFoodItemIds,
        Guid excludeJobId,
        CancellationToken cancellationToken = default
    )
    {
        var distinctIds = candidateFoodItemIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (distinctIds.Count == 0)
        {
            return 0;
        }

        var usedInIngredients = await dbContext
            .RecipeIngredients.AsNoTracking()
            .Where(ri => ri.FoodItemId.HasValue && distinctIds.Contains(ri.FoodItemId.Value))
            .Select(ri => ri.FoodItemId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        var usedInReplacements = await dbContext
            .RecipeIngredientReplacements.AsNoTracking()
            .Where(r => r.FoodItemId.HasValue && distinctIds.Contains(r.FoodItemId.Value))
            .Select(r => r.FoodItemId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        var usedInShopping = await dbContext
            .ShoppingListItems.AsNoTracking()
            .Where(s => s.FoodItemId.HasValue && distinctIds.Contains(s.FoodItemId.Value))
            .Select(s => s.FoodItemId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        var stillReferenced = new HashSet<Guid>(
            usedInIngredients.Concat(usedInReplacements).Concat(usedInShopping)
        );
        var remaining = distinctIds.Where(id => !stillReferenced.Contains(id)).ToList();
        if (remaining.Count == 0)
        {
            return 0;
        }

        // Also check pending ReadyForReview drafts so we never delete a FoodItem a draft links to.
        var reviewDrafts = await dbContext
            .IngestionJobs.AsNoTracking()
            .Where(j =>
                j.Id != excludeJobId
                && j.Status == IngestionJobStatus.ReadyForReview
                && j.ExtractedDraftJson != null
            )
            .Select(j => j.ExtractedDraftJson!)
            .ToListAsync(cancellationToken);

        foreach (var draftJson in reviewDrafts)
        {
            foreach (var candidateId in remaining.ToList())
            {
                if (draftJson.Contains(candidateId.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    remaining.Remove(candidateId);
                }
            }
            if (remaining.Count == 0)
            {
                return 0;
            }
        }

        var orphans = await dbContext
            .FoodItems.Include(f => f.Aliases)
            .Where(f => remaining.Contains(f.Id))
            .ToListAsync(cancellationToken);

        if (orphans.Count == 0)
        {
            return 0;
        }

        dbContext.FoodItemAliases.RemoveRange(orphans.SelectMany(f => f.Aliases));
        dbContext.FoodItems.RemoveRange(orphans);
        await dbContext.SaveChangesAsync(cancellationToken);
        return orphans.Count;
    }

    public static async Task<int> PromoteWaitingRecipeReferenceJobsAsync(
        AppDbContext dbContext,
        IRecipeService recipeService,
        IMediaStorageService mediaStorageService,
        bool globalAutoPublish,
        CancellationToken cancellationToken = default,
        ICollection<Guid>? promotedRecipeIds = null
    )
    {
        var totalPromoted = 0;
        var visitedJobIds = new HashSet<Guid>();

        for (var pass = 0; pass < 3; pass++)
        {
            var waitingJobIds = await dbContext
                .IngestionJobs.AsNoTracking()
                .Where(j =>
                    j.Status == IngestionJobStatus.ReadyForReview
                    && j.StatusDetail != null
                    && j.StatusDetail.Contains("is not published yet")
                    && j.ExtractedDraftJson != null
                )
                .OrderBy(j => j.CreatedAt)
                .Select(j => j.Id)
                .ToListAsync(cancellationToken);

            var candidateIds = waitingJobIds.Where(id => !visitedJobIds.Contains(id)).ToList();
            if (candidateIds.Count == 0)
            {
                break;
            }

            var promotedInPass = 0;
            foreach (var jobId in candidateIds)
            {
                visitedJobIds.Add(jobId);

                var waitingJob = await dbContext.IngestionJobs.FirstOrDefaultAsync(
                    j => j.Id == jobId,
                    cancellationToken
                );
                if (
                    waitingJob is null
                    || waitingJob.Status != IngestionJobStatus.ReadyForReview
                    || string.IsNullOrWhiteSpace(waitingJob.ExtractedDraftJson)
                )
                {
                    continue;
                }

                IngestionExtractedDraftDto? draft;
                try
                {
                    draft = JsonSerializer.Deserialize<IngestionExtractedDraftDto>(
                        waitingJob.ExtractedDraftJson,
                        JsonOptions
                    );
                }
                catch
                {
                    continue;
                }

                if (draft is null)
                {
                    continue;
                }

                var (resolvedDraft, refIssues) = await ResolveDraftRecipeReferencesAsync(
                    dbContext,
                    draft,
                    waitingJob.ResultingRecipeId,
                    cancellationToken
                );

                var existingValidationIssues = (resolvedDraft.ValidationIssues ?? [])
                    .Where(i =>
                        !i.Contains(
                            "is not published yet and could not be linked.",
                            StringComparison.Ordinal
                        )
                        && !i.Contains(
                            "is missing a referenced recipe title.",
                            StringComparison.Ordinal
                        )
                    )
                    .ToList();
                var existingQualityWarnings = resolvedDraft.QualityWarnings ?? [];
                var allIssues = existingValidationIssues
                    .Concat(refIssues)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                var hasUnresolvedRefs =
                    resolvedDraft.Ingredients?.Any(i =>
                        i.IsRecipeReference && !i.MatchedRecipeId.HasValue
                    ) == true;

                var draftWithIssues = resolvedDraft with { ValidationIssues = allIssues };
                var updatedScore = IngestionConfidenceScorer.Compute(
                    draftWithIssues,
                    draftWithIssues.PipelineRepairCount
                );

                resolvedDraft = draftWithIssues with { ConfidenceScore = updatedScore };

                var canAutoPublish =
                    allIssues.Count == 0
                    && existingQualityWarnings.Count == 0
                    && !hasUnresolvedRefs
                    && (globalAutoPublish || waitingJob.AutoPublish)
                    && waitingJob.Channel is IngestionChannel.WebUrl or IngestionChannel.RawText
                    && !string.IsNullOrWhiteSpace(resolvedDraft.Title)
                    && (resolvedDraft.Ingredients?.Count ?? 0) >= 1
                    && (resolvedDraft.Steps?.Count ?? 0) >= 1
                    && updatedScore >= (waitingJob.AutoPublish ? 0.60m : 0.85m);

                if (canAutoPublish)
                {
                    var createdRecipe = await PublishDraftAsync(
                        dbContext,
                        recipeService,
                        mediaStorageService,
                        waitingJob,
                        resolvedDraft,
                        cancellationToken
                    );

                    waitingJob.Status = IngestionJobStatus.Published;
                    waitingJob.ResultingRecipeId = createdRecipe.Id;
                    waitingJob.ExtractedDraftJson = JsonSerializer.Serialize(
                        resolvedDraft,
                        JsonOptions
                    );
                    waitingJob.ConfidenceScore = updatedScore;
                    waitingJob.DisplayTitle = resolvedDraft.Title;
                    waitingJob.ProcessedAt = DateTimeOffset.UtcNow;
                    waitingJob.StatusDetail =
                        $"Auto-published after referenced recipe became available ({waitingJob.ExtractedModel ?? "auto-backlink"}).";

                    await dbContext.SaveChangesAsync(cancellationToken);

                    await BacklinkUnresolvedRecipeReferencesAsync(
                        dbContext,
                        createdRecipe.Id,
                        createdRecipe.Title,
                        cancellationToken
                    );

                    promotedRecipeIds?.Add(createdRecipe.Id);
                    promotedInPass++;
                    totalPromoted++;
                }
                else if (resolvedDraft != draft)
                {
                    waitingJob.ExtractedDraftJson = JsonSerializer.Serialize(
                        resolvedDraft,
                        JsonOptions
                    );
                    waitingJob.ConfidenceScore = updatedScore;
                    await dbContext.SaveChangesAsync(cancellationToken);
                }
            }

            if (promotedInPass == 0)
            {
                break;
            }
        }

        return totalPromoted;
    }

    public static async Task<IReadOnlyList<RecipeIngredientReplacementInput>?> MaterializeReplacementsAsync(
        AppDbContext dbContext,
        IReadOnlyList<IngestionDraftReplacementDto>? replacements,
        CancellationToken cancellationToken
    )
    {
        if (replacements is null || replacements.Count == 0)
        {
            return null;
        }

        var result = new List<RecipeIngredientReplacementInput>(replacements.Count);
        foreach (var rep in replacements)
        {
            if (rep is null || string.IsNullOrWhiteSpace(rep.DisplayName))
            {
                continue;
            }

            var foodItemId = rep.MatchedFoodItemId;
            if (
                foodItemId == null
                && rep.CreateNewFoodItem
                && !string.IsNullOrWhiteSpace(rep.ProposedFoodItemName)
            )
            {
                foodItemId = await ResolveOrCreateFoodItemAsync(
                    dbContext,
                    rep.ProposedFoodItemName,
                    rep.SuggestedIsPantryStaple,
                    rep.SuggestedCategory,
                    cancellationToken
                );
            }

            var (cleanQty, cleanMaxQty) = RecipeUnitAndSectionHelpers.NormalizeQuantities(
                rep.Quantity,
                rep.MaxQuantity
            );
            var cleanUnit =
                rep.Unit == MeasurementUnit.Custom
                && (
                    string.IsNullOrWhiteSpace(rep.CustomUnitName)
                    || RecipeUnitAndSectionHelpers.IsDisallowedUnitPlaceholder(rep.CustomUnitName)
                )
                    ? null
                    : rep.Unit;

            if (
                (cleanUnit is MeasurementUnit.Pinch or MeasurementUnit.Dash)
                && (cleanQty is null or <= 0.1m)
            )
            {
                cleanQty = 1m;
            }

            result.Add(
                new RecipeIngredientReplacementInput(
                    DisplayName: RecipeUnitAndSectionHelpers.StripLeadingBulletOrIndex(
                        rep.DisplayName.Trim()
                    ),
                    Quantity: cleanQty,
                    MaxQuantity: cleanMaxQty,
                    Unit: cleanUnit,
                    CustomUnitName: cleanUnit == MeasurementUnit.Custom ? rep.CustomUnitName : null,
                    FoodItemId: foodItemId,
                    PreparationNote: RecipeUnitAndSectionHelpers.CleanPreparationNote(
                        rep.PreparationNote,
                        300
                    )
                )
            );
        }

        return result.Count > 0 ? result : null;
    }

    public static async Task<IReadOnlyList<Guid>> ResolveAndMaterializeTagsAsync(
        AppDbContext dbContext,
        IReadOnlyList<Guid>? suggestedTagIds,
        IReadOnlyList<string>? proposedTagNames,
        CancellationToken cancellationToken,
        bool allowNewCustomTags = true
    )
    {
        var resolvedIds = new List<Guid>();

        if (suggestedTagIds is { Count: > 0 })
        {
            var distinctInputIds = suggestedTagIds.Distinct().ToList();

            var existingIds = await dbContext
                .Tags.AsNoTracking()
                .Where(t => distinctInputIds.Contains(t.Id))
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);

            foreach (var id in existingIds)
            {
                if (!resolvedIds.Contains(id))
                {
                    resolvedIds.Add(id);
                }
            }
        }

        if (proposedTagNames is not { Count: > 0 })
        {
            return resolvedIds;
        }

        var allTags = await dbContext.Tags.ToListAsync(cancellationToken);

        foreach (var rawTag in proposedTagNames)
        {
            if (string.IsNullOrWhiteSpace(rawTag))
            {
                continue;
            }

            var cleanName = RecipeUnitAndSectionHelpers.ToTagDisplayCase(
                rawTag.Trim().TrimStart('#')
            );

            if (BuiltInTags.IsDisallowedTagName(cleanName))
            {
                continue;
            }

            if (BuiltInTags.CanonicalAliases.TryGetValue(cleanName, out var canonicalAlias))
            {
                cleanName = canonicalAlias;
            }

            var normName = cleanName.ToLowerInvariant();

            var singularNorm = RecipeUnitAndSectionHelpers.Singularize(normName);

            var existingTag = allTags.FirstOrDefault(t =>
                t.NormalizedName == normName
                || RecipeUnitAndSectionHelpers.Singularize(t.NormalizedName) == singularNorm
            );

            if (existingTag is not null)
            {
                if (!resolvedIds.Contains(existingTag.Id))
                {
                    resolvedIds.Add(existingTag.Id);
                }

                continue;
            }

            var category = RecipeUnitAndSectionHelpers.InferTagCategory(cleanName);
            if (!allowNewCustomTags && category == TagCategory.Custom)
            {
                continue;
            }

            var slug = GenerateUniqueTagSlug(cleanName, category, allTags);

            var newTag = new Tag
            {
                Name = cleanName,

                NormalizedName = normName,

                Slug = slug,

                Category = category,

                SortOrder = 0,

                IsSystemSeeded = false,
            };

            dbContext.Tags.Add(newTag);

            await dbContext.SaveChangesAsync(cancellationToken);

            allTags.Add(newTag);

            resolvedIds.Add(newTag.Id);
        }

        return resolvedIds;
    }

    private static string GenerateUniqueTagSlug(
        string name,
        TagCategory category,
        IReadOnlyCollection<Tag> existingTags
    )
    {
        var catPrefix = category.ToString().ToLowerInvariant();

        var lower = name.Trim().ToLowerInvariant();

        var clean = Regex.Replace(lower, @"[^a-z0-9\s-]", "");

        var rawNameSlug = Regex.Replace(clean, @"[\s-]+", "-").Trim('-');

        var baseSlug = string.IsNullOrWhiteSpace(rawNameSlug)
            ? $"{catPrefix}-tag"
            : $"{catPrefix}-{rawNameSlug}";

        if (baseSlug.Length > 90)
        {
            baseSlug = baseSlug[..90].TrimEnd('-');
        }

        var slug = baseSlug;

        var counter = 2;

        while (
            existingTags.Any(t => string.Equals(t.Slug, slug, StringComparison.OrdinalIgnoreCase))
        )
        {
            slug = $"{baseSlug}-{counter}";

            counter++;
        }

        return slug;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task PromoteOrAttachSourceFilesAsync(
        AppDbContext dbContext,
        IMediaStorageService mediaStorageService,
        IngestionJob job,
        IngestionExtractedDraftDto draft,
        Guid recipeId,
        CancellationToken cancellationToken
    )
    {
        if (!draft.RetainSourceAttachments)
        {
            if (!string.IsNullOrWhiteSpace(job.SourceFilesJson))
            {
                try
                {
                    var sourceFiles = JsonSerializer.Deserialize<List<IngestionSourceFileDto>>(
                        job.SourceFilesJson,
                        JsonOptions
                    );

                    if (sourceFiles is { Count: > 0 })
                    {
                        foreach (var sf in sourceFiles)
                        {
                            await mediaStorageService.DeleteMediaFileAsync(
                                sf.UrlPath,
                                cancellationToken
                            );
                        }
                    }
                }
                catch
                {
                    // Non-blocking cleanup failure
                }
            }

            return;
        }

        var hasAttachments = await dbContext.RecipeAttachments.AnyAsync(
            a => a.RecipeId == recipeId,
            cancellationToken
        );

        if (hasAttachments)
        {
            return;
        }

        // 1. Promote files from job.SourceFilesJson if present

        if (!string.IsNullOrWhiteSpace(job.SourceFilesJson))
        {
            try
            {
                var sourceFiles = JsonSerializer.Deserialize<List<IngestionSourceFileDto>>(
                    job.SourceFilesJson,
                    JsonOptions
                );

                if (sourceFiles is { Count: > 0 })
                {
                    foreach (var sf in sourceFiles)
                    {
                        var promoted =
                            await mediaStorageService.PromoteIngestionFileToAttachmentAsync(
                                sf.UrlPath,
                                cancellationToken
                            );

                        string label;

                        if (
                            sf.ContentType == "text/plain"
                            || sf.OriginalFileName.EndsWith(
                                ".txt",
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        {
                            label = "Original Source Text";
                        }
                        else if (
                            sf.ContentType == "application/pdf"
                            || sf.OriginalFileName.EndsWith(
                                ".pdf",
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        {
                            label = "Original PDF";
                        }
                        else
                        {
                            label =
                                sf.PageNumber == 1
                                    ? "Source Cover / Page"
                                    : $"Source Page {sf.PageNumber}";
                        }

                        dbContext.RecipeAttachments.Add(
                            new RecipeAttachment
                            {
                                Id = Guid.NewGuid(),

                                RecipeId = recipeId,

                                SortOrder = sf.PageNumber,

                                FileName = sf.OriginalFileName,

                                MediaUrlPath = promoted.MediaUrlPath,

                                ContentType = promoted.ContentType,

                                FileSizeBytes = promoted.FileSizeBytes,

                                Label = label,
                            }
                        );
                    }

                    await dbContext.SaveChangesAsync(cancellationToken);

                    return;
                }
            }
            catch
            {
                // Non-blocking attachment promotion failure
            }
        }

        // 2. Fallback: If no SourceFilesJson, but RawText or NormalizedSourceText exists,

        // create and attach a source text attachment file so the recipe retains original text!

        var rawTextContent = !string.IsNullOrWhiteSpace(job.RawText)
            ? job.RawText
            : job.NormalizedSourceText;

        if (!string.IsNullOrWhiteSpace(rawTextContent))
        {
            try
            {
                var title = !string.IsNullOrWhiteSpace(draft.Title)
                    ? draft.Title
                    : (!string.IsNullOrWhiteSpace(job.DisplayTitle) ? job.DisplayTitle : "Recipe");

                var cleanTitle = string.Join("_", title.Split(Path.GetInvalidFileNameChars()))
                    .Trim();

                if (cleanTitle.Length > 80)
                    cleanTitle = cleanTitle[..80].Trim();

                var fileName = $"{cleanTitle} - Source Text.txt";

                var bytes = System.Text.Encoding.UTF8.GetBytes(rawTextContent);

                using var ms = new MemoryStream(bytes);

                var saved = await mediaStorageService.SaveDirectRecipeAttachmentAsync(
                    ms,
                    fileName,
                    "text/plain",
                    bytes.Length,
                    cancellationToken
                );

                dbContext.RecipeAttachments.Add(
                    new RecipeAttachment
                    {
                        Id = Guid.NewGuid(),

                        RecipeId = recipeId,

                        SortOrder = 1,

                        FileName = fileName,

                        MediaUrlPath = saved.MediaUrlPath,

                        ContentType = saved.ContentType,

                        FileSizeBytes = saved.FileSizeBytes,

                        Label = "Original Source Text",
                    }
                );

                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                // Non-blocking fallback attachment failure
            }
        }
    }
}
