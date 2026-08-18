using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using System.Collections.Concurrent;
using System.ComponentModel;

var builder = Host.CreateApplicationBuilder(args);

// Stdio servers must not write logs to stdout — it would corrupt the JSON-RPC stream.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<OperationTools>();

await builder.Build().RunAsync();

[McpServerToolType]
public class OperationTools
{
    // How long a started operation takes before it is "done".
    // Scaled down for the demo; the tool descriptions below are scaled to match.
    private static readonly TimeSpan CompletionDelay = TimeSpan.FromSeconds(
        double.TryParse(Environment.GetEnvironmentVariable("OPERATION_DELAY_SECONDS"), out var s) ? s : 4);

    // In-memory store keyed by the operation handle. In production this would be
    // durable storage (a database or task queue): the stateless design rules from
    // Chapter 5 mean any replica may receive the status poll, so the state must
    // live somewhere every replica can reach — not in process memory.
    private static readonly ConcurrentDictionary<string, Operation> Operations = new();

    [McpServerTool(Name = "start_data_processing"),
     Description("Start an asynchronous data-processing job on the named dataset. " +
        "Returns immediately with an operation ID — it does NOT wait for the job to finish. " +
        "Processing typically takes 3-6 seconds. Poll check_operation_status with the " +
        "returned operation ID; do not poll more than once every 2 seconds.")]
    public static string StartDataProcessing(
        [Description("Name of the dataset to process.")] string dataset)
    {
        var op = StartOperation("data_processing", dataset,
            resultTemplate: $"Dataset '{dataset}' processed: 1204 rows read, 1187 rows transformed, 17 rows rejected (schema mismatch).");
        return $"Started data processing for dataset '{dataset}'. Operation ID: {op.OperationId}. " +
               $"Typically completes in 3-6 seconds. Check progress with check_operation_status, " +
               $"waiting at least 2 seconds between checks.";
    }

    [McpServerTool(Name = "start_search"),
     Description("Start an asynchronous deep search across the archive for the given query. " +
        "Returns immediately with an operation ID — it does NOT wait for results. " +
        "Searches typically take 3-6 seconds. Poll check_operation_status with the " +
        "returned operation ID; do not poll more than once every 2 seconds.")]
    public static string StartSearch(
        [Description("Search query text.")] string query)
    {
        var op = StartOperation("search", query,
            resultTemplate: $"Search for '{query}' finished: 3 matching documents — 'Q3 capacity plan' (0.92), 'Incident 4411 retro' (0.87), 'Archive index 2024' (0.71).");
        return $"Started search for '{query}'. Operation ID: {op.OperationId}. " +
               $"Typically completes in 3-6 seconds. Check progress with check_operation_status, " +
               $"waiting at least 2 seconds between checks.";
    }

    [McpServerTool(Name = "check_operation_status", UseStructuredContent = true),
     Description("Check the status of an operation previously started with start_data_processing " +
        "or start_search. Requires the operation ID those tools returned. Reports 'running' or " +
        "'completed', and includes the result once completed. If still running, wait at least " +
        "2 seconds before checking again.")]
    public static OperationStatus CheckOperationStatus(
        [Description("Operation ID returned by start_data_processing or start_search.")] string operationId)
    {
        if (!Operations.TryGetValue(operationId, out var op))
        {
            // Instructive text for unknown handles: tell the model how to recover,
            // not just that it failed.
            return new OperationStatus(operationId, "unknown", "not_found", Result: null,
                Guidance: $"No operation with ID '{operationId}' exists on this server. Operation IDs " +
                          "are returned by start_data_processing and start_search. Call " +
                          "list_all_operations to see every operation this server knows about.");
        }

        return op.IsCompleted
            ? new OperationStatus(op.OperationId, op.Kind, "completed", op.Result, Guidance: null)
            : new OperationStatus(op.OperationId, op.Kind, "running", Result: null,
                Guidance: "Still running. Wait at least 2 seconds before checking again.");
    }

    [McpServerTool(Name = "list_all_operations"),
     Description("List every operation this server knows about, with its current status. " +
        "Use this to recover an operation ID you no longer have, or to get an overview " +
        "of running and completed operations.")]
    public static string ListAllOperations()
    {
        if (Operations.IsEmpty)
        {
            return "No operations have been started yet. Start one with start_data_processing or start_search.";
        }

        var lines = Operations.Values
            .OrderBy(o => o.StartedAt)
            .Select(o => $"{o.OperationId}  kind={o.Kind}  status={(o.IsCompleted ? "completed" : "running")}  input='{o.Input}'");
        return string.Join('\n', lines);
    }

    private static Operation StartOperation(string kind, string input, string resultTemplate)
    {
        // Short, distinctive handles: the model has to reproduce them verbatim,
        // so op_3f9c beats a 36-character UUID (Chapter 2, Section 6.3).
        while (true)
        {
            var op = new Operation(
                OperationId: $"op_{Random.Shared.Next(0x10000):x4}",
                Kind: kind,
                Input: input,
                StartedAt: DateTimeOffset.UtcNow,
                CompletesAt: DateTimeOffset.UtcNow + CompletionDelay,
                Result: resultTemplate);
            if (Operations.TryAdd(op.OperationId, op))
            {
                return op;
            }
        }
    }
}

/// <summary>A long-running operation. Completion is derived from the clock — no background workers.</summary>
public sealed record Operation(
    string OperationId, string Kind, string Input,
    DateTimeOffset StartedAt, DateTimeOffset CompletesAt, string Result)
{
    public bool IsCompleted => DateTimeOffset.UtcNow >= CompletesAt;
}

/// <summary>Status report returned by check_operation_status (also published as the tool's output schema).</summary>
public sealed record OperationStatus(
    string OperationId, string Kind, string Status, string? Result, string? Guidance);
