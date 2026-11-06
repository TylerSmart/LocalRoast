using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIngredientAlternativesAndStepAmounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CustomUnitName",
                table: "RecipeStepIngredients",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true
            );

            migrationBuilder.AddColumn<decimal>(
                name: "MaxQuantity",
                table: "RecipeStepIngredients",
                type: "decimal(10,4)",
                precision: 10,
                scale: 4,
                nullable: true
            );

            migrationBuilder.AddColumn<decimal>(
                name: "Quantity",
                table: "RecipeStepIngredients",
                type: "decimal(10,4)",
                precision: 10,
                scale: 4,
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "Unit",
                table: "RecipeStepIngredients",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "AlternativeCustomUnitName",
                table: "RecipeIngredients",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true
            );

            migrationBuilder.AddColumn<decimal>(
                name: "AlternativeMaxQuantity",
                table: "RecipeIngredients",
                type: "decimal(10,4)",
                precision: 10,
                scale: 4,
                nullable: true
            );

            migrationBuilder.AddColumn<decimal>(
                name: "AlternativeQuantity",
                table: "RecipeIngredients",
                type: "decimal(10,4)",
                precision: 10,
                scale: 4,
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "AlternativeUnit",
                table: "RecipeIngredients",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true
            );

            migrationBuilder.AddColumn<bool>(
                name: "PreferAlternativeMeasurement",
                table: "RecipeIngredients",
                type: "bit",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.AddColumn<bool>(
                name: "OllamaCpuOnly",
                table: "HouseholdLlmSettings",
                type: "bit",
                nullable: false,
                defaultValue: false
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "CustomUnitName", table: "RecipeStepIngredients");

            migrationBuilder.DropColumn(name: "MaxQuantity", table: "RecipeStepIngredients");

            migrationBuilder.DropColumn(name: "Quantity", table: "RecipeStepIngredients");

            migrationBuilder.DropColumn(name: "Unit", table: "RecipeStepIngredients");

            migrationBuilder.DropColumn(
                name: "AlternativeCustomUnitName",
                table: "RecipeIngredients"
            );

            migrationBuilder.DropColumn(name: "AlternativeMaxQuantity", table: "RecipeIngredients");

            migrationBuilder.DropColumn(name: "AlternativeQuantity", table: "RecipeIngredients");

            migrationBuilder.DropColumn(name: "AlternativeUnit", table: "RecipeIngredients");

            migrationBuilder.DropColumn(
                name: "PreferAlternativeMeasurement",
                table: "RecipeIngredients"
            );

            migrationBuilder.DropColumn(name: "OllamaCpuOnly", table: "HouseholdLlmSettings");
        }
    }
}
