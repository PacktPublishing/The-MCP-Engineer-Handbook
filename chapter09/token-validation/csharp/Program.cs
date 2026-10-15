using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Protocol;

// The canonical URI of THIS server — the value clients send as the RFC 8707
// `resource` parameter, and the audience every accepted token must carry.
const string CanonicalResourceUri = "http://localhost:5309/mcp";
const string ResourceMetadataUri =
    "http://localhost:5309/.well-known/oauth-protected-resource/mcp";

// DEMO ONLY. A real deployment has no issuer constants in the resource server:
// tokens come from a real Authorization Server, and validation uses that AS's
// published keys via standard JWT bearer middleware. The HMAC key below exists
// so this sample runs with no external AS.
const string DemoIssuer = "https://demo-as.example";
const string DemoSigningKey = "demo-signing-key-do-not-use-anywhere-real-0123456789";

var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(DemoSigningKey));

// --- `make-token` CLI mode: mint a demo token and exit ---------------------
// dotnet run -- make-token [--scopes "reports:read reports:admin"] [--audience <uri>]
if (args.Length > 0 && args[0] == "make-token")
{
    var scopes = "reports:read";
    var audience = CanonicalResourceUri;
    for (var i = 1; i < args.Length - 1; i++)
    {
        if (args[i] == "--scopes") scopes = args[i + 1];
        if (args[i] == "--audience") audience = args[i + 1];
    }

    Console.WriteLine(new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = DemoIssuer,
        Audience = audience,
        Expires = DateTime.UtcNow.AddHours(1),
        Subject = new ClaimsIdentity([new Claim("sub", "demo-user")]),
        Claims = new Dictionary<string, object> { ["scope"] = scopes },
        SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256),
    }));
    return;
}

// --- The MCP server ---------------------------------------------------------

// Tool name -> every scope the token must carry for that tool. Enforced at the
// HTTP layer by the middleware below, and again by the tools/call filter.
var requiredScopes = new Dictionary<string, string[]>
{
    ["list_reports"] = ["reports:read"],
    ["read_report"] = ["reports:read"],
    ["delete_report"] = ["reports:admin"],
};

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "token-validation-server",
            Version = "1.0.0",
        };
    })
    .WithHttpTransport()
    .WithTools<ReportTools>()
    .WithRequestFilters(filters =>
    {
        // Defense in depth behind the middleware's 403. A 2026-07-28 request
        // never reaches this point with too few scopes, but a request on an
        // older protocol version carries no Mcp-Name header the SDK checks
        // against the body, so the per-tool policy is enforced here as well,
        // on the tool the SDK actually dispatched. The middleware has already
        // authenticated the token; the ASP.NET Core transport flows the
        // resulting ClaimsPrincipal to request.User.
        filters.AddCallToolFilter(next => async (request, cancellationToken) =>
        {
            var toolName = request.MatchedPrimitive?.Id ?? "unknown";
            if (requiredScopes.TryGetValue(toolName, out var required))
            {
                // OAuth convention: one space-delimited `scope` claim.
                var granted = request.User?.FindFirst("scope")?.Value?.Split(' ')
                    ?? [];
                if (!required.All(granted.Contains))
                {
                    return new CallToolResult
                    {
                        Content = [new TextContentBlock
                        {
                            Text = $"Insufficient scope for '{toolName}': the token grants " +
                                   $"[{string.Join(", ", granted)}] but this tool requires " +
                                   $"[{string.Join(", ", required)}]. Re-authorize for the " +
                                   $"required scopes together with the scopes already requested.",
                        }],
                        IsError = true,
                    };
                }
            }
            return await next(request, cancellationToken);
        });
    });

var app = builder.Build();

// RFC 9728 protected resource metadata, at the path-aware well-known location
// for a resource at /mcp. This is the first stop of the discovery chain: the
// 401 and 403 challenges below point here, and this document points at the
// Authorization Server.
app.MapGet("/.well-known/oauth-protected-resource/mcp", () => Results.Json(new
{
    resource = CanonicalResourceUri,
    authorization_servers = new[] { DemoIssuer },
    scopes_supported = new[] { "reports:read", "reports:admin" },
    bearer_methods_supported = new[] { "header" },
}));

// Token validation in front of the MCP endpoint. The server's entire auth
// duty as an OAuth Resource Server: validate what comes in, never mint,
// never forward. Anything downstream gets its own credentials (Section 9.4).
app.Use(async (context, nextMiddleware) =>
{
    if (!context.Request.Path.StartsWithSegments("/mcp"))
    {
        await nextMiddleware();
        return;
    }

    var authorization = context.Request.Headers.Authorization.ToString();
    if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal))
    {
        await Unauthorized(context, "invalid_request", "No bearer token was provided.");
        return;
    }

    // Signature, expiry, and issuer. Audience is deliberately checked by hand
    // afterwards so the rejection can say what actually went wrong.
    var result = await new JsonWebTokenHandler().ValidateTokenAsync(
        authorization["Bearer ".Length..],
        new TokenValidationParameters
        {
            ValidIssuer = DemoIssuer,
            IssuerSigningKey = signingKey,
            ValidateAudience = false,
        });
    if (!result.IsValid)
    {
        await Unauthorized(context, "invalid_token",
            "The token is malformed, expired, or not signed by a trusted issuer.");
        return;
    }

    // The audience check — the mechanical half of the Downstream Trap
    // argument (Sections 9.1.2 and 9.2.4). The signature check above proved
    // the token is GENUINE; only this check proves it is OURS. A genuine
    // token minted for some other resource is exactly what a compromised
    // server would try to replay here, and exactly what this server must
    // never accept — or forward.
    var token = (JsonWebToken)result.SecurityToken;
    if (!token.Audiences.Contains(CanonicalResourceUri))
    {
        await Unauthorized(context, "invalid_token",
            $"Token audience is '{string.Join(" ", token.Audiences)}' — this is a token " +
            $"for a different resource. This server only accepts tokens minted for " +
            $"'{CanonicalResourceUri}' (RFC 8707 resource indicator).");
        return;
    }

    // Per-tool scope policy (Section 9.7.4). The token is valid for this
    // server; the request just exceeds it, so the answer is a 403
    // insufficient_scope challenge naming everything the tool needs, which
    // a step-up client unions with what it already requested. The 2026-07-28
    // revision requires Mcp-Method and Mcp-Name headers that mirror the
    // JSON-RPC body, and the SDK rejects a request whose headers and body
    // disagree, so the middleware can read the tool name without parsing the
    // body. (Non-ASCII names arrive Base64-wrapped; DecodeValue unwraps them.)
    if (context.Request.Headers["Mcp-Method"].ToString().Trim() == "tools/call"
        && McpHeaderEncoder.DecodeValue(context.Request.Headers["Mcp-Name"].ToString().Trim())
            is { } toolName
        && requiredScopes.TryGetValue(toolName, out var required))
    {
        var granted = result.ClaimsIdentity.FindFirst("scope")?.Value?.Split(' ') ?? [];
        if (!required.All(granted.Contains))
        {
            await InsufficientScope(context, required,
                $"'{toolName}' requires [{string.Join(", ", required)}]; the token grants " +
                $"[{string.Join(", ", granted)}].");
            return;
        }
    }

    context.User = new ClaimsPrincipal(result.ClaimsIdentity);
    await nextMiddleware();
});

app.MapMcp("/mcp");

app.Run("http://localhost:5309");

// 401 with the WWW-Authenticate header RFC 9728 prescribes: the
// resource_metadata parameter hands the client the start of the discovery
// chain (Section 9.2.2). The JSON body repeats the reason for humans.
static async Task Unauthorized(HttpContext context, string error, string description)
{
    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    context.Response.Headers.WWWAuthenticate =
        $"Bearer error=\"{error}\", resource_metadata=\"{ResourceMetadataUri}\"";
    await context.Response.WriteAsJsonAsync(new { error, error_description = description });
}

// 403 with the RFC 6750 insufficient_scope challenge: the scope parameter
// names ALL the scopes the operation needs, in one challenge, and
// resource_metadata matches the 401 (Section 9.7.4).
static async Task InsufficientScope(HttpContext context, string[] required, string description)
{
    context.Response.StatusCode = StatusCodes.Status403Forbidden;
    context.Response.Headers.WWWAuthenticate =
        $"Bearer error=\"insufficient_scope\", scope=\"{string.Join(' ', required)}\", " +
        $"resource_metadata=\"{ResourceMetadataUri}\"";
    await context.Response.WriteAsJsonAsync(
        new { error = "insufficient_scope", error_description = description });
}
