using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Data.Configurations;

public sealed class CookLogConfiguration : IEntityTypeConfiguration<CookLog>
{
    public void Configure(EntityTypeBuilder<CookLog> builder)
    {
        builder.ToTable("CookLogs");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.CookedByDisplayName).IsRequired().HasMaxLength(150);

        builder.Property(c => c.ServingsMade).HasPrecision(8, 2);

        builder.Property(c => c.Notes).HasMaxLength(2000);

        builder.Property(c => c.PhotoImagePath).HasMaxLength(500);

        builder.Property(c => c.PhotoThumbnailPath).HasMaxLength(500);

        builder.Property(c => c.CreatedBy).IsRequired().HasMaxLength(256);

        builder.Property(c => c.ModifiedBy).IsRequired().HasMaxLength(256);

        builder.HasIndex(c => new { c.RecipeId, c.CookedAt });

        builder.HasIndex(c => c.CookedByUserId);

        builder
            .HasOne(c => c.Recipe)
            .WithMany(r => r.CookLogs)
            .HasForeignKey(c => c.RecipeId)
            .OnDelete(DeleteBehavior.Cascade);

        builder
            .HasOne(c => c.CookedByUser)
            .WithMany(u => u.CookLogs)
            .HasForeignKey(c => c.CookedByUserId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
