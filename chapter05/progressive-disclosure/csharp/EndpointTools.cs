// The four progressive-disclosure tools. Tool results carry the same text
// (and isError flag) as the TypeScript canonical, so they return
// CallToolResult rather than plain strings.

using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

[McpServerToolType]
public sealed class EndpointTools
{
    private static readonly Manifest Manifest = Manifest.Load();

    private static readonly string[] GroupNames = Manifest.Groups.Select(g => g.Name).ToArray();

    // Mirrors JSON.stringify(value, null, 2): 2-space indent, camelCase keys,
    // "—" and friends left unescaped.
    private static readonly JsonSerializerOptions JsonOut = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static CallToolResult Text(string s) =>
        new() { Content = [new TextContentBlock { Text = s }] };

    private static CallToolResult ErrorText(string s) =>
        new() { Content = [new TextContentBlock { Text = s }], IsError = true };

    // --- Tool 1: list_endpoints (navigation) ---

    [McpServerTool(Name = "list_endpoints"), Description(
        "List the available endpoint groups of the document management API, or list the " +
        "endpoints within a specific group. Call without arguments to see all groups. " +
        "Provide a group name to see its endpoints.")]
    public static CallToolResult ListEndpoints(
        [Description("Group name to list endpoints for. Omit to see all groups.")] string? group = null)
    {
        if (group is null)
        {
            var lines = Manifest.Groups.Select(g =>
            {
                var count = Manifest.Endpoints.Count(e => e.Group == g.Name);
                return $"- {g.Name} ({count} endpoint{(count == 1 ? "" : "s")})";
            });
            return Text(
                $"Available API groups:\n\n{string.Join("\n", lines)}\n\n" +
                "Use list_endpoints with a group name to see the endpoints in that group.");
        }
        var match = GroupNames.FirstOrDefault(n => string.Equals(n, group, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            return ErrorText(
                $"Unknown group \"{group}\". Available groups: {string.Join(", ", GroupNames)}. " +
                "Call list_endpoints without arguments to see all groups.");
        }
        var endpointLines = Manifest.Endpoints
            .Where(e => e.Group == match)
            .Select(e => $"- {e.Method} {e.Path} — {e.Summary}");
        return Text(
            $"Endpoints in \"{match}\":\n\n{string.Join("\n", endpointLines)}\n\n" +
            "Use describe_endpoint to get full details for a specific endpoint.");
    }

    // --- Tool 2: search_endpoints (search) ---

    [McpServerTool(Name = "search_endpoints"), Description(
        "Search the document management API endpoints with a free-text query. Matches " +
        "endpoint paths, summaries, and descriptions — API metadata, not document content. " +
        "Returns concise results; use describe_endpoint for full details.")]
    public static CallToolResult SearchEndpoints(
        [Description("Free-text search terms, e.g. \"write permission\" or \"compare versions\".")] string query)
    {
        var terms = query.ToLowerInvariant().Split(' ', '\t', '\n', '\r').Where(t => t.Length > 0).ToArray();
        if (terms.Length == 0)
        {
            return ErrorText("Provide one or more search terms, e.g. \"write permission\".");
        }
        var matches = Manifest.Endpoints.Where(e =>
        {
            var haystack = $"{e.Method} {e.Path} {e.Group} {e.Summary} {e.Description}".ToLowerInvariant();
            return terms.All(haystack.Contains);
        }).ToList();
        if (matches.Count == 0)
        {
            return Text(
                $"No endpoints match \"{query}\". Try fewer or more general keywords, " +
                "or use list_endpoints to browse the API groups.");
        }
        var lines = matches.Select(e => $"- [{e.Group}] {e.Method} {e.Path} — {e.Summary}");
        return Text(
            $"Found {matches.Count} endpoint(s) matching \"{query}\":\n\n{string.Join("\n", lines)}\n\n" +
            "Use describe_endpoint to get full details before executing.");
    }

    // --- Tool 3: describe_endpoint (full metadata) ---

    [McpServerTool(Name = "describe_endpoint"), Description(
        "Get the full details of a single API endpoint: description, parameters, request " +
        "body schema, and response schema. Call this before using execute_endpoint.")]
    public static CallToolResult DescribeEndpoint(
        [Description("HTTP method, e.g. \"GET\".")] string method,
        [Description("Endpoint path as shown by list_endpoints or search_endpoints, " +
            "e.g. \"/api/documents/{id}/permissions\".")] string path)
    {
        var m = method.ToUpperInvariant();
        var found =
            Manifest.Endpoints.FirstOrDefault(e => e.Method == m && e.Path == path)
            ?? Manifest.MatchEndpoint(m, path)?.Endpoint;
        if (found is null)
        {
            return ErrorText(
                $"No endpoint matches {m} {path}. Use list_endpoints to browse groups " +
                "or search_endpoints to find functionality.");
        }
        var parts = new List<string>
        {
            $"{found.Method} {found.Path} — {found.Summary}",
            $"Group: {found.Group}",
            found.Description,
        };
        if (found.Parameters.Count > 0)
        {
            var lines = found.Parameters.Select(p =>
                $"- {p.Name} ({p.In}, {p.Type}, {(p.Required ? "required" : "optional")}) — {p.Description}");
            parts.Add($"Parameters:\n{string.Join("\n", lines)}");
        }
        else
        {
            parts.Add("Parameters: none");
        }
        if (found.RequestBody is not null)
        {
            parts.Add(
                $"Request body ({found.RequestBody.ContentType}):\n" +
                JsonSerializer.Serialize(found.RequestBody.Schema, JsonOut));
        }
        else
        {
            parts.Add("Request body: none");
        }
        parts.Add(
            $"Response: {found.Response.Description}\n" +
            JsonSerializer.Serialize(found.Response.Schema, JsonOut));
        return Text(string.Join("\n\n", parts));
    }

    // --- Tool 4: execute_endpoint (invocation) ---

    [McpServerTool(Name = "execute_endpoint"), Description(
        "Execute a document management API endpoint. Provide the HTTP method and the path " +
        "with path parameters filled in (e.g. \"/api/documents/doc-001/permissions\"). Query " +
        "strings can be embedded in the path or passed separately; POST and PATCH bodies " +
        "are passed as a JSON string. Use describe_endpoint first to see the exact schema.")]
    public static CallToolResult ExecuteEndpoint(
        [Description("HTTP method of the endpoint, e.g. \"GET\" or \"POST\".")] string method,
        [Description("Endpoint path with path parameters filled in, e.g. \"/api/documents/doc-001/permissions\". " +
            "May include a query string.")] string path,
        [Description("Query string without the leading \"?\", e.g. \"q=quarterly report\". " +
            "Alternative to embedding it in the path.")] string? query = null,
        [Description("JSON request body, for POST and PATCH endpoints.")] string? body = null)
    {
        var m = method.ToUpperInvariant();
        var pieces = path.Split('?');
        var purePath = pieces[0];
        var apiQuery = new ApiQuery();
        if (pieces.Length > 1) apiQuery.AppendFrom(pieces[1]);
        apiQuery.AppendFrom(query ?? "");

        var match = Manifest.MatchEndpoint(m, purePath);
        if (match is null)
        {
            var otherMethods = Manifest.Endpoints
                .Where(e => e.Method != m && Manifest.MatchEndpoint(e.Method, purePath)?.Endpoint == e)
                .ToList();
            var hint = otherMethods.Count > 0
                ? "The path exists with other methods: " +
                  string.Join(", ", otherMethods.Select(e => $"{e.Method} {e.Path}")) + "."
                : "Use list_endpoints to browse groups or search_endpoints to find functionality, " +
                  "then describe_endpoint to confirm the exact method and path.";
            return ErrorText($"No endpoint matches {m} {purePath}. {hint}");
        }

        JsonNode? parsedBody = null;
        if (body is not null)
        {
            try
            {
                parsedBody = JsonNode.Parse(body);
            }
            catch (JsonException err)
            {
                return ErrorText(
                    $"The body argument is not valid JSON ({err.Message}). " +
                    "Pass the request body as a JSON string; use describe_endpoint with " +
                    $"{m} {match.Value.Endpoint.Path} to see the request schema.");
            }
        }

        var handler = SimulatedApi.Handlers[$"{match.Value.Endpoint.Method} {match.Value.Endpoint.Path}"];
        try
        {
            var result = handler(match.Value.Params, apiQuery, parsedBody);
            return Text(JsonSerializer.Serialize(result, JsonOut));
        }
        catch (ApiError err)
        {
            return ErrorText(err.Message);
        }
    }
}
