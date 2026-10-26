using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Data.Configurations;

public sealed class TagConfiguration : IEntityTypeConfiguration<Tag>
{
    public void Configure(EntityTypeBuilder<Tag> builder)
    {
        builder.ToTable("Tags");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();

        builder.Property(t => t.Name).IsRequired().HasMaxLength(80);
        builder.Property(t => t.NormalizedName).IsRequired().HasMaxLength(80);

        builder.Property(t => t.Slug).IsRequired().HasMaxLength(100);
        builder.HasIndex(t => t.Slug).IsUnique();

        builder.HasIndex(t => new { t.Category, t.NormalizedName }).IsUnique();

        builder.Property(t => t.CreatedBy).IsRequired().HasMaxLength(256);
        builder.Property(t => t.ModifiedBy).IsRequired().HasMaxLength(256);
    }
}
