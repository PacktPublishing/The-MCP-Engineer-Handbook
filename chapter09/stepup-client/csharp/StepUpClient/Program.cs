using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// ---------------------------------------------------------------------------
// Step-up authorization client (Section 9.7.4, SEP-2350).
//
// The rule this sample exists for: when a 403 insufficient_scope challenge
// arrives, re-authorize for the UNION of the scopes you have already
// requested and the scopes the challenge names — never just the new ones.
// Scope accumulation is the client's job; the Authorization Server and the
// MCP server both stay stateless about what you were granted before, so a
// re-authorization REPLACES your token. Drop a held scope from the request
// and it is gone.
//
// Run with --naive to see the anti-pattern: re-authorizing for only the
// missing scopes, which silently breaks the tools that used to work.
// ---------------------------------------------------------------------------

// Demo shortcut: the AS is hardcoded. A real client follows the challenge's
// resource_metadata to the server's RFC 9728 document, and from there to the
// AS's own metadata (Section 9.2.2).
const string AuthServerUrl = "http://localhost:5301";
const string McpServerUrl = "http://localhost:5302";

var naive = args.Contains("--naive");
var http = new HttpClient();
var requestId = 0;

// The client's memory of what it has asked for so far — the state that makes
// the union rule work (the spec's "previously requested scope set"). Nobody
// else keeps this.
var requestedScopes = new SortedSet<string>();
// What the AS actually granted, from the token response. It can be narrower
// than the request; the user may have declined part of it.
var grantedScopes = new SortedSet<string>();
var accessToken = "";

Console.WriteLine(naive
    ? "== step-up client: --naive mode (re-authorize with only the new scopes) =="
    : "== step-up client: correct mode (re-authorize with the union) ==");
Console.WriteLine();

Console.WriteLine("[1] initial grant: requesting scope \"reports:read\"");
if (!await AuthorizeAsync(["reports:read"])) return 1;

Console.WriteLine("[2] tools/call read_summary");
if (!(await CallToolAsync("read_summary")).Ok)
{
    Console.WriteLine("    expected read_summary to work with reports:read — aborting.");
    return 1;
}

Console.WriteLine("[3] tools/call export_full_data");
var (_, status, challenge) = await CallToolAsync("export_full_data");

if (status != HttpStatusCode.Forbidden || challenge is null)
{
    Console.WriteLine("    expected a 403 challenge and did not get one — aborting.");
    return 1;
}

// Parse the RFC 6750 challenge: Bearer error="insufficient_scope", scope="...".
// A production client also bounds its step-up attempts (a couple of tries,
// then surface the failure to a human — Section 9.7.4); this transcript needs
// exactly one.
var challengeScopes = ParseChallengeScopes(challenge);
if (challengeScopes.Count == 0)
{
    Console.WriteLine("    403 without a parseable insufficient_scope challenge — aborting.");
    return 1;
}

if (!naive)
{
    // THE UNION RULE: requested ∪ challenged. The challenge is authoritative
    // about what this operation needs; only the client knows what it asked for.
    var union = new SortedSet<string>(requestedScopes.Union(challengeScopes));
    Console.WriteLine("[4] step-up: re-authorize for the UNION of requested and challenged scopes");
    Console.WriteLine($"    requested = {string.Join(' ', requestedScopes)}");
    Console.WriteLine($"    challenge = {string.Join(' ', challengeScopes)}");
    Console.WriteLine($"    union     = {string.Join(' ', union)}");
    if (!await AuthorizeAsync(union)) return 1;
}
else
{
    // THE ANTI-PATTERN: request only what was missing, assuming grants
    // accumulate somewhere. They don't — the new token replaces the old one.
    var missing = new SortedSet<string>(challengeScopes.Except(grantedScopes));
    Console.WriteLine("[4] NAIVE step-up: re-authorize for only the scopes we were missing");
    Console.WriteLine($"    granted   = {string.Join(' ', grantedScopes)}");
    Console.WriteLine($"    challenge = {string.Join(' ', challengeScopes)}");
    Console.WriteLine($"    missing   = {string.Join(' ', missing)}   <- requesting ONLY these");
    if (!await AuthorizeAsync(missing)) return 1;
}

Console.WriteLine("[5] retry tools/call export_full_data");
var export = await CallToolAsync("export_full_data");

Console.WriteLine("[6] tools/call read_summary with the new token");
var read = await CallToolAsync("read_summary");

Console.WriteLine();
if (!naive)
{
    if (export.Ok && read.Ok)
    {
        Console.WriteLine("done: the union preserved reports:read across the step-up — nothing was lost.");
        return 0;
    }
    Console.WriteLine($"FAILED: after the step-up, export_full_data {Outcome(export)} and " +
                      $"read_summary {Outcome(read)}.");
    return 1;
}

// The naive run succeeds only if it demonstrates the damage: both calls
// rejected with an insufficient_scope challenge.
if (IsScopeChallenge(export) && IsScopeChallenge(read))
{
    Console.WriteLine("lesson: the naive re-authorization LOST reports:read — a tool that worked in [2] now fails.");
    return 0;
}
Console.WriteLine($"unexpected: the naive step-up should have broken both calls, but export_full_data " +
                  $"{Outcome(export)} and read_summary {Outcome(read)}.");
return 1;

// --- helpers ---------------------------------------------------------------

// Requests a token for exactly these scopes, replacing the current one.
// Returns false if the AS granted less than was asked for: retrying the
// operation would only fail again, so the client stops and says why.
async Task<bool> AuthorizeAsync(IEnumerable<string> scopes)
{
    var scope = string.Join(' ', scopes);
    var response = await http.PostAsync($"{AuthServerUrl}/token", new FormUrlEncodedContent(
        new Dictionary<string, string>
        {
            ["grant_type"] = "urn:stepup-demo:pre-consented",
            ["scope"] = scope,
            ["resource"] = McpServerUrl, // RFC 8707: bind the token to the MCP server
        }));
    response.EnsureSuccessStatusCode();

    var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    accessToken = json.GetProperty("access_token").GetString()!;
    requestedScopes = new SortedSet<string>(scope.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    // RFC 6749 Section 5.1: the response's scope is OPTIONAL when it matches
    // the request, and REQUIRED when the AS granted something different.
    var grantedScope = json.TryGetProperty("scope", out var granted) ? granted.GetString() : null;
    grantedScopes = grantedScope is null
        ? new SortedSet<string>(requestedScopes)
        : new SortedSet<string>(grantedScope.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    Console.WriteLine($"    granted: scope=\"{string.Join(' ', grantedScopes)}\"");

    var withheld = requestedScopes.Except(grantedScopes).ToList();
    if (withheld.Count == 0) return true;
    Console.WriteLine($"    the AS withheld {string.Join(' ', withheld)} — the grant is narrower " +
                      "than the request, so a retry cannot succeed. Surface this to the user.");
    return false;
}

// Ok only for HTTP 200 carrying a JSON-RPC result that is not a tool error.
async Task<(bool Ok, HttpStatusCode Status, string? Challenge)> CallToolAsync(string toolName)
{
    // Stateless 2026-07-28 tools/call over Streamable HTTP — no handshake needed.
    var request = new HttpRequestMessage(HttpMethod.Post, McpServerUrl);
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
    request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
    request.Headers.Add("Mcp-Method", "tools/call");
    request.Headers.Add("Mcp-Name", toolName);
    request.Content = new StringContent(JsonSerializer.Serialize(new
    {
        jsonrpc = "2.0",
        id = ++requestId,
        method = "tools/call",
        @params = new Dictionary<string, object>
        {
            ["name"] = toolName,
            ["arguments"] = new { },
            ["_meta"] = new Dictionary<string, object>
            {
                ["io.modelcontextprotocol/clientInfo"] = new { name = "stepup-client", version = "1.0.0" },
                ["io.modelcontextprotocol/clientCapabilities"] = new { },
                ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
            },
        },
    }), Encoding.UTF8, "application/json");

    var response = await http.SendAsync(request);
    var body = await response.Content.ReadAsStringAsync();

    if (response.StatusCode == HttpStatusCode.OK)
    {
        var (ok, text) = ReadResult(body, response.Content.Headers.ContentType?.MediaType);
        Console.WriteLine($"    200 OK -> {text}");
        return (ok, response.StatusCode, null);
    }

    var challenge = response.Headers.TryGetValues("WWW-Authenticate", out var values)
        ? string.Join(", ", values)
        : null;
    Console.WriteLine($"    {(int)response.StatusCode} {response.StatusCode}");
    if (challenge is not null)
        Console.WriteLine($"    WWW-Authenticate: {challenge}");
    else if (body.Length > 0)
        Console.WriteLine($"    {body}");
    return (false, response.StatusCode, challenge);
}

static bool IsScopeChallenge((bool Ok, HttpStatusCode Status, string? Challenge) call)
    => call.Status == HttpStatusCode.Forbidden
       && call.Challenge is not null
       && ParseChallengeScopes(call.Challenge).Count > 0;

static string Outcome((bool Ok, HttpStatusCode Status, string? Challenge) call)
    => call.Ok ? "succeeded"
        : call.Status == HttpStatusCode.OK ? "returned an error"
        : $"failed with {(int)call.Status}";

static List<string> ParseChallengeScopes(string challenge)
{
    if (!challenge.Contains("error=\"insufficient_scope\"")) return [];
    var match = Regex.Match(challenge, "scope=\"([^\"]*)\"");
    return match.Success
        ? [.. match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)]
        : [];
}

static (bool Ok, string Text) ReadResult(string body, string? mediaType)
{
    // Streamable HTTP may answer application/json or a text/event-stream with
    // one message event; take the first data: line in the SSE case.
    var json = mediaType == "text/event-stream"
        ? body.Split('\n').FirstOrDefault(l => l.StartsWith("data:"))?["data:".Length..].Trim()
        : body;
    if (string.IsNullOrEmpty(json)) return (false, "(no JSON-RPC message in the response)");

    // A 200 is only the transport succeeding. The JSON-RPC layer can still
    // carry an error, and the tool can still report isError: true.
    var root = JsonDocument.Parse(json).RootElement;
    if (root.TryGetProperty("error", out var error))
        return (false, $"JSON-RPC error {error.GetProperty("code")}: " +
                       error.GetProperty("message").GetString());
    if (!root.TryGetProperty("result", out var result))
        return (false, "(neither result nor error)");

    var text = result.TryGetProperty("content", out var content)
        ? content.EnumerateArray()
            .Where(b => b.TryGetProperty("type", out var t) && t.GetString() == "text")
            .Select(b => b.GetProperty("text").GetString())
            .FirstOrDefault() ?? "(no text content)"
        : "(no content)";
    var isError = result.TryGetProperty("isError", out var flag) && flag.ValueKind == JsonValueKind.True;
    return isError ? (false, $"tool error: {text}") : (true, text);
}
