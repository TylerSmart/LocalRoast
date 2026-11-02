using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Data.Configurations;

public sealed class ShoppingListItemSourceConfiguration
    : IEntityTypeConfiguration<ShoppingListItemSource>
{
    public void Configure(EntityTypeBuilder<ShoppingListItemSource> builder)
    {
        builder.ToTable("ShoppingListItemSources");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.SourceKind).HasConversion<string>().HasMaxLength(32);

        builder.Property(s => s.SourceLabel).IsRequired().HasMaxLength(200);

        builder.Property(s => s.ContributedQuantity).HasPrecision(10, 4);
        builder.Property(s => s.ContributedMaxQuantity).HasPrecision(10, 4);

        builder.Property(s => s.CreatedBy).IsRequired().HasMaxLength(256);
        builder.Property(s => s.ModifiedBy).IsRequired().HasMaxLength(256);

        builder.HasIndex(s => s.ShoppingListItemId);
        builder.HasIndex(s => s.MealPlanEntryId);
        builder.HasIndex(s => s.RecipeId);

        builder
            .HasOne(s => s.ShoppingListItem)
            .WithMany(i => i.Sources)
            .HasForeignKey(s => s.ShoppingListItemId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
