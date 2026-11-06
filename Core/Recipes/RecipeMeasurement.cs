using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using App.Core.Exceptions;

namespace App.Core.Recipes;

public record RecipeMeasurement(
    [Required, Range(typeof(decimal), "0.0001", "999999.9999")] decimal? Quantity,
    [Required] MeasurementUnit? Unit,
    [Range(typeof(decimal), "0.0001", "999999.9999")] decimal? MaxQuantity = null,
    [MaxLength(50)] string? CustomUnitName = null
);

public record RecipeStepIngredientAmountInput(
    [property: JsonRequired] [Range(0, int.MaxValue)] int IngredientIndex,
    [Required] RecipeMeasurement Measurement
);

public record RecipeStepIngredientAmountDto(Guid IngredientId, RecipeMeasurement Measurement);

public static class RecipeMeasurementValidation
{
    public static void Validate(RecipeMeasurement measurement)
    {
        if (
            measurement.Quantity is null or < 0.0001m or > 999999.9999m
            || measurement.Unit is null
            || !Enum.IsDefined(measurement.Unit.Value)
            || (
                measurement.MaxQuantity.HasValue
                && (
                    measurement.MaxQuantity < measurement.Quantity
                    || measurement.MaxQuantity > 999999.9999m
                )
            )
            || (
                measurement.Unit == MeasurementUnit.Custom
                && string.IsNullOrWhiteSpace(measurement.CustomUnitName)
            )
            || measurement.CustomUnitName?.Length > 50
        )
        {
            throw new DomainValidationException(
                "Measurements require a positive quantity, a valid unit, and a maximum no smaller than the quantity. Custom units require a name."
            );
        }
    }
}
