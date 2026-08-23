using AdminConsole.Infrastructure.Monitoring;

namespace AdminConsole.Tests.Monitoring;

public sealed class OnDemandSnapshotThrottleTests
{
    [Fact]
    public async Task GetOrRunAsync_WithinWindowOfAPriorCall_ReturnsCachedResultWithoutInvokingAction()
    {
        var throttle = new OnDemandSnapshotThrottle<int>(TimeSpan.FromMinutes(5));
        int calls = 0;
        Task<int> Action(CancellationToken ct) { calls++; return Task.FromResult(1); }

        await throttle.GetOrRunAsync(Action, CancellationToken.None);
        var second = await throttle.GetOrRunAsync(Action, CancellationToken.None);

        Assert.Equal(1, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SetResultAsync_ThenGetOrRunAsyncWithinWindow_ReturnsTheInjectedResultWithoutInvokingAction()
    {
        // A background poller (e.g. ZabbixPollerService.PollAsync, run out-of-cycle
        // after a Settings change) fetches fresh data through its OWN code path,
        // bypassing GetOrRunAsync entirely. SetResultAsync is how it hands that
        // fresh result to the on-demand cache, so the NEXT page load/REST call
        // doesn't serve a stale pre-change snapshot for the rest of the window.
        var throttle = new OnDemandSnapshotThrottle<string>(TimeSpan.FromMinutes(5));
        int calls = 0;
        Task<string> Action(CancellationToken ct) { calls++; return Task.FromResult("from-action"); }

        await throttle.SetResultAsync("from-background-poll", CancellationToken.None);
        var result = await throttle.GetOrRunAsync(Action, CancellationToken.None);

        Assert.Equal("from-background-poll", result);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task SetResultAsync_ThenGetOrRunAsyncAfterTheWindowExpires_InvokesActionAgain()
    {
        var throttle = new OnDemandSnapshotThrottle<string>(TimeSpan.FromMilliseconds(20));

        await throttle.SetResultAsync("stale", CancellationToken.None);
        await Task.Delay(60);

        var result = await throttle.GetOrRunAsync(_ => Task.FromResult("fresh"), CancellationToken.None);

        Assert.Equal("fresh", result);
    }
}
