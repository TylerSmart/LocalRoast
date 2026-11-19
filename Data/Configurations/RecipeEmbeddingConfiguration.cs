using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Data.Configurations;

public sealed class RecipeEmbeddingConfiguration : IEntityTypeConfiguration<RecipeEmbedding>
{
    public void Configure(EntityTypeBuilder<RecipeEmbedding> builder)
    {
        builder.ToTable("RecipeEmbeddings");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.ModelTag).IsRequired().HasMaxLength(200);
        builder.Property(e => e.ContentHash).IsRequired().HasMaxLength(64);
        builder.Property(e => e.EmbeddingText).IsRequired().HasMaxLength(2000);
        builder.Property(e => e.VectorBytes).IsRequired();

        builder.HasIndex(e => new { e.RecipeId, e.ModelTag }).IsUnique();
        builder.HasIndex(e => e.ModelTag);

        builder
            .HasOne(e => e.Recipe)
            .WithMany(r => r.Embeddings)
            .HasForeignKey(e => e.RecipeId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(e => e.CreatedBy).IsRequired().HasMaxLength(256);
        builder.Property(e => e.ModifiedBy).IsRequired().HasMaxLength(256);
    }
}
