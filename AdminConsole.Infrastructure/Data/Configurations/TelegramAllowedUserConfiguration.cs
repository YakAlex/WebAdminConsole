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

        // ChatId приходить ЗЗОВНІ від Telegram (не сурогатний ключ) — без
        // цього EF Core за конвенцією позначив би єдиний integer PK як
        // autoincrement і намагався б генерувати власні значення.
        builder.Property(u => u.ChatId).ValueGeneratedNever();
    }
}
