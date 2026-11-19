using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddOnDemandRecipeRefinement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RefinementRequestedAt",
                table: "Recipes",
                type: "datetimeoffset",
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "RefinementStatus",
                table: "Recipes",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "None"
            );

            migrationBuilder.AddColumn<string>(
                name: "RefinementStatusDetail",
                table: "Recipes",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true
            );

            migrationBuilder.AddColumn<bool>(
                name: "RefinementRunNowRequested",
                table: "IngestionJobs",
                type: "bit",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_RefinementStatus_RefinementRequestedAt",
                table: "Recipes",
                columns: new[] { "RefinementStatus", "RefinementRequestedAt" }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Recipes_RefinementStatus_RefinementRequestedAt",
                table: "Recipes"
            );

            migrationBuilder.DropColumn(name: "RefinementRequestedAt", table: "Recipes");

            migrationBuilder.DropColumn(name: "RefinementStatus", table: "Recipes");

            migrationBuilder.DropColumn(name: "RefinementStatusDetail", table: "Recipes");

            migrationBuilder.DropColumn(name: "RefinementRunNowRequested", table: "IngestionJobs");
        }
    }
}
