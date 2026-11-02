using System.Globalization;
using App.Core.Recipes;

namespace App.Core.Shopping;

public record ConsolidatableIngredientInput(
    Guid? FoodItemId,
    string CanonicalOrDisplayName,
    bool IsPantryStaple,
    decimal? Quantity,
    decimal? MaxQuantity,
    MeasurementUnit? Unit,
    string? CustomUnitName,
    string? PreparationNote,
    ShoppingListSourceKind SourceKind,
    Guid? MealPlanEntryId,
    Guid? RecipeId,
    string SourceLabel,
    DateOnly? ScheduledDate
);

public record ConsolidatedSourceContribution(
    ShoppingListSourceKind SourceKind,
    Guid? MealPlanEntryId,
    Guid? RecipeId,
    string SourceLabel,
    DateOnly? ScheduledDate,
    decimal? QuantityInConsolidatedUnit,
    decimal? MaxQuantityInConsolidatedUnit
);

public record ConsolidatedShoppingBucket(
    string ConsolidationKey,
    Guid? FoodItemId,
    string DisplayName,
    bool IsPantryStaple,
    MeasurementDimension Dimension,
    decimal? Quantity,
    decimal? MaxQuantity,
    MeasurementUnit? Unit,
    string? CustomUnitName,
    string? MergedNotes,
    IReadOnlyList<ConsolidatedSourceContribution> Sources
);

public static class ShoppingConsolidator
{
    private static readonly decimal[] CleanFractionTargets =
    [
        0.125m,
        0.25m,
        0.3333333m,
        0.375m,
        0.5m,
        0.625m,
        0.6666667m,
        0.75m,
        0.875m,
    ];

    public static MeasurementDimension GetDimension(MeasurementUnit? unit, decimal? quantity)
    {
        if (!quantity.HasValue || quantity.Value <= 0m)
        {
            return MeasurementDimension.Unspecified;
        }

        if (unit is null)
        {
            return MeasurementDimension.Discrete;
        }

        return unit.Value switch
        {
            MeasurementUnit.Tsp
            or MeasurementUnit.Tbsp
            or MeasurementUnit.FlOz
            or MeasurementUnit.Cup
            or MeasurementUnit.Pt
            or MeasurementUnit.Qt
            or MeasurementUnit.Gal
            or MeasurementUnit.Ml
            or MeasurementUnit.L => MeasurementDimension.Volume,

            MeasurementUnit.Oz or MeasurementUnit.Lb or MeasurementUnit.G or MeasurementUnit.Kg =>
                MeasurementDimension.Weight,

            MeasurementUnit.Pinch
            or MeasurementUnit.Dash
            or MeasurementUnit.Clove
            or MeasurementUnit.Can
            or MeasurementUnit.Bunch
            or MeasurementUnit.Slice
            or MeasurementUnit.Whole
            or MeasurementUnit.Custom => MeasurementDimension.Discrete,

            _ => MeasurementDimension.Discrete,
        };
    }

    public static string BuildConsolidationKey(
        Guid? foodItemId,
        string name,
        MeasurementDimension dim,
        MeasurementUnit? unit,
        string? customUnitName
    )
    {
        var identityPrefix = foodItemId.HasValue
            ? $"fi:{foodItemId.Value:N}"
            : $"name:{name.Trim().ToLowerInvariant()}";

        var dimensionSuffix = dim switch
        {
            MeasurementDimension.Volume => "vol",
            MeasurementDimension.Weight => "wt",
            MeasurementDimension.Discrete => unit == MeasurementUnit.Custom
                ? $"custom:{(customUnitName ?? string.Empty).Trim().ToLowerInvariant()}"
                : $"disc:{unit ?? MeasurementUnit.Whole}",
            _ => "unspec",
        };

        return $"{identityPrefix}:{dimensionSuffix}";
    }

    public static string FormatMealPlanSourceLabel(
        DateOnly? scheduledDate,
        bool isOnDeck,
        string title
    )
    {
        if (isOnDeck || !scheduledDate.HasValue)
        {
            return $"On-Deck: {title}";
        }

        var dayName = scheduledDate.Value.DayOfWeek.ToString();
        var shortDay = dayName.Length >= 3 ? dayName[..3] : dayName;
        return $"{shortDay}: {title}";
    }

    public static IReadOnlyList<ConsolidatedShoppingBucket> Consolidate(
        IEnumerable<ConsolidatableIngredientInput> inputs
    )
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var grouped = new Dictionary<string, List<ConsolidatableIngredientInput>>(
            StringComparer.OrdinalIgnoreCase
        );

        foreach (var item in inputs)
        {
            var dim = GetDimension(item.Unit, item.Quantity);
            var key = BuildConsolidationKey(
                item.FoodItemId,
                item.CanonicalOrDisplayName,
                dim,
                item.Unit,
                item.CustomUnitName
            );

            if (!grouped.TryGetValue(key, out var list))
            {
                list = [];
                grouped[key] = list;
            }

            list.Add(item);
        }

        var results = new List<ConsolidatedShoppingBucket>();

        foreach (var (key, bucketItems) in grouped)
        {
            var first = bucketItems[0];
            var dim = GetDimension(first.Unit, first.Quantity);
            var foodItemId = bucketItems.FirstOrDefault(b => b.FoodItemId.HasValue)?.FoodItemId;
            var isPantryStaple = bucketItems.Any(b => b.IsPantryStaple);
            var displayName =
                bucketItems
                    .Select(b => b.CanonicalOrDisplayName)
                    .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                ?? first.CanonicalOrDisplayName;

            var notes = bucketItems
                .Select(b => b.PreparationNote?.Trim())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var mergedNotes = notes.Count > 0 ? string.Join(", ", notes) : null;

            switch (dim)
            {
                case MeasurementDimension.Volume:
                    results.Add(
                        ConsolidateVolumeBucket(
                            key,
                            foodItemId,
                            displayName,
                            isPantryStaple,
                            mergedNotes,
                            bucketItems
                        )
                    );
                    break;

                case MeasurementDimension.Weight:
                    results.Add(
                        ConsolidateWeightBucket(
                            key,
                            foodItemId,
                            displayName,
                            isPantryStaple,
                            mergedNotes,
                            bucketItems
                        )
                    );
                    break;

                case MeasurementDimension.Discrete:
                    results.Add(
                        ConsolidateDiscreteBucket(
                            key,
                            foodItemId,
                            displayName,
                            isPantryStaple,
                            mergedNotes,
                            bucketItems
                        )
                    );
                    break;

                case MeasurementDimension.Unspecified:
                default:
                    results.Add(
                        ConsolidateUnspecifiedBucket(
                            key,
                            foodItemId,
                            displayName,
                            isPantryStaple,
                            mergedNotes,
                            bucketItems
                        )
                    );
                    break;
            }
        }

        return results;
    }

    private static ConsolidatedShoppingBucket ConsolidateVolumeBucket(
        string key,
        Guid? foodItemId,
        string displayName,
        bool isPantryStaple,
        string? mergedNotes,
        List<ConsolidatableIngredientInput> items
    )
    {
        var hasMetric = items.Any(i => i.Unit is MeasurementUnit.Ml or MeasurementUnit.L);
        var hasUs = items.Any(i =>
            i.Unit
                is MeasurementUnit.Tsp
                    or MeasurementUnit.Tbsp
                    or MeasurementUnit.FlOz
                    or MeasurementUnit.Cup
                    or MeasurementUnit.Pt
                    or MeasurementUnit.Qt
                    or MeasurementUnit.Gal
        );

        var firstUnit = items.FirstOrDefault(i => i.Unit.HasValue)?.Unit;
        var firstIsMetric = firstUnit is MeasurementUnit.Ml or MeasurementUnit.L;

        var hasAnyMax = items.Any(i => i.MaxQuantity.HasValue && i.MaxQuantity > i.Quantity);

        decimal totalMin;
        decimal? totalMax;
        MeasurementUnit finalUnit;
        Func<ConsolidatableIngredientInput, (decimal Min, decimal? Max)> toFinalUnit;

        if (!hasMetric && hasUs)
        {
            decimal totalTspMin = 0m;
            decimal totalTspMax = 0m;

            foreach (var i in items)
            {
                var min = i.Quantity ?? 0m;
                var max = i.MaxQuantity ?? min;
                var factor = GetUsVolumeTspFactor(i.Unit ?? MeasurementUnit.Tsp);
                totalTspMin += min * factor;
                totalTspMax += max * factor;
            }

            finalUnit = SelectBestUsVolumeUnit(totalTspMin);
            var finalFactor = GetUsVolumeTspFactor(finalUnit);

            totalMin = totalTspMin / finalFactor;
            totalMax = hasAnyMax ? totalTspMax / finalFactor : null;

            toFinalUnit = i =>
            {
                var min = i.Quantity ?? 0m;
                var max = i.MaxQuantity ?? min;
                var factor = GetUsVolumeTspFactor(i.Unit ?? MeasurementUnit.Tsp);
                var fMin = (min * factor) / finalFactor;
                var fMax = i.MaxQuantity.HasValue ? (max * factor) / finalFactor : (decimal?)null;
                return (fMin, fMax);
            };
        }
        else if (hasMetric && !hasUs)
        {
            decimal totalMlMin = 0m;
            decimal totalMlMax = 0m;

            foreach (var i in items)
            {
                var min = i.Quantity ?? 0m;
                var max = i.MaxQuantity ?? min;
                var factor = i.Unit == MeasurementUnit.L ? 1000m : 1m;
                totalMlMin += min * factor;
                totalMlMax += max * factor;
            }

            finalUnit = totalMlMin >= 1000m ? MeasurementUnit.L : MeasurementUnit.Ml;
            var finalFactor = finalUnit == MeasurementUnit.L ? 1000m : 1m;

            totalMin = totalMlMin / finalFactor;
            totalMax = hasAnyMax ? totalMlMax / finalFactor : null;

            toFinalUnit = i =>
            {
                var min = i.Quantity ?? 0m;
                var max = i.MaxQuantity ?? min;
                var factor = i.Unit == MeasurementUnit.L ? 1000m : 1m;
                var fMin = (min * factor) / finalFactor;
                var fMax = i.MaxQuantity.HasValue ? (max * factor) / finalFactor : (decimal?)null;
                return (fMin, fMax);
            };
        }
        else
        {
            // Mixed US and Metric
            decimal totalMlMin = 0m;
            decimal totalMlMax = 0m;

            foreach (var i in items)
            {
                var min = i.Quantity ?? 0m;
                var max = i.MaxQuantity ?? min;
                var mlFactor = GetAnyVolumeMlFactor(i.Unit ?? MeasurementUnit.Tsp);
                totalMlMin += min * mlFactor;
                totalMlMax += max * mlFactor;
            }

            if (firstIsMetric)
            {
                finalUnit = totalMlMin >= 1000m ? MeasurementUnit.L : MeasurementUnit.Ml;
                var finalFactor = finalUnit == MeasurementUnit.L ? 1000m : 1m;
                totalMin = totalMlMin / finalFactor;
                totalMax = hasAnyMax ? totalMlMax / finalFactor : null;

                toFinalUnit = i =>
                {
                    var min = i.Quantity ?? 0m;
                    var max = i.MaxQuantity ?? min;
                    var factor = GetAnyVolumeMlFactor(i.Unit ?? MeasurementUnit.Tsp);
                    var fMin = (min * factor) / finalFactor;
                    var fMax = i.MaxQuantity.HasValue
                        ? (max * factor) / finalFactor
                        : (decimal?)null;
                    return (fMin, fMax);
                };
            }
            else
            {
                // Convert ML to TSP
                const decimal tspMl = 4.92892m;
                var totalTspMin = totalMlMin / tspMl;
                var totalTspMax = totalMlMax / tspMl;

                finalUnit = SelectBestUsVolumeUnit(totalTspMin);
                var finalTspFactor = GetUsVolumeTspFactor(finalUnit);

                totalMin = totalTspMin / finalTspFactor;
                totalMax = hasAnyMax ? totalTspMax / finalTspFactor : null;

                toFinalUnit = i =>
                {
                    var min = i.Quantity ?? 0m;
                    var max = i.MaxQuantity ?? min;
                    var mlF = GetAnyVolumeMlFactor(i.Unit ?? MeasurementUnit.Tsp);
                    var tspValMin = (min * mlF) / tspMl;
                    var tspValMax = (max * mlF) / tspMl;
                    var fMin = tspValMin / finalTspFactor;
                    var fMax = i.MaxQuantity.HasValue ? tspValMax / finalTspFactor : (decimal?)null;
                    return (fMin, fMax);
                };
            }
        }

        var sources = BuildDeduplicatedSources(items, toFinalUnit);

        return new ConsolidatedShoppingBucket(
            key,
            foodItemId,
            displayName,
            isPantryStaple,
            MeasurementDimension.Volume,
            totalMin,
            totalMax,
            finalUnit,
            null,
            mergedNotes,
            sources
        );
    }

    private static ConsolidatedShoppingBucket ConsolidateWeightBucket(
        string key,
        Guid? foodItemId,
        string displayName,
        bool isPantryStaple,
        string? mergedNotes,
        List<ConsolidatableIngredientInput> items
    )
    {
        var hasMetric = items.Any(i => i.Unit is MeasurementUnit.G or MeasurementUnit.Kg);
        var hasUs = items.Any(i => i.Unit is MeasurementUnit.Oz or MeasurementUnit.Lb);

        var firstUnit = items.FirstOrDefault(i => i.Unit.HasValue)?.Unit;
        var firstIsMetric = firstUnit is MeasurementUnit.G or MeasurementUnit.Kg;

        var hasAnyMax = items.Any(i => i.MaxQuantity.HasValue && i.MaxQuantity > i.Quantity);

        decimal totalMin;
        decimal? totalMax;
        MeasurementUnit finalUnit;
        Func<ConsolidatableIngredientInput, (decimal Min, decimal? Max)> toFinalUnit;

        if (!hasMetric && hasUs)
        {
            decimal totalOzMin = 0m;
            decimal totalOzMax = 0m;

            foreach (var i in items)
            {
                var min = i.Quantity ?? 0m;
                var max = i.MaxQuantity ?? min;
                var factor = i.Unit == MeasurementUnit.Lb ? 16m : 1m;
                totalOzMin += min * factor;
                totalOzMax += max * factor;
            }

            finalUnit = totalOzMin >= 16m ? MeasurementUnit.Lb : MeasurementUnit.Oz;
            var finalFactor = finalUnit == MeasurementUnit.Lb ? 16m : 1m;

            totalMin = totalOzMin / finalFactor;
            totalMax = hasAnyMax ? totalOzMax / finalFactor : null;

            toFinalUnit = i =>
            {
                var min = i.Quantity ?? 0m;
                var max = i.MaxQuantity ?? min;
                var factor = i.Unit == MeasurementUnit.Lb ? 16m : 1m;
                var fMin = (min * factor) / finalFactor;
                var fMax = i.MaxQuantity.HasValue ? (max * factor) / finalFactor : (decimal?)null;
                return (fMin, fMax);
            };
        }
        else if (hasMetric && !hasUs)
        {
            decimal totalGMin = 0m;
            decimal totalGMax = 0m;

            foreach (var i in items)
            {
                var min = i.Quantity ?? 0m;
                var max = i.MaxQuantity ?? min;
                var factor = i.Unit == MeasurementUnit.Kg ? 1000m : 1m;
                totalGMin += min * factor;
                totalGMax += max * factor;
            }

            finalUnit = totalGMin >= 1000m ? MeasurementUnit.Kg : MeasurementUnit.G;
            var finalFactor = finalUnit == MeasurementUnit.Kg ? 1000m : 1m;

            totalMin = totalGMin / finalFactor;
            totalMax = hasAnyMax ? totalGMax / finalFactor : null;

            toFinalUnit = i =>
            {
                var min = i.Quantity ?? 0m;
                var max = i.MaxQuantity ?? min;
                var factor = i.Unit == MeasurementUnit.Kg ? 1000m : 1m;
                var fMin = (min * factor) / finalFactor;
                var fMax = i.MaxQuantity.HasValue ? (max * factor) / finalFactor : (decimal?)null;
                return (fMin, fMax);
            };
        }
        else
        {
            // Mixed US & Metric Weight
            decimal totalGMin = 0m;
            decimal totalGMax = 0m;

            foreach (var i in items)
            {
                var min = i.Quantity ?? 0m;
                var max = i.MaxQuantity ?? min;
                var gFactor = GetAnyWeightGFactor(i.Unit ?? MeasurementUnit.Oz);
                totalGMin += min * gFactor;
                totalGMax += max * gFactor;
            }

            if (firstIsMetric)
            {
                finalUnit = totalGMin >= 1000m ? MeasurementUnit.Kg : MeasurementUnit.G;
                var finalFactor = finalUnit == MeasurementUnit.Kg ? 1000m : 1m;
                totalMin = totalGMin / finalFactor;
                totalMax = hasAnyMax ? totalGMax / finalFactor : null;

                toFinalUnit = i =>
                {
                    var min = i.Quantity ?? 0m;
                    var max = i.MaxQuantity ?? min;
                    var factor = GetAnyWeightGFactor(i.Unit ?? MeasurementUnit.Oz);
                    var fMin = (min * factor) / finalFactor;
                    var fMax = i.MaxQuantity.HasValue
                        ? (max * factor) / finalFactor
                        : (decimal?)null;
                    return (fMin, fMax);
                };
            }
            else
            {
                const decimal ozG = 28.3495m;
                var totalOzMin = totalGMin / ozG;
                var totalOzMax = totalGMax / ozG;

                finalUnit = totalOzMin >= 16m ? MeasurementUnit.Lb : MeasurementUnit.Oz;
                var finalFactor = finalUnit == MeasurementUnit.Lb ? 16m : 1m;

                totalMin = totalOzMin / finalFactor;
                totalMax = hasAnyMax ? totalOzMax / finalFactor : null;

                toFinalUnit = i =>
                {
                    var min = i.Quantity ?? 0m;
                    var max = i.MaxQuantity ?? min;
                    var gFactor = GetAnyWeightGFactor(i.Unit ?? MeasurementUnit.Oz);
                    var ozValMin = (min * gFactor) / ozG;
                    var ozValMax = (max * gFactor) / ozG;
                    var fMin = ozValMin / finalFactor;
                    var fMax = i.MaxQuantity.HasValue ? ozValMax / finalFactor : (decimal?)null;
                    return (fMin, fMax);
                };
            }
        }

        var sources = BuildDeduplicatedSources(items, toFinalUnit);

        return new ConsolidatedShoppingBucket(
            key,
            foodItemId,
            displayName,
            isPantryStaple,
            MeasurementDimension.Weight,
            totalMin,
            totalMax,
            finalUnit,
            null,
            mergedNotes,
            sources
        );
    }

    private static ConsolidatedShoppingBucket ConsolidateDiscreteBucket(
        string key,
        Guid? foodItemId,
        string displayName,
        bool isPantryStaple,
        string? mergedNotes,
        List<ConsolidatableIngredientInput> items
    )
    {
        var first = items[0];
        var unit = first.Unit ?? MeasurementUnit.Whole;
        var customUnitName = unit == MeasurementUnit.Custom ? first.CustomUnitName : null;

        var hasAnyMax = items.Any(i => i.MaxQuantity.HasValue && i.MaxQuantity > i.Quantity);

        decimal totalMin = 0m;
        decimal totalMax = 0m;

        foreach (var i in items)
        {
            var min = i.Quantity ?? 0m;
            var max = i.MaxQuantity ?? min;
            totalMin += min;
            totalMax += max;
        }

        var finalMax = hasAnyMax ? totalMax : (decimal?)null;

        var sources = BuildDeduplicatedSources(items, i => (i.Quantity ?? 0m, i.MaxQuantity));

        return new ConsolidatedShoppingBucket(
            key,
            foodItemId,
            displayName,
            isPantryStaple,
            MeasurementDimension.Discrete,
            totalMin,
            finalMax,
            unit,
            customUnitName,
            mergedNotes,
            sources
        );
    }

    private static ConsolidatedShoppingBucket ConsolidateUnspecifiedBucket(
        string key,
        Guid? foodItemId,
        string displayName,
        bool isPantryStaple,
        string? mergedNotes,
        List<ConsolidatableIngredientInput> items
    )
    {
        var consistentUnit =
            items.Select(i => i.Unit).Distinct().Count() == 1 ? items[0].Unit : null;
        var customUnitName =
            consistentUnit == MeasurementUnit.Custom ? items[0].CustomUnitName : null;

        var sources = BuildDeduplicatedSources(items, _ => (0m, null));

        return new ConsolidatedShoppingBucket(
            key,
            foodItemId,
            displayName,
            isPantryStaple,
            MeasurementDimension.Unspecified,
            null,
            null,
            consistentUnit,
            customUnitName,
            mergedNotes,
            sources
        );
    }

    private static IReadOnlyList<ConsolidatedSourceContribution> BuildDeduplicatedSources(
        List<ConsolidatableIngredientInput> items,
        Func<ConsolidatableIngredientInput, (decimal Min, decimal? Max)> toFinalUnit
    )
    {
        var dict =
            new Dictionary<
                (ShoppingListSourceKind, Guid?, Guid?, string),
                (DateOnly? ScheduledDate, decimal Qty, decimal? MaxQty, bool HasMax)
            >();

        foreach (var i in items)
        {
            var srcKey = (i.SourceKind, i.MealPlanEntryId, i.RecipeId, i.SourceLabel);
            var (min, max) = toFinalUnit(i);

            if (!dict.TryGetValue(srcKey, out var current))
            {
                dict[srcKey] = (i.ScheduledDate, min, max, max.HasValue);
            }
            else
            {
                var newMin = current.Qty + min;
                var currentMax = current.MaxQty ?? current.Qty;
                var addMax = max ?? min;
                var hasMax = current.HasMax || max.HasValue;
                dict[srcKey] = (
                    current.ScheduledDate ?? i.ScheduledDate,
                    newMin,
                    hasMax ? currentMax + addMax : null,
                    hasMax
                );
            }
        }

        return dict.Select(kvp => new ConsolidatedSourceContribution(
                kvp.Key.Item1,
                kvp.Key.Item2,
                kvp.Key.Item3,
                kvp.Key.Item4,
                kvp.Value.ScheduledDate,
                kvp.Value.Qty,
                kvp.Value.MaxQty
            ))
            .ToList();
    }

    private static MeasurementUnit SelectBestUsVolumeUnit(decimal totalTsp)
    {
        if (totalTsp < 3m)
        {
            return MeasurementUnit.Tsp;
        }

        if (totalTsp < 12m)
        {
            return MeasurementUnit.Tbsp;
        }

        if (totalTsp < 192m)
        {
            var cupVal = totalTsp / 48m;
            var tbspVal = totalTsp / 3m;

            if (
                totalTsp < 48m
                && !IsCleanKitchenFraction(cupVal)
                && IsCleanKitchenFraction(tbspVal)
            )
            {
                return MeasurementUnit.Tbsp;
            }

            return MeasurementUnit.Cup;
        }

        if (totalTsp < 768m)
        {
            return MeasurementUnit.Qt;
        }

        return MeasurementUnit.Gal;
    }

    private static bool IsCleanKitchenFraction(decimal val)
    {
        var whole = Math.Floor(val);
        var frac = val - whole;

        if (frac < 0.04m || frac >= 0.96m)
        {
            return true;
        }

        foreach (var target in CleanFractionTargets)
        {
            if (Math.Abs(frac - target) <= 0.045m)
            {
                return true;
            }
        }

        return false;
    }

    private static decimal GetUsVolumeTspFactor(MeasurementUnit unit) =>
        unit switch
        {
            MeasurementUnit.Tsp => 1m,
            MeasurementUnit.Tbsp => 3m,
            MeasurementUnit.FlOz => 6m,
            MeasurementUnit.Cup => 48m,
            MeasurementUnit.Pt => 96m,
            MeasurementUnit.Qt => 192m,
            MeasurementUnit.Gal => 768m,
            _ => 1m,
        };

    private static decimal GetAnyVolumeMlFactor(MeasurementUnit unit) =>
        unit switch
        {
            MeasurementUnit.Ml => 1m,
            MeasurementUnit.L => 1000m,
            MeasurementUnit.Tsp => 4.92892m,
            MeasurementUnit.Tbsp => 14.7868m,
            MeasurementUnit.FlOz => 29.5735m,
            MeasurementUnit.Cup => 236.588m,
            MeasurementUnit.Pt => 473.176m,
            MeasurementUnit.Qt => 946.353m,
            MeasurementUnit.Gal => 3785.41m,
            _ => 1m,
        };

    private static decimal GetAnyWeightGFactor(MeasurementUnit unit) =>
        unit switch
        {
            MeasurementUnit.G => 1m,
            MeasurementUnit.Kg => 1000m,
            MeasurementUnit.Oz => 28.3495m,
            MeasurementUnit.Lb => 453.592m,
            _ => 1m,
        };
}
