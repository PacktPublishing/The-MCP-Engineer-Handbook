using System.Buffers.Text;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;

// ---------------------------------------------------------------------------
// HTTP MCP server with per-tool scope enforcement (Section 9.7.4). When the
// bearer token's scopes don't cover the tool being called, the server answers
// with HTTP 403 and a WWW-Authenticate challenge carrying
// error="insufficient_scope" and a scope parameter (RFC 6750) naming
// EVERYTHING the operation needs — one challenge, not one missing scope per
// retry. The server stays stateless about what any client was granted before.
// ---------------------------------------------------------------------------

const string ServerUrl = "http://localhost:5302";
const string AuthServerUrl = "http://localhost:5301";
const string ResourceMetadataUrl = $"{ServerUrl}/.well-known/oauth-protected-resource";
const string SharedSecret = "stepup-demo-hmac-secret--demo-only-never-do-this-in-production"; // must match FakeAuthServer

// The server owns this map; clients learn it through challenges, not documentation.
var requiredScopes = new Dictionary<string, string[]>
{
    ["read_summary"] = ["reports:read"],
    ["export_full_data"] = ["reports:read", "reports:export"],
};

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(ServerUrl);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<ReportTools>();

var app = builder.Build();

// RFC 9728 protected resource metadata for a resource at the root (Section 9.2.2).
// Both challenges below point here. scopes_supported lists only the minimal
// scope for basic use; the rest is requested through step-up.
app.MapGet("/.well-known/oauth-protected-resource", () => Results.Json(new
{
    resource = ServerUrl,
    authorization_servers = new[] { AuthServerUrl },
    scopes_supported = new[] { "reports:read" },
    bearer_methods_supported = new[] { "header" },
}));

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/.well-known"))
    {
        await next();
        return;
    }

    // 1. Every request needs a valid token bound to this server (aud check, Section 9.2.4).
    var authorization = context.Request.Headers.Authorization.ToString();
    var claims = authorization.StartsWith("Bearer ")
        ? MiniJwt.Validate(authorization["Bearer ".Length..], SharedSecret)
        : null;
    if (claims is null || claims.Value.GetProperty("aud").GetString() != ServerUrl)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate =
            $"Bearer error=\"invalid_token\", resource_metadata=\"{ResourceMetadataUrl}\"";
        return;
    }

    var granted = (claims.Value.GetProperty("scope").GetString() ?? "")
        .Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    // 2. tools/call requests are additionally checked against the tool's scopes.
    //    The tool name lives in the JSON-RPC body, so peek at it before the MCP
    //    endpoint runs (EnableBuffering lets the endpoint re-read the stream).
    //    On 2026-07-28 the Mcp-Name header carries it too (the token-validation
    //    sample routes on that); the body works for every protocol version.
    if (HttpMethods.IsPost(context.Request.Method)
        && await PeekToolNameAsync(context.Request) is { } toolName
        && requiredScopes.TryGetValue(toolName, out var required)
        && !required.All(granted.Contains))
    {
        // The challenge names the FULL set the operation needs (RFC 6750's scope
        // parameter), not just the missing piece. Computing the union with what
        // it already holds is the client's job.
        var scope = string.Join(' ', required);
        Console.WriteLine($"[reports]  {toolName}: token has \"{string.Join(' ', granted)}\" " +
                          $"-> 403 challenge, scope=\"{scope}\"");
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.Headers.WWWAuthenticate =
            $"Bearer error=\"insufficient_scope\", " +
            $"error_description=\"{toolName} requires scopes the token does not carry\", " +
            $"scope=\"{scope}\", resource_metadata=\"{ResourceMetadataUrl}\"";
        return;
    }

    await next();
});

app.MapMcp();

Console.WriteLine($"[reports]  MCP server listening on {ServerUrl}");
app.Run();

static async Task<string?> PeekToolNameAsync(HttpRequest request)
{
    request.EnableBuffering();
    string body;
    using (var reader = new StreamReader(request.Body, leaveOpen: true))
        body = await reader.ReadToEndAsync(request.HttpContext.RequestAborted);
    request.Body.Position = 0;

    try
    {
        var root = JsonDocument.Parse(body).RootElement;
        return root.TryGetProperty("method", out var method) && method.GetString() == "tools/call"
            && root.TryGetProperty("params", out var p) && p.TryGetProperty("name", out var name)
            ? name.GetString()
            : null;
    }
    catch (JsonException)
    {
        return null; // not JSON-RPC; let the MCP endpoint reject it
    }
}

[McpServerToolType]
public class ReportTools
{
    [McpServerTool(Name = "read_summary"),
     Description("Returns the quarterly report summary. Requires scope: reports:read.")]
    public static string ReadSummary()
        => "Q2 summary: revenue up 8%, 3 open incidents, 12 reports on file.";

    [McpServerTool(Name = "export_full_data"),
     Description("Exports the full report dataset. Requires scopes: reports:read reports:export.")]
    public static string ExportFullData()
        => "export.csv: 12 reports, 4,812 rows (full dataset).";
}

// Demo-only HMAC-JWT validation, mirroring FakeAuthServer's MiniJwt.Sign.
// A real resource server validates against the AS's published JWKS (Section 9.2.4).
static class MiniJwt
{
    public static JsonElement? Validate(string token, string secret)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return null;

        var expected = Base64Url.EncodeToString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}")));
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(parts[2])))
            return null;

        var payload = JsonSerializer.Deserialize<JsonElement>(Base64Url.DecodeFromChars(parts[1]));
        var expired = payload.TryGetProperty("exp", out var exp)
            && exp.GetInt64() < DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return expired ? null : payload;
    }
}
