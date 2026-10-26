using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Data.Configurations;

public sealed class IngestionJobConfiguration : IEntityTypeConfiguration<IngestionJob>
{
    public void Configure(EntityTypeBuilder<IngestionJob> builder)
    {
        builder.ToTable("IngestionJobs");

        builder.HasKey(j => j.Id);

        builder.Property(j => j.Channel).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(j => j.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(j => j.DisplayTitle).IsRequired().HasMaxLength(250);
        builder.Property(j => j.SourceUrl).HasMaxLength(2048);
        builder.Property(j => j.RawText).HasMaxLength(100000);
        builder.Property(j => j.ScrapedPageJson).HasMaxLength(200000);
        builder.Property(j => j.NormalizedSourceText).HasMaxLength(100000);
        builder.Property(j => j.DownloadedCoverImagePath).HasMaxLength(500);
        builder.Property(j => j.DownloadedThumbnailImagePath).HasMaxLength(500);
        builder.Property(j => j.ExtractedDraftJson).HasMaxLength(200000);
        builder.Property(j => j.ConfidenceScore).HasPrecision(4, 3);
        builder.Property(j => j.StatusDetail).HasMaxLength(2000);

        builder.Property(j => j.CreatedBy).IsRequired().HasMaxLength(256);
        builder.Property(j => j.ModifiedBy).IsRequired().HasMaxLength(256);

        builder.HasIndex(j => j.Status);
        builder.HasIndex(j => new { j.Status, j.NextRetryAt });
        builder.HasIndex(j => j.CreatedAt);
        builder.HasIndex(j => j.ResultingRecipeId);

        builder
            .HasOne(j => j.ResultingRecipe)
            .WithMany(r => r.IngestionJobs)
            .HasForeignKey(j => j.ResultingRecipeId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
