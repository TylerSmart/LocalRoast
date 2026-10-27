using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVisionPdfAndRecipeAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PdfInspectionJson",
                table: "IngestionJobs",
                type: "nvarchar(max)",
                maxLength: 200000,
                nullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "SourceBookPage",
                table: "IngestionJobs",
                type: "int",
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "SourceBookTitle",
                table: "IngestionJobs",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "SourceFilesJson",
                table: "IngestionJobs",
                type: "nvarchar(max)",
                maxLength: 200000,
                nullable: true
            );

            migrationBuilder.CreateTable(
                name: "RecipeAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    FileName = table.Column<string>(
                        type: "nvarchar(255)",
                        maxLength: 255,
                        nullable: false
                    ),
                    MediaUrlPath = table.Column<string>(
                        type: "nvarchar(500)",
                        maxLength: 500,
                        nullable: false
                    ),
                    ContentType = table.Column<string>(
                        type: "nvarchar(100)",
                        maxLength: 100,
                        nullable: false
                    ),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Label = table.Column<string>(
                        type: "nvarchar(150)",
                        maxLength: 150,
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
                    table.PrimaryKey("PK_RecipeAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecipeAttachments_Recipes_RecipeId",
                        column: x => x.RecipeId,
                        principalTable: "Recipes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_RecipeAttachments_RecipeId_SortOrder",
                table: "RecipeAttachments",
                columns: new[] { "RecipeId", "SortOrder" }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "RecipeAttachments");

            migrationBuilder.DropColumn(name: "PdfInspectionJson", table: "IngestionJobs");

            migrationBuilder.DropColumn(name: "SourceBookPage", table: "IngestionJobs");

            migrationBuilder.DropColumn(name: "SourceBookTitle", table: "IngestionJobs");

            migrationBuilder.DropColumn(name: "SourceFilesJson", table: "IngestionJobs");
        }
    }
}
