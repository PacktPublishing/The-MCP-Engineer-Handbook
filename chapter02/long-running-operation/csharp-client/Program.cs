// Demonstrates the Chapter 2, Section 2 work-around: when the model API you bind
// tools to has no native outputSchema support, serialize the tool's output schema
// into the description you hand to the model. The server itself is untouched —
// McpClientTool.WithDescription only changes the client-side presentation.
using ModelContextProtocol.Client;
using System.Text.Json;

var serverProject = args.Length > 0 ? args[0] : "csharp";

var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "long-running-operation",
    Command = "dotnet",
    Arguments = ["run", "--project", serverProject],
});

await using var client = await McpClient.CreateAsync(transport);

var tools = await client.ListToolsAsync();

Console.WriteLine("Tools on the server:");
foreach (var tool in tools)
{
    Console.WriteLine($"  {tool.Name}  (output schema: {(tool.ReturnJsonSchema is null ? "none" : "yes")})");
}

// Pick a tool that declares an output schema and lift that schema into the
// description. This is the one-liner from Chapter 2, Section 2:
var statusTool = tools.First(t => t.ReturnJsonSchema is not null);

var patchedTool = statusTool.WithDescription(
    $"{statusTool.Description} Returns JSON matching this schema: {JsonSerializer.Serialize(statusTool.ReturnJsonSchema)}");

Console.WriteLine();
Console.WriteLine($"Patched description for '{patchedTool.Name}':");
Console.WriteLine(patchedTool.Description);
