using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmModelProfilesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ModelProfilesSeeded",
                table: "HouseholdLlmSettings",
                type: "bit",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.CreateTable(
                name: "LlmModelProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Slug = table.Column<string>(
                        type: "nvarchar(100)",
                        maxLength: 100,
                        nullable: false
                    ),
                    DisplayName = table.Column<string>(
                        type: "nvarchar(150)",
                        maxLength: 150,
                        nullable: false
                    ),
                    ModelTag = table.Column<string>(
                        type: "nvarchar(200)",
                        maxLength: 200,
                        nullable: false
                    ),
                    MatchPrefixesJson = table.Column<string>(
                        type: "nvarchar(1000)",
                        maxLength: 1000,
                        nullable: false
                    ),
                    Summary = table.Column<string>(
                        type: "nvarchar(500)",
                        maxLength: 500,
                        nullable: false
                    ),
                    EnableThinking = table.Column<bool>(type: "bit", nullable: false),
                    Temperature = table.Column<double>(type: "float", nullable: true),
                    TopP = table.Column<double>(type: "float", nullable: true),
                    TopK = table.Column<int>(type: "int", nullable: true),
                    ContextWindowTokens = table.Column<int>(
                        type: "int",
                        nullable: false,
                        defaultValue: 32768
                    ),
                    RequestTimeoutSeconds = table.Column<int>(
                        type: "int",
                        nullable: false,
                        defaultValue: 600
                    ),
                    RecommendedAsRefineModel = table.Column<bool>(type: "bit", nullable: false),
                    NotesJson = table.Column<string>(
                        type: "nvarchar(4000)",
                        maxLength: 4000,
                        nullable: false
                    ),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsBuiltInDefault = table.Column<bool>(type: "bit", nullable: false),
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
                    table.PrimaryKey("PK_LlmModelProfiles", x => x.Id);
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_LlmModelProfiles_Slug",
                table: "LlmModelProfiles",
                column: "Slug",
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "LlmModelProfiles");

            migrationBuilder.DropColumn(name: "ModelProfilesSeeded", table: "HouseholdLlmSettings");
        }
    }
}
