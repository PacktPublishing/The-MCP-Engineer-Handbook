using System.ClientModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// A minimal harness (§12.2): it owns the loop and the policy — when to poll, when to
// invoke the model, when to stop. MCP supplies its edges: the channel server carries
// the conversation (§12.4), and every message to the user is an observable tool call.

var serverProject = args.Length > 0 ? args[0] : "../ChannelServer";

// The stop policy. MAX_TURNS caps processed batches (idle polls don't count); unset means
// run until Ctrl+C. Each batch also gets a deadline, a model round-trip cap, and a send budget.
var maxTurns = int.MaxValue;
if (Environment.GetEnvironmentVariable("MAX_TURNS") is { } maxTurnsText
    && (!int.TryParse(maxTurnsText, out maxTurns) || maxTurns < 1))
{
    Console.Error.WriteLine($"[harness] MAX_TURNS must be a positive integer, not '{maxTurnsText}'.");
    return 1;
}
var batchDeadline = TimeSpan.FromSeconds(60);
const int RepliesPerBatch = 1;

// The demo channel is one trusted local conversation, so never a shared, guessable
// directory: without CHANNEL_DIR, create a private one (owner-only on Unix) and say where.
var channelDir = Environment.GetEnvironmentVariable("CHANNEL_DIR");
if (string.IsNullOrEmpty(channelDir))
{
    channelDir = Directory.CreateTempSubdirectory("mcp-channel-").FullName;
    Console.WriteLine($"[harness] CHANNEL_DIR unset; created {channelDir} (export it for the Chat app)");
}

var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "demo-channel",
    Command = "dotnet",
    Arguments = ["run", "--project", serverProject],
    EnvironmentVariables = new Dictionary<string, string?> { ["CHANNEL_DIR"] = channelDir },
});

await using var channel = await McpClient.CreateAsync(transport);
var tools = await channel.ListToolsAsync();

// Polling stays in the harness: the model sees only reply, behind a per-batch send budget.
var reply = new ReplyBudget(tools.Single(t => t.Name == "reply"), RepliesPerBatch);
Console.WriteLine($"[harness] channel tools: {string.Join(", ", tools.Select(t => t.Name))} (model sees: reply)");
Console.WriteLine($"[harness] responder: {ModelLabel()}");
Console.WriteLine("[harness] polling every 2s — run the Chat app and say something.");

// Two tool round trips per batch (reply, then one spare); the middleware's own default is 40.
IChatClient loop = new ChatClientBuilder(CreateChatClient())
    .UseFunctionInvocation(configure: f => f.MaximumIterationsPerRequest = 2)
    .Build();

// History grows for the life of the process: the demo has no retention or compaction policy.
var history = new List<ChatMessage>
{
    new(ChatRole.System,
        "You are a helpful assistant reached through a chat channel. Each user turn is one " +
        "batch of channel messages as JSON: {\"messages\":[{\"id\",\"from\",\"text\"}]}. The " +
        "reply tool is the only way to communicate with the user — plain output goes nowhere. " +
        "Call reply exactly once per batch, answering every message in it. Keep replies " +
        "short, then stop."),
};

for (int turn = 0, failedPolls = 0; turn < maxTurns;)
{
    // A failed poll is a harness event, never model input: back off, give up after three.
    if (await PollAsync(channel) is not { } batch)
    {
        if (++failedPolls == 3)
        {
            Console.Error.WriteLine("[harness] check_messages failed 3 times in a row; stopping.");
            return 1;
        }
        await Task.Delay(2000 * failedPolls);
        continue;
    }
    failedPolls = 0;

    if (batch.Rejected.Count > 0)
        Console.WriteLine($"[harness] channel rejected malformed inbox line(s): {string.Join(", ", batch.Rejected)}");
    if (batch.Messages.Count == 0)
    {
        await Task.Delay(2000); // idle: no model call
        continue;
    }

    turn++;
    foreach (var message in batch.Messages)
        Console.WriteLine($"[harness] inbound #{message.Id} {message.From}: {message.Text.ReplaceLineEndings(" | ")}");

    // The batch goes to the model as data: id, sender, and text stay separate fields, and
    // control flow above never looked at the text.
    history.Add(new ChatMessage(ChatRole.User, JsonSerializer.Serialize(new { batch.Messages }, JsonSerializerOptions.Web)));

    // One model turn. UseFunctionInvocation executes the reply tool call for us;
    // the messages it returns are the transcript of communication-as-tool-traffic.
    reply.StartBatch();
    using var deadline = new CancellationTokenSource(batchDeadline);
    try
    {
        var response = await loop.GetResponseAsync(history, new ChatOptions { Tools = [reply] }, deadline.Token);
        history.AddRange(response.Messages);

        var contents = response.Messages.SelectMany(m => m.Contents).ToList();
        foreach (var call in contents.OfType<FunctionCallContent>())
        {
            var outcome = contents.OfType<FunctionResultContent>().FirstOrDefault(r => r.CallId == call.CallId)?.Result;
            Console.WriteLine($"[harness] tools/call {call.Name}({JsonSerializer.Serialize(call.Arguments)}) -> {outcome ?? "not run"}");
        }
    }
    catch (OperationCanceledException) when (deadline.IsCancellationRequested)
    {
        Console.WriteLine($"[harness] batch hit its {batchDeadline.TotalSeconds:0}s deadline; not retried.");
    }

    // Assistant text is not a delivery: only a successful reply call reaches the user.
    if (reply.Delivered == 0)
        Console.WriteLine("[harness] no reply delivered for this batch.");
}

return 0;

static async Task<MessageBatch?> PollAsync(McpClient channel)
{
    try
    {
        var result = await channel.CallToolAsync("check_messages", new Dictionary<string, object?>());
        if (result.IsError is true)
        {
            Console.Error.WriteLine($"[harness] check_messages failed: {string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text))}");
            return null;
        }
        return result.StructuredContent?.Deserialize<MessageBatch>(JsonSerializerOptions.Web) is { Messages: not null, Rejected: not null } batch
            ? batch
            : throw new JsonException("check_messages returned no well-formed batch");
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        Console.Error.WriteLine($"[harness] check_messages failed: {ex.Message}");
        return null;
    }
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

record ChannelMessage(int Id, string From, string Text);
record MessageBatch(List<ChannelMessage> Messages, List<int> Rejected);

// The send budget is harness policy, not a prompt request: once a batch has used it,
// reply refuses without touching the channel. Only a successful call counts as delivered.
sealed class ReplyBudget(McpClientTool reply, int perBatch) : DelegatingAIFunction(reply)
{
    public int Delivered { get; private set; }
    public void StartBatch() => Delivered = 0;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        if (Delivered >= perBatch)
            return "Not sent: this batch's reply budget is spent. End your turn.";

        var result = await reply.CallAsync(arguments, cancellationToken: cancellationToken);
        if (result.IsError is not true) Delivered++;
        return string.Concat(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
    }
}
