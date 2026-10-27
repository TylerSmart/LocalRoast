using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Data.Configurations;

public sealed class RecipeAttachmentConfiguration : IEntityTypeConfiguration<RecipeAttachment>
{
    public void Configure(EntityTypeBuilder<RecipeAttachment> builder)
    {
        builder.ToTable("RecipeAttachments");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.FileName).IsRequired().HasMaxLength(255);
        builder.Property(a => a.MediaUrlPath).IsRequired().HasMaxLength(500);
        builder.Property(a => a.ContentType).IsRequired().HasMaxLength(100);
        builder.Property(a => a.Label).HasMaxLength(150);

        builder.Property(a => a.CreatedBy).IsRequired().HasMaxLength(256);
        builder.Property(a => a.ModifiedBy).IsRequired().HasMaxLength(256);

        builder.HasIndex(a => new { a.RecipeId, a.SortOrder });

        builder
            .HasOne(a => a.Recipe)
            .WithMany(r => r.Attachments)
            .HasForeignKey(a => a.RecipeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
