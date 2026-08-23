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
    public async Task<List<ZabbixProblem>> GetActiveProblemsAsync(
        string  url,
        string  auth,
        int[]   severities,
        CancellationToken ct = default)
    {
        var parameters = new JsonObject
        {
            ["output"]      = new JsonArray("eventid", "name", "severity", "clock", "hosts"),
            ["severities"]  = new JsonArray(severities.Select(s => JsonValue.Create(s)).ToArray()),
            ["suppressed"]  = false,
            ["recent"]      = false,
            ["selectHosts"] = new JsonArray("host", "name"),
            ["sortorder"]   = "DESC",
            ["limit"]       = 200
        };

        var request  = BuildRequest("problem.get", parameters, auth);
        var response = await PostAsync(url, request, ct, auth).ConfigureAwait(false);
        if (response is null) return [];

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
        if (resultArray is null) return [];

        var problems = new List<ZabbixProblem>(resultArray.Count);

        foreach (var node in resultArray)
        {
            if (node is null) continue;

            var eventId     = node["eventid"]?.GetValue<string>() ?? "0";
            var name        = node["name"]?.GetValue<string>()    ?? "(no description)";
            var severityStr = node["severity"]?.GetValue<string>() ?? "0";
            int.TryParse(severityStr, out int severityInt);
            var clockStr    = node["clock"]?.GetValue<string>()   ?? "0";

            var hostsArray  = node["hosts"]?.AsArray();
            var hostName    = hostsArray?.FirstOrDefault()?["name"]?.GetValue<string>()
                           ?? hostsArray?.FirstOrDefault()?["host"]?.GetValue<string>()
                           ?? "Unknown";

            var severity  = (ZabbixSeverity)Math.Clamp(severityInt, 0, 5);
            long.TryParse(clockStr, out long clockUnix);
            var startTime = DateTimeOffset.FromUnixTimeSeconds(clockUnix);
            var age       = FormatAge(DateTimeOffset.UtcNow - startTime);

            problems.Add(new ZabbixProblem(
                EventId:     eventId,
                HostName:    hostName,
                Description: name,
                Severity:    severity,
                StartTime:   startTime,
                AgeDisplay:  age));
        }

        return problems;
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
