using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// ---------------------------------------------------------------------------
// Fake Authorization Server for the step-up sample. A real AS runs the
// authorization-code + PKCE flow here: redirect, login, a consent screen for
// each requested scope. This one grants whatever scopes are asked for,
// simulating a user who consents to everything — the step-up mechanics on the
// client and resource server are what the sample demonstrates.
//
// It tracks nothing between requests. Scope accumulation across step-ups is
// deliberately the CLIENT's job (Section 9.7.4): neither the AS nor the MCP
// server remembers what was granted before.
// ---------------------------------------------------------------------------

const string Issuer = "http://localhost:5301";
const string SharedSecret = "stepup-demo-hmac-secret--demo-only-never-do-this-in-production";

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Issuer);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

app.MapPost("/token", async (HttpRequest request) =>
{
    var form = await request.ReadFormAsync();
    var scope = form["scope"].ToString();
    var resource = form["resource"].ToString(); // RFC 8707 resource indicator -> aud claim

    var payload = JsonSerializer.Serialize(new
    {
        iss = Issuer,
        aud = string.IsNullOrEmpty(resource) ? "urn:stepup-demo:unspecified" : resource,
        scope,
        exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(),
    });

    Console.WriteLine($"[fake-as]  token granted: scope=\"{scope}\" aud=\"{resource}\"");
    return Results.Json(new
    {
        access_token = MiniJwt.Sign(payload, SharedSecret),
        token_type = "Bearer",
        expires_in = 600,
        scope,
    });
});

Console.WriteLine($"[fake-as]  listening on {Issuer}/token");
app.Run();

// Demo-only HMAC-signed JWT. A real AS signs with an asymmetric key and
// publishes the public half via JWKS; see Section 9.2.4.
static class MiniJwt
{
    public static string Sign(string payloadJson, string secret)
    {
        var header = Base64Url.EncodeToString(Encoding.UTF8.GetBytes("""{"alg":"HS256","typ":"JWT"}"""));
        var payload = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payloadJson));
        var signature = Base64Url.EncodeToString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{header}.{payload}")));
        return $"{header}.{payload}.{signature}";
    }
}
