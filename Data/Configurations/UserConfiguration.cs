using App.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace App.Data.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("Users");

        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).ValueGeneratedNever();

        builder.Property(u => u.Email).IsRequired().HasMaxLength(256);

        builder.Property(u => u.NormalizedEmail).IsRequired().HasMaxLength(256);

        builder.HasIndex(u => u.NormalizedEmail).IsUnique();

        builder.Property(u => u.Username).IsRequired().HasMaxLength(100);

        builder.Property(u => u.NormalizedUsername).IsRequired().HasMaxLength(100);

        builder.HasIndex(u => u.NormalizedUsername).IsUnique();

        builder.Property(u => u.PasswordHash).IsRequired();

        builder.Property(u => u.ExternalProvider).HasMaxLength(100);

        builder.Property(u => u.ExternalSubjectId).HasMaxLength(256);

        builder.HasIndex(u => new { u.ExternalProvider, u.ExternalSubjectId });

        builder.Property(u => u.ThemeSettings).HasMaxLength(4000);

        builder.Property(u => u.CreatedBy).IsRequired().HasMaxLength(256);

        builder.Property(u => u.ModifiedBy).IsRequired().HasMaxLength(256);
    }
}
