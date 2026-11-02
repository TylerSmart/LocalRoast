using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Data.Configurations;

public sealed class ShoppingListConfiguration : IEntityTypeConfiguration<ShoppingList>
{
    public void Configure(EntityTypeBuilder<ShoppingList> builder)
    {
        builder.ToTable("ShoppingLists");

        builder.HasKey(l => l.Id);

        builder.Property(l => l.Name).IsRequired().HasMaxLength(150);

        builder.Property(l => l.NormalizedName).IsRequired().HasMaxLength(150);
        builder.HasIndex(l => l.NormalizedName).IsUnique();

        builder.HasIndex(l => new { l.IsPinned, l.ModifiedAt });

        builder.Property(l => l.CreatedBy).IsRequired().HasMaxLength(256);
        builder.Property(l => l.ModifiedBy).IsRequired().HasMaxLength(256);
    }
}
