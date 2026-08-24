using System.Net;
using System.Text;
using System.Text.Json;
using AdminConsole.Infrastructure.Zabbix;

namespace AdminConsole.Tests.Zabbix;

/// <summary>
/// Regression tests for two related bugs found 2026-08-24:
///
/// 1. "72 phantom Zabbix alerts" — GetActiveProblemsAsync included problems
///    belonging to hosts (or individual triggers) the admin has disabled.
///    Zabbix's own web UI hides these; AdminConsole did not.
///
/// 2. "Host: Unknown" for every problem — problem.get has never supported a
///    "hosts" sub-select at all (confirmed against Zabbix's own API
///    reference for 6.2 and 7.0). The only way to resolve a problem's host
///    is a second call to trigger.get (keyed by the problem's objectid),
///    which does support selectHosts.
///
/// Plus (2026-08-25): surfaces Acknowledged status and a HiddenCount so the
/// UI can show "+N hidden (disabled host/trigger)" instead of silently
/// dropping problems with no visible trace.
/// </summary>
public sealed class ZabbixApiClientTests
{
    /// <summary>Routes each JSON-RPC call to a canned response by method name — problem.get and trigger.get need different answers in the same test.</summary>
    private sealed class StubHandler(Func<string, string> responseFor) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var bodyText = await request.Content!.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(bodyText);
            var method = doc.RootElement.GetProperty("method").GetString()!;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseFor(method), Encoding.UTF8, "application/json")
            };
        }
    }

    private static ZabbixApiClient CreateClient(Func<string, string> responseFor) =>
        new(new HttpClient(new StubHandler(responseFor)));

    private const string OneProblemFromTrigger24116 = """
        { "jsonrpc": "2.0", "result": [
            { "eventid": "2560759", "objectid": "24116", "name": "Unavailable by ICMP ping", "severity": "4", "clock": "1768827878", "acknowledged": "1" }
        ], "id": 1 }
        """;

    private const string EnabledHostTrigger24116 = """
        { "jsonrpc": "2.0", "result": [
            { "triggerid": "24116", "status": "0", "hosts": [ { "host": "IP_reg1", "name": "IP_reg1", "status": "0" } ] }
        ], "id": 1 }
        """;

    [Fact]
    public async Task GetActiveProblemsAsync_ResolvesHostNameViaSeparateTriggerLookup()
    {
        string ResponseFor(string method) => method switch
        {
            "problem.get" => OneProblemFromTrigger24116,
            "trigger.get" => EnabledHostTrigger24116,
            _ => throw new InvalidOperationException($"Unexpected method: {method}")
        };

        var client = CreateClient(ResponseFor);

        var (problems, hiddenCount) = await client.GetActiveProblemsAsync("http://fake", "token", [0, 1, 2, 3, 4, 5]);

        var problem = Assert.Single(problems);
        Assert.Equal("IP_reg1", problem.HostName);
        Assert.Equal(0, hiddenCount);
    }

    [Fact]
    public async Task GetActiveProblemsAsync_ParsesAcknowledgedFlag()
    {
        string ResponseFor(string method) => method switch
        {
            "problem.get" => OneProblemFromTrigger24116,
            "trigger.get" => EnabledHostTrigger24116,
            _ => throw new InvalidOperationException($"Unexpected method: {method}")
        };

        var client = CreateClient(ResponseFor);

        var (problems, _) = await client.GetActiveProblemsAsync("http://fake", "token", [0, 1, 2, 3, 4, 5]);

        Assert.True(Assert.Single(problems).Acknowledged);
    }

    [Fact]
    public async Task GetActiveProblemsAsync_HostDisabled_ExcludesProblemAndCountsAsHidden()
    {
        string ResponseFor(string method) => method switch
        {
            "problem.get" => OneProblemFromTrigger24116,
            "trigger.get" => """
                { "jsonrpc": "2.0", "result": [
                    { "triggerid": "24116", "status": "0", "hosts": [ { "host": "MAILSVR", "name": "MAILSVR", "status": "1" } ] }
                ], "id": 1 }
                """,
            _ => throw new InvalidOperationException($"Unexpected method: {method}")
        };

        var client = CreateClient(ResponseFor);

        var (problems, hiddenCount) = await client.GetActiveProblemsAsync("http://fake", "token", [0, 1, 2, 3, 4, 5]);

        Assert.Empty(problems);
        Assert.Equal(1, hiddenCount);
    }

    [Fact]
    public async Task GetActiveProblemsAsync_TriggerItselfIsDisabled_ExcludesProblemAndCountsAsHidden()
    {
        // Regression test: a host can stay fully enabled while an admin
        // disables one specific trigger on it (Tsvr3's "IPBAN3 is not
        // running", Unifi_ap2's "High error rate" — both confirmed live).
        string ResponseFor(string method) => method switch
        {
            "problem.get" => OneProblemFromTrigger24116,
            "trigger.get" => """
                { "jsonrpc": "2.0", "result": [
                    { "triggerid": "24116", "status": "1", "hosts": [ { "host": "Tsvr3", "name": "Tsvr3", "status": "0" } ] }
                ], "id": 1 }
                """,
            _ => throw new InvalidOperationException($"Unexpected method: {method}")
        };

        var client = CreateClient(ResponseFor);

        var (problems, hiddenCount) = await client.GetActiveProblemsAsync("http://fake", "token", [0, 1, 2, 3, 4, 5]);

        Assert.Empty(problems);
        Assert.Equal(1, hiddenCount);
    }

    [Fact]
    public async Task GetActiveProblemsAsync_TriggerLookupHasNoHostData_StillIncludesProblemAsUnknownAndNotHidden()
    {
        // Fail OPEN, not closed: if the trigger lookup can't resolve a host
        // at all, we must not silently hide what might be a real, active
        // problem — and it must not be counted as "hidden" either, since we
        // don't actually know it's disabled.
        string ResponseFor(string method) => method switch
        {
            "problem.get" => OneProblemFromTrigger24116,
            "trigger.get" => """{ "jsonrpc": "2.0", "result": [], "id": 1 }""",
            _ => throw new InvalidOperationException($"Unexpected method: {method}")
        };

        var client = CreateClient(ResponseFor);

        var (problems, hiddenCount) = await client.GetActiveProblemsAsync("http://fake", "token", [0, 1, 2, 3, 4, 5]);

        var problem = Assert.Single(problems);
        Assert.Equal("Unknown", problem.HostName);
        Assert.Equal(0, hiddenCount);
    }

    [Fact]
    public async Task GetActiveProblemsAsync_NoProblems_ReturnsEmptyWithoutCallingTriggerGet()
    {
        string ResponseFor(string method) => method switch
        {
            "problem.get" => """{ "jsonrpc": "2.0", "result": [], "id": 1 }""",
            "trigger.get" => throw new InvalidOperationException(
                "trigger.get should not be called when there are no problems to resolve hosts for."),
            _ => throw new InvalidOperationException($"Unexpected method: {method}")
        };

        var client = CreateClient(ResponseFor);

        var (problems, hiddenCount) = await client.GetActiveProblemsAsync("http://fake", "token", [0, 1, 2, 3, 4, 5]);

        Assert.Empty(problems);
        Assert.Equal(0, hiddenCount);
    }

    [Fact]
    public async Task GetActiveProblemsAsync_MultipleHiddenProblems_CountsEachOne()
    {
        string ResponseFor(string method) => method switch
        {
            "problem.get" => """
                { "jsonrpc": "2.0", "result": [
                    { "eventid": "1", "objectid": "100", "name": "P1", "severity": "3", "clock": "1", "acknowledged": "0" },
                    { "eventid": "2", "objectid": "200", "name": "P2", "severity": "3", "clock": "1", "acknowledged": "0" },
                    { "eventid": "3", "objectid": "300", "name": "P3", "severity": "3", "clock": "1", "acknowledged": "0" }
                ], "id": 1 }
                """,
            "trigger.get" => """
                { "jsonrpc": "2.0", "result": [
                    { "triggerid": "100", "status": "0", "hosts": [ { "host": "A", "name": "A", "status": "1" } ] },
                    { "triggerid": "200", "status": "0", "hosts": [ { "host": "B", "name": "B", "status": "0" } ] },
                    { "triggerid": "300", "status": "1", "hosts": [ { "host": "C", "name": "C", "status": "0" } ] }
                ], "id": 1 }
                """,
            _ => throw new InvalidOperationException($"Unexpected method: {method}")
        };

        var client = CreateClient(ResponseFor);

        var (problems, hiddenCount) = await client.GetActiveProblemsAsync("http://fake", "token", [0, 1, 2, 3, 4, 5]);

        Assert.Equal("B", Assert.Single(problems).HostName);
        Assert.Equal(2, hiddenCount);
    }
}
