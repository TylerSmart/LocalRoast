using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Data.Configurations;

public sealed class MealPlanTemplateItemConfiguration
    : IEntityTypeConfiguration<MealPlanTemplateItem>
{
    public void Configure(EntityTypeBuilder<MealPlanTemplateItem> builder)
    {
        builder.ToTable("MealPlanTemplateItems");

        builder.HasKey(i => i.Id);

        builder.Property(i => i.Slot).HasConversion<string>().HasMaxLength(32);
        builder.Property(i => i.EntryType).HasConversion<string>().HasMaxLength(32);

        builder.Property(i => i.TargetServings).HasPrecision(8, 2);
        builder.Property(i => i.CustomTitle).HasMaxLength(200);
        builder.Property(i => i.Notes).HasMaxLength(500);

        builder.Property(i => i.CreatedBy).IsRequired().HasMaxLength(256);
        builder.Property(i => i.ModifiedBy).IsRequired().HasMaxLength(256);

        builder.HasIndex(i => new
        {
            i.TemplateId,
            i.IsOnDeck,
            i.DayOffset,
            i.Slot,
            i.SortOrder,
        });

        builder
            .HasOne(i => i.Template)
            .WithMany(t => t.Items)
            .HasForeignKey(i => i.TemplateId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasOne(i => i.Recipe)
            .WithMany(r => r.MealPlanTemplateItems)
            .HasForeignKey(i => i.RecipeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
