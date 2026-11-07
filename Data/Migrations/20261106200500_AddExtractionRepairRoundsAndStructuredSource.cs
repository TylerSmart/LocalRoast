using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExtractionRepairRoundsAndStructuredSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StructuredSourceText",
                table: "IngestionJobs",
                type: "nvarchar(max)",
                maxLength: 100000,
                nullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "ExtractionRepairRounds",
                table: "HouseholdLlmSettings",
                type: "int",
                nullable: false,
                defaultValue: 1
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "StructuredSourceText", table: "IngestionJobs");

            migrationBuilder.DropColumn(
                name: "ExtractionRepairRounds",
                table: "HouseholdLlmSettings"
            );
        }
    }
}
