using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Data.Configurations;

public sealed class CollectionConfiguration : IEntityTypeConfiguration<Collection>
{
    public void Configure(EntityTypeBuilder<Collection> builder)
    {
        builder.ToTable("Collections");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        builder.Property(c => c.Name).IsRequired().HasMaxLength(100);
        builder.Property(c => c.NormalizedName).IsRequired().HasMaxLength(100);
        builder.HasIndex(c => c.NormalizedName).IsUnique();

        builder.Property(c => c.Slug).IsRequired().HasMaxLength(120);
        builder.HasIndex(c => c.Slug).IsUnique();

        builder.Property(c => c.Description).HasMaxLength(500);

        builder.Property(c => c.CreatedBy).IsRequired().HasMaxLength(256);
        builder.Property(c => c.ModifiedBy).IsRequired().HasMaxLength(256);
    }
}
