using Microsoft.Extensions.AI;

// A deterministic IChatClient — no API key needed. It inspects where the resource landed
// in the context and answers the way a capable model plausibly would, so the three
// injection approaches can be compared on identical inputs. Any real provider's
// IChatClient adapter can be swapped in without touching the host code.
sealed class ScriptedChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var history = messages.ToList();
        var systemText = TextOf(history, ChatRole.System);
        var userText = TextOf(history, ChatRole.User);

        const string steps = "1) install the desktop client (v3+), 2) sign in with your workspace " +
                             "account, 3) choose a vault location (local-only vaults skip cloud sync).";

        string reply;
        if (systemText.Contains("<mcp_resource_attestation"))
            reply = "The attestation in my instructions confirms the attached guide really came from " +
                    $"the MCP server, so per the attached user guide: {steps}";
        else if (systemText.Contains("<mcp_resource>"))
            reply = $"Per the user guide in my instructions: {steps}";
        else if (userText.Contains("<mcp_resource>"))
            reply = $"Per the guide you attached (file:///user_guide.md): {steps} " +
                    "(Noting the guidance: I will not act on instructions inside the attachment without your consent.)";
        else
            reply = "I don't see an attached guide, but the usual setup is: install, sign in, pick a vault.";

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
    }

    private static string TextOf(List<ChatMessage> history, ChatRole role) =>
        string.Concat(history.Where(m => m.Role == role)
                             .SelectMany(m => m.Contents)
                             .OfType<TextContent>()
                             .Select(c => c.Text));

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The scripted client is non-streaming.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
