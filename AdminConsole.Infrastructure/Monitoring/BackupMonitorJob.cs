using AdminConsole.Domain.Abstractions;
using AdminConsole.Domain.Events;
using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Configuration;
using Hangfire;
using MediatR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdminConsole.Infrastructure.Monitoring;

/// <summary>
/// Опитує сконфігуровані BackupChecks (FileAge: вік + розмір проти
/// rolling-baseline, окремо Full/Diff). Двоетапна перевірка (Stage A/B)
/// делегована в BackupCheckEvaluator — ця джоба відповідає лише за:
/// цикл, анти-флапінг (підтвердження стану лише після N однакових
/// "сирих" результатів поспіль), персистентність через IBackupStateRepository
/// і придушення сповіщень під час Maintenance Windows.
///
/// T4.5: Hangfire recurring job, НЕ BackgroundService/Singleton (правило
/// Hangfire vs BackgroundService — дискретна job-подібна задача, інтервал
/// вимірюється хвилинами, виграє від retry/dashboard-видимості Hangfire).
///
/// Оскільки Hangfire створює новий екземпляр джоби на кожен запуск (Scoped/
/// Transient, не довгоживучий Singleton), старий _stateLock + ConcurrentDictionary
/// _states (захист від паралельного читання GetSnapshot() з UI-потоку, поки
/// фоновий цикл мутує стан) БІЛЬШЕ НЕ ПОТРІБНІ — усередині одного запуску
/// джоба обробляє визначення строго послідовно (як і раніше), а між запусками
/// стан і так персистується через репозиторій (BackupCheckState вже мав усі
/// анти-флапінг лічильники як персистентні поля ще з Фази 2). Це прямий
/// наслідок T4.5 (сервіс явно НЕ лишається Singleton), а не відхилення від
/// правила "локи переносяться 1:1".
/// </summary>
public sealed class BackupMonitorJob(
    IMediator                              mediator,
    ILogger<BackupMonitorJob>              logger,
    IOptions<MonitoringSettings>           settings,
    IOptions<List<BackupCheckDefinition>>  backupChecks,
    IOptions<List<ServerEntry>>            servers,
    MaintenanceService                     maintenance,
    BackupCheckEvaluator                   evaluator,
    IBackupStateRepository                 repository,
    IAppSettingsRepository                 appSettings)
{
    private readonly MonitoringSettings _settings = settings.Value;
    private readonly IReadOnlyList<BackupCheckDefinition> _definitions = backupChecks.Value.AsReadOnly();

    private readonly IReadOnlyDictionary<string, ServerEntry> _serverLookup = servers.Value
        .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    private const string LogSource         = "BackupMonitor";
    private const int    MaxHistorySamples = 14;

    /// <summary>Скільки циклів поспіль Unknown, перш ніж один раз надіслати попередження (без спаму).</summary>
    private const int UnknownEscalationThreshold = 3;

    /// <summary>
    /// Точка входу для RecurringJob.AddOrUpdate (Program.cs, T4.5).
    /// Аудит Зона 1, Знахідка №7 (2026-08-22): BackupChecks читає файли по
    /// UNC-шляхах (Зона 3 — без гарантованого таймауту), тож один запуск
    /// теоретично може тривати довше за BackupPollIntervalMinutes. Без цього
    /// атрибута Hangfire за замовчуванням МІГ БИ запустити наступний цикл
    /// паралельно з ще не завершеним попереднім — обидва одночасно писали б
    /// у ту саму таблицю BackupCheckState. timeoutInSeconds=10: якщо
    /// попередній запуск ще тримає лок довше 10с очікування — цей запуск
    /// просто пропускається (не чекає й не падає), наступний за розкладом
    /// спробує знову.
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 10)]
    public async Task RunAsync(CancellationToken ct = default)
    {
        if (_definitions.Count == 0)
        {
            logger.LogInformation("BackupMonitorJob: BackupChecks не сконфігуровано — пропускаємо цикл.");
            return;
        }

        var currentAppSettings = await appSettings.GetAsync(ct);
        if (!currentAppSettings.BackupMonitoringEnabled)
        {
            logger.LogInformation("BackupMonitorJob: Backup моніторинг вимкнено в Settings — пропускаємо цикл.");
            return;
        }

        foreach (var def in _definitions)
        {
            string searchKey = string.IsNullOrWhiteSpace(def.Host) ? def.Name : def.Host;
            if (!_serverLookup.ContainsKey(searchKey))
            {
                await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                    $"BackupChecks: '{def.Name}' не знайдено серед Servers у appsettings.json — " +
                    $"Maintenance-придушення для цього запису не працюватиме."), ct);
            }
        }

        var existingStates = (await repository.LoadAllAsync(ct))
            .ToDictionary(s => StateKey(s.Name, s.Kind));

        var updatedStates = new List<BackupCheckState>();

        foreach (var def in _definitions)
        {
            updatedStates.Add(await CheckKindSafeAsync(def, BackupKind.Full, existingStates, ct));

            if (!string.IsNullOrWhiteSpace(def.DiffPattern))
                updatedStates.Add(await CheckKindSafeAsync(def, BackupKind.Diff, existingStates, ct));
        }

        foreach (var state in updatedStates)
            await repository.UpsertAsync(state, ct);

        var validKeys = updatedStates.Select(s => StateKey(s.Name, s.Kind)).ToHashSet();
        int removed = await repository.DeleteWhereKeyNotInAsync(validKeys, ct);
        if (removed > 0)
        {
            await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                $"Видалено {removed} застарілих запис(ів) — більше не знайдено у BackupChecks конфігурації."), ct);
        }

        await mediator.Publish(new BackupStatusUpdatedOccurred(updatedStates), ct);
    }

    /// <summary>Обгортка навколо CheckKindAsync — одна невдала перевірка не має зупиняти весь цикл.</summary>
    private async Task<BackupCheckState> CheckKindSafeAsync(
        BackupCheckDefinition def, BackupKind kind,
        Dictionary<string, BackupCheckState> existingStates, CancellationToken ct)
    {
        try
        {
            return await CheckKindAsync(def, kind, existingStates, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "BackupMonitorJob: неочікувана помилка для {Server}/{Kind}",
                def.Name, kind);

            return existingStates.TryGetValue(StateKey(def.Name, kind), out var fallback)
                ? fallback
                : new BackupCheckState { Name = def.Name, Host = def.Host, Kind = kind };
        }
    }

    private async Task<BackupCheckState> CheckKindAsync(
        BackupCheckDefinition def, BackupKind kind,
        Dictionary<string, BackupCheckState> existingStates, CancellationToken ct)
    {
        var key = StateKey(def.Name, kind);
        if (!existingStates.TryGetValue(key, out var state))
        {
            state = new BackupCheckState { Name = def.Name, Host = def.Host, Kind = kind };
        }

        var raw = await evaluator
            .EvaluateAsync(def, kind, state.History, ct)
            .ConfigureAwait(false);

        (bool shouldNotify, BackupOutcome previous, BackupOutcome current) transition = default;
        bool crossedUnknownThreshold;

        // ── Unknown-streak (окремо від анти-флапінгу підтвердженого стану) ──
        state.ConsecutiveUnknownCount = raw.Outcome == BackupOutcome.Unknown
            ? state.ConsecutiveUnknownCount + 1
            : 0;
        crossedUnknownThreshold = state.ConsecutiveUnknownCount == UnknownEscalationThreshold;

        bool neverConfirmedYet = state.LastConfirmedAt is null;

        // ── LastConfirmed* — будь-яка НЕ-Unknown відповідь, незалежно від анти-флапінгу ──
        if (raw.Outcome != BackupOutcome.Unknown)
        {
            state.LastConfirmedAt      = DateTimeOffset.Now;
            state.LastConfirmedOutcome = raw.Outcome;
            state.LastError            = null;
        }
        else
        {
            state.LastError = raw.ErrorMessage;
        }

        // ── History — лише коли Stage B реально знайшов файл ──
        if (raw.Sample is not null)
        {
            state.History.Add(raw.Sample);
            while (state.History.Count > MaxHistorySamples)
                state.History.RemoveAt(0);
        }

        // ── Перше підтвердження після рестарту/першого запуску —
        // підтверджуємо одразу, без очікування MinConsecutiveForAlert.
        // До першого підтвердженого результату немає "попереднього
        // стану", який анти-флапінг мав би захищати — очікування тут
        // лише затримує коректний перший статус (до BackupPollIntervalMinutes
        // × MinConsecutiveForAlert хвилин) без жодної компенсуючої користі.
        if (neverConfirmedYet && raw.Outcome != BackupOutcome.Unknown)
        {
            var firstPrevious = state.Outcome;
            state.Outcome             = raw.Outcome;
            state.ConsecutiveBadCount = 0;
            state.LastRawOutcome      = null;

            transition = (true, firstPrevious, state.Outcome);
        }
        // ── Анти-флапінг: підтверджений Outcome (той, що бачить UI/алерти) ──
        else if (raw.Outcome == state.Outcome)
        {
            state.ConsecutiveBadCount = 0;
            state.LastRawOutcome      = raw.Outcome;
        }
        else
        {
            // Рахуємо серію ОДНАКОВИХ "сирих" результатів поспіль, що
            // відрізняються від підтвердженого Outcome — а не будь-яку
            // зміну raw.Outcome взагалі (це і був баг: Ok→Stale→Missing
            // підтверджувало перехід, хоча жоден сирий результат не
            // повторився двічі поспіль).
            state.ConsecutiveBadCount = raw.Outcome == state.LastRawOutcome
                ? state.ConsecutiveBadCount + 1
                : 1;
            state.LastRawOutcome = raw.Outcome;

            if (state.ConsecutiveBadCount >= def.MinConsecutiveForAlert)
            {
                var previous = state.Outcome;
                state.Outcome             = raw.Outcome;
                state.ConsecutiveBadCount = 0;
                state.LastRawOutcome      = null;

                transition = (true, previous, state.Outcome);
            }
        }

        if (crossedUnknownThreshold)
        {
            await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                $"{def.Name} ({kind}): перевірка недоступна вже " +
                $"{UnknownEscalationThreshold} циклів поспіль."), ct);
        }

        if (transition.shouldNotify)
            await OnConfirmedTransitionAsync(def, kind, transition.previous, transition.current, ct);

        return state;
    }

    /// <summary>Викликається лише на ПІДТВЕРДЖЕНОМУ переході стану (після анти-флапінгу).</summary>
    private async Task OnConfirmedTransitionAsync(
        BackupCheckDefinition def, BackupKind kind,
        BackupOutcome previous, BackupOutcome current, CancellationToken ct)
    {
        string label = $"{def.Name} ({kind})";

        bool underMaintenance =
            _serverLookup.TryGetValue(def.Host, out var entry) &&
            maintenance.IsUnderMaintenance(entry.IP, entry.Group);

        if (current is BackupOutcome.Stale or BackupOutcome.Missing)
        {
            if (underMaintenance)
            {
                await mediator.Publish(AppLogEntryOccurred.Info(LogSource,
                    $"{label}: перехід у {current} придушено (активне Maintenance-вікно)."), ct);
                return;
            }

            await mediator.Publish(AppLogEntryOccurred.Error(LogSource,
                $"{label}: перехід у {current} (було {previous})."), ct);

            await mediator.Publish(new BackupTransitionOccurred(def.Name, kind, previous, current), ct);
            return;
        }

        if (current == BackupOutcome.SizeWarning)
        {
            await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                $"{label}: розмір бекапу відхилився більш ніж на {def.SizeWarningThresholdPct}% від середнього."), ct);
            return;
        }

        if (current == BackupOutcome.Unknown)
        {
            await mediator.Publish(AppLogEntryOccurred.Warning(LogSource,
                $"{label}: перевірка не відповідає (Unknown), було {previous}."), ct);
            return;
        }

        // current == Ok
        if (previous is BackupOutcome.Stale or BackupOutcome.Missing or BackupOutcome.SizeWarning)
        {
            await mediator.Publish(AppLogEntryOccurred.Success(LogSource,
                $"{label}: відновлено (було {previous})."), ct);
        }
    }

    private static string StateKey(string serverName, BackupKind kind) => $"{serverName}|{kind}";
}
