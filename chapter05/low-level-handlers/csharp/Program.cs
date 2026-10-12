using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

// One list feeds both tools/list and the known-tool check in tools/call
Tool[] tools =
[
    new Tool
    {
        Name = "echo",
        Description = "Echo back the provided message",
        InputSchema = MessageSchema("The message to echo back"),
    },
    new Tool
    {
        Name = "reverse",
        Description = "Reverse the provided message",
        InputSchema = MessageSchema("The message to reverse"),
    },
    new Tool
    {
        Name = "shout",
        Description = "Return the provided message in uppercase",
        InputSchema = MessageSchema("The message to shout"),
    },
];

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
    .WithListToolsHandler(async (request, cancellationToken) => new ListToolsResult { Tools = tools })
    // Register a handler for tools/call. The low-level handler receives whatever
    // arrives: rejecting unknown tools and arguments that don't match the
    // advertised schema is now this handler's job.
    .WithCallToolHandler(async (request, cancellationToken) =>
    {
        var name = request.Params?.Name;
        if (!tools.Any(tool => tool.Name == name))
        {
            // Unknown tool: a protocol error, as the high-level API reports it
            throw new McpProtocolException($"Unknown tool: {name}", McpErrorCode.InvalidParams);
        }

        // The C# SDK ships no JSON Schema validator, so check what the schema
        // declares explicitly: 'message' is required and must be a string.
        // Invalid arguments are a tool error the model can read and correct.
        if (request.Params?.Arguments?.TryGetValue("message", out var argument) is not true)
        {
            return InvalidArguments(name, "'message' is a required property");
        }
        if (argument.ValueKind != JsonValueKind.String)
        {
            return InvalidArguments(name, "'message' must be a string");
        }
        var message = argument.GetString()!;

        return name switch
        {
            "echo" => new CallToolResult
            {
                Content = [new TextContentBlock { Text = $"Echo: {message}" }],
            },
            "reverse" => new CallToolResult
            {
                Content = [new TextContentBlock { Text = string.Concat(message.EnumerateRunes().Reverse()) }],
            },
            _ => new CallToolResult
            {
                Content = [new TextContentBlock { Text = message.ToUpperInvariant() }],
            },
        };
    });

await builder.Build().RunAsync();

static JsonElement MessageSchema(string description) => JsonSerializer.SerializeToElement(new
{
    type = "object",
    properties = new { message = new { type = "string", description } },
    required = new[] { "message" },
});

static CallToolResult InvalidArguments(string? name, string details) => new()
{
    Content = [new TextContentBlock { Text = $"Invalid arguments for tool {name}: {details}" }],
    IsError = true,
};

