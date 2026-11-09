using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInlineIngredientNameSpans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "IngredientSpanLength",
                table: "RecipeStepInlineAmounts",
                type: "int",
                nullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "IngredientSpanStart",
                table: "RecipeStepInlineAmounts",
                type: "int",
                nullable: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IngredientSpanLength",
                table: "RecipeStepInlineAmounts"
            );

            migrationBuilder.DropColumn(
                name: "IngredientSpanStart",
                table: "RecipeStepInlineAmounts"
            );
        }
    }
}
