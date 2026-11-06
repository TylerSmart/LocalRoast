using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Data.Configurations;

public sealed class RecipeIngredientConfiguration : IEntityTypeConfiguration<RecipeIngredient>
{
    public void Configure(EntityTypeBuilder<RecipeIngredient> builder)
    {
        builder.ToTable("RecipeIngredients");

        builder.HasKey(ri => ri.Id);

        builder.HasIndex(ri => new { ri.RecipeId, ri.SortOrder });

        builder.Property(ri => ri.SectionName).HasMaxLength(150);

        builder.Property(ri => ri.Quantity).HasPrecision(10, 4);
        builder.Property(ri => ri.MaxQuantity).HasPrecision(10, 4);

        builder.Property(ri => ri.Unit).HasConversion<string>().HasMaxLength(32);
        builder.Property(ri => ri.CustomUnitName).HasMaxLength(50);
        builder.Property(ri => ri.AlternativeQuantity).HasPrecision(10, 4);
        builder.Property(ri => ri.AlternativeMaxQuantity).HasPrecision(10, 4);
        builder.Property(ri => ri.AlternativeUnit).HasConversion<string>().HasMaxLength(32);
        builder.Property(ri => ri.AlternativeCustomUnitName).HasMaxLength(50);

        builder.Property(ri => ri.DisplayName).IsRequired().HasMaxLength(200);
        builder.Property(ri => ri.PreparationNote).HasMaxLength(200);
        builder.Property(ri => ri.OriginalRawText).HasMaxLength(500);
        builder.Property(ri => ri.UnresolvedRecipeTitle).HasMaxLength(200);

        builder
            .HasOne(ri => ri.Recipe)
            .WithMany(r => r.Ingredients)
            .HasForeignKey(ri => ri.RecipeId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasOne(ri => ri.FoodItem)
            .WithMany(f => f.RecipeIngredients)
            .HasForeignKey(ri => ri.FoodItemId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder
            .HasOne(ri => ri.LinkedRecipe)
            .WithMany(r => r.ReferencedAsSubRecipeIn)
            .HasForeignKey(ri => ri.LinkedRecipeId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(ri => ri.CreatedBy).IsRequired().HasMaxLength(256);
        builder.Property(ri => ri.ModifiedBy).IsRequired().HasMaxLength(256);
    }
}
