using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Data.Configurations;

public sealed class LlmConnectionConfiguration : IEntityTypeConfiguration<LlmConnection>
{
    public void Configure(EntityTypeBuilder<LlmConnection> builder)
    {
        builder.ToTable("LlmConnections");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.Name).IsRequired().HasMaxLength(100);
        builder.HasIndex(c => c.Name).IsUnique();

        builder.Property(c => c.Provider).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(c => c.BaseUrl).IsRequired().HasMaxLength(500);
        builder.Property(c => c.ApiKey).HasMaxLength(500);
        builder.Property(c => c.OllamaKeepAlive).IsRequired().HasMaxLength(32);
        builder.Property(c => c.EnableRateLimitSafeguards).HasDefaultValue(true);
        builder.Property(c => c.MaxConcurrentRequests).HasDefaultValue(1);
        builder.Property(c => c.DailyQuotaResetTimezone).HasMaxLength(64).HasDefaultValue("UTC");
        builder.Property(c => c.DailyQuotaResetTime).HasMaxLength(10).HasDefaultValue("00:00");

        builder.Property(c => c.CreatedBy).IsRequired().HasMaxLength(256);
        builder.Property(c => c.ModifiedBy).IsRequired().HasMaxLength(256);
    }
}
