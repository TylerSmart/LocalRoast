using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddRawTextMetadataAndAutoPublishToIngestionJob : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoPublish",
                table: "IngestionJobs",
                type: "bit",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.AddColumn<string>(
                name: "InitialCollectionIdsJson",
                table: "IngestionJobs",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "SourceAuthor",
                table: "IngestionJobs",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "SourceName",
                table: "IngestionJobs",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "AutoPublish", table: "IngestionJobs");

            migrationBuilder.DropColumn(name: "InitialCollectionIdsJson", table: "IngestionJobs");

            migrationBuilder.DropColumn(name: "SourceAuthor", table: "IngestionJobs");

            migrationBuilder.DropColumn(name: "SourceName", table: "IngestionJobs");
        }
    }
}
