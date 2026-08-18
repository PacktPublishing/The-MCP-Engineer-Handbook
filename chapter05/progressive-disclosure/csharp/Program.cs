// Progressive disclosure over a document-management API (Chapter 5, §5.1.2).
// Four static tools — list, search, describe, execute — front an endpoint
// manifest loaded from ../data/endpoints.json. The endpoints are data, not tools.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

var builder = Host.CreateApplicationBuilder(args);

// Stdio servers must not write logs to stdout — it would corrupt the JSON-RPC stream.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation { Name = "document-management-api", Version = "1.0.0" };
        options.ServerInstructions =
            "This server provides access to a document management API with features for " +
            "managing documents, users, groups, permissions, and document versioning. " +
            "Use list_endpoints to browse available API groups, search_endpoints to find " +
            "specific functionality, describe_endpoint to get full details before calling, " +
            "and execute_endpoint to invoke API operations. " +
            "Common workflows: querying document permissions, checking user access levels, " +
            "comparing document versions, and managing document lifecycle.";
    })
    .WithStdioServerTransport()
    .WithTools<EndpointTools>();

await builder.Build().RunAsync();
