using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCookLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RatingCount",
                table: "Recipes",
                type: "int",
                nullable: false,
                defaultValue: 0
            );

            migrationBuilder.CreateTable(
                name: "CookLogs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CookedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CookedByDisplayName = table.Column<string>(
                        type: "nvarchar(150)",
                        maxLength: 150,
                        nullable: false
                    ),
                    CookedAt = table.Column<DateTimeOffset>(
                        type: "datetimeoffset",
                        nullable: false
                    ),
                    ServingsMade = table.Column<decimal>(
                        type: "decimal(8,2)",
                        precision: 8,
                        scale: 2,
                        nullable: true
                    ),
                    Rating = table.Column<int>(type: "int", nullable: true),
                    Notes = table.Column<string>(
                        type: "nvarchar(2000)",
                        maxLength: 2000,
                        nullable: true
                    ),
                    PhotoImagePath = table.Column<string>(
                        type: "nvarchar(500)",
                        maxLength: 500,
                        nullable: true
                    ),
                    PhotoThumbnailPath = table.Column<string>(
                        type: "nvarchar(500)",
                        maxLength: 500,
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
                    table.PrimaryKey("PK_CookLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CookLogs_Recipes_RecipeId",
                        column: x => x.RecipeId,
                        principalTable: "Recipes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_CookLogs_Users_CookedByUserId",
                        column: x => x.CookedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_TimesCooked_AverageRating_LastCookedAt",
                table: "Recipes",
                columns: new[] { "TimesCooked", "AverageRating", "LastCookedAt" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_CookLogs_CookedByUserId",
                table: "CookLogs",
                column: "CookedByUserId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_CookLogs_RecipeId_CookedAt",
                table: "CookLogs",
                columns: new[] { "RecipeId", "CookedAt" }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "CookLogs");

            migrationBuilder.DropIndex(
                name: "IX_Recipes_TimesCooked_AverageRating_LastCookedAt",
                table: "Recipes"
            );

            migrationBuilder.DropColumn(name: "RatingCount", table: "Recipes");
        }
    }
}
