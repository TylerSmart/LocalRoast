using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmConnectionsAndModelConfigurations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RefineModelConfigurationId",
                table: "HouseholdLlmSettings",
                type: "uniqueidentifier",
                nullable: true
            );

            migrationBuilder.AddColumn<Guid>(
                name: "TextModelConfigurationId",
                table: "HouseholdLlmSettings",
                type: "uniqueidentifier",
                nullable: true
            );

            migrationBuilder.AddColumn<Guid>(
                name: "VisionModelConfigurationId",
                table: "HouseholdLlmSettings",
                type: "uniqueidentifier",
                nullable: true
            );

            migrationBuilder.CreateTable(
                name: "LlmConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(
                        type: "nvarchar(100)",
                        maxLength: 100,
                        nullable: false
                    ),
                    Provider = table.Column<string>(
                        type: "nvarchar(32)",
                        maxLength: 32,
                        nullable: false
                    ),
                    BaseUrl = table.Column<string>(
                        type: "nvarchar(500)",
                        maxLength: 500,
                        nullable: false
                    ),
                    ApiKey = table.Column<string>(
                        type: "nvarchar(500)",
                        maxLength: 500,
                        nullable: true
                    ),
                    OllamaKeepAlive = table.Column<string>(
                        type: "nvarchar(32)",
                        maxLength: 32,
                        nullable: false
                    ),
                    OllamaCpuOnly = table.Column<bool>(type: "bit", nullable: false),
                    EnableRateLimitSafeguards = table.Column<bool>(
                        type: "bit",
                        nullable: false,
                        defaultValue: true
                    ),
                    MaxConcurrentRequests = table.Column<int>(
                        type: "int",
                        nullable: false,
                        defaultValue: 1
                    ),
                    RequestsPerMinute = table.Column<int>(type: "int", nullable: true),
                    DailyRequestQuota = table.Column<int>(type: "int", nullable: true),
                    DailyQuotaResetTimezone = table.Column<string>(
                        type: "nvarchar(64)",
                        maxLength: 64,
                        nullable: false,
                        defaultValue: "UTC"
                    ),
                    DailyQuotaResetTime = table.Column<string>(
                        type: "nvarchar(10)",
                        maxLength: 10,
                        nullable: false,
                        defaultValue: "00:00"
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
                    table.PrimaryKey("PK_LlmConnections", x => x.Id);
                }
            );

            migrationBuilder.CreateTable(
                name: "LlmModelConfigurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(
                        type: "nvarchar(150)",
                        maxLength: 150,
                        nullable: false
                    ),
                    ConnectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Model = table.Column<string>(
                        type: "nvarchar(200)",
                        maxLength: 200,
                        nullable: false
                    ),
                    ProfileSlug = table.Column<string>(
                        type: "nvarchar(100)",
                        maxLength: 100,
                        nullable: true
                    ),
                    EnableThinking = table.Column<bool>(type: "bit", nullable: false),
                    Temperature = table.Column<double>(type: "float", nullable: true),
                    TopP = table.Column<double>(type: "float", nullable: true),
                    TopK = table.Column<int>(type: "int", nullable: true),
                    ContextWindowTokens = table.Column<int>(
                        type: "int",
                        nullable: false,
                        defaultValue: 8192
                    ),
                    RequestTimeoutSeconds = table.Column<int>(
                        type: "int",
                        nullable: false,
                        defaultValue: 120
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
                    table.PrimaryKey("PK_LlmModelConfigurations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LlmModelConfigurations_LlmConnections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "LlmConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdLlmSettings_RefineModelConfigurationId",
                table: "HouseholdLlmSettings",
                column: "RefineModelConfigurationId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdLlmSettings_TextModelConfigurationId",
                table: "HouseholdLlmSettings",
                column: "TextModelConfigurationId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdLlmSettings_VisionModelConfigurationId",
                table: "HouseholdLlmSettings",
                column: "VisionModelConfigurationId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_LlmConnections_Name",
                table: "LlmConnections",
                column: "Name",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_LlmModelConfigurations_ConnectionId",
                table: "LlmModelConfigurations",
                column: "ConnectionId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_LlmModelConfigurations_Name",
                table: "LlmModelConfigurations",
                column: "Name",
                unique: true
            );

            migrationBuilder.AddForeignKey(
                name: "FK_HouseholdLlmSettings_LlmModelConfigurations_RefineModelConfigurationId",
                table: "HouseholdLlmSettings",
                column: "RefineModelConfigurationId",
                principalTable: "LlmModelConfigurations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "FK_HouseholdLlmSettings_LlmModelConfigurations_TextModelConfigurationId",
                table: "HouseholdLlmSettings",
                column: "TextModelConfigurationId",
                principalTable: "LlmModelConfigurations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "FK_HouseholdLlmSettings_LlmModelConfigurations_VisionModelConfigurationId",
                table: "HouseholdLlmSettings",
                column: "VisionModelConfigurationId",
                principalTable: "LlmModelConfigurations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict
            );

            // Carry the existing single-host setup forward as a saved connection plus one saved
            // model configuration per assigned workflow so nothing has to be retyped.
            migrationBuilder.Sql(
                """
                DECLARE @SettingsId uniqueidentifier;
                SELECT TOP (1) @SettingsId = Id FROM HouseholdLlmSettings;

                IF @SettingsId IS NOT NULL
                BEGIN
                    DECLARE @ConnectionId uniqueidentifier = NEWID();
                    DECLARE @TextId uniqueidentifier = NULL;
                    DECLARE @VisionId uniqueidentifier = NULL;
                    DECLARE @RefineId uniqueidentifier = NULL;
                    DECLARE @Now datetimeoffset = SYSDATETIMEOFFSET();

                    INSERT INTO LlmConnections (Id, Name, Provider, BaseUrl, ApiKey, OllamaKeepAlive, OllamaCpuOnly,
                        EnableRateLimitSafeguards, MaxConcurrentRequests, RequestsPerMinute, DailyRequestQuota,
                        DailyQuotaResetTimezone, DailyQuotaResetTime, CreatedAt, CreatedBy, ModifiedAt, ModifiedBy)
                    SELECT @ConnectionId, N'Default connection', Provider, BaseUrl, ApiKey, OllamaKeepAlive, OllamaCpuOnly,
                        EnableRateLimitSafeguards, MaxConcurrentRequests, RequestsPerMinute, DailyRequestQuota,
                        ISNULL(DailyQuotaResetTimezone, N'UTC'), ISNULL(DailyQuotaResetTime, N'00:00'),
                        @Now, N'System', @Now, N'System'
                    FROM HouseholdLlmSettings WHERE Id = @SettingsId;

                    IF EXISTS (SELECT 1 FROM HouseholdLlmSettings WHERE Id = @SettingsId AND NULLIF(LTRIM(RTRIM(TextModel)), N'') IS NOT NULL)
                    BEGIN
                        SET @TextId = NEWID();
                        INSERT INTO LlmModelConfigurations (Id, Name, ConnectionId, Model, ProfileSlug, EnableThinking,
                            Temperature, TopP, TopK, ContextWindowTokens, RequestTimeoutSeconds, CreatedAt, CreatedBy, ModifiedAt, ModifiedBy)
                        SELECT @TextId, LEFT(N'Text - ' + LTRIM(RTRIM(TextModel)), 150), @ConnectionId, LEFT(LTRIM(RTRIM(TextModel)), 200), NULL, EnableThinking,
                            Temperature, TopP, TopK, ContextWindowTokens, RequestTimeoutSeconds, @Now, N'System', @Now, N'System'
                        FROM HouseholdLlmSettings WHERE Id = @SettingsId;
                    END

                    IF EXISTS (SELECT 1 FROM HouseholdLlmSettings WHERE Id = @SettingsId AND NULLIF(LTRIM(RTRIM(VisionModel)), N'') IS NOT NULL)
                    BEGIN
                        SET @VisionId = NEWID();
                        INSERT INTO LlmModelConfigurations (Id, Name, ConnectionId, Model, ProfileSlug, EnableThinking,
                            Temperature, TopP, TopK, ContextWindowTokens, RequestTimeoutSeconds, CreatedAt, CreatedBy, ModifiedAt, ModifiedBy)
                        SELECT @VisionId, LEFT(N'Vision - ' + LTRIM(RTRIM(VisionModel)), 150), @ConnectionId, LEFT(LTRIM(RTRIM(VisionModel)), 200), NULL, EnableThinking,
                            Temperature, TopP, TopK, ContextWindowTokens, RequestTimeoutSeconds, @Now, N'System', @Now, N'System'
                        FROM HouseholdLlmSettings WHERE Id = @SettingsId;
                    END

                    IF EXISTS (SELECT 1 FROM HouseholdLlmSettings WHERE Id = @SettingsId AND NULLIF(LTRIM(RTRIM(RefineModel)), N'') IS NOT NULL)
                    BEGIN
                        SET @RefineId = NEWID();
                        INSERT INTO LlmModelConfigurations (Id, Name, ConnectionId, Model, ProfileSlug, EnableThinking,
                            Temperature, TopP, TopK, ContextWindowTokens, RequestTimeoutSeconds, CreatedAt, CreatedBy, ModifiedAt, ModifiedBy)
                        SELECT @RefineId, LEFT(N'Refinement - ' + LTRIM(RTRIM(RefineModel)), 150), @ConnectionId, LEFT(LTRIM(RTRIM(RefineModel)), 200), NULL, RefineEnableThinking,
                            RefineTemperature, RefineTopP, RefineTopK, ContextWindowTokens, RefineRequestTimeoutSeconds, @Now, N'System', @Now, N'System'
                        FROM HouseholdLlmSettings WHERE Id = @SettingsId;
                    END

                    UPDATE HouseholdLlmSettings
                    SET TextModelConfigurationId = @TextId,
                        VisionModelConfigurationId = @VisionId,
                        RefineModelConfigurationId = @RefineId
                    WHERE Id = @SettingsId;
                END
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HouseholdLlmSettings_LlmModelConfigurations_RefineModelConfigurationId",
                table: "HouseholdLlmSettings"
            );

            migrationBuilder.DropForeignKey(
                name: "FK_HouseholdLlmSettings_LlmModelConfigurations_TextModelConfigurationId",
                table: "HouseholdLlmSettings"
            );

            migrationBuilder.DropForeignKey(
                name: "FK_HouseholdLlmSettings_LlmModelConfigurations_VisionModelConfigurationId",
                table: "HouseholdLlmSettings"
            );

            migrationBuilder.DropTable(name: "LlmModelConfigurations");

            migrationBuilder.DropTable(name: "LlmConnections");

            migrationBuilder.DropIndex(
                name: "IX_HouseholdLlmSettings_RefineModelConfigurationId",
                table: "HouseholdLlmSettings"
            );

            migrationBuilder.DropIndex(
                name: "IX_HouseholdLlmSettings_TextModelConfigurationId",
                table: "HouseholdLlmSettings"
            );

            migrationBuilder.DropIndex(
                name: "IX_HouseholdLlmSettings_VisionModelConfigurationId",
                table: "HouseholdLlmSettings"
            );

            migrationBuilder.DropColumn(
                name: "RefineModelConfigurationId",
                table: "HouseholdLlmSettings"
            );

            migrationBuilder.DropColumn(
                name: "TextModelConfigurationId",
                table: "HouseholdLlmSettings"
            );

            migrationBuilder.DropColumn(
                name: "VisionModelConfigurationId",
                table: "HouseholdLlmSettings"
            );
        }
    }
}
