using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;

var builder = Host.CreateApplicationBuilder(args); 
builder.Logging.AddConsole(options => 
{ 
    options.LogToStandardErrorThreshold = LogLevel.Trace; 
});
builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<EchoTool>();
 
await builder.Build().RunAsync(); 

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
