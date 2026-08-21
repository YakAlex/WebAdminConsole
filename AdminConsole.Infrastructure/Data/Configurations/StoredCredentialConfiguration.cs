using AdminConsole.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AdminConsole.Infrastructure.Data.Configurations;

public sealed class StoredCredentialConfiguration : IEntityTypeConfiguration<StoredCredential>
{
    public void Configure(EntityTypeBuilder<StoredCredential> builder)
    {
        builder.ToTable("StoredCredentials");
        builder.HasKey(c => c.Target);

        builder.Property(c => c.ProtectedSecret).IsRequired();
    }
}
