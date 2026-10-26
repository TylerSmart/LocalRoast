using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Data.Configurations;

public sealed class RecipeCollectionConfiguration : IEntityTypeConfiguration<RecipeCollection>
{
    public void Configure(EntityTypeBuilder<RecipeCollection> builder)
    {
        builder.ToTable("RecipeCollections");

        builder.HasKey(rc => rc.Id);
        builder.Property(rc => rc.Id).ValueGeneratedNever();

        builder.HasIndex(rc => new { rc.RecipeId, rc.CollectionId }).IsUnique();

        builder
            .HasOne(rc => rc.Recipe)
            .WithMany(r => r.RecipeCollections)
            .HasForeignKey(rc => rc.RecipeId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasOne(rc => rc.Collection)
            .WithMany(c => c.RecipeCollections)
            .HasForeignKey(rc => rc.CollectionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(rc => rc.CreatedBy).IsRequired().HasMaxLength(256);
        builder.Property(rc => rc.ModifiedBy).IsRequired().HasMaxLength(256);
    }
}
