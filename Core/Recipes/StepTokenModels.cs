using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using App.Core.Ingestion;

namespace App.Core.Recipes;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StepTokenType
{
    Ingredient = 1,
    Timer = 2,
    Temperature = 3,
    Mixture = 4,
    Dimension = 5,
    Fixed = 6,
}

[JsonConverter(typeof(FlexibleDimensionUnitJsonConverter))]
public enum DimensionUnit
{
    Inch = 1,
    Foot = 2,
    Yard = 3,
    Mm = 4,
    Cm = 5,
    M = 6,
}

public static class DimensionUnitHelper
{
    public static bool TryParse(string? raw, out DimensionUnit unit)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            unit = default;
            return false;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "inch" or "inches" or "in" or "in.":
                unit = DimensionUnit.Inch;
                return true;
            case "foot" or "feet" or "ft" or "ft.":
                unit = DimensionUnit.Foot;
                return true;
            case "yard" or "yards" or "yd" or "yds" or "yd.":
                unit = DimensionUnit.Yard;
                return true;
            case "mm" or "millimeter" or "millimeters" or "millimetre" or "millimetres":
                unit = DimensionUnit.Mm;
                return true;
            case "cm" or "centimeter" or "centimeters" or "centimetre" or "centimetres":
                unit = DimensionUnit.Cm;
                return true;
            case "m" or "meter" or "meters" or "metre" or "metres":
                unit = DimensionUnit.M;
                return true;
            default:
                unit = default;
                return false;
        }
    }

    public static string ToJsonString(DimensionUnit unit) =>
        unit switch
        {
            DimensionUnit.Inch => "inch",
            DimensionUnit.Foot => "foot",
            DimensionUnit.Yard => "yard",
            DimensionUnit.Mm => "mm",
            DimensionUnit.Cm => "cm",
            DimensionUnit.M => "m",
            _ => "inch",
        };

    public static bool IsMetric(DimensionUnit unit) =>
        unit is DimensionUnit.Mm or DimensionUnit.Cm or DimensionUnit.M;

    public static string GetDisplayLabel(
        DimensionUnit unit,
        decimal effectiveValue,
        bool hyphenated
    )
    {
        if (hyphenated || effectiveValue <= 1.05m)
        {
            return unit switch
            {
                DimensionUnit.Inch => "inch",
                DimensionUnit.Foot => "foot",
                DimensionUnit.Yard => "yard",
                DimensionUnit.Mm => "mm",
                DimensionUnit.Cm => "cm",
                DimensionUnit.M => "m",
                _ => "inch",
            };
        }

        return unit switch
        {
            DimensionUnit.Inch => "inches",
            DimensionUnit.Foot => "feet",
            DimensionUnit.Yard => "yards",
            DimensionUnit.Mm => "mm",
            DimensionUnit.Cm => "cm",
            DimensionUnit.M => "m",
            _ => "inches",
        };
    }

    public static (decimal Value, decimal? MaxValue, DimensionUnit Unit) ConvertDimension(
        decimal value,
        decimal? maxValue,
        DimensionUnit unit,
        UnitSystem targetSystem
    )
    {
        if (targetSystem == UnitSystem.Original)
        {
            return (value, maxValue, unit);
        }

        if (targetSystem == UnitSystem.Metric)
        {
            switch (unit)
            {
                case DimensionUnit.Inch:
                    var cm = value * 2.54m;
                    if (cm < 1m)
                    {
                        return (
                            Math.Round(value * 25.4m, 0, MidpointRounding.AwayFromZero),
                            maxValue.HasValue
                                ? Math.Round(
                                    maxValue.Value * 25.4m,
                                    0,
                                    MidpointRounding.AwayFromZero
                                )
                                : null,
                            DimensionUnit.Mm
                        );
                    }
                    return (
                        Math.Round(cm, 1, MidpointRounding.AwayFromZero),
                        maxValue.HasValue
                            ? Math.Round(maxValue.Value * 2.54m, 1, MidpointRounding.AwayFromZero)
                            : null,
                        DimensionUnit.Cm
                    );
                case DimensionUnit.Foot:
                    return (
                        Math.Round(value * 30.48m, 0, MidpointRounding.AwayFromZero),
                        maxValue.HasValue
                            ? Math.Round(maxValue.Value * 30.48m, 0, MidpointRounding.AwayFromZero)
                            : null,
                        DimensionUnit.Cm
                    );
                case DimensionUnit.Yard:
                    return (
                        Math.Round(value * 0.9144m, 2, MidpointRounding.AwayFromZero),
                        maxValue.HasValue
                            ? Math.Round(maxValue.Value * 0.9144m, 2, MidpointRounding.AwayFromZero)
                            : null,
                        DimensionUnit.M
                    );
                default:
                    return (value, maxValue, unit);
            }
        }

        if (targetSystem == UnitSystem.UsCustomary)
        {
            switch (unit)
            {
                case DimensionUnit.Mm:
                    return (
                        Math.Round(value / 25.4m, 2, MidpointRounding.AwayFromZero),
                        maxValue.HasValue
                            ? Math.Round(maxValue.Value / 25.4m, 2, MidpointRounding.AwayFromZero)
                            : null,
                        DimensionUnit.Inch
                    );
                case DimensionUnit.Cm:
                    var inches = value / 2.54m;
                    if (inches >= 24m)
                    {
                        return (
                            Math.Round(inches / 12m, 2, MidpointRounding.AwayFromZero),
                            maxValue.HasValue
                                ? Math.Round(
                                    maxValue.Value / 30.48m,
                                    2,
                                    MidpointRounding.AwayFromZero
                                )
                                : null,
                            DimensionUnit.Foot
                        );
                    }
                    return (
                        Math.Round(inches, 2, MidpointRounding.AwayFromZero),
                        maxValue.HasValue
                            ? Math.Round(maxValue.Value / 2.54m, 2, MidpointRounding.AwayFromZero)
                            : null,
                        DimensionUnit.Inch
                    );
                case DimensionUnit.M:
                    return (
                        Math.Round(value * 3.28084m, 2, MidpointRounding.AwayFromZero),
                        maxValue.HasValue
                            ? Math.Round(
                                maxValue.Value * 3.28084m,
                                2,
                                MidpointRounding.AwayFromZero
                            )
                            : null,
                        DimensionUnit.Foot
                    );
                default:
                    return (value, maxValue, unit);
            }
        }

        return (value, maxValue, unit);
    }
}

public sealed class FlexibleDimensionUnitJsonConverter : JsonConverter<DimensionUnit>
{
    public override DimensionUnit Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var raw = reader.GetString();
            if (DimensionUnitHelper.TryParse(raw, out var unit))
            {
                return unit;
            }
            if (Enum.TryParse<DimensionUnit>(raw, ignoreCase: true, out var enumParsed))
            {
                return enumParsed;
            }
        }
        else if (
            reader.TokenType == JsonTokenType.Number
            && reader.TryGetInt32(out var intVal)
            && Enum.IsDefined(typeof(DimensionUnit), intVal)
        )
        {
            return (DimensionUnit)intVal;
        }

        return DimensionUnit.Inch;
    }

    public override void Write(
        Utf8JsonWriter writer,
        DimensionUnit value,
        JsonSerializerOptions options
    ) => writer.WriteStringValue(DimensionUnitHelper.ToJsonString(value));
}

public sealed class FlexibleMeasurementUnitJsonConverter : JsonConverter<MeasurementUnit?>
{
    public override MeasurementUnit? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString()?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(str) || str is "null" or "none")
            {
                return null;
            }

            return str switch
            {
                "tsp" or "teaspoon" or "teaspoons" => MeasurementUnit.Tsp,
                "tbsp" or "tablespoon" or "tablespoons" => MeasurementUnit.Tbsp,
                "fl_oz" or "fl oz" or "fluid ounce" or "fluid ounces" => MeasurementUnit.FlOz,
                "cup" or "cups" => MeasurementUnit.Cup,
                "pt" or "pint" or "pints" => MeasurementUnit.Pt,
                "qt" or "quart" or "quarts" => MeasurementUnit.Qt,
                "gal" or "gallon" or "gallons" => MeasurementUnit.Gal,
                "ml" or "milliliter" or "milliliters" => MeasurementUnit.Ml,
                "l" or "liter" or "liters" => MeasurementUnit.L,
                "oz" or "ounce" or "ounces" => MeasurementUnit.Oz,
                "lb" or "lbs" or "pound" or "pounds" => MeasurementUnit.Lb,
                "g" or "gram" or "grams" => MeasurementUnit.G,
                "kg" or "kilogram" or "kilograms" => MeasurementUnit.Kg,
                "pinch" or "pinches" => MeasurementUnit.Pinch,
                "dash" or "dashes" => MeasurementUnit.Dash,
                "clove" or "cloves" => MeasurementUnit.Clove,
                "can" or "cans" => MeasurementUnit.Can,
                "bunch" or "bunches" => MeasurementUnit.Bunch,
                "slice" or "slices" => MeasurementUnit.Slice,
                "whole" or "wholes" => MeasurementUnit.Whole,
                "custom" => MeasurementUnit.Custom,
                _ => Enum.TryParse<MeasurementUnit>(str, ignoreCase: true, out var parsed)
                    ? parsed
                    : MeasurementUnit.Custom,
            };
        }

        if (
            reader.TokenType == JsonTokenType.Number
            && reader.TryGetInt32(out var intVal)
            && Enum.IsDefined(typeof(MeasurementUnit), intVal)
        )
        {
            return (MeasurementUnit)intVal;
        }

        return null;
    }

    public override void Write(
        Utf8JsonWriter writer,
        MeasurementUnit? value,
        JsonSerializerOptions options
    )
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value.Value.ToString().ToLowerInvariant());
        }
    }
}

public abstract record StepToken([property: JsonPropertyName("type")] StepTokenType Type);

public sealed record StepIngredientToken(
    [property: JsonPropertyName("ingredient")] string Ingredient,
    [property: JsonPropertyName("ingredientId")] Guid? IngredientId = null,
    [property:
        JsonPropertyName("amount"),
        JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))
    ]
        decimal? Amount = null,
    [property:
        JsonPropertyName("unit"),
        JsonConverter(typeof(FlexibleMeasurementUnitJsonConverter))
    ]
        MeasurementUnit? Unit = null,
    [property: JsonPropertyName("customUnitName")] string? CustomUnitName = null,
    [property: JsonPropertyName("displayText")] string? DisplayText = null,
    [property: JsonPropertyName("notes")] string? Notes = null,
    [property:
        JsonPropertyName("maxAmount"),
        JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))
    ]
        decimal? MaxAmount = null,
    [property: JsonPropertyName("ingredientIndex")] int? IngredientIndex = null
) : StepToken(StepTokenType.Ingredient);

public sealed record StepMixtureToken(
    [property: JsonPropertyName("name")] string Name,
    [property:
        JsonPropertyName("amount"),
        JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))
    ]
        decimal? Amount = null,
    [property:
        JsonPropertyName("unit"),
        JsonConverter(typeof(FlexibleMeasurementUnitJsonConverter))
    ]
        MeasurementUnit? Unit = null,
    [property: JsonPropertyName("customUnitName")] string? CustomUnitName = null,
    [property:
        JsonPropertyName("maxAmount"),
        JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))
    ]
        decimal? MaxAmount = null
) : StepToken(StepTokenType.Mixture);

public sealed record StepFixedToken(
    [property:
        JsonPropertyName("amount"),
        JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))
    ]
        decimal? Amount = null,
    [property:
        JsonPropertyName("unit"),
        JsonConverter(typeof(FlexibleMeasurementUnitJsonConverter))
    ]
        MeasurementUnit? Unit = null,
    [property: JsonPropertyName("customUnitName")] string? CustomUnitName = null,
    [property:
        JsonPropertyName("maxAmount"),
        JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))
    ]
        decimal? MaxAmount = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("hyphenated")] bool Hyphenated = false
) : StepToken(StepTokenType.Fixed);

public sealed record StepDimensionToken(
    [property: JsonPropertyName("value")] decimal Value,
    [property: JsonPropertyName("unit")] DimensionUnit Unit,
    [property:
        JsonPropertyName("maxValue"),
        JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))
    ]
        decimal? MaxValue = null,
    [property: JsonPropertyName("hyphenated")] bool Hyphenated = false
) : StepToken(StepTokenType.Dimension);

public sealed record StepTimerToken(
    [property: JsonPropertyName("seconds")] int Seconds,
    [property: JsonPropertyName("label")] string? Label = null,
    [property:
        JsonPropertyName("maxSeconds"),
        JsonConverter(typeof(FlexibleNullableIntJsonConverter))
    ]
        int? MaxSeconds = null
) : StepToken(StepTokenType.Timer);

public sealed record StepTemperatureToken(
    [property:
        JsonPropertyName("fahrenheit"),
        JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))
    ]
        decimal? Fahrenheit = null,
    [property:
        JsonPropertyName("celsius"),
        JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))
    ]
        decimal? Celsius = null,
    [property:
        JsonPropertyName("maxFahrenheit"),
        JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))
    ]
        decimal? MaxFahrenheit = null,
    [property:
        JsonPropertyName("maxCelsius"),
        JsonConverter(typeof(FlexibleNullableDecimalJsonConverter))
    ]
        decimal? MaxCelsius = null
) : StepToken(StepTokenType.Temperature);

public sealed record StepInstructionSegment(string Text, StepToken? Token = null)
{
    public bool IsToken => Token != null;
}
