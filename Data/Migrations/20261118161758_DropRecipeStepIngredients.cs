using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace App.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropRecipeStepIngredients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "RecipeStepIngredients");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RecipeStepIngredients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeIngredientId = table.Column<Guid>(
                        type: "uniqueidentifier",
                        nullable: false
                    ),
                    RecipeStepId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(
                        type: "datetimeoffset",
                        nullable: false
                    ),
                    CreatedBy = table.Column<string>(
                        type: "nvarchar(256)",
                        maxLength: 256,
                        nullable: false
                    ),
                    CustomUnitName = table.Column<string>(
                        type: "nvarchar(50)",
                        maxLength: 50,
                        nullable: true
                    ),
                    MaxQuantity = table.Column<decimal>(
                        type: "decimal(10,4)",
                        precision: 10,
                        scale: 4,
                        nullable: true
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
                    Quantity = table.Column<decimal>(
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
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeStepIngredients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecipeStepIngredients_RecipeIngredients_RecipeIngredientId",
                        column: x => x.RecipeIngredientId,
                        principalTable: "RecipeIngredients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_RecipeStepIngredients_RecipeSteps_RecipeStepId",
                        column: x => x.RecipeStepId,
                        principalTable: "RecipeSteps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_RecipeStepIngredients_RecipeIngredientId",
                table: "RecipeStepIngredients",
                column: "RecipeIngredientId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_RecipeStepIngredients_RecipeStepId_RecipeIngredientId",
                table: "RecipeStepIngredients",
                columns: new[] { "RecipeStepId", "RecipeIngredientId" },
                unique: true
            );
        }
    }
}
