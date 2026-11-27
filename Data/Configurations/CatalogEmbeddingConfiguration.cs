using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Data.Configurations;

public sealed class CatalogEmbeddingConfiguration : IEntityTypeConfiguration<CatalogEmbedding>
{
    public void Configure(EntityTypeBuilder<CatalogEmbedding> builder)
    {
        builder.ToTable("CatalogEmbeddings");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.ModelTag).IsRequired().HasMaxLength(200);
        builder.Property(e => e.NormalizedText).IsRequired().HasMaxLength(400);
        builder.Property(e => e.VectorBytes).IsRequired();

        builder.HasIndex(e => new { e.ModelTag, e.NormalizedText }).IsUnique();

        builder.Property(e => e.CreatedBy).IsRequired().HasMaxLength(256);
        builder.Property(e => e.ModifiedBy).IsRequired().HasMaxLength(256);
    }
}
