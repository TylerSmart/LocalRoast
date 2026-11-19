using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Data.Configurations;

public sealed class LlmModelConfigurationConfiguration
    : IEntityTypeConfiguration<LlmModelConfiguration>
{
    public void Configure(EntityTypeBuilder<LlmModelConfiguration> builder)
    {
        builder.ToTable("LlmModelConfigurations");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Name).IsRequired().HasMaxLength(150);
        builder.HasIndex(c => c.Name).IsUnique();

        builder.Property(c => c.Model).IsRequired().HasMaxLength(200);
        builder.HasIndex(c => new { c.ConnectionId, c.Model }).IsUnique();

        builder.Property(c => c.DailyQuotaResetTimezone).HasMaxLength(64);
        builder.Property(c => c.DailyQuotaResetTime).HasMaxLength(10);

        builder
            .HasOne(c => c.Connection)
            .WithMany(c => c.ModelConfigurations)
            .HasForeignKey(c => c.ConnectionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(c => c.CreatedBy).IsRequired().HasMaxLength(256);
        builder.Property(c => c.ModifiedBy).IsRequired().HasMaxLength(256);
    }
}
