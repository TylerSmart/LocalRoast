using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Data.Configurations;

public sealed class MealPlanEntryConfiguration : IEntityTypeConfiguration<MealPlanEntry>
{
    public void Configure(EntityTypeBuilder<MealPlanEntry> builder)
    {
        builder.ToTable("MealPlanEntries");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Slot).HasConversion<string>().HasMaxLength(32);
        builder.Property(e => e.EntryType).HasConversion<string>().HasMaxLength(32);

        builder.Property(e => e.TargetServings).HasPrecision(8, 2);
        builder.Property(e => e.CustomTitle).HasMaxLength(200);
        builder.Property(e => e.Notes).HasMaxLength(500);

        builder.Property(e => e.CreatedBy).IsRequired().HasMaxLength(256);
        builder.Property(e => e.ModifiedBy).IsRequired().HasMaxLength(256);

        builder.HasIndex(e => new
        {
            e.WeekStartDate,
            e.IsOnDeck,
            e.SortOrder,
        });
        builder.HasIndex(e => new
        {
            e.ScheduledDate,
            e.Slot,
            e.SortOrder,
        });
        builder.HasIndex(e => e.RecipeId);

        builder
            .HasOne(e => e.Recipe)
            .WithMany(r => r.MealPlanEntries)
            .HasForeignKey(e => e.RecipeId)
            .OnDelete(DeleteBehavior.Restrict);

        builder
            .HasOne(e => e.SourceMealPlanEntry)
            .WithMany(s => s.DerivedLeftoverEntries)
            .HasForeignKey(e => e.SourceMealPlanEntryId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
