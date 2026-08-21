using AdminConsole.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AdminConsole.Infrastructure.Data.Configurations;

/// <summary>
/// BackupCheckState (1) → BackupSample (N), FK по BackupCheckStateId (T2.1).
/// Сурогатний int PK замість композитного (Name, Kind) — так простіше
/// оголосити FK для дочірньої History; (Name, Kind) лишається унікальним
/// індексом для пошуку — саме за ним BackupMonitorService шукає стан
/// (StateKey = "Name|Kind").
///
/// History — EF Core owned collection: BackupSample не має власної
/// ідентичності поза батьком (завжди читається/пишеться разом з ним) —
/// саме для цього сценарію призначені owned types, замість ручного
/// shadow-FK/Id на кожному дочірньому записі.
/// </summary>
public sealed class BackupCheckStateConfiguration : IEntityTypeConfiguration<BackupCheckState>
{
    public void Configure(EntityTypeBuilder<BackupCheckState> builder)
    {
        builder.ToTable("BackupCheckStates");

        builder.Property<int>("BackupCheckStateId");
        builder.HasKey("BackupCheckStateId");
        builder.HasIndex(s => new { s.Name, s.Kind }).IsUnique();

        builder.Property(s => s.Name).IsRequired();
        builder.Property(s => s.Host).IsRequired();

        builder.OwnsMany(s => s.History, history =>
        {
            history.ToTable("BackupSamples");

            // Id — єдиний PK (одноколонковий autoincrement, SQLite ROWID) замість
            // композитного (BackupCheckStateId, Id): SQLite не підтримує генеровані
            // значення в композитних ключах ("SQLite does not support generated
            // values on composite keys" — EF Core валідатор моделі). FK лишається
            // звичайним, НЕ-ключовим стовпцем.
            history.Property<int>("Id");
            history.HasKey("Id");
            history.WithOwner().HasForeignKey("BackupCheckStateId");

            history.Property(h => h.ObservedAt).IsRequired();
            history.Property(h => h.SizeBytes).IsRequired();
        });
    }
}
