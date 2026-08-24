using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AdminConsole.Domain.Models;

namespace AdminConsole.Infrastructure.Zabbix;

/// <summary>
/// Thin JSON-RPC 2.0 client for the Zabbix API. API-token auth only
/// (Zabbix 5.4+) — username/password session-token auth was retired
/// 2026-08-23 (never reachable from the UI; API tokens are the modern,
/// secure standard).
///
/// All methods are async and allocate minimally.
/// This class is stateless except for the injected HttpClient.
///
/// T4.10: carried over unchanged.
/// </summary>
public sealed class ZabbixApiClient(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // ── Connection test ───────────────────────────────────────────────────────

    /// <summary>
    /// Checks Zabbix API availability and token validity.
    /// Uses the lightweight apiinfo.version method — no auth required to get the
    /// version, but the next step (problem.get) verifies the token.
    /// Returns (true, "Zabbix 6.4.0", null) or (false, null, "error message").
    /// </summary>
    public async Task<(bool Success, string? Version, string? Error)> TestConnectionAsync(
        string url, string token, CancellationToken ct = default)
    {
        // Step 1 — check API availability (no auth)
        try
        {
            var versionRequest = BuildRequest("apiinfo.version", new JsonObject());
            var versionResponse = await PostAsync(url, versionRequest, ct)
                .ConfigureAwait(false);

            var version = versionResponse?["result"]?.GetValue<string>();
            if (version is null)
                return (false, null, "Zabbix API did not respond correctly");

            // Step 2 — verify the token via user.checkAuthentication.
            // Zabbix 6.0+: auth is passed ONLY via the Bearer header or the
            // "token" field in params — NOT via the "auth" field in the JSON-RPC body.
            // BuildRequest without the third argument = doesn't add "auth" to the body.
            var testRequest = BuildRequest("user.checkAuthentication", new JsonObject
            {
                ["token"] = token
            });

            var testResponse = await PostAsync(url, testRequest, ct, token)
                .ConfigureAwait(false);

            var errorNode = testResponse?["error"];
            if (errorNode is not null)
            {
                var errorData = errorNode["data"]?.GetValue<string>()
                    ?? errorNode["message"]?.GetValue<string>()
                    ?? "Unknown error";
                return (false, version, $"Token is invalid: {errorData}");
            }

            return (true, version, null);
        }
        catch (OperationCanceledException)
        {
            return (false, null, "Check cancelled");
        }
        catch (HttpRequestException ex)
        {
            return (false, null, $"Failed to connect: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, null, $"Error: {ex.Message}");
        }
    }

    // ── Problem fetch ─────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches active problems filtered to the given severities.
    /// auth: API token string, set in the Authorization header (Zabbix 5.4+).
    /// </summary>
    public async Task<(List<ZabbixProblem> Problems, int HiddenCount)> GetActiveProblemsAsync(
        string  url,
        string  auth,
        int[]   severities,
        CancellationToken ct = default)
    {
        // Audit fix (2026-08-24): problem.get has never supported a "selectHosts"
        // sub-select — confirmed against Zabbix's own API reference for both
        // this deployment's version (6.2) and the current one. Requesting it
        // was silently ignored by the server, which is why every problem came
        // back with no host information at all ("Host: Unknown" for 100% of
        // results, including genuinely active ones). "objectid" (the trigger
        // that raised the problem) is the only link back to a host — it's
        // resolved via a second trigger.get call below, which DOES support
        // selectHosts.
        var parameters = new JsonObject
        {
            ["output"]     = new JsonArray("eventid", "objectid", "name", "severity", "clock", "acknowledged"),
            ["severities"] = new JsonArray(severities.Select(s => JsonValue.Create(s)).ToArray()),
            ["suppressed"] = false,
            ["recent"]     = false,
            ["sortorder"]  = "DESC",
            ["limit"]      = 200
        };

        var request  = BuildRequest("problem.get", parameters, auth);
        var response = await PostAsync(url, request, ct, auth).ConfigureAwait(false);
        if (response is null) return ([], 0);

        // ── Check whether Zabbix returned an error in the response body ──────────
        // Zabbix returns HTTP 200 even on authentication errors,
        // but the "error" field is present while "result" is absent.
        var errorNode = response["error"];
        if (errorNode is not null)
        {
            int    code = errorNode["code"]?.GetValue<int>() ?? 0;
            string data = errorNode["data"]?.GetValue<string>() ?? string.Empty;

            // Codes meaning an invalid token / no access:
            // -32602 = Invalid params / No permissions
            // -32500 = Application error (usually auth)
            bool isAuthError = code is -32602 or -32500
                || data.Contains("No permissions", StringComparison.OrdinalIgnoreCase)
                || data.Contains("re-login", StringComparison.OrdinalIgnoreCase)
                || data.Contains("Not authorised", StringComparison.OrdinalIgnoreCase);

            if (isAuthError)
                throw new ZabbixAuthException(
                    $"Zabbix rejected the token (code={code}): {data}");

            throw new InvalidOperationException(
                $"Zabbix API error (code={code}): {data}");
        }

        var resultArray = response["result"]?.AsArray();
        if (resultArray is null || resultArray.Count == 0) return ([], 0);

        var rawProblems = new List<(string EventId, string ObjectId, string Name, int Severity, long Clock, bool Acknowledged)>(resultArray.Count);
        var triggerIds  = new HashSet<string>();

        foreach (var node in resultArray)
        {
            if (node is null) continue;

            var eventId     = node["eventid"]?.GetValue<string>()  ?? "0";
            var objectId    = node["objectid"]?.GetValue<string>() ?? "0";
            var name        = node["name"]?.GetValue<string>()     ?? "(no description)";
            var severityStr = node["severity"]?.GetValue<string>() ?? "0";
            int.TryParse(severityStr, out int severityInt);
            var clockStr    = node["clock"]?.GetValue<string>()    ?? "0";
            long.TryParse(clockStr, out long clockUnix);
            var acknowledged = node["acknowledged"]?.GetValue<string>() == "1";

            triggerIds.Add(objectId);
            rawProblems.Add((eventId, objectId, name, severityInt, clockUnix, acknowledged));
        }

        var hostsByTrigger = await GetHostsByTriggerAsync(url, auth, triggerIds, ct).ConfigureAwait(false);

        var problems    = new List<ZabbixProblem>(rawProblems.Count);
        var hiddenCount = 0;

        foreach (var raw in rawProblems)
        {
            hostsByTrigger.TryGetValue(raw.ObjectId, out var host);

            // Zabbix keeps a problem row "unresolved" forever once its host OR
            // its specific trigger is disabled — either way, nothing is
            // evaluating it anymore, so it never gets a chance to recover and
            // close. The native Zabbix web UI hides both cases by default; we
            // do the same (audit finding, 2026-08-24: a disabled host's
            // years-old stuck problems were flooding this list — and, once
            // that was fixed, three more turned out to belong to fully
            // enabled hosts whose *trigger* had been individually disabled,
            // e.g. Tsvr3's "IPBAN3 is not running" — same underlying cause,
            // one level down).
            //
            // Fail OPEN, not closed: only skip when Zabbix positively confirms
            // status "1" (disabled). If the trigger lookup can't resolve a
            // host at all, treat it as "status unknown" and keep showing the
            // problem rather than silently hiding a possibly-real one — and
            // don't count it toward hiddenCount either, since we don't
            // actually know it's disabled (audit fix 2026-08-25: hiddenCount
            // exists so the UI can say "+N hidden" instead of silently
            // dropping problems with no visible trace — a count that's wrong
            // in the "unknown" case would just move the confusion elsewhere).
            if (host.HostStatus == "1" || host.TriggerStatus == "1")
            {
                hiddenCount++;
                continue;
            }

            var severity  = (ZabbixSeverity)Math.Clamp(raw.Severity, 0, 5);
            var startTime = DateTimeOffset.FromUnixTimeSeconds(raw.Clock);
            var age       = FormatAge(DateTimeOffset.UtcNow - startTime);

            problems.Add(new ZabbixProblem(
                EventId:      raw.EventId,
                HostName:     host.Name ?? "Unknown",
                Description:  raw.Name,
                Severity:     severity,
                StartTime:    startTime,
                AgeDisplay:   age,
                Acknowledged: raw.Acknowledged));
        }

        return (problems, hiddenCount);
    }

    /// <summary>
    /// Resolves host name/status for each problem's triggering trigger.
    /// problem.get carries no host information of its own (see the comment
    /// above GetActiveProblemsAsync's request) — trigger.get is the only
    /// Zabbix API method that supports selectHosts for this lookup.
    /// </summary>
    private async Task<Dictionary<string, (string Name, string? HostStatus, string? TriggerStatus)>> GetHostsByTriggerAsync(
        string url, string auth, IReadOnlySet<string> triggerIds, CancellationToken ct)
    {
        var map = new Dictionary<string, (string Name, string? HostStatus, string? TriggerStatus)>();
        if (triggerIds.Count == 0) return map;

        var parameters = new JsonObject
        {
            ["output"]      = new JsonArray("triggerid", "status"),
            ["triggerids"]  = new JsonArray(triggerIds.Select(id => JsonValue.Create(id)).ToArray()),
            ["selectHosts"] = new JsonArray("host", "name", "status")
        };

        var request  = BuildRequest("trigger.get", parameters, auth);
        var response = await PostAsync(url, request, ct, auth).ConfigureAwait(false);

        var resultArray = response?["result"]?.AsArray();
        if (resultArray is null) return map;

        foreach (var node in resultArray)
        {
            var triggerId     = node?["triggerid"]?.GetValue<string>();
            if (triggerId is null) continue;

            var triggerStatus = node!["status"]?.GetValue<string>();
            var firstHost     = node["hosts"]?.AsArray()?.FirstOrDefault();

            var name       = firstHost?["name"]?.GetValue<string>() ?? firstHost?["host"]?.GetValue<string>() ?? "Unknown";
            var hostStatus = firstHost?["status"]?.GetValue<string>();
            map[triggerId] = (name, hostStatus, triggerStatus);
        }

        return map;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static JsonObject BuildRequest(
        string method,
        JsonObject parameters,
        string? auth = null)
    {
        var obj = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"]  = method,
            ["params"]  = parameters,
            ["id"]      = 1
        };

        if (auth is not null)
            obj["auth"] = auth;

        return obj;
    }

    private async Task<JsonNode?> PostAsync(
        string url,
        JsonObject body,
        CancellationToken ct,
        string? bearerToken = null)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };
        if (bearerToken is not null)
            msg.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);

        var response = await http.SendAsync(msg, ct).ConfigureAwait(false);

        var json = await response.Content
            .ReadAsStringAsync(ct)
            .ConfigureAwait(false);

        if (response.StatusCode is
            System.Net.HttpStatusCode.Unauthorized or
            System.Net.HttpStatusCode.Forbidden)
        {
            throw new ZabbixAuthException(
                $"HTTP {(int)response.StatusCode}: {json}");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode}: {json}");
        }

        return JsonNode.Parse(json);
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalDays >= 1)
            return $"{(int)age.TotalDays}d {age.Hours}h";
        if (age.TotalHours >= 1)
            return $"{(int)age.TotalHours}h {age.Minutes}m";
        if (age.TotalMinutes >= 1)
            return $"{(int)age.TotalMinutes}m";
        return "< 1m";
    }
}

/// <summary>
/// Thrown by ZabbixApiClient when Zabbix returns an authentication error
/// in the response body (HTTP 200 + error field) or HTTP 401/403.
/// Caught in ZabbixPollerService.PollAsync — logs a warning and asks the
/// admin to update the token in Settings (only a human can fix an invalid
/// API token; the poller has no credentials of its own to fall back to).
/// Moved out of a nested class in ZabbixPollerService into its own file —
/// avoids the circular dependency of "the client throws an exception defined in the poller".
/// </summary>
public sealed class ZabbixAuthException(string message) : Exception(message);
