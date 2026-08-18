using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;

var builder = Host.CreateApplicationBuilder(args);

// Stdio servers must not write logs to stdout — it would corrupt the JSON-RPC stream.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<ReportTools>();

await builder.Build().RunAsync();

// A stand-in for any long-running backend: start_report returns immediately with a handle,
// the actual work finishes a few seconds later. With the Tasks extension the server would
// return a CreateTaskResult instead of a handle-in-content, but the host-side injection
// problem this sample demonstrates is identical either way.
[McpServerToolType]
public class ReportTools
{
    // null value = still running; non-null = the finished report.
    private static readonly ConcurrentDictionary<string, string?> Results = new();
    private static int _counter;

    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(
        double.TryParse(Environment.GetEnvironmentVariable("REPORT_DELAY_SECONDS"), out var s) ? s : 3);

    [McpServerTool(Name = "start_report")]
    [Description("Start generating a report. Returns immediately with an operation handle; the report completes a few seconds later.")]
    public static string StartReport([Description("What the report should cover.")] string topic)
    {
        var id = $"op-{Interlocked.Increment(ref _counter):D3}";
        Results[id] = null;
        _ = Task.Delay(Delay).ContinueWith(_ =>
            Results[id] = $"Report on {topic}: revenue up 14% quarter over quarter, " +
                          "driven by the new subscription tier. Two regions missed target; details in the appendix.");
        return JsonSerializer.Serialize(new { operationId = id, status = "running" });
    }

    [McpServerTool(Name = "get_report_result")]
    [Description("Fetch the result of a previously started report. Returns status 'running' until it completes.")]
    public static string GetReportResult([Description("Handle returned by start_report.")] string operationId)
    {
        if (!Results.TryGetValue(operationId, out var report))
            return JsonSerializer.Serialize(new { operationId, status = "unknown" });

        return report is null
            ? JsonSerializer.Serialize(new { operationId, status = "running" })
            : JsonSerializer.Serialize(new { operationId, status = "completed", report });
    }
}
