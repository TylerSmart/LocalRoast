using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace App.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveStoreAisles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FoodItems_StoreAisles_DefaultStoreAisleId",
                table: "FoodItems"
            );

            migrationBuilder.DropTable(name: "StoreAisles");

            migrationBuilder.DropIndex(
                name: "IX_FoodItems_DefaultStoreAisleId",
                table: "FoodItems"
            );

            migrationBuilder.DropColumn(name: "DefaultStoreAisleId", table: "FoodItems");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DefaultStoreAisleId",
                table: "FoodItems",
                type: "uniqueidentifier",
                nullable: true
            );

            migrationBuilder.CreateTable(
                name: "StoreAisles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(
                        type: "datetimeoffset",
                        nullable: false
                    ),
                    CreatedBy = table.Column<string>(
                        type: "nvarchar(256)",
                        maxLength: 256,
                        nullable: false
                    ),
                    IsSystemSeeded = table.Column<bool>(type: "bit", nullable: false),
                    ModifiedAt = table.Column<DateTimeOffset>(
                        type: "datetimeoffset",
                        nullable: false
                    ),
                    ModifiedBy = table.Column<string>(
                        type: "nvarchar(256)",
                        maxLength: 256,
                        nullable: false
                    ),
                    Name = table.Column<string>(
                        type: "nvarchar(100)",
                        maxLength: 100,
                        nullable: false
                    ),
                    NormalizedName = table.Column<string>(
                        type: "nvarchar(100)",
                        maxLength: 100,
                        nullable: false
                    ),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoreAisles", x => x.Id);
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_FoodItems_DefaultStoreAisleId",
                table: "FoodItems",
                column: "DefaultStoreAisleId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_StoreAisles_NormalizedName",
                table: "StoreAisles",
                column: "NormalizedName",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_StoreAisles_SortOrder",
                table: "StoreAisles",
                column: "SortOrder"
            );

            migrationBuilder.AddForeignKey(
                name: "FK_FoodItems_StoreAisles_DefaultStoreAisleId",
                table: "FoodItems",
                column: "DefaultStoreAisleId",
                principalTable: "StoreAisles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict
            );
        }
    }
}
