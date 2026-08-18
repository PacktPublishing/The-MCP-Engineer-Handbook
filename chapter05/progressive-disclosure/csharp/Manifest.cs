// Endpoint manifest (generated data — see ../agent-instructions.md), loaded
// from the shared ../data/endpoints.json, plus the path-template matcher.

using System.Text.Json;

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
                    parameters[t[1..^1]] = Uri.UnescapeDataString(segments[i]);
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
