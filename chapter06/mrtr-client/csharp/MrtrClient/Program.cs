// MRTR client drivers (Chapter 6, Section 6.2.5).
//
// Default mode — the SDK-native path, C#'s only full-SDK route: in
// the 2.0.0 SDK `McpClient` fulfils `input_required` results AUTOMATICALLY and
// unconditionally, inside SendRequestAsync. Each embedded request is
// dispatched to the matching handler in `McpClientOptions.Handlers`
// (ElicitationHandler here), the call retries with the collected
// inputResponses and a byte-exact requestState echo on a fresh request id,
// up to a HARD-CODED 10 rounds. There is no opt-out, no way to see the
// interim result, and no budget knob — so the retry budget you control is
// the SDK's fixed 10, and timeout/cancel come from the CancellationToken.
//
// --manual mode — the book's gather-and-retry loop with a configurable
// budget, expressed over the raw transport (see ManualLoop.cs for why the
// public client API cannot host it).
//
// Run (after `dotnet build` of both projects, from the csharp/ directory):
//   dotnet run --project MrtrClient -- [book_meeting|never_satisfied] [--manual]
// Env: MRTR_ANSWERS / MRTR_POLICY (see InputHandler.cs); MRTR_MAX_ROUNDS
// (manual mode only — the native path's 10 is not configurable).

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json.Nodes;

var manual = args.Contains("--manual");
var toolName = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "book_meeting";
var maxRounds = int.TryParse(Environment.GetEnvironmentVariable("MRTR_MAX_ROUNDS"), out var mr) ? mr : 10;

// The companion server, built alongside this project.
var serverDll = Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "DemoServer", "bin", "Debug", "net10.0", "DemoServer.dll"));
if (!File.Exists(serverDll))
{
    Console.Error.WriteLine($"Build DemoServer first: {serverDll} not found.");
    return 1;
}
var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Command = "dotnet",
    Arguments = [serverDll],
    Name = "mrtr-demo-server",
});

// User-facing cancel: Ctrl+C cancels the in-flight call and the loop.
// Whole-flow timeout: 120 s across all rounds.
using var canceller = new CancellationTokenSource(TimeSpan.FromSeconds(120));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; canceller.Cancel(); };

try
{
    if (manual)
    {
        // Raw-transport session: handshake-less 2026-07-28 requests.
        await using var session = await transport.ConnectAsync(canceller.Token);
        Console.WriteLine($"-> tools/call {toolName} (manual gather-and-retry loop)");
        var arguments = toolName == "book_meeting" ? new JsonObject { ["room"] = "4B" } : [];
        var result = await ManualLoop.CallToolGatheringInputAsync(
            session, toolName, arguments, maxRounds, TimeSpan.FromSeconds(120), canceller.Token);
        Console.WriteLine($"<- final result: {FirstText(result)}");
    }
    else
    {
        var options = new McpClientOptions
        {
            ClientInfo = new Implementation { Name = "mrtr-client", Version = "0.1.0" },
            // Without this, requests carry no elicitation capability and a
            // spec-following server must not embed elicit requests.
            Capabilities = new ClientCapabilities { Elicitation = new ElicitationCapability() },
            Handlers = new McpClientHandlers
            {
                // The SDK's MRTR driver dispatches embedded elicitation
                // requests here. Form rendering, policy, and validation stay
                // ours — see InputHandler.cs.
                ElicitationHandler = (request, _) =>
                    InputHandler.HandleElicitationAsync(request ?? throw new ArgumentNullException(nameof(request))),
            },
            // ProtocolVersion unset: probe server/discover, prefer 2026-07-28.
        };
        await using var client = await McpClient.CreateAsync(transport, options, cancellationToken: canceller.Token);
        Console.WriteLine(
            $"-> tools/call {toolName} (SDK drives the MRTR rounds; negotiated {client.NegotiatedProtocolVersion})");
        var arguments = toolName == "book_meeting" ? new Dictionary<string, object?> { ["room"] = "4B" } : [];
        var result = await client.CallToolAsync(toolName, arguments, cancellationToken: canceller.Token);
        Console.WriteLine($"<- final result: {FirstText(result)}");
    }
    return 0;
}
catch (Exception error) when (error is not OperationCanceledException)
{
    Console.WriteLine($"<- failed: {error.Message}");
    return 1;
}
catch (OperationCanceledException)
{
    Console.WriteLine("<- failed: tool call cancelled or timed out");
    return 1;
}

static string FirstText(CallToolResult result)
    => result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text
        ?? System.Text.Json.JsonSerializer.Serialize(result.Content, ModelContextProtocol.McpJsonUtilities.DefaultOptions);
