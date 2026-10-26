using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Data.Configurations;

public sealed class ShareLinkConfiguration : IEntityTypeConfiguration<ShareLink>
{
    public void Configure(EntityTypeBuilder<ShareLink> builder)
    {
        builder.ToTable("ShareLinks");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Token).IsRequired().HasMaxLength(64);

        builder.Property(s => s.Label).HasMaxLength(120);

        builder.Property(s => s.CreatedBy).IsRequired().HasMaxLength(100);

        builder.Property(s => s.ModifiedBy).IsRequired().HasMaxLength(100);

        builder.HasIndex(s => s.Token).IsUnique();

        builder.HasIndex(s => s.RecipeId);

        builder
            .HasOne(s => s.Recipe)
            .WithMany(r => r.ShareLinks)
            .HasForeignKey(s => s.RecipeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
