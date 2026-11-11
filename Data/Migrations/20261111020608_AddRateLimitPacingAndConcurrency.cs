using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRateLimitPacingAndConcurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoResumeQueue",
                table: "HouseholdLlmSettings",
                type: "bit",
                nullable: false,
                defaultValue: true
            );

            migrationBuilder.AddColumn<int>(
                name: "DailyRequestQuota",
                table: "HouseholdLlmSettings",
                type: "int",
                nullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "MaxConcurrentRequests",
                table: "HouseholdLlmSettings",
                type: "int",
                nullable: false,
                defaultValue: 1
            );

            migrationBuilder.AddColumn<string>(
                name: "PauseReason",
                table: "HouseholdLlmSettings",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true
            );

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PausedUntilUtc",
                table: "HouseholdLlmSettings",
                type: "datetimeoffset",
                nullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "RequestsPerMinute",
                table: "HouseholdLlmSettings",
                type: "int",
                nullable: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "AutoResumeQueue", table: "HouseholdLlmSettings");

            migrationBuilder.DropColumn(name: "DailyRequestQuota", table: "HouseholdLlmSettings");

            migrationBuilder.DropColumn(
                name: "MaxConcurrentRequests",
                table: "HouseholdLlmSettings"
            );

            migrationBuilder.DropColumn(name: "PauseReason", table: "HouseholdLlmSettings");

            migrationBuilder.DropColumn(name: "PausedUntilUtc", table: "HouseholdLlmSettings");

            migrationBuilder.DropColumn(name: "RequestsPerMinute", table: "HouseholdLlmSettings");
        }
    }
}
