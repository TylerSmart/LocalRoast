using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Data.Configurations;

public sealed class RecipeIngredientReplacementConfiguration
    : IEntityTypeConfiguration<RecipeIngredientReplacement>
{
    public void Configure(EntityTypeBuilder<RecipeIngredientReplacement> builder)
    {
        builder.ToTable("RecipeIngredientReplacements");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();

        builder.HasIndex(r => new { r.RecipeIngredientId, r.SortOrder });

        builder.Property(r => r.DisplayName).IsRequired().HasMaxLength(200);
        builder.Property(r => r.Quantity).HasPrecision(10, 4);
        builder.Property(r => r.MaxQuantity).HasPrecision(10, 4);
        builder.Property(r => r.Unit).HasConversion<string>().HasMaxLength(32);
        builder.Property(r => r.CustomUnitName).HasMaxLength(64);
        builder.Property(r => r.PreparationNote).HasMaxLength(300);

        builder
            .HasOne(r => r.RecipeIngredient)
            .WithMany(i => i.Replacements)
            .HasForeignKey(r => r.RecipeIngredientId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict: SQL Server rejects multiple cascade/set-null paths from FoodItem.
        builder
            .HasOne(r => r.FoodItem)
            .WithMany()
            .HasForeignKey(r => r.FoodItemId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(r => r.CreatedBy).IsRequired().HasMaxLength(256);
        builder.Property(r => r.ModifiedBy).IsRequired().HasMaxLength(256);
    }
}
