using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Data.Configurations;

public sealed class FoodItemConfiguration : IEntityTypeConfiguration<FoodItem>
{
    public void Configure(EntityTypeBuilder<FoodItem> builder)
    {
        builder.ToTable("FoodItems");

        builder.HasKey(f => f.Id);

        builder.Property(f => f.Name).IsRequired().HasMaxLength(150);

        builder.Property(f => f.NormalizedName).IsRequired().HasMaxLength(150);
        builder.HasIndex(f => f.NormalizedName).IsUnique();

        builder.HasIndex(f => f.IsPantryStaple);

        builder.Property(f => f.Category).HasConversion<string>().HasMaxLength(32);
        builder.HasIndex(f => f.Category);

        builder.Property(f => f.CreatedBy).IsRequired().HasMaxLength(256);
        builder.Property(f => f.ModifiedBy).IsRequired().HasMaxLength(256);
    }
}
