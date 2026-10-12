// Endpoint manifest (generated data — see ../agent-instructions.md), loaded
// from the shared ../data/endpoints.json, plus the path-template matcher.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

public sealed record EndpointParameter(string Name, string In, string Type, bool Required, string Description);

public sealed record EndpointRequestBody(string ContentType, JsonElement Schema);

public sealed record EndpointResponse(string Description, JsonElement Schema);

public sealed record Endpoint(
    string Method,
    string Path,
    string Group,
    string Summary,
    string Description,
    List<EndpointParameter> Parameters,
    EndpointRequestBody? RequestBody,
    EndpointResponse Response);

public sealed record ApiGroup(string Name, string Description);

public sealed record Manifest(string Api, string Version, List<ApiGroup> Groups, List<Endpoint> Endpoints)
{
    public static Manifest Load()
    {
        // The manifest is shared by all three language ports and lives at the
        // sample root. Walk up from the build output (csharp/bin/Debug/...)
        // until data/endpoints.json appears.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "data", "endpoints.json");
            if (File.Exists(candidate))
            {
                return JsonSerializer.Deserialize<Manifest>(
                    File.ReadAllText(candidate),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            }
        }
        throw new FileNotFoundException("data/endpoints.json not found above " + AppContext.BaseDirectory);
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // A path parameter fills exactly one template segment. Reject values that
    // decode to anything else — "%2F", "..", malformed escapes — so a filled-in
    // path can never reach outside the endpoint it matched. Harmless against this
    // in-memory simulation; essential once a handler forwards to a real HTTP API.
    private static string? DecodePathParam(string segment)
    {
        if (Regex.IsMatch(segment, "%(?![0-9A-Fa-f]{2})"))
        {
            return null;
        }
        string value;
        try
        {
            // Strict UTF-8, so invalid byte sequences fail as in the other ports
            // (Uri.UnescapeDataString would leave them escaped). "+" is literal in a path.
            var raw = Encoding.UTF8.GetBytes(segment.Replace("+", "%2B"));
            value = StrictUtf8.GetString(WebUtility.UrlDecodeToBytes(raw, 0, raw.Length));
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        return value is "." or ".." || value.Contains('/') || value.Contains('\\') ? null : value;
    }

    // Match a path (either the "{id}" template itself or a concrete path like
    // "/api/documents/doc-001/permissions") against the manifest. Literal
    // segments beat placeholders, so "/api/documents/search" is not swallowed
    // by "/api/documents/{id}".
    public (Endpoint Endpoint, Dictionary<string, string> Params)? MatchEndpoint(string method, string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        (Endpoint Endpoint, Dictionary<string, string> Params, int Literals)? best = null;
        foreach (var endpoint in Endpoints)
        {
            if (endpoint.Method != method) continue;
            var templateSegments = endpoint.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (templateSegments.Length != segments.Length) continue;
            var parameters = new Dictionary<string, string>();
            var literals = 0;
            var matched = true;
            for (var i = 0; i < segments.Length; i++)
            {
                var t = templateSegments[i];
                if (t.StartsWith('{') && t.EndsWith('}'))
                {
                    var value = DecodePathParam(segments[i]);
                    if (value is null)
                    {
                        matched = false;
                        break;
                    }
                    parameters[t[1..^1]] = value;
                }
                else if (t == segments[i])
                {
                    literals++;
                }
                else
                {
                    matched = false;
                    break;
                }
            }
            if (matched && (best is null || literals > best.Value.Literals))
            {
                best = (endpoint, parameters, literals);
            }
        }
        return best is null ? null : (best.Value.Endpoint, best.Value.Params);
    }
}
