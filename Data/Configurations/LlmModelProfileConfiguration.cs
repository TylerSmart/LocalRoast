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

        builder.Property(p => p.Slug).IsRequired().HasMaxLength(100);
        builder.HasIndex(p => p.Slug).IsUnique();

        builder.Property(p => p.DisplayName).IsRequired().HasMaxLength(150);
        builder.Property(p => p.ModelTag).IsRequired().HasMaxLength(200);
        builder.Property(p => p.Summary).IsRequired().HasMaxLength(500);
        builder.Property(p => p.MatchPrefixesJson).IsRequired().HasMaxLength(1000);
        builder.Property(p => p.NotesJson).IsRequired().HasMaxLength(4000);

        builder.Property(p => p.ContextWindowTokens).HasDefaultValue(32768);
        builder.Property(p => p.RequestTimeoutSeconds).HasDefaultValue(600);

        builder.Property(p => p.CreatedBy).IsRequired().HasMaxLength(256);
        builder.Property(p => p.ModifiedBy).IsRequired().HasMaxLength(256);
    }
}
