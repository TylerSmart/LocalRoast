using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Data.Configurations;

public sealed class RecipeStepInlineAmountConfiguration
    : IEntityTypeConfiguration<RecipeStepInlineAmount>
{
    public void Configure(EntityTypeBuilder<RecipeStepInlineAmount> builder)
    {
        builder.ToTable("RecipeStepInlineAmounts");

        builder.HasKey(a => a.Id);
        builder.HasIndex(a => new { a.RecipeStepId, a.SortOrder }).IsUnique();

        builder.Property(a => a.Kind).HasConversion<string>().HasMaxLength(16);
        builder.Property(a => a.Quantity).HasPrecision(10, 4);
        builder.Property(a => a.MaxQuantity).HasPrecision(10, 4);
        builder.Property(a => a.Unit).HasConversion<string>().HasMaxLength(32);
        builder.Property(a => a.CustomUnitName).HasMaxLength(50);
        builder.Property(a => a.AlternativeQuantity).HasPrecision(10, 4);
        builder.Property(a => a.AlternativeMaxQuantity).HasPrecision(10, 4);
        builder.Property(a => a.AlternativeUnit).HasConversion<string>().HasMaxLength(32);
        builder.Property(a => a.AlternativeCustomUnitName).HasMaxLength(50);

        builder
            .HasOne(a => a.RecipeStep)
            .WithMany(s => s.InlineAmounts)
            .HasForeignKey(a => a.RecipeStepId)
            .OnDelete(DeleteBehavior.Cascade);

        // A cascade here would create multiple cascade paths from Recipe; services delete
        // inline amounts before ingredients.
        builder
            .HasOne(a => a.RecipeIngredient)
            .WithMany(i => i.InlineAmountReferences)
            .HasForeignKey(a => a.RecipeIngredientId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(a => a.CreatedBy).IsRequired().HasMaxLength(256);
        builder.Property(a => a.ModifiedBy).IsRequired().HasMaxLength(256);
    }
}
