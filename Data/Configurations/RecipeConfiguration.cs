using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Data.Configurations;

public sealed class RecipeConfiguration : IEntityTypeConfiguration<Recipe>
{
    public void Configure(EntityTypeBuilder<Recipe> builder)
    {
        builder.ToTable("Recipes");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Title).IsRequired().HasMaxLength(200);
        builder.HasIndex(r => r.Title);

        builder.Property(r => r.Slug).IsRequired().HasMaxLength(220);
        builder.HasIndex(r => r.Slug).IsUnique();

        builder.Property(r => r.Description).HasMaxLength(2000);

        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(32);
        builder.HasIndex(r => r.Status);

        builder.Property(r => r.BaseServings).HasPrecision(8, 2);
        builder.Property(r => r.YieldDisplay).HasMaxLength(100);

        builder.Property(r => r.Difficulty).HasConversion<string>().HasMaxLength(32);

        builder.Property(r => r.SourceUrl).HasMaxLength(2048);
        builder.Property(r => r.SourceName).HasMaxLength(200);
        builder.Property(r => r.SourceAuthor).HasMaxLength(200);
        builder.Property(r => r.SourceBookTitle).HasMaxLength(200);

        builder.Property(r => r.CoverImagePath).HasMaxLength(500);
        builder.Property(r => r.ThumbnailImagePath).HasMaxLength(500);

        builder.Property(r => r.AverageRating).HasPrecision(3, 2);

        builder.HasIndex(r => new
        {
            r.TimesCooked,
            r.AverageRating,
            r.LastCookedAt,
        });

        builder.Property(r => r.CreatedBy).IsRequired().HasMaxLength(256);
        builder.Property(r => r.ModifiedBy).IsRequired().HasMaxLength(256);
    }
}
