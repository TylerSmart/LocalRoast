using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDailyQuotaResetSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DailyQuotaResetTime",
                table: "HouseholdLlmSettings",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "00:00"
            );

            migrationBuilder.AddColumn<string>(
                name: "DailyQuotaResetTimezone",
                table: "HouseholdLlmSettings",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "UTC"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "DailyQuotaResetTime", table: "HouseholdLlmSettings");

            migrationBuilder.DropColumn(
                name: "DailyQuotaResetTimezone",
                table: "HouseholdLlmSettings"
            );
        }
    }
}
