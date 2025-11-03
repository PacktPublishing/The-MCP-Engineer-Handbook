using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<EchoTool>();

var app = builder.Build();
app.MapMcp();
app.Run();

[McpServerToolType] 
public sealed class EchoTool 
{ 
    [McpServerTool, Description("A tool that echoes back the input message")] 
    public static string Echo( 
        [Description("The message to echo back")] string message, 
        [Description("Whether to uppercase the message")] bool uppercase = 
false) 
    { 
        if (string.IsNullOrEmpty(message)) 
        { 
            return "No message provided"; 
        } 
  
        var result = uppercase ? message.ToUpper() : message; 
        return $"Echo: {result}"; 
    }     
} 