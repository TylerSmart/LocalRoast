using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRefinementTogglesAndTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RefinedModel",
                table: "Recipes",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true
            );

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RefinedAt",
                table: "IngestionJobs",
                type: "datetimeoffset",
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "RefinedModel",
                table: "IngestionJobs",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "RefinementAttemptCount",
                table: "IngestionJobs",
                type: "int",
                nullable: false,
                defaultValue: 0
            );

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RefinementNextRetryAt",
                table: "IngestionJobs",
                type: "datetimeoffset",
                nullable: true
            );

            migrationBuilder.AddColumn<bool>(
                name: "RefineDuringIdle",
                table: "HouseholdLlmSettings",
                type: "bit",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.AddColumn<bool>(
                name: "RefineDuringIngestion",
                table: "HouseholdLlmSettings",
                type: "bit",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.CreateIndex(
                name: "IX_IngestionJobs_Status_RefinedAt_CreatedAt",
                table: "IngestionJobs",
                columns: new[] { "Status", "RefinedAt", "CreatedAt" }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_IngestionJobs_Status_RefinedAt_CreatedAt",
                table: "IngestionJobs"
            );

            migrationBuilder.DropColumn(name: "RefinedModel", table: "Recipes");

            migrationBuilder.DropColumn(name: "RefinedAt", table: "IngestionJobs");

            migrationBuilder.DropColumn(name: "RefinedModel", table: "IngestionJobs");

            migrationBuilder.DropColumn(name: "RefinementAttemptCount", table: "IngestionJobs");

            migrationBuilder.DropColumn(name: "RefinementNextRetryAt", table: "IngestionJobs");

            migrationBuilder.DropColumn(name: "RefineDuringIdle", table: "HouseholdLlmSettings");

            migrationBuilder.DropColumn(
                name: "RefineDuringIngestion",
                table: "HouseholdLlmSettings"
            );
        }
    }
}
