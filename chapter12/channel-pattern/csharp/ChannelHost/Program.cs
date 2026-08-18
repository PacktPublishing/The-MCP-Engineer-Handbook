using System.ClientModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// A minimal harness (§12.2): it owns the loop and the policy — when to poll, when to
// invoke the model, when to stop. MCP supplies its edges: the channel server carries
// the conversation (§12.4), and every message to the user is an observable tool call.

var serverProject = args.Length > 0 ? args[0] : "../ChannelServer";
var maxTurns = int.TryParse(Environment.GetEnvironmentVariable("MAX_TURNS"), out var mt) ? mt : int.MaxValue;

var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "demo-channel",
    Command = "dotnet",
    Arguments = ["run", "--project", serverProject],
});

await using var channel = await McpClient.CreateAsync(transport);
var tools = await channel.ListToolsAsync();
Console.WriteLine($"[harness] channel tools: {string.Join(", ", tools.Select(t => t.Name))}");
Console.WriteLine($"[harness] responder: {ModelLabel()}");
Console.WriteLine("[harness] polling every 2s — run the Chat app and say something.");

IChatClient loop = new ChatClientBuilder(CreateChatClient()).UseFunctionInvocation().Build();

var history = new List<ChatMessage>
{
    new(ChatRole.System,
        "You are a helpful assistant reached through a chat channel. The reply tool is " +
        "the only way to communicate with the user — plain output goes nowhere. Keep " +
        "replies short. Reply exactly once per incoming message, then stop."),
};

for (var turn = 0; turn < maxTurns;)
{
    var check = await channel.CallToolAsync("check_messages", new Dictionary<string, object?>());
    var inbound = string.Concat(check.Content.OfType<TextContentBlock>().Select(c => c.Text));
    if (inbound.Contains("No new messages"))
    {
        await Task.Delay(2000);
        continue;
    }

    turn++;
    Console.WriteLine($"[harness] inbound: {inbound.ReplaceLineEndings(" | ")}");
    history.Add(new ChatMessage(ChatRole.User, inbound));

    // One model turn. UseFunctionInvocation executes the reply tool call for us;
    // the messages it returns are the transcript of communication-as-tool-traffic.
    var response = await loop.GetResponseAsync(history, new ChatOptions { Tools = [.. tools] });
    history.AddRange(response.Messages);

    foreach (var call in response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>())
        Console.WriteLine($"[harness] tools/call {call.Name}({JsonSerializer.Serialize(call.Arguments)})");
}

// Scripted by default so the sample runs (and smoke-tests) without any model. Set
// OPENAI_API_KEY — plus OPENAI_BASE_URL for any OpenAI-compatible endpoint, Ollama
// included — to put a real model in the loop.
static IChatClient CreateChatClient()
{
    var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    if (string.IsNullOrEmpty(key)) return new ScriptedChatClient();

    var model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4.1-mini";
    var baseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
    var options = baseUrl is null ? null : new OpenAI.OpenAIClientOptions { Endpoint = new Uri(baseUrl) };
    return new OpenAI.Chat.ChatClient(model, new ApiKeyCredential(key), options).AsIChatClient();
}

static string ModelLabel() =>
    string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_API_KEY"))
        ? "scripted (no model; set OPENAI_API_KEY, optionally OPENAI_BASE_URL/OPENAI_MODEL)"
        : Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4.1-mini";
