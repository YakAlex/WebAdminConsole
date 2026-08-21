using AdminConsole.Domain.Models;
using MediatR;

namespace AdminConsole.Domain.Events;

/// <summary>
/// Публікується BackupMonitorService раз на цикл (після SaveToDb),
/// зі знімком усіх BackupCheckState. Заміна BackupStatusUpdatedMessage.
///
/// Знімок (не оригінальні мутабельні об'єкти) — той самий принцип,
/// що вже застосований для DowntimeRecord/UptimeTrackerService
/// (CloneRecord), щоб уникнути гонки даних між фоновим циклом і читачами.
/// </summary>
public sealed record BackupStatusUpdatedOccurred(
    IReadOnlyList<BackupCheckState> Snapshot
) : INotification;
