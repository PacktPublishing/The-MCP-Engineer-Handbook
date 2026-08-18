// ModelResourceClient — Pattern 3 (§3.3.3): model-controlled resource access via
// tool wrappers.
//
// Two host-side tools — list_resources and read_resource — give resources the
// model-native integration point the protocol doesn't define. The list tool aggregates
// across ALL connected servers, tagging each entry with a host-assigned server name;
// the read tool routes a read to the right server by that name. To prove the
// aggregation, this host spawns the SAME demo server twice under different labels
// ("docs" and "wiki") — same catalog, distinct routing keys.
//
// Usage: dotnet run

using Microsoft.Extensions.AI;
using System.Text.Json;

await using var clientManager = new ClientManager();
foreach (var serverName in (string[])["docs", "wiki"])
{
    await clientManager.ConnectAsync(serverName, new()
    {
        Name = serverName, // host-assigned label; routing never trusts the server's self-reported name
        Command = "dotnet",
        Arguments = [ResolveServerDll()],
    });
}

// Wrap the two methods as model-callable functions. Describing what an MCP *resource*
// is (not a file, not an HTTP URL) is what makes the model use these well — see §3.3.3.
var resourceTools = new ResourceTools(clientManager);
var tools = new List<AITool>
{
    AIFunctionFactory.Create(resourceTools.ListResources, "list_resources",
        "Lists available MCP resources from all connected servers. Resources are curated " +
        "context items (documents, reference material, configuration) identified by a URI " +
        "that is only meaningful to the server that owns it — it is not a file path or web URL. " +
        "Each entry includes the serverName needed to read it."),
    AIFunctionFactory.Create(resourceTools.ReadResource, "read_resource",
        "Reads one MCP resource and returns its content. Pass the serverName and uri exactly " +
        "as returned by list_resources; a URI is only valid on the server it was listed from."),
};

using IChatClient chat = new ScriptedChatClient();
var options = new ChatOptions { Tools = tools };
var history = new List<ChatMessage>
{
    new(ChatRole.User,
        "What reference material do we have across the connected servers? " +
        "Then show me the release notes from the wiki server."),
};

// The standard tool loop: run the model, execute its tool calls (locally — these are
// host tools, not MCP server tools), feed results back, repeat until it answers in text.
while (true)
{
    var response = await chat.GetResponseAsync(history, options);
    history.AddRange(response.Messages);

    var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
    if (calls.Count == 0)
        break;

    var results = new List<AIContent>();
    foreach (var call in calls)
    {
        var function = (AIFunction)tools.First(t => t.Name == call.Name);
        var result = await function.InvokeAsync(new(call.Arguments));
        results.Add(new FunctionResultContent(call.CallId, result?.ToString() ?? ""));
    }
    history.Add(new ChatMessage(ChatRole.Tool, results));
}

PrintHistory(history);
return;

static void PrintHistory(List<ChatMessage> history)
{
    Console.WriteLine(new string('=', 78));
    Console.WriteLine("CONVERSATION — resources reached the model through the wrapper tools");
    Console.WriteLine(new string('=', 78));
    for (int i = 0; i < history.Count; i++)
    {
        var role = history[i].Role.Value.ToUpperInvariant().PadRight(9);
        foreach (var content in history[i].Contents)
        {
            var line = content switch
            {
                FunctionCallContent fc =>
                    $"(tool call {fc.CallId}) {fc.Name}({JsonSerializer.Serialize(fc.Arguments)})",
                FunctionResultContent fr =>
                    $"(result for {fr.CallId}) {Truncate(fr.Result?.ToString() ?? "", 320)}",
                TextContent tc => Truncate(tc.Text, 320),
                _ => $"({content.GetType().Name})",
            };
            Console.WriteLine($"  [{i + 1}] {role} {line.ReplaceLineEndings("\n" + new string(' ', 18))}");
        }
    }
}

static string Truncate(string text, int max) =>
    text.Length <= max ? text : $"{text[..max]}… ({text.Length} chars total)";

// Locate the built demo server next to this sample. Build chapter03/demo-resource-server first.
static string ResolveServerDll()
{
    if (Environment.GetEnvironmentVariable("DEMO_RESOURCE_SERVER_DLL") is { Length: > 0 } overridePath)
        return overridePath;

    var candidate = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "demo-resource-server", "csharp", "bin", "Debug", "net10.0", "DemoResourceServer.dll"));
    if (!File.Exists(candidate))
        throw new FileNotFoundException(
            $"DemoResourceServer.dll not found at {candidate}. Run 'dotnet build' in " +
            "chapter03/demo-resource-server/csharp first, or point DEMO_RESOURCE_SERVER_DLL at the built server.");
    return candidate;
}
