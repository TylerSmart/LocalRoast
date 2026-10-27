using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Data.Configurations;

public sealed class PersonalAccessTokenConfiguration : IEntityTypeConfiguration<PersonalAccessToken>
{
    public void Configure(EntityTypeBuilder<PersonalAccessToken> builder)
    {
        builder.ToTable("PersonalAccessTokens");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Name).IsRequired().HasMaxLength(100);

        builder.Property(t => t.TokenPrefix).IsRequired().HasMaxLength(24);

        builder.Property(t => t.TokenHash).IsRequired().HasMaxLength(64);

        builder.Property(t => t.CreatedBy).IsRequired().HasMaxLength(256);

        builder.Property(t => t.ModifiedBy).IsRequired().HasMaxLength(256);

        builder.HasIndex(t => t.TokenHash).IsUnique();

        builder.HasIndex(t => new { t.UserId, t.CreatedAt });

        builder
            .HasOne(t => t.User)
            .WithMany(u => u.PersonalAccessTokens)
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
