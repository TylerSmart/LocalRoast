using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddModelProfilesAndDraftRefinement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExtractionStrategy",
                table: "HouseholdLlmSettings",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Auto"
            );

            migrationBuilder.AddColumn<bool>(
                name: "RefineEnableThinking",
                table: "HouseholdLlmSettings",
                type: "bit",
                nullable: false,
                defaultValue: true
            );

            migrationBuilder.AddColumn<string>(
                name: "RefineModel",
                table: "HouseholdLlmSettings",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "RefineRequestTimeoutSeconds",
                table: "HouseholdLlmSettings",
                type: "int",
                nullable: false,
                defaultValue: 1200
            );

            migrationBuilder.AddColumn<double>(
                name: "RefineTemperature",
                table: "HouseholdLlmSettings",
                type: "float",
                nullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "RefineTopK",
                table: "HouseholdLlmSettings",
                type: "int",
                nullable: true
            );

            migrationBuilder.AddColumn<double>(
                name: "RefineTopP",
                table: "HouseholdLlmSettings",
                type: "float",
                nullable: true
            );

            migrationBuilder.CreateTable(
                name: "IngestionDraftRefinements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IngestionJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<string>(
                        type: "nvarchar(32)",
                        maxLength: 32,
                        nullable: false
                    ),
                    Model = table.Column<string>(
                        type: "nvarchar(200)",
                        maxLength: 200,
                        nullable: false
                    ),
                    Detail = table.Column<string>(
                        type: "nvarchar(2000)",
                        maxLength: 2000,
                        nullable: true
                    ),
                    BaseDraftJson = table.Column<string>(
                        type: "nvarchar(max)",
                        maxLength: 200000,
                        nullable: true
                    ),
                    ProposalJson = table.Column<string>(
                        type: "nvarchar(max)",
                        maxLength: 200000,
                        nullable: true
                    ),
                    IssuesBefore = table.Column<int>(type: "int", nullable: true),
                    IssuesAfter = table.Column<int>(type: "int", nullable: true),
                    RequestedAt = table.Column<DateTimeOffset>(
                        type: "datetimeoffset",
                        nullable: false
                    ),
                    CompletedAt = table.Column<DateTimeOffset>(
                        type: "datetimeoffset",
                        nullable: true
                    ),
                    CreatedAt = table.Column<DateTimeOffset>(
                        type: "datetimeoffset",
                        nullable: false
                    ),
                    CreatedBy = table.Column<string>(
                        type: "nvarchar(256)",
                        maxLength: 256,
                        nullable: false
                    ),
                    ModifiedAt = table.Column<DateTimeOffset>(
                        type: "datetimeoffset",
                        nullable: false
                    ),
                    ModifiedBy = table.Column<string>(
                        type: "nvarchar(256)",
                        maxLength: 256,
                        nullable: false
                    ),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IngestionDraftRefinements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IngestionDraftRefinements_IngestionJobs_IngestionJobId",
                        column: x => x.IngestionJobId,
                        principalTable: "IngestionJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_IngestionDraftRefinements_IngestionJobId",
                table: "IngestionDraftRefinements",
                column: "IngestionJobId",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_IngestionDraftRefinements_Status_RequestedAt",
                table: "IngestionDraftRefinements",
                columns: new[] { "Status", "RequestedAt" }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "IngestionDraftRefinements");

            migrationBuilder.DropColumn(name: "ExtractionStrategy", table: "HouseholdLlmSettings");

            migrationBuilder.DropColumn(
                name: "RefineEnableThinking",
                table: "HouseholdLlmSettings"
            );

            migrationBuilder.DropColumn(name: "RefineModel", table: "HouseholdLlmSettings");

            migrationBuilder.DropColumn(
                name: "RefineRequestTimeoutSeconds",
                table: "HouseholdLlmSettings"
            );

            migrationBuilder.DropColumn(name: "RefineTemperature", table: "HouseholdLlmSettings");

            migrationBuilder.DropColumn(name: "RefineTopK", table: "HouseholdLlmSettings");

            migrationBuilder.DropColumn(name: "RefineTopP", table: "HouseholdLlmSettings");
        }
    }
}
