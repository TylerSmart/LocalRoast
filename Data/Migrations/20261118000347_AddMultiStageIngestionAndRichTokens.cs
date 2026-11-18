using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiStageIngestionAndRichTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EmbeddingModel",
                table: "HouseholdLlmSettings",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true
            );

            migrationBuilder.AddColumn<Guid>(
                name: "EmbeddingModelConfigurationId",
                table: "HouseholdLlmSettings",
                type: "uniqueidentifier",
                nullable: true
            );

            migrationBuilder.CreateTable(
                name: "CatalogEmbeddings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ModelTag = table.Column<string>(
                        type: "nvarchar(200)",
                        maxLength: 200,
                        nullable: false
                    ),
                    NormalizedText = table.Column<string>(
                        type: "nvarchar(400)",
                        maxLength: 400,
                        nullable: false
                    ),
                    Dimensions = table.Column<int>(type: "int", nullable: false),
                    VectorBytes = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
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
                    table.PrimaryKey("PK_CatalogEmbeddings", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "RecipeIngredientReplacements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeIngredientId = table.Column<Guid>(
                        type: "uniqueidentifier",
                        nullable: false
                    ),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    DisplayName = table.Column<string>(
                        type: "nvarchar(200)",
                        maxLength: 200,
                        nullable: false
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
                        type: "nvarchar(64)",
                        maxLength: 64,
                        nullable: true
                    ),
                    FoodItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PreparationNote = table.Column<string>(
                        type: "nvarchar(300)",
                        maxLength: 300,
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
                    table.PrimaryKey("PK_RecipeIngredientReplacements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecipeIngredientReplacements_FoodItems_FoodItemId",
                        column: x => x.FoodItemId,
                        principalTable: "FoodItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                    table.ForeignKey(
                        name: "FK_RecipeIngredientReplacements_RecipeIngredients_RecipeIngredientId",
                        column: x => x.RecipeIngredientId,
                        principalTable: "RecipeIngredients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdLlmSettings_EmbeddingModelConfigurationId",
                table: "HouseholdLlmSettings",
                column: "EmbeddingModelConfigurationId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_CatalogEmbeddings_ModelTag_NormalizedText",
                table: "CatalogEmbeddings",
                columns: new[] { "ModelTag", "NormalizedText" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_RecipeIngredientReplacements_FoodItemId",
                table: "RecipeIngredientReplacements",
                column: "FoodItemId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_RecipeIngredientReplacements_RecipeIngredientId_SortOrder",
                table: "RecipeIngredientReplacements",
                columns: new[] { "RecipeIngredientId", "SortOrder" }
            );

            migrationBuilder.AddForeignKey(
                name: "FK_HouseholdLlmSettings_LlmModelConfigurations_EmbeddingModelConfigurationId",
                table: "HouseholdLlmSettings",
                column: "EmbeddingModelConfigurationId",
                principalTable: "LlmModelConfigurations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HouseholdLlmSettings_LlmModelConfigurations_EmbeddingModelConfigurationId",
                table: "HouseholdLlmSettings"
            );

            migrationBuilder.DropTable(name: "CatalogEmbeddings");

            migrationBuilder.DropTable(name: "RecipeIngredientReplacements");

            migrationBuilder.DropIndex(
                name: "IX_HouseholdLlmSettings_EmbeddingModelConfigurationId",
                table: "HouseholdLlmSettings"
            );

            migrationBuilder.DropColumn(name: "EmbeddingModel", table: "HouseholdLlmSettings");

            migrationBuilder.DropColumn(
                name: "EmbeddingModelConfigurationId",
                table: "HouseholdLlmSettings"
            );
        }
    }
}
