using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Data.Configurations;

public sealed class ShoppingListItemConfiguration : IEntityTypeConfiguration<ShoppingListItem>
{
    public void Configure(EntityTypeBuilder<ShoppingListItem> builder)
    {
        builder.ToTable("ShoppingListItems");

        builder.HasKey(i => i.Id);

        builder.Property(i => i.DisplayName).IsRequired().HasMaxLength(200);

        builder.Property(i => i.Quantity).HasPrecision(10, 4);
        builder.Property(i => i.MaxQuantity).HasPrecision(10, 4);

        builder.Property(i => i.Unit).HasConversion<string>().HasMaxLength(32);
        builder.Property(i => i.CustomUnitName).HasMaxLength(60);

        builder.Property(i => i.Dimension).HasConversion<string>().HasMaxLength(32);

        builder.Property(i => i.Notes).HasMaxLength(300);

        builder.Property(i => i.CheckedByDisplayName).HasMaxLength(150);

        builder.Property(i => i.CreatedBy).IsRequired().HasMaxLength(256);
        builder.Property(i => i.ModifiedBy).IsRequired().HasMaxLength(256);

        builder.HasIndex(i => new
        {
            i.ShoppingListId,
            i.IsPantryStapleExcluded,
            i.IsChecked,
            i.SortOrder,
        });

        builder.HasIndex(i => i.FoodItemId);

        builder
            .HasOne(i => i.ShoppingList)
            .WithMany(l => l.Items)
            .HasForeignKey(i => i.ShoppingListId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasOne(i => i.FoodItem)
            .WithMany(f => f.ShoppingListItems)
            .HasForeignKey(i => i.FoodItemId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
