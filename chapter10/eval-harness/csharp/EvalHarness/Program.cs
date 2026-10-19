// EvalHarness — an MCP server eval harness as a minimal host (§10.1.5).
//
// The server under test (DocsServer's tool classes) runs IN-PROCESS: the harness hosts it
// over the SDK's stream transports wired to a pair of in-memory pipes — no network, no
// subprocess, no port allocation — while exercising the full protocol stack. Each eval task
// is a prompt plus outcome checks, each task runs N times, and the report is pass RATES,
// not single-run verdicts (§10.1.5: one run of an eval task is an anecdote).
//
// Usage: dotnet run                 rung-2 style: real server, fixture data (default)
//        dotnet run -- --mock      rung-1: surface snapshotted from tools/list, fabricated results
//        dotnet run -- --runs 20   change N (default 5, 1..100)
//        dotnet run -- --dump dir  write each run's model-visible context to dir/<task>-run<N>.json

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.IO.Pipelines;
using System.Text.Json;

bool mock = args.Contains("--mock");
int runsPerTask = 5;
if (Array.IndexOf(args, "--runs") is var ri and >= 0 &&
    !(ri + 1 < args.Length && int.TryParse(args[ri + 1], out runsPerTask) && runsPerTask is >= 1 and <= 100))
    return Usage("--runs takes a whole number from 1 to 100");
string? dumpDir = null;
if (Array.IndexOf(args, "--dump") is var di and >= 0)
{
    if (di + 1 >= args.Length || args[di + 1].StartsWith("--"))
        return Usage("--dump takes a directory");
    dumpDir = Directory.CreateDirectory(args[di + 1]).FullName;
}

// --- Wire the server under test in-process over an in-memory stream pair. ------------------
// Two pipes make a full-duplex channel: the server reads what the client writes and vice
// versa. WithStreamServerTransport / StreamClientTransport are the C# SDK's in-memory seam.
var clientToServer = new Pipe();
var serverToClient = new Pipe();

var builder = Host.CreateApplicationBuilder();
builder.Logging.ClearProviders(); // keep the eval report readable
builder.Services
    .AddMcpServer()
    .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
    .WithTools<DocsTools>()
    .WithRequestFilters(ToolOrder.ListInDeclarationOrder<DocsTools>);
using var server = builder.Build();
await server.StartAsync();

var mcp = await McpClient.CreateAsync(new StreamClientTransport(
    serverInput: clientToServer.Writer.AsStream(),
    serverOutput: serverToClient.Reader.AsStream()));

IList<McpClientTool> realTools = await mcp.ListToolsAsync();

// --- Choose the tool surface and the dispatcher for the requested rung. --------------------
// Real mode calls through the protocol; mock mode (rung 1, §10.1.4) keeps the surface —
// snapshotted from tools/list so it cannot drift — and fabricates the results.
IList<AITool> surface;
Func<string, Dictionary<string, object?>, Task<(string Text, bool IsError)>> dispatch;

if (mock)
{
    var mockTools = MockSurface.FromRealTools(realTools);
    surface = [.. mockTools];
    dispatch = (name, callArgs) => Task.FromResult(MockSurface.Fabricate(name, callArgs));

    // Rung 1 needs no server: the snapshot is taken, so tear the real one down.
    await mcp.DisposeAsync();
    await server.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
}
else
{
    surface = [.. realTools];
    dispatch = async (name, callArgs) =>
    {
        try
        {
            CallToolResult result = await mcp.CallToolAsync(name, callArgs);
            var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text));
            return (text, result.IsError == true);
        }
        catch (McpException ex)
        {
            // A JSON-RPC error (a hallucinated tool name, say) goes back to the model as a
            // failed call, as a host would report it, instead of aborting the whole matrix.
            return (ex.Message, true);
        }
    };
}

// --- Run the corpus: N runs per task, scored as pass rates. --------------------------------
Console.WriteLine("=== MCP server eval — docs-server ===");
Console.WriteLine(mock
    ? $"mode:  rung-1 mock — surface snapshotted from tools/list ({surface.Count} tools), results fabricated; outcome checks SKIPPED"
    : "mode:  real server, in-process (WithStreamServerTransport <-> StreamClientTransport over paired pipes)");
Console.WriteLine($"model: {ModelLabel()}");
Console.WriteLine($"runs per task: {runsPerTask}\n");

var taskResults = new List<(EvalTask Task, int Passed, int Duds, Dictionary<string, int> FailedChecks)>();

foreach (var task in EvalCorpus.Tasks)
{
    int passed = 0, duds = 0;
    var failedChecks = new Dictionary<string, int>();

    for (int run = 0; run < runsPerTask; run++)
    {
        using var chat = CreateChatClient(run);
        Transcript transcript = await RunTaskAsync(chat, task.Prompt, surface, dispatch,
            dumpDir is null ? null : Path.Combine(dumpDir, $"{task.Id}-run{run}.json"));

        var applicable = Checks.Common.Concat(task.Checks)
            .Where(c => !mock || c.Kind == CheckKind.Behavioral).ToList();
        var failures = applicable.Where(c => !c.Passes(transcript)).ToList();

        if (failures.Count == 0)
        {
            passed++;
            continue;
        }
        foreach (var f in failures)
            failedChecks[f.Description] = failedChecks.GetValueOrDefault(f.Description) + 1;

        // The dud pattern (§10.1.2): every call technically succeeded and the model answered
        // confidently — the transcript says "success" — yet an outcome check failed.
        if (failures.All(f => f.Kind == CheckKind.Outcome) &&
            !transcript.ToolCalls.Any(c => c.IsError) &&
            transcript.FinalAnswer.Length > 0)
            duds++;
    }
    taskResults.Add((task, passed, duds, failedChecks));
}

// --- Report. --------------------------------------------------------------------------------
Console.WriteLine($"{"task",-24} {"rate",5}  {"runs",5}  notes");
foreach (var (task, passed, duds, failedChecks) in taskResults)
{
    var notes = new List<string>();
    if (duds > 0)
        notes.Add($"DUD x{duds}: transcript clean, but " +
                  string.Join("; ", failedChecks.Select(kv => $"'{kv.Key}' failed x{kv.Value}")));
    else if (failedChecks.Count > 0)
        notes.Add(string.Join("; ", failedChecks.Select(kv => $"'{kv.Key}' failed x{kv.Value}")));
    Console.WriteLine(
        $"{task.Id,-24} {100.0 * passed / runsPerTask,4:0}%  {passed + "/" + runsPerTask,5}  {string.Join(" ", notes)}");
}

int totalRuns = taskResults.Count * runsPerTask;
int totalPassed = taskResults.Sum(r => r.Passed);
int totalDuds = taskResults.Sum(r => r.Duds);
Console.WriteLine($"\naggregate: {totalPassed}/{totalRuns} runs passed " +
                  $"({100.0 * totalPassed / totalRuns:0.#}%); " +
                  $"{taskResults.Count(r => r.Passed == runsPerTask)}/{taskResults.Count} tasks at 100%");
if (totalDuds > 0)
    Console.WriteLine($"duds caught: {totalDuds} run(s) completed without a single tool error " +
                      "and still failed an outcome check — invisible to transcript-level scoring");
if (mock)
    Console.WriteLine("note: rung 1 cannot see duds — with fabricated results there is no wrong " +
                      "answer to catch; run without --mock for outcome checks");
if (dumpDir is not null)
    Console.WriteLine($"transcripts: {totalRuns} file(s) in {dumpDir}");

if (!mock)
{
    await mcp.DisposeAsync();
    await server.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
}
return totalPassed == totalRuns ? 0 : 1;

static int Usage(string problem)
{
    Console.Error.WriteLine($"error: {problem}");
    Console.Error.WriteLine("usage: dotnet run [-- [--mock] [--runs N] [--dump DIR]]");
    return 2;
}

// One eval run: present the surface, run the model loop, record everything. The Transcript is
// the scoring summary; with --dump, the run also writes exactly what the model saw and did
// (the tool surface, options, and every message) as the debugging ground truth (§10.1.5).
static async Task<Transcript> RunTaskAsync(
    IChatClient chat, string prompt, IList<AITool> tools,
    Func<string, Dictionary<string, object?>, Task<(string Text, bool IsError)>> dispatch,
    string? dumpPath)
{
    var transcript = new Transcript();
    var options = new ChatOptions { Tools = tools };
    var history = new List<ChatMessage> { new(ChatRole.User, prompt) }; // no system prompt
    string? responseModelId = null;

    for (int turn = 0; turn < 8; turn++) // budget guard: a looping model fails the run
    {
        transcript.ModelTurns++;
        ChatResponse response = await chat.GetResponseAsync(history, options);
        history.AddRange(response.Messages);
        responseModelId = response.ModelId ?? responseModelId;

        var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
        if (calls.Count == 0)
        {
            transcript.FinalAnswer = response.Text;
            transcript.Termination = "final_answer";
            break;
        }

        var results = new List<AIContent>();
        foreach (var call in calls)
        {
            var callArgs = call.Arguments?.ToDictionary(kv => kv.Key, kv => kv.Value)
                           ?? new Dictionary<string, object?>();
            var (text, isError) = await dispatch(call.Name, callArgs);
            transcript.ToolCalls.Add(new ToolCallRecord(
                call.Name, JsonSerializer.Serialize(callArgs), text, isError));
            results.Add(new FunctionResultContent(call.CallId, text));
        }
        history.Add(new ChatMessage(ChatRole.Tool, results));
    }
    // A run that used up its turns keeps Termination = "turn_budget"; Checks.Completed fails it.

    if (dumpPath is not null)
        await File.WriteAllTextAsync(dumpPath, JsonSerializer.Serialize(new
        {
            model = ModelLabel(),
            responseModelId,
            options = new { options.ModelId, options.Temperature, options.MaxOutputTokens, options.Instructions },
            tools = tools.OfType<AIFunction>().Select(t => new { t.Name, t.Description, inputSchema = t.JsonSchema }),
            messages = history,
            modelTurns = transcript.ModelTurns,
            termination = transcript.Termination,
        }, new JsonSerializerOptions(AIJsonUtilities.DefaultOptions) { WriteIndented = true }));
    return transcript;
}

// Default: the deterministic scripted model, parameterized by run index so its one flaky
// behavior is reproducible. Set OPENAI_API_KEY (and optionally OPENAI_MODEL) to point the
// same harness at a real model — pass rates then measure real context quality.
static IChatClient CreateChatClient(int runIndex)
{
    var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    if (!string.IsNullOrEmpty(key))
        return new OpenAI.Chat.ChatClient(
            Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4.1-mini", key).AsIChatClient();
    return new ScriptedChatClient(runIndex);
}

static string ModelLabel() =>
    string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_API_KEY"))
        ? "scripted (deterministic per run index; no API key needed)"
        : $"openai:{Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4.1-mini"}";
