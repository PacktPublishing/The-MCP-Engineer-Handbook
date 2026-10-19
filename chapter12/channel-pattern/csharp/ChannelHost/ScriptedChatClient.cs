using System.Text.Json;
using Microsoft.Extensions.AI;

// A deterministic IChatClient so the harness runs without a model. It behaves like a
// minimal channel-aware assistant: every inbound batch gets exactly one reply-tool call,
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

        // The user turn is the harness's JSON batch: {"messages":[{"id","from","text"}]}.
        var batch = JsonSerializer.Deserialize<JsonElement>(list.Last(m => m.Role == ChatRole.User).Text);
        var texts = batch.GetProperty("messages").EnumerateArray()
            .Select(m => m.GetProperty("text").GetString() ?? "").ToList();
        var replyText = texts.Any(t => t.Contains("time", StringComparison.OrdinalIgnoreCase))
            ? $"It is {DateTime.Now:HH:mm} here. (scripted responder)"
            : $"You said: {string.Join(" / ", texts.Select(t => $"\"{t}\""))} (scripted responder)";

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
