using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;

var builder = Host.CreateApplicationBuilder(args);

// Stdio servers must not write logs to stdout — it would corrupt the JSON-RPC stream.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<HelloTools>();

await builder.Build().RunAsync();

[McpServerToolType]
public class HelloTools
{
    [McpServerTool, Description("Greets the caller by name.")]
    public static string SayHello([Description("Name to greet.")] string name)
        => $"Hello, {name}! This server runs on the 2026-07-28 MCP revision.";
}
