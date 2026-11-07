using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace App.Data.Migrations
{
    /// <inheritdoc />
    public partial class TaggedInstructionAmounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExtractionRepairRounds",
                table: "HouseholdLlmSettings"
            );

            migrationBuilder.DropColumn(name: "ExtractionStrategy", table: "HouseholdLlmSettings");

            migrationBuilder.CreateTable(
                name: "RecipeStepInlineAmounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeStepId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    Start = table.Column<int>(type: "int", nullable: false),
                    Length = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(
                        type: "nvarchar(16)",
                        maxLength: 16,
                        nullable: false
                    ),
                    RecipeIngredientId = table.Column<Guid>(
                        type: "uniqueidentifier",
                        nullable: true
                    ),
                    Quantity = table.Column<decimal>(
                        type: "decimal(10,4)",
                        precision: 10,
                        scale: 4,
                        nullable: true
                    ),
                    MaxQuantity = table.Column<decimal>(
                        type: "decimal(10,4)",
                        precision: 10,
                        scale: 4,
                        nullable: true
                    ),
                    Unit = table.Column<string>(
                        type: "nvarchar(32)",
                        maxLength: 32,
                        nullable: true
                    ),
                    CustomUnitName = table.Column<string>(
                        type: "nvarchar(50)",
                        maxLength: 50,
                        nullable: true
                    ),
                    AlternativeQuantity = table.Column<decimal>(
                        type: "decimal(10,4)",
                        precision: 10,
                        scale: 4,
                        nullable: true
                    ),
                    AlternativeMaxQuantity = table.Column<decimal>(
                        type: "decimal(10,4)",
                        precision: 10,
                        scale: 4,
                        nullable: true
                    ),
                    AlternativeUnit = table.Column<string>(
                        type: "nvarchar(32)",
                        maxLength: 32,
                        nullable: true
                    ),
                    AlternativeCustomUnitName = table.Column<string>(
                        type: "nvarchar(50)",
                        maxLength: 50,
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
                    table.PrimaryKey("PK_RecipeStepInlineAmounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecipeStepInlineAmounts_RecipeIngredients_RecipeIngredientId",
                        column: x => x.RecipeIngredientId,
                        principalTable: "RecipeIngredients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_RecipeStepInlineAmounts_RecipeSteps_RecipeStepId",
                        column: x => x.RecipeStepId,
                        principalTable: "RecipeSteps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_RecipeStepInlineAmounts_RecipeIngredientId",
                table: "RecipeStepInlineAmounts",
                column: "RecipeIngredientId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_RecipeStepInlineAmounts_RecipeStepId_SortOrder",
                table: "RecipeStepInlineAmounts",
                columns: new[] { "RecipeStepId", "SortOrder" },
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "RecipeStepInlineAmounts");

            migrationBuilder.AddColumn<int>(
                name: "ExtractionRepairRounds",
                table: "HouseholdLlmSettings",
                type: "int",
                nullable: false,
                defaultValue: 1
            );

            migrationBuilder.AddColumn<string>(
                name: "ExtractionStrategy",
                table: "HouseholdLlmSettings",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "Auto"
            );
        }
    }
}
