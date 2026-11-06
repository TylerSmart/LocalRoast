using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmSamplingOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "Temperature",
                table: "HouseholdLlmSettings",
                type: "float",
                nullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "TopK",
                table: "HouseholdLlmSettings",
                type: "int",
                nullable: true
            );

            migrationBuilder.AddColumn<double>(
                name: "TopP",
                table: "HouseholdLlmSettings",
                type: "float",
                nullable: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "Temperature", table: "HouseholdLlmSettings");

            migrationBuilder.DropColumn(name: "TopK", table: "HouseholdLlmSettings");

            migrationBuilder.DropColumn(name: "TopP", table: "HouseholdLlmSettings");
        }
    }
}
