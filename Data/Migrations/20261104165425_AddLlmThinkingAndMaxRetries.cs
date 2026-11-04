using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmThinkingAndMaxRetries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnableThinking",
                table: "HouseholdLlmSettings",
                type: "bit",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.AddColumn<int>(
                name: "MaxIngestionRetries",
                table: "HouseholdLlmSettings",
                type: "int",
                nullable: false,
                defaultValue: 0
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "EnableThinking", table: "HouseholdLlmSettings");

            migrationBuilder.DropColumn(name: "MaxIngestionRetries", table: "HouseholdLlmSettings");
        }
    }
}
