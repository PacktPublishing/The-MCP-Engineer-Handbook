using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// ---------------------------------------------------------------------------
// Step-up authorization client (Section 9.7.4, SEP-2350).
//
// The rule this sample exists for: when a 403 insufficient_scope challenge
// arrives, re-authorize for the UNION of the scopes you already hold and the
// scopes the challenge names — never just the new ones. Scope accumulation is
// the client's job; the Authorization Server and the MCP server both stay
// stateless about what you were granted before, so a re-authorization REPLACES
// your token. Drop a held scope from the request and it is gone.
//
// Run with --naive to see the anti-pattern: re-authorizing for only the
// missing scopes, which silently breaks the tools that used to work.
// ---------------------------------------------------------------------------

const string AuthServerUrl = "http://localhost:5301";
const string McpServerUrl = "http://localhost:5302";

var naive = args.Contains("--naive");
var http = new HttpClient();
var requestId = 0;

// The client's memory of what it has asked for so far — the state that makes
// the union rule work. Nobody else keeps this.
var heldScopes = new SortedSet<string>();
var accessToken = "";

Console.WriteLine(naive
    ? "== step-up client: --naive mode (re-authorize with only the new scopes) =="
    : "== step-up client: correct mode (re-authorize with the union) ==");
Console.WriteLine();

Console.WriteLine("[1] initial grant: requesting scope \"reports:read\"");
await AuthorizeAsync(["reports:read"]);

Console.WriteLine("[2] tools/call read_summary");
await CallToolAsync("read_summary");

Console.WriteLine("[3] tools/call export_full_data");
var (status, challenge, _) = await CallToolAsync("export_full_data");

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
    // THE UNION RULE: held ∪ challenged. The challenge is authoritative about
    // what this operation needs; only the client knows what it already had.
    var union = new SortedSet<string>(heldScopes.Union(challengeScopes));
    Console.WriteLine("[4] step-up: re-authorize for the UNION of held and challenged scopes");
    Console.WriteLine($"    held      = {string.Join(' ', heldScopes)}");
    Console.WriteLine($"    challenge = {string.Join(' ', challengeScopes)}");
    Console.WriteLine($"    union     = {string.Join(' ', union)}");
    await AuthorizeAsync(union);
}
else
{
    // THE ANTI-PATTERN: request only what was missing, assuming grants
    // accumulate somewhere. They don't — the new token replaces the old one.
    var missing = new SortedSet<string>(challengeScopes.Except(heldScopes));
    Console.WriteLine("[4] NAIVE step-up: re-authorize for only the scopes we were missing");
    Console.WriteLine($"    held      = {string.Join(' ', heldScopes)}");
    Console.WriteLine($"    challenge = {string.Join(' ', challengeScopes)}");
    Console.WriteLine($"    missing   = {string.Join(' ', missing)}   <- requesting ONLY these");
    await AuthorizeAsync(missing);
}

Console.WriteLine("[5] retry tools/call export_full_data");
await CallToolAsync("export_full_data");

Console.WriteLine("[6] tools/call read_summary with the new token");
var (readStatus, _, _) = await CallToolAsync("read_summary");

Console.WriteLine();
Console.WriteLine(naive
    ? "lesson: the naive re-authorization LOST reports:read — a tool that worked in [2] now fails."
    : "done: the union preserved reports:read across the step-up — nothing was lost.");
return 0;

// --- helpers ---------------------------------------------------------------

async Task AuthorizeAsync(IEnumerable<string> scopes)
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
    heldScopes = new SortedSet<string>(scope.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    Console.WriteLine($"    granted: scope=\"{json.GetProperty("scope")}\"");
}

async Task<(HttpStatusCode Status, string? Challenge, string? Text)> CallToolAsync(string toolName)
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
        var text = ExtractResultText(body, response.Content.Headers.ContentType?.MediaType);
        Console.WriteLine($"    200 OK -> {text}");
        return (response.StatusCode, null, text);
    }

    var challenge = response.Headers.TryGetValues("WWW-Authenticate", out var values)
        ? string.Join(", ", values)
        : null;
    Console.WriteLine($"    {(int)response.StatusCode} {response.StatusCode}");
    if (challenge is not null)
        Console.WriteLine($"    WWW-Authenticate: {challenge}");
    return (response.StatusCode, challenge, null);
}

static List<string> ParseChallengeScopes(string challenge)
{
    if (!challenge.Contains("error=\"insufficient_scope\"")) return [];
    var match = Regex.Match(challenge, "scope=\"([^\"]*)\"");
    return match.Success
        ? [.. match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)]
        : [];
}

static string ExtractResultText(string body, string? mediaType)
{
    // Streamable HTTP may answer application/json or a text/event-stream with
    // one message event; take the first data: line in the SSE case.
    var json = mediaType == "text/event-stream"
        ? body.Split('\n').First(l => l.StartsWith("data:"))["data:".Length..].Trim()
        : body;
    var root = JsonDocument.Parse(json).RootElement;
    return root.TryGetProperty("result", out var result)
        ? result.GetProperty("content")[0].GetProperty("text").GetString() ?? "(empty)"
        : $"JSON-RPC error: {root.GetProperty("error").GetProperty("message").GetString()}";
}
