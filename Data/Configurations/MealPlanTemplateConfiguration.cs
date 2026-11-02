using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Data.Configurations;

public sealed class MealPlanTemplateConfiguration : IEntityTypeConfiguration<MealPlanTemplate>
{
    public void Configure(EntityTypeBuilder<MealPlanTemplate> builder)
    {
        builder.ToTable("MealPlanTemplates");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Name).IsRequired().HasMaxLength(120);
        builder.Property(t => t.NormalizedName).IsRequired().HasMaxLength(120);
        builder.Property(t => t.Description).HasMaxLength(500);

        builder.Property(t => t.CreatedBy).IsRequired().HasMaxLength(256);
        builder.Property(t => t.ModifiedBy).IsRequired().HasMaxLength(256);

        builder.HasIndex(t => t.NormalizedName).IsUnique();
    }
}
