using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace App.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddModelRateLimitsAndScopedProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HouseholdLlmSettings_LlmModelConfigurations_EmbeddingModelConfigurationId",
                table: "HouseholdLlmSettings"
            );

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

            migrationBuilder.DropIndex(
                name: "IX_HouseholdLlmSettings_EmbeddingModelConfigurationId",
                table: "HouseholdLlmSettings"
            );

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

            migrationBuilder.DropIndex(name: "IX_LlmModelProfiles_Slug", table: "LlmModelProfiles");

            migrationBuilder.DropIndex(
                name: "IX_LlmModelConfigurations_ConnectionId",
                table: "LlmModelConfigurations"
            );

            // Clear legacy global profiles before restructuring LlmModelProfiles to be strictly model-scoped.
            // Existing per-model tunings are preserved from LlmModelConfigurations into each model's Defaults profile below.
            migrationBuilder.Sql("DELETE FROM LlmModelProfiles;");

            migrationBuilder.DropColumn(name: "IsBuiltInDefault", table: "LlmModelProfiles");

            migrationBuilder.DropColumn(name: "MatchPrefixesJson", table: "LlmModelProfiles");

            migrationBuilder.DropColumn(name: "ModelTag", table: "LlmModelProfiles");

            migrationBuilder.DropColumn(
                name: "RecommendedAsRefineModel",
                table: "LlmModelProfiles"
            );

            migrationBuilder.DropColumn(name: "Slug", table: "LlmModelProfiles");

            migrationBuilder.DropColumn(name: "SortOrder", table: "LlmModelProfiles");

            migrationBuilder.AlterColumn<int>(
                name: "RequestTimeoutSeconds",
                table: "LlmModelProfiles",
                type: "int",
                nullable: false,
                defaultValue: 120,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 600
            );

            migrationBuilder.AlterColumn<int>(
                name: "ContextWindowTokens",
                table: "LlmModelProfiles",
                type: "int",
                nullable: false,
                defaultValue: 8192,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 32768
            );

            migrationBuilder.AddColumn<bool>(
                name: "IsDefault",
                table: "LlmModelProfiles",
                type: "bit",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.AddColumn<Guid>(
                name: "ModelConfigurationId",
                table: "LlmModelProfiles",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000")
            );

            // Add per-model rate-limit columns on LlmModelConfigurations (do NOT rename TopK).
            migrationBuilder.AddColumn<int>(
                name: "MaxConcurrentRequests",
                table: "LlmModelConfigurations",
                type: "int",
                nullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "RequestsPerMinute",
                table: "LlmModelConfigurations",
                type: "int",
                nullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "DailyRequestQuota",
                table: "LlmModelConfigurations",
                type: "int",
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "DailyQuotaResetTimezone",
                table: "LlmModelConfigurations",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true
            );

            migrationBuilder.AddColumn<string>(
                name: "DailyQuotaResetTime",
                table: "LlmModelConfigurations",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true
            );

            // Add workflow profile FKs on HouseholdLlmSettings while legacy *ModelConfigurationId columns are still readable.
            migrationBuilder.AddColumn<Guid>(
                name: "TextModelProfileId",
                table: "HouseholdLlmSettings",
                type: "uniqueidentifier",
                nullable: true
            );

            migrationBuilder.AddColumn<Guid>(
                name: "VisionModelProfileId",
                table: "HouseholdLlmSettings",
                type: "uniqueidentifier",
                nullable: true
            );

            migrationBuilder.AddColumn<Guid>(
                name: "RefineModelProfileId",
                table: "HouseholdLlmSettings",
                type: "uniqueidentifier",
                nullable: true
            );

            migrationBuilder.AddColumn<Guid>(
                name: "EmbeddingModelProfileId",
                table: "HouseholdLlmSettings",
                type: "uniqueidentifier",
                nullable: true
            );

            // Lossless data migration:
            // 1. Trim LlmModelConfigurations.Model.
            // 2. Deduplicate (ConnectionId, LOWER(Model)) into a single surviving LlmModelConfiguration per pair.
            // 3. Create each surviving model's undeletable "Defaults" profile copying its existing tuning.
            // 4. For any merged duplicate with distinct tuning, create a custom child profile under the survivor.
            // 5. Point HouseholdLlmSettings.*ModelProfileId to the matching migrated profile.
            // 6. Delete merged duplicate LlmModelConfigurations rows.
            migrationBuilder.Sql(
                """
                UPDATE LlmModelConfigurations
                SET Model = LTRIM(RTRIM(Model));

                DECLARE @Ranked TABLE (
                    OrigConfigId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                    ConnectionId UNIQUEIDENTIFIER NOT NULL,
                    NormalizedModel NVARCHAR(200) NOT NULL,
                    SurvivorConfigId UNIQUEIDENTIFIER NULL,
                    IsSurvivor BIT NOT NULL,
                    RowNum INT NOT NULL
                );

                WITH RankedModels AS (
                    SELECT
                        c.Id AS OrigConfigId,
                        c.ConnectionId,
                        LOWER(c.Model) AS NormalizedModel,
                        ROW_NUMBER() OVER (
                            PARTITION BY c.ConnectionId, LOWER(c.Model)
                            ORDER BY
                                CASE WHEN EXISTS (SELECT 1 FROM HouseholdLlmSettings s WHERE s.TextModelConfigurationId = c.Id) THEN 0 ELSE 1 END,
                                CASE WHEN EXISTS (SELECT 1 FROM HouseholdLlmSettings s WHERE s.VisionModelConfigurationId = c.Id) THEN 0 ELSE 1 END,
                                CASE WHEN EXISTS (SELECT 1 FROM HouseholdLlmSettings s WHERE s.RefineModelConfigurationId = c.Id) THEN 0 ELSE 1 END,
                                CASE WHEN EXISTS (SELECT 1 FROM HouseholdLlmSettings s WHERE s.EmbeddingModelConfigurationId = c.Id) THEN 0 ELSE 1 END,
                                c.CreatedAt ASC,
                                c.Id ASC
                        ) AS RowNum
                    FROM LlmModelConfigurations c
                )
                INSERT INTO @Ranked (OrigConfigId, ConnectionId, NormalizedModel, IsSurvivor, RowNum)
                SELECT
                    OrigConfigId,
                    ConnectionId,
                    NormalizedModel,
                    CASE WHEN RowNum = 1 THEN 1 ELSE 0 END,
                    RowNum
                FROM RankedModels;

                UPDATE r
                SET SurvivorConfigId = s.OrigConfigId
                FROM @Ranked r
                INNER JOIN @Ranked s
                    ON s.ConnectionId = r.ConnectionId
                    AND s.NormalizedModel = r.NormalizedModel
                    AND s.IsSurvivor = 1;

                -- Preserve OllamaGpuLayers from any duplicate onto the survivor if the survivor did not specify one.
                UPDATE surv
                SET OllamaGpuLayers = COALESCE(surv.OllamaGpuLayers, agg.MaxGpuLayers)
                FROM LlmModelConfigurations surv
                INNER JOIN (
                    SELECT r.SurvivorConfigId, MAX(c.OllamaGpuLayers) AS MaxGpuLayers
                    FROM @Ranked r
                    INNER JOIN LlmModelConfigurations c ON c.Id = r.OrigConfigId
                    GROUP BY r.SurvivorConfigId
                ) agg ON agg.SurvivorConfigId = surv.Id
                WHERE surv.OllamaGpuLayers IS NULL AND agg.MaxGpuLayers IS NOT NULL;

                DECLARE @ConfigToProfile TABLE (
                    OrigConfigId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                    SurvivorConfigId UNIQUEIDENTIFIER NOT NULL,
                    ProfileId UNIQUEIDENTIFIER NOT NULL,
                    IsSurvivor BIT NOT NULL
                );

                -- Create the undeletable Defaults profile for every surviving model configuration.
                INSERT INTO @ConfigToProfile (OrigConfigId, SurvivorConfigId, ProfileId, IsSurvivor)
                SELECT r.OrigConfigId, r.SurvivorConfigId, NEWID(), 1
                FROM @Ranked r
                WHERE r.IsSurvivor = 1;

                INSERT INTO LlmModelProfiles (
                    Id,
                    ModelConfigurationId,
                    DisplayName,
                    Summary,
                    EnableThinking,
                    Temperature,
                    TopP,
                    TopK,
                    ContextWindowTokens,
                    RequestTimeoutSeconds,
                    NotesJson,
                    IsDefault,
                    CreatedAt,
                    CreatedBy,
                    ModifiedAt,
                    ModifiedBy
                )
                SELECT
                    m.ProfileId,
                    c.Id,
                    N'Defaults',
                    N'Default tuning for this model.',
                    c.EnableThinking,
                    c.Temperature,
                    c.TopP,
                    c.TopK,
                    c.ContextWindowTokens,
                    c.RequestTimeoutSeconds,
                    N'[]',
                    1,
                    c.CreatedAt,
                    c.CreatedBy,
                    c.ModifiedAt,
                    c.ModifiedBy
                FROM @ConfigToProfile m
                INNER JOIN LlmModelConfigurations c ON c.Id = m.OrigConfigId
                WHERE m.IsSurvivor = 1;

                -- Map duplicates with identical tuning directly to the survivor's Defaults profile.
                INSERT INTO @ConfigToProfile (OrigConfigId, SurvivorConfigId, ProfileId, IsSurvivor)
                SELECT
                    r.OrigConfigId,
                    r.SurvivorConfigId,
                    survMap.ProfileId,
                    0
                FROM @Ranked r
                INNER JOIN @ConfigToProfile survMap ON survMap.OrigConfigId = r.SurvivorConfigId
                INNER JOIN LlmModelConfigurations dup ON dup.Id = r.OrigConfigId
                INNER JOIN LlmModelConfigurations surv ON surv.Id = r.SurvivorConfigId
                WHERE r.IsSurvivor = 0
                  AND dup.EnableThinking = surv.EnableThinking
                  AND ISNULL(dup.Temperature, -999999.0) = ISNULL(surv.Temperature, -999999.0)
                  AND ISNULL(dup.TopP, -999999.0) = ISNULL(surv.TopP, -999999.0)
                  AND ISNULL(dup.TopK, -999999) = ISNULL(surv.TopK, -999999)
                  AND dup.ContextWindowTokens = surv.ContextWindowTokens
                  AND dup.RequestTimeoutSeconds = surv.RequestTimeoutSeconds;

                -- Preserve any remaining duplicate with distinct tuning as a custom profile under the survivor.
                DECLARE @CustomDuplicates TABLE (
                    OrigConfigId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                    SurvivorConfigId UNIQUEIDENTIFIER NOT NULL,
                    ProfileId UNIQUEIDENTIFIER NOT NULL,
                    DisplayName NVARCHAR(150) NOT NULL
                );

                WITH DistinctDuplicates AS (
                    SELECT
                        r.OrigConfigId,
                        r.SurvivorConfigId,
                        CASE
                            WHEN LOWER(LTRIM(RTRIM(dup.Name))) = N'defaults' OR LTRIM(RTRIM(dup.Name)) = N''
                                THEN N'Imported Tuning'
                            ELSE LEFT(LTRIM(RTRIM(dup.Name)), 140)
                        END AS CandidateName,
                        dup.CreatedAt
                    FROM @Ranked r
                    INNER JOIN LlmModelConfigurations dup ON dup.Id = r.OrigConfigId
                    WHERE r.IsSurvivor = 0
                      AND NOT EXISTS (SELECT 1 FROM @ConfigToProfile m WHERE m.OrigConfigId = r.OrigConfigId)
                ),
                NamedDuplicates AS (
                    SELECT
                        OrigConfigId,
                        SurvivorConfigId,
                        CandidateName,
                        ROW_NUMBER() OVER (
                            PARTITION BY SurvivorConfigId, LOWER(CandidateName)
                            ORDER BY CreatedAt ASC, OrigConfigId ASC
                        ) AS NameRank
                    FROM DistinctDuplicates
                )
                INSERT INTO @CustomDuplicates (OrigConfigId, SurvivorConfigId, ProfileId, DisplayName)
                SELECT
                    OrigConfigId,
                    SurvivorConfigId,
                    NEWID(),
                    CASE
                        WHEN NameRank = 1 THEN CandidateName
                        ELSE LEFT(CandidateName, 135) + N' (' + CAST(NameRank AS NVARCHAR(10)) + N')'
                    END
                FROM NamedDuplicates;

                INSERT INTO LlmModelProfiles (
                    Id,
                    ModelConfigurationId,
                    DisplayName,
                    Summary,
                    EnableThinking,
                    Temperature,
                    TopP,
                    TopK,
                    ContextWindowTokens,
                    RequestTimeoutSeconds,
                    NotesJson,
                    IsDefault,
                    CreatedAt,
                    CreatedBy,
                    ModifiedAt,
                    ModifiedBy
                )
                SELECT
                    cd.ProfileId,
                    cd.SurvivorConfigId,
                    cd.DisplayName,
                    N'Migrated from duplicate model configuration.',
                    dup.EnableThinking,
                    dup.Temperature,
                    dup.TopP,
                    dup.TopK,
                    dup.ContextWindowTokens,
                    dup.RequestTimeoutSeconds,
                    N'[]',
                    0,
                    dup.CreatedAt,
                    dup.CreatedBy,
                    dup.ModifiedAt,
                    dup.ModifiedBy
                FROM @CustomDuplicates cd
                INNER JOIN LlmModelConfigurations dup ON dup.Id = cd.OrigConfigId;

                INSERT INTO @ConfigToProfile (OrigConfigId, SurvivorConfigId, ProfileId, IsSurvivor)
                SELECT OrigConfigId, SurvivorConfigId, ProfileId, 0
                FROM @CustomDuplicates;

                -- Remap HouseholdLlmSettings workflow selections to the corresponding profile IDs.
                UPDATE s
                SET
                    TextModelProfileId = (SELECT ProfileId FROM @ConfigToProfile WHERE OrigConfigId = s.TextModelConfigurationId),
                    VisionModelProfileId = (SELECT ProfileId FROM @ConfigToProfile WHERE OrigConfigId = s.VisionModelConfigurationId),
                    RefineModelProfileId = (SELECT ProfileId FROM @ConfigToProfile WHERE OrigConfigId = s.RefineModelConfigurationId),
                    EmbeddingModelProfileId = (SELECT ProfileId FROM @ConfigToProfile WHERE OrigConfigId = s.EmbeddingModelConfigurationId)
                FROM HouseholdLlmSettings s;

                -- Remove merged duplicate LlmModelConfigurations before creating the unique index.
                DELETE FROM LlmModelConfigurations
                WHERE Id IN (SELECT OrigConfigId FROM @Ranked WHERE IsSurvivor = 0);
                """
            );

            // Drop legacy tuning columns from LlmModelConfigurations.
            migrationBuilder.DropColumn(
                name: "ContextWindowTokens",
                table: "LlmModelConfigurations"
            );

            migrationBuilder.DropColumn(name: "EnableThinking", table: "LlmModelConfigurations");

            migrationBuilder.DropColumn(name: "ProfileSlug", table: "LlmModelConfigurations");

            migrationBuilder.DropColumn(
                name: "RequestTimeoutSeconds",
                table: "LlmModelConfigurations"
            );

            migrationBuilder.DropColumn(name: "Temperature", table: "LlmModelConfigurations");

            migrationBuilder.DropColumn(name: "TopP", table: "LlmModelConfigurations");

            migrationBuilder.DropColumn(name: "TopK", table: "LlmModelConfigurations");

            // Drop legacy workflow model-config FK columns & seeding flag from HouseholdLlmSettings.
            migrationBuilder.DropColumn(
                name: "TextModelConfigurationId",
                table: "HouseholdLlmSettings"
            );

            migrationBuilder.DropColumn(
                name: "VisionModelConfigurationId",
                table: "HouseholdLlmSettings"
            );

            migrationBuilder.DropColumn(
                name: "RefineModelConfigurationId",
                table: "HouseholdLlmSettings"
            );

            migrationBuilder.DropColumn(
                name: "EmbeddingModelConfigurationId",
                table: "HouseholdLlmSettings"
            );

            migrationBuilder.DropColumn(name: "ModelProfilesSeeded", table: "HouseholdLlmSettings");

            migrationBuilder.CreateIndex(
                name: "IX_LlmModelProfiles_ModelConfigurationId_DisplayName",
                table: "LlmModelProfiles",
                columns: new[] { "ModelConfigurationId", "DisplayName" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_LlmModelConfigurations_ConnectionId_Model",
                table: "LlmModelConfigurations",
                columns: new[] { "ConnectionId", "Model" },
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdLlmSettings_EmbeddingModelProfileId",
                table: "HouseholdLlmSettings",
                column: "EmbeddingModelProfileId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdLlmSettings_RefineModelProfileId",
                table: "HouseholdLlmSettings",
                column: "RefineModelProfileId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdLlmSettings_TextModelProfileId",
                table: "HouseholdLlmSettings",
                column: "TextModelProfileId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdLlmSettings_VisionModelProfileId",
                table: "HouseholdLlmSettings",
                column: "VisionModelProfileId"
            );

            migrationBuilder.AddForeignKey(
                name: "FK_HouseholdLlmSettings_LlmModelProfiles_EmbeddingModelProfileId",
                table: "HouseholdLlmSettings",
                column: "EmbeddingModelProfileId",
                principalTable: "LlmModelProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "FK_HouseholdLlmSettings_LlmModelProfiles_RefineModelProfileId",
                table: "HouseholdLlmSettings",
                column: "RefineModelProfileId",
                principalTable: "LlmModelProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "FK_HouseholdLlmSettings_LlmModelProfiles_TextModelProfileId",
                table: "HouseholdLlmSettings",
                column: "TextModelProfileId",
                principalTable: "LlmModelProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "FK_HouseholdLlmSettings_LlmModelProfiles_VisionModelProfileId",
                table: "HouseholdLlmSettings",
                column: "VisionModelProfileId",
                principalTable: "LlmModelProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict
            );

            migrationBuilder.AddForeignKey(
                name: "FK_LlmModelProfiles_LlmModelConfigurations_ModelConfigurationId",
                table: "LlmModelProfiles",
                column: "ModelConfigurationId",
                principalTable: "LlmModelConfigurations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_HouseholdLlmSettings_LlmModelProfiles_EmbeddingModelProfileId",
                table: "HouseholdLlmSettings"
            );

            migrationBuilder.DropForeignKey(
                name: "FK_HouseholdLlmSettings_LlmModelProfiles_RefineModelProfileId",
                table: "HouseholdLlmSettings"
            );

            migrationBuilder.DropForeignKey(
                name: "FK_HouseholdLlmSettings_LlmModelProfiles_TextModelProfileId",
                table: "HouseholdLlmSettings"
            );

            migrationBuilder.DropForeignKey(
                name: "FK_HouseholdLlmSettings_LlmModelProfiles_VisionModelProfileId",
                table: "HouseholdLlmSettings"
            );

            migrationBuilder.DropForeignKey(
                name: "FK_LlmModelProfiles_LlmModelConfigurations_ModelConfigurationId",
                table: "LlmModelProfiles"
            );

            migrationBuilder.DropIndex(
                name: "IX_HouseholdLlmSettings_EmbeddingModelProfileId",
                table: "HouseholdLlmSettings"
            );

            migrationBuilder.DropIndex(
                name: "IX_HouseholdLlmSettings_RefineModelProfileId",
                table: "HouseholdLlmSettings"
            );

            migrationBuilder.DropIndex(
                name: "IX_HouseholdLlmSettings_TextModelProfileId",
                table: "HouseholdLlmSettings"
            );

            migrationBuilder.DropIndex(
                name: "IX_HouseholdLlmSettings_VisionModelProfileId",
                table: "HouseholdLlmSettings"
            );

            migrationBuilder.DropIndex(
                name: "IX_LlmModelProfiles_ModelConfigurationId_DisplayName",
                table: "LlmModelProfiles"
            );

            migrationBuilder.DropIndex(
                name: "IX_LlmModelConfigurations_ConnectionId_Model",
                table: "LlmModelConfigurations"
            );

            // Restore legacy tuning columns on LlmModelConfigurations and workflow model-config columns on HouseholdLlmSettings.
            migrationBuilder.AddColumn<int>(
                name: "ContextWindowTokens",
                table: "LlmModelConfigurations",
                type: "int",
                nullable: false,
                defaultValue: 8192
            );

            migrationBuilder.AddColumn<bool>(
                name: "EnableThinking",
                table: "LlmModelConfigurations",
                type: "bit",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.AddColumn<string>(
                name: "ProfileSlug",
                table: "LlmModelConfigurations",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "RequestTimeoutSeconds",
                table: "LlmModelConfigurations",
                type: "int",
                nullable: false,
                defaultValue: 120
            );

            migrationBuilder.AddColumn<double>(
                name: "Temperature",
                table: "LlmModelConfigurations",
                type: "float",
                nullable: true
            );

            migrationBuilder.AddColumn<double>(
                name: "TopP",
                table: "LlmModelConfigurations",
                type: "float",
                nullable: true
            );

            migrationBuilder.AddColumn<int>(
                name: "TopK",
                table: "LlmModelConfigurations",
                type: "int",
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

            migrationBuilder.AddColumn<Guid>(
                name: "RefineModelConfigurationId",
                table: "HouseholdLlmSettings",
                type: "uniqueidentifier",
                nullable: true
            );

            migrationBuilder.AddColumn<Guid>(
                name: "EmbeddingModelConfigurationId",
                table: "HouseholdLlmSettings",
                type: "uniqueidentifier",
                nullable: true
            );

            migrationBuilder.AddColumn<bool>(
                name: "ModelProfilesSeeded",
                table: "HouseholdLlmSettings",
                type: "bit",
                nullable: false,
                defaultValue: false
            );

            // Restore per-model tunings from each model's Defaults profile and remap workflow FKs back to models.
            migrationBuilder.Sql(
                """
                UPDATE c
                SET
                    EnableThinking = p.EnableThinking,
                    Temperature = p.Temperature,
                    TopP = p.TopP,
                    TopK = p.TopK,
                    ContextWindowTokens = p.ContextWindowTokens,
                    RequestTimeoutSeconds = p.RequestTimeoutSeconds
                FROM LlmModelConfigurations c
                INNER JOIN LlmModelProfiles p ON p.ModelConfigurationId = c.Id AND p.IsDefault = 1;

                UPDATE s
                SET
                    TextModelConfigurationId = (SELECT ModelConfigurationId FROM LlmModelProfiles WHERE Id = s.TextModelProfileId),
                    VisionModelConfigurationId = (SELECT ModelConfigurationId FROM LlmModelProfiles WHERE Id = s.VisionModelProfileId),
                    RefineModelConfigurationId = (SELECT ModelConfigurationId FROM LlmModelProfiles WHERE Id = s.RefineModelProfileId),
                    EmbeddingModelConfigurationId = (SELECT ModelConfigurationId FROM LlmModelProfiles WHERE Id = s.EmbeddingModelProfileId)
                FROM HouseholdLlmSettings s;

                DELETE FROM LlmModelProfiles;
                """
            );

            migrationBuilder.DropColumn(name: "TextModelProfileId", table: "HouseholdLlmSettings");

            migrationBuilder.DropColumn(
                name: "VisionModelProfileId",
                table: "HouseholdLlmSettings"
            );

            migrationBuilder.DropColumn(
                name: "RefineModelProfileId",
                table: "HouseholdLlmSettings"
            );

            migrationBuilder.DropColumn(
                name: "EmbeddingModelProfileId",
                table: "HouseholdLlmSettings"
            );

            migrationBuilder.DropColumn(name: "IsDefault", table: "LlmModelProfiles");

            migrationBuilder.DropColumn(name: "ModelConfigurationId", table: "LlmModelProfiles");

            migrationBuilder.DropColumn(
                name: "DailyQuotaResetTime",
                table: "LlmModelConfigurations"
            );

            migrationBuilder.DropColumn(
                name: "DailyQuotaResetTimezone",
                table: "LlmModelConfigurations"
            );

            migrationBuilder.DropColumn(name: "DailyRequestQuota", table: "LlmModelConfigurations");

            migrationBuilder.DropColumn(
                name: "MaxConcurrentRequests",
                table: "LlmModelConfigurations"
            );

            migrationBuilder.DropColumn(name: "RequestsPerMinute", table: "LlmModelConfigurations");

            migrationBuilder.AlterColumn<int>(
                name: "RequestTimeoutSeconds",
                table: "LlmModelProfiles",
                type: "int",
                nullable: false,
                defaultValue: 600,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 120
            );

            migrationBuilder.AlterColumn<int>(
                name: "ContextWindowTokens",
                table: "LlmModelProfiles",
                type: "int",
                nullable: false,
                defaultValue: 32768,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 8192
            );

            migrationBuilder.AddColumn<bool>(
                name: "IsBuiltInDefault",
                table: "LlmModelProfiles",
                type: "bit",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.AddColumn<string>(
                name: "MatchPrefixesJson",
                table: "LlmModelProfiles",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: ""
            );

            migrationBuilder.AddColumn<string>(
                name: "ModelTag",
                table: "LlmModelProfiles",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: ""
            );

            migrationBuilder.AddColumn<bool>(
                name: "RecommendedAsRefineModel",
                table: "LlmModelProfiles",
                type: "bit",
                nullable: false,
                defaultValue: false
            );

            migrationBuilder.AddColumn<string>(
                name: "Slug",
                table: "LlmModelProfiles",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: ""
            );

            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "LlmModelProfiles",
                type: "int",
                nullable: false,
                defaultValue: 0
            );

            migrationBuilder.CreateIndex(
                name: "IX_LlmModelProfiles_Slug",
                table: "LlmModelProfiles",
                column: "Slug",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "IX_LlmModelConfigurations_ConnectionId",
                table: "LlmModelConfigurations",
                column: "ConnectionId"
            );

            migrationBuilder.CreateIndex(
                name: "IX_HouseholdLlmSettings_EmbeddingModelConfigurationId",
                table: "HouseholdLlmSettings",
                column: "EmbeddingModelConfigurationId"
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

            migrationBuilder.AddForeignKey(
                name: "FK_HouseholdLlmSettings_LlmModelConfigurations_EmbeddingModelConfigurationId",
                table: "HouseholdLlmSettings",
                column: "EmbeddingModelConfigurationId",
                principalTable: "LlmModelConfigurations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict
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
        }
    }
}
