using Microsoft.Extensions.AI;

// A deterministic IChatClient so the harness runs without a model. It behaves like a
// minimal channel-aware assistant: every user message gets exactly one reply-tool call,
// then a plain-text turn to end the loop. Swap in a real model via OPENAI_API_KEY.
sealed class ScriptedChatClient : IChatClient
{
    private int _callId;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var last = list[^1];

        // After the reply tool result comes back, end the turn with plain text
        // (which, on a channel, goes nowhere — the reply already went out).
        if (last.Contents.OfType<FunctionResultContent>().Any())
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Replied.")));

        var userText = list.Last(m => m.Role == ChatRole.User).Text;
        var replyText = userText.Contains("time", StringComparison.OrdinalIgnoreCase)
            ? $"It is {DateTime.Now:HH:mm} here. (scripted responder)"
            : $"You said: \"{userText.Split(':', 2)[^1].Trim()}\" (scripted responder)";

        var call = new FunctionCallContent($"call_{++_callId}", "reply",
            new Dictionary<string, object?> { ["text"] = replyText });
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Scripted client is non-streaming.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
