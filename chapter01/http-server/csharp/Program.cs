using ModelContextProtocol.Server;
using System.ComponentModel;

// The same echo tool as the stdio server, served over the Streamable HTTP
// transport instead. The client sample (chapter01/client) connects to this
// server on http://localhost:5000.
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5000");

builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<EchoTool>();

var app = builder.Build();
app.MapMcp();
await app.RunAsync();

[McpServerToolType]
public sealed class EchoTool
{
    [McpServerTool, Description("A tool that echoes back the input message")]
    public static string Echo(
        [Description("The message to echo back")] string message,
        [Description("Whether to uppercase the message")] bool uppercase = false)
    {
        // On HTTP, stdout is ours (the protocol owns it on stdio) — log the
        // call so a reader can verify the tool was invoked, as §1.8 suggests.
        Console.WriteLine($"echo tool called: message=\"{message}\", uppercase={uppercase}");

        if (string.IsNullOrEmpty(message))
        {
            return "No message provided";
        }

        var result = uppercase ? message.ToUpper() : message;
        return $"Echo: {result}";
    }
}
