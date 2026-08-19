// Completions client (section 5.3.3).
//
// Spawns the sample server over stdio and requests completions for a partial
// `path` value — the C# counterpart of the chapter's Python extract.
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

var serverProject = args.Length > 0 ? args[0] : "csharp";

var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "completions-demo",
    Command = "dotnet",
    Arguments = ["run", "--project", serverProject],
});

await using var client = await McpClient.CreateAsync(transport);

// Request completions for the "path" argument
var result = await client.CompleteAsync(
    new ResourceTemplateReference { Uri = "file:///{path}" },
    argumentName: "path",
    argumentValue: "docs/re");

Console.WriteLine($"values: [{string.Join(", ", result.Completion.Values)}]");
Console.WriteLine($"total: {result.Completion.Total} hasMore: {result.Completion.HasMore}");
