using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// Stdio servers must not write logs to stdout — it would corrupt the JSON-RPC stream.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<DocumentStore>();
builder.Services.AddSingleton<PreviewStore>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<FindReplaceTools>();

await builder.Build().RunAsync();
