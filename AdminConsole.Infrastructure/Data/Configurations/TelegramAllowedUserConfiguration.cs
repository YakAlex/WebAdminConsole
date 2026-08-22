using AdminConsole.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AdminConsole.Infrastructure.Data.Configurations;

public sealed class TelegramAllowedUserConfiguration : IEntityTypeConfiguration<TelegramAllowedUser>
{
    public void Configure(EntityTypeBuilder<TelegramAllowedUser> builder)
    {
        builder.ToTable("TelegramAllowedUsers");
        builder.HasKey(u => u.ChatId);

        // ChatId comes from OUTSIDE, from Telegram (not a surrogate key) — without
        // this, EF Core would by convention mark the sole integer PK as
        // autoincrement and try to generate its own values.
        builder.Property(u => u.ChatId).ValueGeneratedNever();
    }
}
