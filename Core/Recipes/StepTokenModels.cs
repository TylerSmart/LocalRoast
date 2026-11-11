using System.Text.Json;
using System.Text.Json.Serialization;

namespace App.Core.Recipes;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StepTokenType
{
    Ingredient = 1,
    Timer = 2,
    Temperature = 3,
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
    [property: JsonPropertyName("amount")] decimal? Amount = null,
    [property:
        JsonPropertyName("unit"),
        JsonConverter(typeof(FlexibleMeasurementUnitJsonConverter))
    ]
        MeasurementUnit? Unit = null,
    [property: JsonPropertyName("customUnitName")] string? CustomUnitName = null,
    [property: JsonPropertyName("displayAmount")] decimal? DisplayAmount = null,
    [property:
        JsonPropertyName("displayUnit"),
        JsonConverter(typeof(FlexibleMeasurementUnitJsonConverter))
    ]
        MeasurementUnit? DisplayUnit = null,
    [property: JsonPropertyName("displayCustomUnitName")] string? DisplayCustomUnitName = null,
    [property: JsonPropertyName("displayText")] string? DisplayText = null,
    [property: JsonPropertyName("notes")] string? Notes = null
) : StepToken(StepTokenType.Ingredient);

public sealed record StepTimerToken(
    [property: JsonPropertyName("seconds")] int Seconds,
    [property: JsonPropertyName("label")] string? Label = null
) : StepToken(StepTokenType.Timer);

public sealed record StepTemperatureToken(
    [property: JsonPropertyName("fahrenheit")] int? Fahrenheit = null,
    [property: JsonPropertyName("celsius")] int? Celsius = null
) : StepToken(StepTokenType.Temperature);

public sealed record StepInstructionSegment(string Text, StepToken? Token = null)
{
    public bool IsToken => Token != null;
}
