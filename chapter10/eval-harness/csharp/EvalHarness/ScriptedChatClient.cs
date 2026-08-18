using Microsoft.Extensions.AI;
using System.Text.Json;

// A deterministic IChatClient — no API key needed. It pattern-matches the eval task (the
// first user message) plus the tool results so far, and replies with the turn a mid-tier
// tool-calling model would plausibly produce — including one deliberate dud (it reads the
// first search hit for the refund task, which is the deprecated FAQ) and one deliberately
// flaky behavior (it only reaches for count_documents on even run indices, standing in for
// the marginal-context variance that N-run pass rates exist to surface, §10.1.5).
//
// It builds answers from the actual tool-result payloads rather than from canned strings,
// so the same script drives both the real server and the rung-1 mock surface.
sealed class ScriptedChatClient(int runIndex) : IChatClient
{
    private int _callCounter;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var history = messages.ToList();
        return Task.FromResult(new ChatResponse(NextTurn(history)));
    }

    private ChatMessage NextTurn(List<ChatMessage> history)
    {
        var task = history.First(m => m.Role == ChatRole.User).Text;
        var calls = history.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
        var lastResult = history[^1].Contents.OfType<FunctionResultContent>().LastOrDefault()
            ?.Result?.ToString() ?? "";

        if (task.Contains("shipping", StringComparison.OrdinalIgnoreCase))
        {
            if (calls.Count == 0)
                return Call("search_documents", new() { ["query"] = "shipping" });
            var (title, snippet) = FirstMatch(lastResult);
            return Text($"Per '{title}': {snippet}");
        }

        if (task.Contains("refund window", StringComparison.OrdinalIgnoreCase))
        {
            if (calls.Count == 0)
                return Call("search_documents", new() { ["query"] = "refund" });
            if (calls.Count == 1) // read the FIRST hit — the deprecated FAQ. The dud.
                return Call("read_document", new() { ["id"] = FirstMatchId(lastResult) });
            return Text($"According to '{Field(lastResult, "title")}': {Field(lastResult, "body")}");
        }

        if (task.Contains("How many documents", StringComparison.OrdinalIgnoreCase))
        {
            if (runIndex % 2 != 0) // flaky: on odd runs the tool never gets picked up
                return Text("There are 2 documents tagged 'billing'.");
            if (calls.Count == 0)
                return Call("count_documents", new() { ["tag"] = "billing" });
            return Text($"There are {Field(lastResult, "count")} documents tagged 'billing'.");
        }

        if (task.Contains("api-limits", StringComparison.OrdinalIgnoreCase))
        {
            if (calls.Count == 0) // obey the prompt's (wrong) id — this call fails
                return Call("read_document", new() { ["id"] = "api-limits" });
            if (lastResult.StartsWith("No document", StringComparison.Ordinal))
                return Call("search_documents", new() { ["query"] = "API rate limit" });
            if (calls[^1].Name == "search_documents")
                return Call("read_document", new() { ["id"] = FirstMatchId(lastResult) });
            return Text($"From '{Field(lastResult, "title")}': {Field(lastResult, "body")}");
        }

        if (task.Contains("404"))
            return Text("HTTP 404 means Not Found: the server could not locate the requested resource.");

        if (task.Contains("onboarding", StringComparison.OrdinalIgnoreCase))
        {
            if (calls.Count == 0)
                return Call("search_documents", new() { ["query"] = "onboarding checklist" });
            if (calls.Count == 1)
                return Call("read_document", new() { ["id"] = FirstMatchId(lastResult) });
            return Text($"Summary of '{Field(lastResult, "title")}': {Field(lastResult, "body")}");
        }

        return Text("(scripted model has no line for this input)");
    }

    private static (string Title, string Snippet) FirstMatch(string searchResultJson)
    {
        var m = JsonDocument.Parse(searchResultJson).RootElement.GetProperty("matches")[0];
        return (m.GetProperty("title").GetString()!, m.GetProperty("snippet").GetString()!);
    }

    private static string FirstMatchId(string searchResultJson) =>
        JsonDocument.Parse(searchResultJson).RootElement
            .GetProperty("matches")[0].GetProperty("id").GetString()!;

    private static string Field(string json, string name) =>
        JsonDocument.Parse(json).RootElement.GetProperty(name).ToString();

    private static ChatMessage Text(string text) => new(ChatRole.Assistant, text);

    private ChatMessage Call(string name, Dictionary<string, object?> args) =>
        new(ChatRole.Assistant, [new FunctionCallContent($"call-{++_callCounter}", name, args)]);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The scripted client is non-streaming.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
