using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace App.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLegacyIngestionRefinement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "IngestionDraftRefinements");

            migrationBuilder.DropColumn(name: "StructuredSourceText", table: "IngestionJobs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StructuredSourceText",
                table: "IngestionJobs",
                type: "nvarchar(max)",
                maxLength: 100000,
                nullable: true
            );

            migrationBuilder.CreateTable(
                name: "IngestionDraftRefinements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IngestionJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BaseDraftJson = table.Column<string>(
                        type: "nvarchar(max)",
                        maxLength: 200000,
                        nullable: true
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
                    Detail = table.Column<string>(
                        type: "nvarchar(2000)",
                        maxLength: 2000,
                        nullable: true
                    ),
                    IssuesAfter = table.Column<int>(type: "int", nullable: true),
                    IssuesBefore = table.Column<int>(type: "int", nullable: true),
                    Model = table.Column<string>(
                        type: "nvarchar(200)",
                        maxLength: 200,
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
                    ProposalJson = table.Column<string>(
                        type: "nvarchar(max)",
                        maxLength: 200000,
                        nullable: true
                    ),
                    RequestedAt = table.Column<DateTimeOffset>(
                        type: "datetimeoffset",
                        nullable: false
                    ),
                    Status = table.Column<string>(
                        type: "nvarchar(32)",
                        maxLength: 32,
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
    }
}
