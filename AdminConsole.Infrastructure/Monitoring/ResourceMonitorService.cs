using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AdminConsole.Domain.Events;
using AdminConsole.Domain.Models;
using AdminConsole.Infrastructure.Configuration;
using MediatR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdminConsole.Infrastructure.Monitoring;

/// <summary>
/// Polls CPU and RAM usage on a fixed interval using PerformanceCounters
/// (Windows-only) and publishes ResourceSnapshotUpdatedOccurred.
///
/// Threading: runs entirely on the thread pool via BackgroundService.
///
/// T4.9: BackgroundService, тісний цикл (кілька секунд) — без Hangfire.
/// На відміну від RemoteResourceService/RemoteManagementService (on-demand,
/// без циклу) — цей сервіс справді опитує ЛОКАЛЬНУ машину на фіксованому
/// інтервалі, тому єдиний з трійки T4.9, що реально є BackgroundService.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ResourceMonitorService(
    IMediator                        mediator,
    ILogger<ResourceMonitorService>  logger,
    IOptions<MonitoringSettings>     settings)
    : BackgroundService
{
    private readonly MonitoringSettings _settings = settings.Value;

    // PerformanceCounter is IDisposable — we own its lifetime.
    private PerformanceCounter? _cpuCounter;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // PerformanceCounter must be initialised on a background thread;
            // the first call to NextValue() always returns 0 (warm-up read),
            // so we do that here before the poll loop starts.
            _cpuCounter = new PerformanceCounter(
                "Processor", "% Processor Time", "_Total", readOnly: true);
            _cpuCounter.NextValue(); // discard warm-up reading

            // Give the counter 1 second to settle before first real read.
            await Task.Delay(1000, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialise CPU PerformanceCounter.");
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ResourceMonitorService started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = BuildSnapshot();
                await mediator.Publish(new ResourceSnapshotUpdatedOccurred(snapshot), stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "ResourceMonitorService: error building snapshot.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_settings.LocalResourcePollIntervalSeconds), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }

        logger.LogInformation("ResourceMonitorService stopped.");
    }

    public override void Dispose()
    {
        _cpuCounter?.Dispose();
        base.Dispose();
    }

    private ResourceSnapshot BuildSnapshot()
    {
        double cpuPercent = 0;

        if (_cpuCounter is not null)
        {
            try { cpuPercent = Math.Round(_cpuCounter.NextValue(), 1); }
            catch { cpuPercent = 0; }
        }

        GetMemoryInfo(out double usedGb, out double totalGb);

        double ramPercent = totalGb > 0
            ? Math.Round(usedGb / totalGb * 100.0, 1)
            : 0;

        return new ResourceSnapshot(
            CpuPercent:  cpuPercent,
            RamUsedGb:   Math.Round(usedGb,  2),
            RamTotalGb:  Math.Round(totalGb, 2),
            RamPercent:  ramPercent,
            Timestamp:   DateTimeOffset.Now);
    }

    /// <summary>
    /// Uses GlobalMemoryStatusEx (Win32) for an accurate RAM reading.
    /// PerformanceCounter for RAM is less reliable than the native call.
    /// </summary>
    private static void GetMemoryInfo(out double usedGb, out double totalGb)
    {
        usedGb  = 0;
        totalGb = 0;

        try
        {
            var status = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(status))
            {
                totalGb = status.ullTotalPhys / (1024.0 * 1024 * 1024);
                double availGb = status.ullAvailPhys / (1024.0 * 1024 * 1024);
                usedGb  = totalGb - availGb;
            }
        }
        catch { /* fallback: values stay 0 */ }
    }

    // ── Win32 interop ────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MEMORYSTATUSEX
    {
        public uint  dwLength       = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        public uint  dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);
}
