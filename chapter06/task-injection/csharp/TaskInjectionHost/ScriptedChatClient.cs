using Microsoft.Extensions.AI;

// A deterministic IChatClient — no API key needed. It pattern-matches the incoming history
// and replies with the turn a capable tool-calling model would plausibly produce, so the
// four injection strategies can be compared on identical conversations. Swap in a real
// provider (see CreateChatClient in Program.cs) to observe actual model behavior.
sealed class ScriptedChatClient : IChatClient
{
    private int _callCounter;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var history = messages.ToList();
        return Task.FromResult(new ChatResponse(NextTurn(history, history[^1], options)));
    }

    private ChatMessage NextTurn(List<ChatMessage> history, ChatMessage last, ChatOptions? options)
    {
        // A tool result just came back — react to it.
        if (last.Role == ChatRole.Tool &&
            last.Contents.OfType<FunctionResultContent>().LastOrDefault() is { } toolResult)
        {
            var callName = FindCallName(history, toolResult.CallId);
            var resultText = toolResult.Result?.ToString() ?? "";
            return callName switch
            {
                // The synthesized-immediate "operation started" result: acknowledge and move on.
                "start_report" => Text(
                    "I've started generating the report. It's running in the background — " +
                    "I'll share the results as soon as they're available."),

                // meta-tool strategy: the meta-tool returned completed work (or nothing).
                "check_completed_operations" => Text(resultText.Contains("No completed")
                    ? "Nothing has finished yet — I'll keep an eye on it."
                    : $"Yes — the report just finished. {SummaryLine()}"),

                _ => Text($"Tool '{callName}' returned: {resultText}"),
            };
        }

        var text = string.Concat(last.Contents.OfType<TextContent>().Select(c => c.Text));

        // prepend strategy: the completed result is riding along inside a real user message.
        if (text.Contains("[Result of completed operation"))
            return Text($"Good news first: the quarterly sales report finished. {SummaryLine()} " +
                        "To answer your question: I'd flag the two underperforming regions for the board.");

        // synthetic-turn strategy: a fabricated turn is announcing the result.
        if (text.Contains("<operation-update>"))
            return Text($"Update: the quarterly sales report is ready. {SummaryLine()}");

        // fresh-invocation strategy: a brand-new run seeded with the result.
        if (text.Contains("has completed. Result:"))
            return Text($"For the record: the operation completed successfully. {SummaryLine()}");

        // meta-tool strategy: the user nudges, so check for finished work.
        if (text.Contains("Any update", StringComparison.OrdinalIgnoreCase) &&
            HasTool(options, "check_completed_operations"))
            return Call("check_completed_operations", new Dictionary<string, object?>());

        // Opening user request: kick off the long-running report.
        if (last.Role == ChatRole.User &&
            text.Contains("report", StringComparison.OrdinalIgnoreCase) &&
            HasTool(options, "start_report"))
            return Call("start_report", new Dictionary<string, object?> { ["topic"] = "quarterly sales" });

        return Text("(scripted model has no line for this input)");
    }

    private static string SummaryLine() =>
        "Revenue is up 14% quarter over quarter, driven by the new subscription tier; " +
        "two regions missed target (details in the appendix).";

    private static string FindCallName(List<ChatMessage> history, string callId) =>
        history.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
               .LastOrDefault(c => c.CallId == callId)?.Name ?? "(unknown)";

    private static bool HasTool(ChatOptions? options, string name) =>
        options?.Tools?.Any(t => t.Name == name) is true;

    private static ChatMessage Text(string text) => new(ChatRole.Assistant, text);

    private ChatMessage Call(string name, IDictionary<string, object?> args) =>
        new(ChatRole.Assistant, [new FunctionCallContent($"call-{++_callCounter}", name, args)]);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The scripted client is non-streaming.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
