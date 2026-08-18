using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

var builder = Host.CreateApplicationBuilder(args);

// Stdio servers must not write logs to stdout — it would corrupt the JSON-RPC stream.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "low-level-handlers-server",
            Version = "1.0.0",
        };
    })
    .WithStdioServerTransport()
    // Register a handler for tools/list
    .WithListToolsHandler(async (request, cancellationToken) =>
    {
        return new ListToolsResult
        {
            Tools =
            [
                new Tool
                {
                    Name = "echo",
                    Description = "Echo back the provided message",
                    InputSchema = JsonSerializer.Deserialize<JsonElement>("""
                        {
                          "type": "object",
                          "properties": {
                            "message": { "type": "string", "description": "The message to echo back" }
                          },
                          "required": ["message"]
                        }
                        """),
                },
                new Tool
                {
                    Name = "reverse",
                    Description = "Reverse the provided message",
                    InputSchema = JsonSerializer.Deserialize<JsonElement>("""
                        {
                          "type": "object",
                          "properties": {
                            "message": { "type": "string", "description": "The message to reverse" }
                          },
                          "required": ["message"]
                        }
                        """),
                },
                new Tool
                {
                    Name = "shout",
                    Description = "Return the provided message in uppercase",
                    InputSchema = JsonSerializer.Deserialize<JsonElement>("""
                        {
                          "type": "object",
                          "properties": {
                            "message": { "type": "string", "description": "The message to shout" }
                          },
                          "required": ["message"]
                        }
                        """),
                },
            ],
        };
    })
    // Register a handler for tools/call
    .WithCallToolHandler(async (request, cancellationToken) =>
    {
        var name = request.Params?.Name;
        var message = request.Params?.Arguments?["message"].GetString() ?? "";

        return name switch
        {
            "echo" => new CallToolResult
            {
                Content = [new TextContentBlock { Text = $"Echo: {message}" }],
            },
            "reverse" => new CallToolResult
            {
                Content = [new TextContentBlock { Text = new string(message.Reverse().ToArray()) }],
            },
            "shout" => new CallToolResult
            {
                Content = [new TextContentBlock { Text = message.ToUpperInvariant() }],
            },
            _ => throw new McpProtocolException($"Unknown tool: {name}", McpErrorCode.InvalidParams),
        };
    });

await builder.Build().RunAsync();
