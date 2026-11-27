using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Data.Configurations;

public sealed class RecipeStepConfiguration : IEntityTypeConfiguration<RecipeStep>
{
    public void Configure(EntityTypeBuilder<RecipeStep> builder)
    {
        builder.ToTable("RecipeSteps");

        builder.HasKey(rs => rs.Id);
        builder.Property(rs => rs.Id).ValueGeneratedNever();

        builder.HasIndex(rs => new { rs.RecipeId, rs.StepNumber }).IsUnique();

        builder.Property(rs => rs.SectionName).HasMaxLength(150);
        builder.Property(rs => rs.Instruction).IsRequired().HasMaxLength(4000);
        builder.Property(rs => rs.ImagePath).HasMaxLength(500);

        builder
            .HasOne(rs => rs.Recipe)
            .WithMany(r => r.Steps)
            .HasForeignKey(rs => rs.RecipeId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(rs => rs.CreatedBy).IsRequired().HasMaxLength(256);
        builder.Property(rs => rs.ModifiedBy).IsRequired().HasMaxLength(256);
    }
}
