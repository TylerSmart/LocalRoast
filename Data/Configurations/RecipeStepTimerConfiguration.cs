using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Data.Configurations;

public sealed class RecipeStepTimerConfiguration : IEntityTypeConfiguration<RecipeStepTimer>
{
    public void Configure(EntityTypeBuilder<RecipeStepTimer> builder)
    {
        builder.ToTable("RecipeStepTimers");

        builder.HasKey(rst => rst.Id);
        builder.Property(rst => rst.Id).ValueGeneratedNever();

        builder.HasIndex(rst => new { rst.RecipeStepId, rst.SortOrder });

        builder.Property(rst => rst.Label).IsRequired().HasMaxLength(150);

        builder
            .HasOne(rst => rst.RecipeStep)
            .WithMany(rs => rs.Timers)
            .HasForeignKey(rst => rst.RecipeStepId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(rst => rst.CreatedBy).IsRequired().HasMaxLength(256);
        builder.Property(rst => rst.ModifiedBy).IsRequired().HasMaxLength(256);
    }
}
