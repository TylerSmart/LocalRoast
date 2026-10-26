using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmSettingsAndIngestionQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HouseholdLlmSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SingletonKey = table.Column<string>(
                        type: "nvarchar(32)",
                        maxLength: 32,
                        nullable: false
                    ),
                    Provider = table.Column<string>(
                        type: "nvarchar(32)",
                        maxLength: 32,
                        nullable: false
                    ),
                    BaseUrl = table.Column<string>(
                        type: "nvarchar(500)",
                        maxLength: 500,
                        nullable: false
                    ),
                    ApiKey = table.Column<string>(
                        type: "nvarchar(500)",
                        maxLength: 500,
                        nullable: true
                    ),
                    TextModel = table.Column<string>(
                        type: "nvarchar(200)",
                        maxLength: 200,
                        nullable: true
                    ),
                    VisionModel = table.Column<string>(
                        type: "nvarchar(200)",
                        maxLength: 200,
                        nullable: true
                    ),
                    OllamaKeepAlive = table.Column<string>(
                        type: "nvarchar(32)",
                        maxLength: 32,
                        nullable: false
                    ),
                    RequestTimeoutSeconds = table.Column<int>(type: "int", nullable: false),
                    AutoPublishHighConfidenceText = table.Column<bool>(
                        type: "bit",
                        nullable: false
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
                    table.PrimaryKey("PK_HouseholdLlmSettings", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "IngestionJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Channel = table.Column<string>(
                        type: "nvarchar(32)",
                        maxLength: 32,
                        nullable: false
                    ),
                    Status = table.Column<string>(
                        type: "nvarchar(32)",
                        maxLength: 32,
                        nullable: false
                    ),
                    DisplayTitle = table.Column<string>(
                        type: "nvarchar(250)",
                        maxLength: 250,
                        nullable: false
                    ),
                    SourceUrl = table.Column<string>(
                        type: "nvarchar(2048)",
                        maxLength: 2048,
                        nullable: true
                    ),
                    RawText = table.Column<string>(
                        type: "nvarchar(max)",
                        maxLength: 100000,
                        nullable: true
                    ),
                    ScrapedPageJson = table.Column<string>(
                        type: "nvarchar(max)",
                        maxLength: 200000,
                        nullable: true
                    ),
                    NormalizedSourceText = table.Column<string>(
                        type: "nvarchar(max)",
                        maxLength: 100000,
                        nullable: true
                    ),
                    DownloadedCoverImagePath = table.Column<string>(
                        type: "nvarchar(500)",
                        maxLength: 500,
                        nullable: true
                    ),
                    DownloadedThumbnailImagePath = table.Column<string>(
                        type: "nvarchar(500)",
                        maxLength: 500,
                        nullable: true
                    ),
                    ExtractedDraftJson = table.Column<string>(
                        type: "nvarchar(max)",
                        maxLength: 200000,
                        nullable: true
                    ),
                    ConfidenceScore = table.Column<decimal>(
                        type: "decimal(4,3)",
                        precision: 4,
                        scale: 3,
                        nullable: true
                    ),
                    StatusDetail = table.Column<string>(
                        type: "nvarchar(2000)",
                        maxLength: 2000,
                        nullable: true
                    ),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    NextRetryAt = table.Column<DateTimeOffset>(
                        type: "datetimeoffset",
                        nullable: true
                    ),
                    ProcessedAt = table.Column<DateTimeOffset>(
                        type: "datetimeoffset",
                        nullable: true
                    ),
                    ResultingRecipeId = table.Column<Guid>(
                        type: "uniqueidentifier",
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
                    table.PrimaryKey("PK_IngestionJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IngestionJobs_Recipes_ResultingRecipeId",
                        column: x => x.ResultingRecipeId,
                        principalTable: "Recipes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdLlmSettings_SingletonKey",
                table: "HouseholdLlmSettings",
                column: "SingletonKey",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_IngestionJobs_CreatedAt",
                table: "IngestionJobs",
                column: "CreatedAt"
            );

            migrationBuilder.CreateIndex(
                name: "IX_IngestionJobs_ResultingRecipeId",
                table: "IngestionJobs",
                column: "ResultingRecipeId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_IngestionJobs_Status",
                table: "IngestionJobs",
                column: "Status"
            );

            migrationBuilder.CreateIndex(
                name: "IX_IngestionJobs_Status_NextRetryAt",
                table: "IngestionJobs",
                columns: new[] { "Status", "NextRetryAt" }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "HouseholdLlmSettings");

            migrationBuilder.DropTable(name: "IngestionJobs");
        }
    }
}
