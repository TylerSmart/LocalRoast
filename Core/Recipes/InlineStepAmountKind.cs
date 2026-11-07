using System.Text.Json.Serialization;

namespace App.Core.Recipes;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InlineStepAmountKind
{
    /// <summary>Measures an ingredient row; scales and counts toward that row's total.</summary>
    Ingredient,

    /// <summary>Measures a mixture or intermediate made in the recipe; scales but has no row.</summary>
    Mixture,

    /// <summary>Not a food amount (reduction target, equipment); never scales.</summary>
    Fixed,
}
