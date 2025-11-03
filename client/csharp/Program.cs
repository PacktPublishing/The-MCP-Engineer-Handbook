using Microsoft.Extensions.AI;
using OllamaSharp;
using ModelContextProtocol.Client;
using System.Diagnostics;
using System.Text;

IClientTransport clientTransport = new HttpClientTransport(new()
    {
        Endpoint = new Uri("http://localhost:5000")
    });

await using var mcpClient = await McpClient.CreateAsync(clientTransport!);

var tools = await mcpClient.ListToolsAsync();
foreach (var tool in tools)
{
    Console.WriteLine($"Connected to server with tools: {tool.Name}");
}

// Set up the LLM chat client
var uri = new Uri("http://127.0.0.1:11434");
var model = "qwen3:1.7b";
IChatClient chatClient = new ChatClientBuilder(new OllamaApiClient(uri, model))
    .UseFunctionInvocation()
    .Build();

// Start the conversation with context for the AI model
List<ChatMessage> chatHistory = new();

while (true)
{
    // Get user prompt and add to chat history
    Console.WriteLine("Your prompt:");
    var userPrompt = Console.ReadLine();
    chatHistory.Add(new ChatMessage(ChatRole.User, userPrompt));

    // Stream the AI response and add to chat history
    Console.WriteLine("AI Response:");
    var response = "";
    await foreach (ChatResponseUpdate item in
        chatClient.GetStreamingResponseAsync(chatHistory, new ChatOptions() 
        { Tools = tools.Cast<AITool>().ToList() }))
    {
        Console.Write(item.Text);
        response += item.Text;
    }
    chatHistory.Add(new ChatMessage(ChatRole.Assistant, response));
    Console.WriteLine();
}
