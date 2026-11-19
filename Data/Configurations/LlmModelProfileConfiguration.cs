using App.Core.Ingestion;
using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Data.Configurations;

public sealed class LlmModelProfileConfiguration : IEntityTypeConfiguration<LlmModelProfile>
{
    public void Configure(EntityTypeBuilder<LlmModelProfile> builder)
    {
        builder.ToTable("LlmModelProfiles");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.DisplayName).IsRequired().HasMaxLength(150);
        builder.HasIndex(p => new { p.ModelConfigurationId, p.DisplayName }).IsUnique();

        builder.Property(p => p.Summary).IsRequired().HasMaxLength(500);
        builder.Property(p => p.NotesJson).IsRequired().HasMaxLength(4000);

        builder
            .Property(p => p.ContextWindowTokens)
            .HasDefaultValue(LlmProfileDefaults.DefaultContextWindowTokens);
        builder
            .Property(p => p.RequestTimeoutSeconds)
            .HasDefaultValue(LlmProfileDefaults.DefaultRequestTimeoutSeconds);
        builder.Property(p => p.IsDefault).HasDefaultValue(false);

        builder
            .HasOne(p => p.ModelConfiguration)
            .WithMany(c => c.Profiles)
            .HasForeignKey(p => p.ModelConfigurationId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(p => p.CreatedBy).IsRequired().HasMaxLength(256);
        builder.Property(p => p.ModifiedBy).IsRequired().HasMaxLength(256);
    }
}
