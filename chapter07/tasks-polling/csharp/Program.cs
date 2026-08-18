using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.ComponentModel;

var builder = Host.CreateApplicationBuilder(args);

// Stdio servers must not write logs to stdout — it would corrupt the JSON-RPC stream.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer(options =>
    {
        // Setting a task store is the entire server-side opt-in: the server declares
        // io.modelcontextprotocol/tasks, serves tasks/get, tasks/update, and
        // tasks/cancel from the store, and wraps tool calls from clients that
        // declared the extension in tasks — returning resultType: "task" immediately
        // while the tool runs in the background.
        options.TaskStore = new InMemoryMcpTaskStore { DefaultPollIntervalMs = 1000 };
    })
    .WithStdioServerTransport()
    .WithTools<ReportTools>();

await builder.Build().RunAsync();

[McpServerToolType]
public class ReportTools
{
    [McpServerTool(Name = "generate_report"), Description("Generates a report on a topic. Slow.")]
    public static async Task<string> GenerateReport(
        [Description("Report topic.")] string topic,
        [Description("Seconds the generation takes.")] int seconds,
        CancellationToken cancellationToken)
    {
        // tasks/cancel surfaces here as cancellation of this token.
        await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
        return $"Report on {topic}: 3 sections, 12 charts, everything nominal.";
    }
}
