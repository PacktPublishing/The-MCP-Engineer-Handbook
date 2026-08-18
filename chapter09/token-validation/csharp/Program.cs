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

// Tool name -> scope the token must carry. Enforced by the tools/call filter.
var requiredScopes = new Dictionary<string, string>
{
    ["list_reports"] = "reports:read",
    ["read_report"] = "reports:read",
    ["delete_report"] = "reports:admin",
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
        // Scope enforcement per tool. The HTTP middleware below has already
        // authenticated the token; the ASP.NET Core transport flows the
        // resulting ClaimsPrincipal to request.User. Failures come back as
        // in-band tool errors so the model can read WHY it was denied and
        // which scope a re-authorization needs.
        filters.AddCallToolFilter(next => async (request, cancellationToken) =>
        {
            var toolName = request.MatchedPrimitive?.Id ?? "unknown";
            if (requiredScopes.TryGetValue(toolName, out var required))
            {
                // OAuth convention: one space-delimited `scope` claim.
                var granted = request.User?.FindFirst("scope")?.Value?.Split(' ')
                    ?? [];
                if (!granted.Contains(required))
                {
                    return new CallToolResult
                    {
                        Content = [new TextContentBlock
                        {
                            Text = $"Insufficient scope for '{toolName}': the token grants " +
                                   $"[{string.Join(", ", granted)}] but this tool requires " +
                                   $"'{required}'. Re-authorize with the missing scope.",
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
// 401 below points here, and this document points at the Authorization Server.
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
        await Reject(context, "invalid_request", "No bearer token was provided.");
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
        await Reject(context, "invalid_token",
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
        await Reject(context, "invalid_token",
            $"Token audience is '{string.Join(" ", token.Audiences)}' — this is a token " +
            $"for a different resource. This server only accepts tokens minted for " +
            $"'{CanonicalResourceUri}' (RFC 8707 resource indicator).");
        return;
    }

    context.User = new ClaimsPrincipal(result.ClaimsIdentity);
    await nextMiddleware();
});

app.MapMcp("/mcp");

app.Run("http://localhost:5309");

// 401 with the WWW-Authenticate header RFC 9728 prescribes: the
// resource_metadata parameter hands the client the start of the discovery
// chain (Section 9.2.2). The JSON body repeats the reason for humans.
static async Task Reject(HttpContext context, string error, string description)
{
    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    context.Response.Headers.WWWAuthenticate =
        $"Bearer error=\"{error}\", resource_metadata=\"{ResourceMetadataUri}\"";
    await context.Response.WriteAsJsonAsync(new { error, error_description = description });
}
