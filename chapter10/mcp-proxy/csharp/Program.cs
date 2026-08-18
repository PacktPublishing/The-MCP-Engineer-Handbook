// mcp-proxy — a minimal MCP-aware reverse proxy. Plain ASP.NET Core, no MCP
// SDK: the routable headers of the 2026-07-28 revision (Mcp-Method, Mcp-Name)
// carry everything the proxy routes on, so JSON-RPC bodies stream through
// unparsed. (Section 10.4.3)
using System.Collections.Concurrent;
using System.Text.Json;

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
    var method = ctx.Request.Headers["Mcp-Method"].FirstOrDefault() ?? "";
    var name = ctx.Request.Headers["Mcp-Name"].FirstOrDefault() ?? "";
    var principal = ctx.Request.Headers["X-Principal"].FirstOrDefault()
                 ?? ctx.Request.Headers.Authorization.FirstOrDefault()
                 ?? "anonymous";

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
        // The only place the proxy opens the envelope: a JSON-RPC error must
        // echo the request id. One well-specified envelope, parsed only on
        // the rejection path.
        object? id = null;
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body,
                cancellationToken: ctx.RequestAborted);
            if (doc.RootElement.TryGetProperty("id", out var i)) id = i.Clone();
        }
        catch (JsonException) { }

        var retryAfter = 60 - (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 60);
        ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;   // for SDKs
        ctx.Response.Headers.RetryAfter = retryAfter.ToString();
        await ctx.Response.WriteAsJsonAsync(new                           // for the model
        {
            jsonrpc = "2.0",
            id,
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
