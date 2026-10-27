using System.Globalization;

namespace App.Core.Recipes;

public record FormattedQuantityResult(
    decimal? Quantity,
    decimal? MaxQuantity,
    MeasurementUnit? Unit,
    string UnitLabel,
    string FormattedAmount
);

public static class CulinaryMath
{
    private static readonly (decimal Target, string Fraction)[] KnownFractions =
    [
        (0.125m, "1/8"),
        (0.25m, "1/4"),
        (0.3333333m, "1/3"),
        (0.375m, "3/8"),
        (0.5m, "1/2"),
        (0.625m, "5/8"),
        (0.6666667m, "2/3"),
        (0.75m, "3/4"),
        (0.875m, "7/8"),
    ];

    public static FormattedQuantityResult ScaleAndConvert(
        decimal? quantity,
        decimal? maxQuantity,
        MeasurementUnit? unit,
        string? customUnitName,
        decimal scaleFactor,
        UnitSystem targetSystem
    )
    {
        if (scaleFactor <= 0m)
        {
            scaleFactor = 1m;
        }

        if (!quantity.HasValue)
        {
            var emptyUnitLabel = GetUnitLabel(unit, customUnitName, null);
            return new FormattedQuantityResult(null, null, unit, emptyUnitLabel, string.Empty);
        }

        var scaledQty = quantity.Value * scaleFactor;
        decimal? scaledMaxQty = maxQuantity.HasValue ? maxQuantity.Value * scaleFactor : null;

        var (convQty, convMaxQty, finalUnit) = ConvertUnit(
            scaledQty,
            scaledMaxQty,
            unit,
            targetSystem
        );

        var isMetric = IsMetricUnit(finalUnit);
        var unitLabel = GetUnitLabel(finalUnit, customUnitName, convQty);

        var formattedAmount = FormatAmount(convQty, convMaxQty, isMetric);

        return new FormattedQuantityResult(
            convQty,
            convMaxQty,
            finalUnit,
            unitLabel,
            formattedAmount
        );
    }

    private static (decimal Qty, decimal? MaxQty, MeasurementUnit? Unit) ConvertUnit(
        decimal qty,
        decimal? maxQty,
        MeasurementUnit? unit,
        UnitSystem targetSystem
    )
    {
        if (unit is null || targetSystem == UnitSystem.Original)
        {
            return (qty, maxQty, unit);
        }

        if (targetSystem == UnitSystem.Metric)
        {
            if (TryGetVolumeMlFactor(unit.Value, out var mlFactor))
            {
                var ml = qty * mlFactor;
                var maxMl = maxQty * mlFactor;

                if (ml >= 1000m)
                {
                    return (ml / 1000m, maxMl / 1000m, MeasurementUnit.L);
                }
                return (ml, maxMl, MeasurementUnit.Ml);
            }

            if (TryGetWeightGFactor(unit.Value, out var gFactor))
            {
                var g = qty * gFactor;
                var maxG = maxQty * gFactor;

                if (g >= 1000m)
                {
                    return (g / 1000m, maxG / 1000m, MeasurementUnit.Kg);
                }
                return (g, maxG, MeasurementUnit.G);
            }
        }
        else if (targetSystem == UnitSystem.UsCustomary)
        {
            if (unit == MeasurementUnit.Ml || unit == MeasurementUnit.L)
            {
                var ml = unit == MeasurementUnit.L ? qty * 1000m : qty;
                var maxMl = maxQty.HasValue
                    ? (unit == MeasurementUnit.L ? maxQty.Value * 1000m : maxQty.Value)
                    : (decimal?)null;

                if (ml < 14.7868m)
                {
                    var f = 4.92892m;
                    return (ml / f, maxMl / f, MeasurementUnit.Tsp);
                }
                if (ml < 59.147m)
                {
                    var f = 14.7868m;
                    return (ml / f, maxMl / f, MeasurementUnit.Tbsp);
                }
                if (ml < 946.353m)
                {
                    var f = 236.588m;
                    return (ml / f, maxMl / f, MeasurementUnit.Cup);
                }
                var qtf = 946.353m;
                return (ml / qtf, maxMl / qtf, MeasurementUnit.Qt);
            }

            if (unit == MeasurementUnit.G || unit == MeasurementUnit.Kg)
            {
                var g = unit == MeasurementUnit.Kg ? qty * 1000m : qty;
                var maxG = maxQty.HasValue
                    ? (unit == MeasurementUnit.Kg ? maxQty.Value * 1000m : maxQty.Value)
                    : (decimal?)null;

                if (g < 453.592m)
                {
                    var ozf = 28.3495m;
                    return (g / ozf, maxG / ozf, MeasurementUnit.Oz);
                }
                var lbf = 453.592m;
                return (g / lbf, maxG / lbf, MeasurementUnit.Lb);
            }
        }

        return (qty, maxQty, unit);
    }

    private static bool TryGetVolumeMlFactor(MeasurementUnit unit, out decimal factor)
    {
        switch (unit)
        {
            case MeasurementUnit.Tsp:
                factor = 4.92892m;
                return true;
            case MeasurementUnit.Tbsp:
                factor = 14.7868m;
                return true;
            case MeasurementUnit.FlOz:
                factor = 29.5735m;
                return true;
            case MeasurementUnit.Cup:
                factor = 236.588m;
                return true;
            case MeasurementUnit.Pt:
                factor = 473.176m;
                return true;
            case MeasurementUnit.Qt:
                factor = 946.353m;
                return true;
            case MeasurementUnit.Gal:
                factor = 3785.41m;
                return true;
            case MeasurementUnit.Ml:
                factor = 1m;
                return true;
            case MeasurementUnit.L:
                factor = 1000m;
                return true;
            default:
                factor = 0m;
                return false;
        }
    }

    private static bool TryGetWeightGFactor(MeasurementUnit unit, out decimal factor)
    {
        switch (unit)
        {
            case MeasurementUnit.Oz:
                factor = 28.3495m;
                return true;
            case MeasurementUnit.Lb:
                factor = 453.592m;
                return true;
            case MeasurementUnit.G:
                factor = 1m;
                return true;
            case MeasurementUnit.Kg:
                factor = 1000m;
                return true;
            default:
                factor = 0m;
                return false;
        }
    }

    public static bool IsMetricUnit(MeasurementUnit? unit) =>
        unit is MeasurementUnit.Ml or MeasurementUnit.L or MeasurementUnit.G or MeasurementUnit.Kg;

    public static string GetUnitLabel(MeasurementUnit? unit, string? customUnitName, decimal? qty)
    {
        if (unit is null)
        {
            return string.Empty;
        }

        return unit switch
        {
            MeasurementUnit.Tsp => "tsp",
            MeasurementUnit.Tbsp => "tbsp",
            MeasurementUnit.FlOz => "fl oz",
            MeasurementUnit.Cup => qty > 1.05m ? "cups" : "cup",
            MeasurementUnit.Pt => "pt",
            MeasurementUnit.Qt => "qt",
            MeasurementUnit.Gal => "gal",
            MeasurementUnit.Ml => "ml",
            MeasurementUnit.L => "l",
            MeasurementUnit.Oz => "oz",
            MeasurementUnit.Lb => "lb",
            MeasurementUnit.G => "g",
            MeasurementUnit.Kg => "kg",
            MeasurementUnit.Pinch => qty > 1.05m ? "pinches" : "pinch",
            MeasurementUnit.Dash => qty > 1.05m ? "dashes" : "dash",
            MeasurementUnit.Clove => qty > 1.05m ? "cloves" : "clove",
            MeasurementUnit.Can => qty > 1.05m ? "cans" : "can",
            MeasurementUnit.Bunch => qty > 1.05m ? "bunches" : "bunch",
            MeasurementUnit.Slice => qty > 1.05m ? "slices" : "slice",
            MeasurementUnit.Whole => string.Empty,
            MeasurementUnit.Custom => customUnitName ?? string.Empty,
            _ => string.Empty,
        };
    }

    public static string FormatAmount(decimal min, decimal? max, bool isMetric)
    {
        var minStr = FormatNumber(min, isMetric);
        if (!max.HasValue || max.Value <= min)
        {
            return minStr;
        }

        var maxStr = FormatNumber(max.Value, isMetric);
        return $"{minStr}–{maxStr}";
    }

    public static string FormatNumber(decimal value, bool isMetric)
    {
        if (value <= 0m)
        {
            return "0";
        }

        if (isMetric)
        {
            if (value >= 10m)
            {
                return Math.Round(value, MidpointRounding.AwayFromZero)
                    .ToString("0", CultureInfo.InvariantCulture);
            }

            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        var whole = (int)Math.Floor(value);
        var frac = value - whole;

        if (frac >= 0.96m)
        {
            return (whole + 1).ToString(CultureInfo.InvariantCulture);
        }

        if (frac < 0.04m)
        {
            return whole.ToString(CultureInfo.InvariantCulture);
        }

        string? matchedFraction = null;
        var minDiff = decimal.MaxValue;

        foreach (var (target, fracStr) in KnownFractions)
        {
            var diff = Math.Abs(frac - target);
            if (diff <= 0.045m && diff < minDiff)
            {
                minDiff = diff;
                matchedFraction = fracStr;
            }
        }

        if (matchedFraction is not null)
        {
            return whole > 0 ? $"{whole} {matchedFraction}" : matchedFraction;
        }

        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    public static string FormatDuration(int durationSeconds)
    {
        if (durationSeconds <= 0)
        {
            return "0 sec";
        }

        if (durationSeconds < 60)
        {
            return $"{durationSeconds} sec";
        }

        if (durationSeconds < 3600)
        {
            var mins = durationSeconds / 60;
            var secs = durationSeconds % 60;
            return secs > 0 ? $"{mins} min {secs} sec" : $"{mins} min";
        }

        var hrs = durationSeconds / 3600;
        var remMins = (durationSeconds % 3600) / 60;
        return remMins > 0 ? $"{hrs} hr {remMins} min" : $"{hrs} hr";
    }

    public static string FormatMinutes(int? minutes)
    {
        if (!minutes.HasValue || minutes.Value < 0)
        {
            return string.Empty;
        }

        if (minutes.Value < 60)
        {
            return $"{minutes.Value} min";
        }

        var hrs = minutes.Value / 60;
        var remMins = minutes.Value % 60;
        return remMins > 0 ? $"{hrs} hr {remMins} min" : $"{hrs} hr";
    }
}
