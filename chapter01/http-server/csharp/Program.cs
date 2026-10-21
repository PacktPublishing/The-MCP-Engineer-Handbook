using Microsoft.AspNetCore.HostFiltering;
using ModelContextProtocol.Server;
using System.ComponentModel;

// The same echo tool as the stdio server, served over the Streamable HTTP
// transport instead. The client sample (chapter01/client) connects to this
// server on http://localhost:5000.
var builder = WebApplication.CreateBuilder(args);

// "localhost" makes Kestrel listen on the loopback addresses only
// (127.0.0.1 and [::1]), never on the machine's network interfaces.
builder.WebHost.UseUrls("http://localhost:5000");

// Kestrel accepts any Host header by default. A local server should accept
// only loopback host names, which protects it against DNS rebinding.
builder.Services.Configure<HostFilteringOptions>(options =>
    options.AllowedHosts = ["localhost", "127.0.0.1", "[::1]"]);

builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<EchoTool>();

var app = builder.Build();

// The Streamable HTTP spec requires servers to validate the Origin header:
// if it is present and not a loopback origin, answer 403. (Non-browser
// clients, like the chapter01 client, send no Origin header.)
app.Use(async (context, next) =>
{
    var origin = context.Request.Headers.Origin.ToString();
    if (origin.Length > 0 &&
        !(Uri.TryCreate(origin, UriKind.Absolute, out var originUri) && originUri.IsLoopback))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next(context);
});

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
