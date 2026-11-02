using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMealPlanCalendarAndTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MealPlanEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WeekStartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    IsOnDeck = table.Column<bool>(type: "bit", nullable: false),
                    ScheduledDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Slot = table.Column<string>(
                        type: "nvarchar(32)",
                        maxLength: 32,
                        nullable: true
                    ),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    EntryType = table.Column<string>(
                        type: "nvarchar(32)",
                        maxLength: 32,
                        nullable: false
                    ),
                    RecipeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TargetServings = table.Column<decimal>(
                        type: "decimal(8,2)",
                        precision: 8,
                        scale: 2,
                        nullable: true
                    ),
                    IsLeftovers = table.Column<bool>(type: "bit", nullable: false),
                    SourceMealPlanEntryId = table.Column<Guid>(
                        type: "uniqueidentifier",
                        nullable: true
                    ),
                    CustomTitle = table.Column<string>(
                        type: "nvarchar(200)",
                        maxLength: 200,
                        nullable: true
                    ),
                    Notes = table.Column<string>(
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
                    table.PrimaryKey("PK_MealPlanEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MealPlanEntries_MealPlanEntries_SourceMealPlanEntryId",
                        column: x => x.SourceMealPlanEntryId,
                        principalTable: "MealPlanEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_MealPlanEntries_Recipes_RecipeId",
                        column: x => x.RecipeId,
                        principalTable: "Recipes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateTable(
                name: "MealPlanTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(
                        type: "nvarchar(120)",
                        maxLength: 120,
                        nullable: false
                    ),
                    NormalizedName = table.Column<string>(
                        type: "nvarchar(120)",
                        maxLength: 120,
                        nullable: false
                    ),
                    Description = table.Column<string>(
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
                    table.PrimaryKey("PK_MealPlanTemplates", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "MealPlanTemplateItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TemplateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsOnDeck = table.Column<bool>(type: "bit", nullable: false),
                    DayOffset = table.Column<int>(type: "int", nullable: true),
                    Slot = table.Column<string>(
                        type: "nvarchar(32)",
                        maxLength: 32,
                        nullable: true
                    ),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    EntryType = table.Column<string>(
                        type: "nvarchar(32)",
                        maxLength: 32,
                        nullable: false
                    ),
                    RecipeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TargetServings = table.Column<decimal>(
                        type: "decimal(8,2)",
                        precision: 8,
                        scale: 2,
                        nullable: true
                    ),
                    IsLeftovers = table.Column<bool>(type: "bit", nullable: false),
                    CustomTitle = table.Column<string>(
                        type: "nvarchar(200)",
                        maxLength: 200,
                        nullable: true
                    ),
                    Notes = table.Column<string>(
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
                    table.PrimaryKey("PK_MealPlanTemplateItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MealPlanTemplateItems_MealPlanTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "MealPlanTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_MealPlanTemplateItems_Recipes_RecipeId",
                        column: x => x.RecipeId,
                        principalTable: "Recipes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_MealPlanEntries_RecipeId",
                table: "MealPlanEntries",
                column: "RecipeId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_MealPlanEntries_ScheduledDate_Slot_SortOrder",
                table: "MealPlanEntries",
                columns: new[] { "ScheduledDate", "Slot", "SortOrder" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_MealPlanEntries_SourceMealPlanEntryId",
                table: "MealPlanEntries",
                column: "SourceMealPlanEntryId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_MealPlanEntries_WeekStartDate_IsOnDeck_SortOrder",
                table: "MealPlanEntries",
                columns: new[] { "WeekStartDate", "IsOnDeck", "SortOrder" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_MealPlanTemplateItems_RecipeId",
                table: "MealPlanTemplateItems",
                column: "RecipeId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_MealPlanTemplateItems_TemplateId_IsOnDeck_DayOffset_Slot_SortOrder",
                table: "MealPlanTemplateItems",
                columns: new[] { "TemplateId", "IsOnDeck", "DayOffset", "Slot", "SortOrder" }
            );

            migrationBuilder.CreateIndex(
                name: "IX_MealPlanTemplates_NormalizedName",
                table: "MealPlanTemplates",
                column: "NormalizedName",
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "MealPlanEntries");

            migrationBuilder.DropTable(name: "MealPlanTemplateItems");

            migrationBuilder.DropTable(name: "MealPlanTemplates");
        }
    }
}
