using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Data.Configurations;

public sealed class FoodItemAliasConfiguration : IEntityTypeConfiguration<FoodItemAlias>
{
    public void Configure(EntityTypeBuilder<FoodItemAlias> builder)
    {
        builder.ToTable("FoodItemAliases");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();

        builder.Property(a => a.Alias).IsRequired().HasMaxLength(150);

        builder.Property(a => a.NormalizedAlias).IsRequired().HasMaxLength(150);
        builder.HasIndex(a => a.NormalizedAlias).IsUnique();

        builder
            .HasOne(a => a.FoodItem)
            .WithMany(f => f.Aliases)
            .HasForeignKey(a => a.FoodItemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(a => a.CreatedBy).IsRequired().HasMaxLength(256);
        builder.Property(a => a.ModifiedBy).IsRequired().HasMaxLength(256);
    }
}
