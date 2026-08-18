// TaskInjectionHost — the model integration problem for long-running operations (§6.2.11).
//
// Chat-model APIs require every tool call to be immediately followed by its result, so when
// an operation outlives the model turn the host synthesizes an immediate "started" result to
// unblock the model. When the REAL result arrives later, it has nowhere natural to go. This
// host demonstrates the four known injection strategies side by side and prints the exact
// conversation-history shape each one produces.
//
// Usage: dotnet run -- [prepend|synthetic-turn|meta-tool|fresh-invocation|--all]

using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;

string[] known = ["prepend", "synthetic-turn", "meta-tool", "fresh-invocation"];
string[] strategies = args switch
{
    [] => ["prepend"],
    ["--all"] => known,
    [var s] when known.Contains(s) => [s],
    _ => [],
};
if (strategies.Length == 0)
{
    Console.Error.WriteLine($"Usage: dotnet run -- [{string.Join('|', known)}|--all]");
    return 1;
}

var summaries = new List<(string Strategy, int Injected, string Shape)>();
foreach (var strategy in strategies)
    summaries.Add(await RunScenarioAsync(strategy));

if (summaries.Count > 1)
{
    Console.WriteLine(new string('=', 78));
    Console.WriteLine("COMPARISON — history shape produced when the real result landed");
    Console.WriteLine(new string('=', 78));
    foreach (var (strategy, injected, shape) in summaries)
        Console.WriteLine($"  {strategy,-17} +{injected} messages  {shape}");
}
return 0;

static async Task<(string, int, string)> RunScenarioAsync(string strategy)
{
    Console.WriteLine(new string('=', 78));
    Console.WriteLine($"STRATEGY: {strategy}");
    Console.WriteLine(new string('=', 78));

    // Connect to the demo server as a stdio child process. McpClient.CreateAsync performs
    // the connection handshake and returns a ready client.
    await using var mcp = await McpClient.CreateAsync(
        new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "report-server",
            Command = "dotnet",
            Arguments = [ResolveServerDll()],
        }));

    // McpClientTool derives from Microsoft.Extensions.AI.AIFunction, so the server's tools
    // drop straight into ChatOptions.Tools for any IChatClient.
    IList<McpClientTool> mcpTools = await mcp.ListToolsAsync();

    // Host-side state and meta-tool for the meta-tool strategy. This tool is implemented by
    // the HOST, not the MCP server: it hands the model whatever finished while it wasn't looking.
    var completedOps = new List<CompletedOperation>();
    AIFunction checkCompleted = AIFunctionFactory.Create(
        () => completedOps.Count == 0
            ? (object)"No completed operations."
            : completedOps,
        "check_completed_operations",
        "Returns the results of long-running operations that completed since the last check.");

    var tools = new List<AITool>(mcpTools);
    if (strategy == "meta-tool")
        tools.Add(checkCompleted);

    using IChatClient chat = CreateChatClient();
    var options = new ChatOptions { Tools = tools };
    var history = new List<ChatMessage> { new(ChatRole.User, "Please generate the quarterly sales report.") };

    // Act 1 — the model calls start_report; the tool returns IMMEDIATELY with a handle. That
    // immediate "operation started" tool result is what keeps the conversation legal: the API
    // would reject a history in which the tool call dangles unanswered. The model acknowledges
    // and the turn ends — with the real work still running.
    string? operationId = null;
    await RunModelTurnAsync(chat, history, options, mcp, completedOps, id => operationId = id);
    if (operationId is null)
        throw new InvalidOperationException("The model did not start the report operation.");

    // The host polls for the real result on the model's behalf. (With the Tasks extension this
    // would be tasks/get instead of a status tool; the injection problem below is unchanged.)
    string report = await PollForResultAsync(mcp, operationId);
    completedOps.Add(new CompletedOperation(operationId, report));
    var before = history.Count;
    Console.WriteLine($"\n>>> Real result for {operationId} arrived on the host. Injecting via '{strategy}'.\n");

    // Act 2 — the real result lands and must re-enter the conversation somehow.
    string shape;
    switch (strategy)
    {
        case "prepend":
            // Hold the result until the NEXT real user message, then prepend it. Nothing
            // reaches the model until the user speaks again — cheap, legal, but not proactive,
            // and the result arrives wearing the user's voice.
            var nextUserText = "Thanks. Anything in it I should flag for the board?";
            history.Add(new ChatMessage(ChatRole.User,
                $"[Result of completed operation {operationId}]\n{report}\n---\n{nextUserText}"));
            await RunModelTurnAsync(chat, history, options, mcp, completedOps);
            shape = "result piggybacks inside the next real user message";
            break;

        case "synthetic-turn":
            // Fabricate a turn right away and run the model on it. Proactive — the assistant
            // can announce the result unprompted — but the history now contains a user-role
            // message no user ever wrote.
            history.Add(new ChatMessage(ChatRole.User,
                $"<operation-update>Operation {operationId} completed. Result:\n{report}</operation-update>"));
            await RunModelTurnAsync(chat, history, options, mcp, completedOps);
            shape = "fabricated user-role turn appended immediately";
            break;

        case "meta-tool":
            // The model pulls the result itself through a host-side tool, keeping every
            // tool call legally paired with its result — but only after something prompts
            // it to look. Here the nudge is the next user message.
            history.Add(new ChatMessage(ChatRole.User, "Any update on the report?"));
            await RunModelTurnAsync(chat, history, options, mcp, completedOps);
            shape = "user nudge -> assistant tool call -> tool result -> assistant summary";
            break;

        case "fresh-invocation":
            // Headless-agent style: abandon the old conversation and start a new invocation
            // with the result in its initial context. Clean history, no synthesis — at the
            // cost of everything the previous conversation knew.
            history = new List<ChatMessage>
            {
                new(ChatRole.System,
                    "You are a reporting agent. A long-running operation you started earlier has completed; " +
                    "summarize its result for the record."),
                new(ChatRole.User,
                    $"Operation {operationId} (quarterly sales report) has completed. Result:\n{report}"),
            };
            before = 0;
            await RunModelTurnAsync(chat, history, options, mcp, completedOps);
            shape = "new invocation seeded with the result; original history abandoned";
            break;

        default:
            throw new ArgumentOutOfRangeException(nameof(strategy));
    }

    PrintHistory(strategy, history, injectedFrom: before);
    return (strategy, history.Count - before, shape);
}

// One model "turn": call the chat model, execute any tool calls it makes (MCP tools via the
// McpClient, the meta-tool locally), feed the results back, and repeat until it answers in text.
static async Task RunModelTurnAsync(
    IChatClient chat, List<ChatMessage> history, ChatOptions options,
    McpClient mcp, List<CompletedOperation> completedOps, Action<string>? onOperationStarted = null)
{
    while (true)
    {
        ChatResponse response = await chat.GetResponseAsync(history, options);
        history.AddRange(response.Messages);

        var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
        if (calls.Count == 0)
            return;

        var results = new List<AIContent>();
        foreach (var call in calls)
        {
            string resultText;
            if (call.Name == "check_completed_operations")
            {
                resultText = completedOps.Count == 0
                    ? "No completed operations."
                    : JsonSerializer.Serialize(completedOps);
            }
            else
            {
                CallToolResult result = await mcp.CallToolAsync(
                    call.Name,
                    call.Arguments?.ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<string, object?>());
                resultText = string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));

                if (call.Name == "start_report")
                    onOperationStarted?.Invoke(
                        JsonDocument.Parse(resultText).RootElement.GetProperty("operationId").GetString()!);
            }
            results.Add(new FunctionResultContent(call.CallId, resultText));
        }
        history.Add(new ChatMessage(ChatRole.Tool, results));
    }
}

static async Task<string> PollForResultAsync(McpClient mcp, string operationId)
{
    while (true)
    {
        CallToolResult result = await mcp.CallToolAsync(
            "get_report_result", new Dictionary<string, object?> { ["operationId"] = operationId });
        var root = JsonDocument.Parse(
            string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text))).RootElement;
        if (root.GetProperty("status").GetString() == "completed")
            return root.GetProperty("report").GetString()!;
        await Task.Delay(500);
    }
}

static void PrintHistory(string label, List<ChatMessage> history, int injectedFrom)
{
    Console.WriteLine($"--- Conversation history after '{label}' " +
                      $"(messages {injectedFrom + 1}+ appeared when the result landed) ---");
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
                    $"(result for {fr.CallId}) {fr.Result}",
                TextContent tc => tc.Text,
                _ => $"({content.GetType().Name})",
            };
            Console.WriteLine($"  [{i + 1}] {role} {line.ReplaceLineEndings("\n" + new string(' ', 18))}");
        }
    }
    Console.WriteLine();
}

// Default: the deterministic scripted model (no key needed). Set OPENAI_API_KEY (and
// optionally OPENAI_MODEL) to run the same scenarios against a real model through the same
// IChatClient interface. Any other provider with an IChatClient adapter plugs in the same way.
static IChatClient CreateChatClient()
{
    var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    if (!string.IsNullOrEmpty(key))
    {
        var model = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4.1-mini";
        Console.WriteLine($"Using OpenAI model '{model}' (OPENAI_API_KEY is set).");
        return new OpenAI.Chat.ChatClient(model, key).AsIChatClient();
    }
    return new ScriptedChatClient();
}

// Locate the built server binary next to this project. Build csharp/ReportServer first.
static string ResolveServerDll()
{
    if (Environment.GetEnvironmentVariable("REPORT_SERVER_DLL") is { Length: > 0 } overridePath)
        return overridePath;

    var candidate = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..",
        "ReportServer", "bin", "Debug", "net10.0", "ReportServer.dll"));
    if (!File.Exists(candidate))
        throw new FileNotFoundException(
            $"ReportServer.dll not found at {candidate}. Run 'dotnet build' in csharp/ReportServer first, " +
            "or point REPORT_SERVER_DLL at the built server.");
    return candidate;
}

record CompletedOperation(string OperationId, string Result);
