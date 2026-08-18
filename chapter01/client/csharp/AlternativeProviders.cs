using Anthropic;
using Microsoft.Extensions.AI;

// The chat loop in Program.cs only needs an IChatClient, so switching from
// local Ollama to an LLM API is a one-line change: replace the chatClient
// assignment with a call to one of these factory methods.
internal static class AlternativeProviders
{
    // Claude, via the official Anthropic SDK.
    // Reads the API key from the ANTHROPIC_API_KEY environment variable.
    public static IChatClient CreateClaudeChatClient(string model = "claude-opus-5") =>
        new ChatClientBuilder(new AnthropicClient().AsIChatClient(model))
            .UseFunctionInvocation()
            .Build();

    // OpenAI, via the official OpenAI SDK and its Microsoft.Extensions.AI adapter.
    // Reads the API key from the OPENAI_API_KEY environment variable.
    public static IChatClient CreateOpenAIChatClient(string model = "gpt-4o-mini") =>
        new ChatClientBuilder(
                new OpenAI.OpenAIClient(Environment.GetEnvironmentVariable("OPENAI_API_KEY"))
                    .GetChatClient(model)
                    .AsIChatClient())
            .UseFunctionInvocation()
            .Build();
}
