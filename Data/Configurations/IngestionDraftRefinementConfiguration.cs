using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Data.Configurations;

public sealed class IngestionDraftRefinementConfiguration
    : IEntityTypeConfiguration<IngestionDraftRefinement>
{
    public void Configure(EntityTypeBuilder<IngestionDraftRefinement> builder)
    {
        builder.ToTable("IngestionDraftRefinements");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(r => r.Model).IsRequired().HasMaxLength(200);
        builder.Property(r => r.Detail).HasMaxLength(2000);
        builder.Property(r => r.BaseDraftJson).HasMaxLength(200000);
        builder.Property(r => r.ProposalJson).HasMaxLength(200000);

        builder.Property(r => r.CreatedBy).IsRequired().HasMaxLength(256);
        builder.Property(r => r.ModifiedBy).IsRequired().HasMaxLength(256);

        builder.HasIndex(r => r.IngestionJobId).IsUnique();
        builder.HasIndex(r => new { r.Status, r.RequestedAt });

        builder
            .HasOne(r => r.IngestionJob)
            .WithOne()
            .HasForeignKey<IngestionDraftRefinement>(r => r.IngestionJobId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
