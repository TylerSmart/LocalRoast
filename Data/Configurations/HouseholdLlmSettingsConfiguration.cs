using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Data.Configurations;

public sealed class HouseholdLlmSettingsConfiguration
    : IEntityTypeConfiguration<HouseholdLlmSettings>
{
    public void Configure(EntityTypeBuilder<HouseholdLlmSettings> builder)
    {
        builder.ToTable("HouseholdLlmSettings");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.SingletonKey).IsRequired().HasMaxLength(32);
        builder.HasIndex(s => s.SingletonKey).IsUnique();

        builder.Property(s => s.Provider).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(s => s.BaseUrl).IsRequired().HasMaxLength(500);
        builder.Property(s => s.ApiKey).HasMaxLength(500);
        builder.Property(s => s.TextModel).HasMaxLength(200);
        builder.Property(s => s.VisionModel).HasMaxLength(200);
        builder.Property(s => s.OllamaKeepAlive).IsRequired().HasMaxLength(32);

        builder.Property(s => s.CreatedBy).IsRequired().HasMaxLength(256);
        builder.Property(s => s.ModifiedBy).IsRequired().HasMaxLength(256);
    }
}
