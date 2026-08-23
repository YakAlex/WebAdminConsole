using AdminConsole.Infrastructure.Zabbix;

namespace AdminConsole.Tests.Zabbix;

public sealed class ZabbixPollerServiceTests
{
    [Fact]
    public void BuildWatchedSeverities_DefaultThresholdHigh_ReturnsHighAndDisaster()
    {
        var result = ZabbixPollerService.BuildWatchedSeverities(4);

        Assert.Equal([4, 5], result);
    }

    [Fact]
    public void BuildWatchedSeverities_ThresholdWarning_ReturnsWarningThroughDisaster()
    {
        var result = ZabbixPollerService.BuildWatchedSeverities(2);

        Assert.Equal([2, 3, 4, 5], result);
    }

    [Fact]
    public void BuildWatchedSeverities_ThresholdNotClassified_ReturnsAllSixLevels()
    {
        var result = ZabbixPollerService.BuildWatchedSeverities(0);

        Assert.Equal([0, 1, 2, 3, 4, 5], result);
    }

    [Fact]
    public void BuildWatchedSeverities_ThresholdAboveDisaster_ClampsToDisasterOnly()
    {
        var result = ZabbixPollerService.BuildWatchedSeverities(10);

        Assert.Equal([5], result);
    }

    [Fact]
    public void BuildWatchedSeverities_NegativeThreshold_ClampsToAllSixLevels()
    {
        var result = ZabbixPollerService.BuildWatchedSeverities(-3);

        Assert.Equal([0, 1, 2, 3, 4, 5], result);
    }
}
