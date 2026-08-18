using System.Diagnostics;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);

// The filters below close over this logger. It is created up front so the
// filter lambdas can use it directly; the app's DI-configured logging is
// unaffected.
using var loggerFactory = LoggerFactory.Create(logging => logging.AddSimpleConsole());
var logger = loggerFactory.CreateLogger("McpFilters");

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "csharp-filters-server",
            Version = "1.0.0",
        };
    })
    .WithHttpTransport()
    .WithTools<DocumentTools>()
    .WithRequestFilters(filters =>
    {
        // Logging filter — logs all tool calls with timing
        filters.AddCallToolFilter((next) => async (request, cancellationToken) =>
        {
            var toolName = request.MatchedPrimitive?.Id ?? "unknown";
            var sw = Stopwatch.StartNew();
            try
            {
                var result = await next(request, cancellationToken);
                sw.Stop();
                logger.LogInformation("Tool {Name} completed in {Elapsed}ms",
                    toolName, sw.ElapsedMilliseconds);
                return result;
            }
            catch (Exception ex)
            {
                sw.Stop();
                logger.LogError(ex, "Tool {Name} failed after {Elapsed}ms",
                    toolName, sw.ElapsedMilliseconds);
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = $"Error: {ex.Message}" }],
                    IsError = true,
                };
            }
        });

        // Authorization filter — blocks admin-prefixed tools
        filters.AddCallToolFilter((next) => async (request, cancellationToken) =>
        {
            var toolName = request.MatchedPrimitive?.Id ?? "unknown";
            if (toolName.StartsWith("admin_", StringComparison.OrdinalIgnoreCase))
            {
                return new CallToolResult
                {
                    Content = [new TextContentBlock
                    {
                        Text = $"Access denied: '{toolName}' requires admin privileges."
                    }],
                    IsError = true,
                };
            }
            return await next(request, cancellationToken);
        });
    });

var app = builder.Build();

app.MapMcp();

app.Run();
