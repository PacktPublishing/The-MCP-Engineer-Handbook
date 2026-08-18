using Microsoft.Extensions.AI;

// A deterministic IChatClient — no API key needed. It plays the model's side of
// Pattern 3: discover what resources exist via list_resources, then route a read to
// the right server via read_resource. Swap in any real provider's IChatClient adapter
// to watch an actual model drive the same two tools.
sealed class ScriptedChatClient : IChatClient
{
    private int _callCounter;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var history = messages.ToList();
        return Task.FromResult(new ChatResponse(NextTurn(history, history[^1])));
    }

    private ChatMessage NextTurn(List<ChatMessage> history, ChatMessage last)
    {
        if (last.Role == ChatRole.Tool &&
            last.Contents.OfType<FunctionResultContent>().LastOrDefault() is { } toolResult)
        {
            var callName = FindCallName(history, toolResult.CallId);
            var resultText = toolResult.Result?.ToString() ?? "";
            return callName switch
            {
                // The aggregated catalog came back; every entry carries its serverName.
                // The user asked for the wiki server's release notes — route the read there.
                "list_resources" when resultText.Contains("\"wiki\"") =>
                    Call("read_resource", new Dictionary<string, object?>
                    {
                        ["serverName"] = "wiki",
                        ["uri"] = "file:///release_notes.md",
                    }),

                "read_resource" => Text(
                    "Both servers expose the same catalog: a user guide, release notes, a tip of " +
                    "the day, plus telemetry and podcast material. From the wiki server's release " +
                    "notes: offline vaults can now be converted to synced vaults in place, search " +
                    "now indexes attachments up to 25 MB, and the legacy nimbus:// scheme is deprecated."),

                _ => Text($"Tool '{callName}' returned: {resultText}"),
            };
        }

        // Opening user request: discover the catalog first.
        if (last.Role == ChatRole.User)
            return Call("list_resources", new Dictionary<string, object?>());

        return Text("(scripted model has no line for this input)");
    }

    private static string FindCallName(List<ChatMessage> history, string callId) =>
        history.SelectMany(m => m.Contents).OfType<FunctionCallContent>()
               .LastOrDefault(c => c.CallId == callId)?.Name ?? "(unknown)";

    private static ChatMessage Text(string text) => new(ChatRole.Assistant, text);

    private ChatMessage Call(string name, IDictionary<string, object?> args) =>
        new(ChatRole.Assistant, [new FunctionCallContent($"call-{++_callCounter}", name, args)]);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The scripted client is non-streaming.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
