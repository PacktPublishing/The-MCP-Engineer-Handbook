using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.ComponentModel;

// The two wrapper tools from §3.3.3: MCP resources have no model-native integration
// point, so the host creates one — list_resources and read_resource, aggregating across
// every connected server. The methods are the book extract; the class, the metadata
// record, and the two Format helpers are the plumbing the extract elides.
sealed class ResourceTools(ClientManager clientManager, OriginPolicy originPolicy)
{
    private readonly ClientManager _clientManager = clientManager;
    private readonly OriginPolicy _originPolicy = originPolicy;

    [Description("Lists available resources from connected servers.")]
    public async Task<IList<ResourceMetadata>> ListResources()
    {
        var resourceMetadata = new List<ResourceMetadata>();
        foreach (var serverName in _clientManager.GetServerNames())
        {
            var client = _clientManager.GetClient(serverName);
            var resources = await client.ListResourcesAsync();
            resourceMetadata.AddRange(FormatResourceMetadata(resources, serverName));
        }
        return resourceMetadata;
    }

    [Description("Reads a resource by server name and URI.")]
    public async Task<string> ReadResource(string serverName, string uri)
    {
        var client = _clientManager.GetClient(serverName);
        // Enforced here, at execution, whatever call the model generated.
        if (_originPolicy.CheckRead(serverName, uri) is { } denial)
            return denial;
        var result = await client.ReadResourceAsync(uri);
        return FormatResourceContent(result);
    }

    // Tag every entry with the HOST-ASSIGNED server name so the model can route reads —
    // and so two servers with colliding URIs (like the two spawns in this demo) stay apart.
    private static IEnumerable<ResourceMetadata> FormatResourceMetadata(
        IList<McpClientResource> resources, string serverName) =>
        resources.Select(r => new ResourceMetadata(
            serverName, r.Uri, r.Name, r.Title, r.Description, r.MimeType));

    // Models (and many chat APIs) only take text from tools; flatten accordingly.
    private static string FormatResourceContent(ReadResourceResult result) =>
        string.Join("\n", result.Contents.Select(c => c switch
        {
            TextResourceContents text => text.Text,
            BlobResourceContents blob => $"[binary content: {blob.MimeType ?? "unknown type"}]",
            _ => $"[unsupported content type: {c.GetType().Name}]",
        }));
}

// The cross-server origin policy from §3.3.3. A valid serverName is a routing key, not
// an authorization: content from one server can instruct the model to read from another
// (the confused deputy). So a conversation is bound to the servers whose content it
// already holds, and a read from any other server needs explicit user approval naming
// both sides. One instance per conversation.
sealed class OriginPolicy(Func<IReadOnlyCollection<string>, string, string, bool> approveCrossOriginRead)
{
    private readonly HashSet<string> _origins = [];

    // Returns null when the read may proceed, otherwise a denial the model can read.
    public string? CheckRead(string serverName, string uri)
    {
        if (_origins.Count > 0 && !_origins.Contains(serverName) &&
            !approveCrossOriginRead(_origins, serverName, uri))
        {
            return $"Read denied by host policy: this conversation holds content from " +
                   $"'{string.Join("', '", _origins)}', and the user did not approve reading " +
                   $"'{uri}' from '{serverName}'.";
        }
        _origins.Add(serverName);
        return null;
    }
}

// What the model sees per resource. ServerName is the routing key for read_resource.
record ResourceMetadata(
    string ServerName, string Uri, string Name, string? Title, string? Description, string? MimeType);

// Owns one McpClient per connected server, keyed by a label the HOST assigns. Never key
// on the server's self-reported name — identity claims from the server are not trusted
// input for routing decisions (§3.3.3).
sealed class ClientManager : IAsyncDisposable
{
    private readonly Dictionary<string, McpClient> _clients = [];

    public async Task ConnectAsync(string serverName, StdioClientTransportOptions transportOptions) =>
        _clients[serverName] = await McpClient.CreateAsync(new StdioClientTransport(transportOptions));

    public IEnumerable<string> GetServerNames() => _clients.Keys;

    public McpClient GetClient(string serverName) =>
        _clients.TryGetValue(serverName, out var client)
            ? client
            : throw new ArgumentException($"No connected server is labeled '{serverName}'.");

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
            await client.DisposeAsync();
    }
}
