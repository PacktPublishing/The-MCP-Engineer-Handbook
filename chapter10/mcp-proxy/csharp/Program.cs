// mcp-proxy — a minimal MCP-aware reverse proxy. Plain ASP.NET Core, no MCP
// SDK: the routable headers of the 2026-07-28 revision (Mcp-Method, Mcp-Name)
// carry everything the proxy routes on, so JSON-RPC bodies stream through
// unparsed. (Section 10.4.3)
//
// That holds only for the 2026-07-28 revision, where the upstream must reject a
// request whose headers disagree with its body. Legacy-era requests carry no
// verified headers, so a body-only tools/call would slip past the routing table
// and the budget. This proxy admits 2026-07-28 traffic only; legacy clients go
// through a compatibility shim (Section 10.3.1), not through this door.
using System.Collections.Concurrent;
using System.Text.Json;

const string SupportedVersion = "2026-07-28";

// Routing table: tool-name prefix -> upstream pool. Everything else — other
// tools, discovery, every non-tools/call method — takes the default upstream.
const string DefaultUpstream = "http://localhost:5101";
(string Prefix, string Upstream)[] routes = [("admin_", "http://localhost:5102")];

// Budget-style rate limit (Section 10.5.1): tool calls per principal per
// minute. Discovery is free — an agent reading tools/list is using the
// server well, not spending its budget.
const int BudgetPerMinute = 5;
var budgets = new ConcurrentDictionary<string, (long Window, int Used)>();

var http = new HttpClient();
var app = WebApplication.CreateBuilder(args).Build();

app.Map("/{**path}", async (HttpContext ctx) =>
{
    // Version gate first: no routing or budget decision on unverified headers.
    var version = ctx.Request.Headers["MCP-Protocol-Version"].FirstOrDefault();
    if (version != SupportedVersion)
    {
        app.Logger.LogInformation("rejected: MCP-Protocol-Version={Version}", version ?? "(none)");
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new
        {
            jsonrpc = "2.0",
            id = await ReadIdAsync(ctx),
            error = new
            {
                code = -32022, // UnsupportedProtocolVersionError
                message = "Unsupported protocol version",
                data = new { supported = new[] { SupportedVersion }, requested = version ?? "" },
            },
        });
        return;
    }

    var method = ctx.Request.Headers["Mcp-Method"].FirstOrDefault() ?? "";
    var name = ctx.Request.Headers["Mcp-Name"].FirstOrDefault() ?? "";
    // Demo stand-in for an identity your authenticating front end resolves and
    // asserts. Never key or log on a raw credential: Authorization is forwarded
    // upstream untouched, and never read here.
    var principal = ctx.Request.Headers["X-Principal"].FirstOrDefault() ?? "anonymous";

    // Route on headers alone. Only tool calls consult the prefix table.
    var upstream = method == "tools/call"
        ? routes.FirstOrDefault(r => name.StartsWith(r.Prefix, StringComparison.Ordinal))
              .Upstream ?? DefaultUpstream
        : DefaultUpstream;

    // Spend the budget. Fixed one-minute windows keyed on the wall clock.
    var remaining = BudgetPerMinute;
    if (method == "tools/call")
    {
        var window = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        var (_, used) = budgets.AddOrUpdate(principal,
            _ => (window, 1),
            (_, b) => b.Window == window ? (b.Window, b.Used + 1) : (window, 1));
        remaining = BudgetPerMinute - used;
    }

    // One line per request: observability at the seam.
    app.Logger.LogInformation(
        "{Method} {Name} principal={Principal} upstream={Upstream} budget={Budget}",
        method.Length > 0 ? method : "(no Mcp-Method)", name.Length > 0 ? name : "-",
        principal, remaining < 0 ? "(rejected)" : upstream, Math.Max(remaining, 0));

    if (remaining < 0)
    {
        var retryAfter = 60 - (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 60);
        ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;   // for SDKs
        ctx.Response.Headers.RetryAfter = retryAfter.ToString();
        await ctx.Response.WriteAsJsonAsync(new                           // for the model
        {
            jsonrpc = "2.0",
            id = await ReadIdAsync(ctx),
            error = new
            {
                code = -32000,
                message = $"Rate limit reached: this principal's budget of " +
                    $"{BudgetPerMinute} tool calls per minute is spent. Retry after " +
                    $"{retryAfter} seconds. Results you already received are still " +
                    "valid — do not repeat completed calls, and combine the remaining " +
                    "work into fewer calls once the window resets.",
                data = new { retryAfterSeconds = retryAfter },
            },
        });
        return;
    }

    // Forward: body streamed through untouched, headers copied through.
    var req = new HttpRequestMessage(new(ctx.Request.Method),
        upstream + ctx.Request.Path + ctx.Request.QueryString);
    if (ctx.Request.ContentLength is > 0 || ctx.Request.Headers.TransferEncoding.Count > 0)
        req.Content = new StreamContent(ctx.Request.Body);
    foreach (var (key, value) in ctx.Request.Headers)
        if (!key.Equals("Host", StringComparison.OrdinalIgnoreCase) &&
            !req.Headers.TryAddWithoutValidation(key, (string[])value!))
            req.Content?.Headers.TryAddWithoutValidation(key, (string[])value!);

    using var resp = await http.SendAsync(req,
        HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
    ctx.Response.StatusCode = (int)resp.StatusCode;
    foreach (var (key, value) in resp.Headers.Concat(resp.Content.Headers))
        if (!key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            ctx.Response.Headers[key] = value.ToArray();
    await resp.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
});

app.Run();

// The only place the proxy opens the envelope: a JSON-RPC error must echo the
// request id. One well-specified envelope, parsed only on the rejection paths.
static async Task<object?> ReadIdAsync(HttpContext ctx)
{
    try
    {
        using var doc = await JsonDocument.ParseAsync(ctx.Request.Body,
            cancellationToken: ctx.RequestAborted);
        // A batch array or a bare value has no single id to echo.
        if (doc.RootElement.ValueKind == JsonValueKind.Object &&
            doc.RootElement.TryGetProperty("id", out var id))
            return id.Clone();
    }
    catch (JsonException) { }
    return null;
}
