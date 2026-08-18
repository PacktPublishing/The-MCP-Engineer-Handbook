using System.Diagnostics;
using System.Diagnostics.Metrics;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Sample-owned instrumentation: a Meter for the token-cost histograms that
// Section 10.2.6 argues for. Chars/4 is a deliberate index, not a truth —
// you don't know the caller's tokenizer, so pick one reference and watch the
// distribution and its drift, not the third significant digit.
// ---------------------------------------------------------------------------
var meter = new Meter("OtelWiring.Sample");
var toolResultTokens = meter.CreateHistogram<long>(
    "mcp.tool_result.tokens_estimate", unit: "{token}",
    description: "Estimated token cost of tool results (chars/4).");
var toolsListTokens = meter.CreateHistogram<long>(
    "mcp.tools_list.tokens_estimate", unit: "{token}",
    description: "Estimated token cost of the tools/list response (chars/4) — the standing tax.");

// ---------------------------------------------------------------------------
// OpenTelemetry wiring. The C# SDK instruments through .NET's platform
// diagnostics primitives, so MCP telemetry is a source you subscribe to:
// both its ActivitySource and its Meter are named
// "Experimental.ModelContextProtocol" (the Experimental. prefix is .NET's
// convention for pre-stable telemetry names — re-check at SDK GA).
// ---------------------------------------------------------------------------
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("otel-wiring-server"))
    .WithTracing(tracing => tracing
        .AddSource("Experimental.ModelContextProtocol") // MCP spans: "tools/call get_forecast" etc.
        .AddAspNetCoreInstrumentation()                 // HTTP spans: the transport rung
        .AddConsoleExporter())                          // swap for .AddOtlpExporter() to ship to a collector
    .WithMetrics(metrics => metrics
        .AddMeter("Experimental.ModelContextProtocol")  // SDK: mcp.server.operation.duration, session.duration
        .AddMeter("OtelWiring.Sample")                  // ours: the token-cost histograms above
        .AddConsoleExporter((_, reader) =>
            reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 10_000));

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation { Name = "otel-wiring-server", Version = "1.0.0" };
    })
    .WithHttpTransport()
    .WithTools<ArchiveTools>()
    .WithRequestFilters(filters =>
    {
        // The one MCP-aware emission point (Section 10.2.5): stamp every tools/call
        // span with its rung on the error ladder. Inside a filter, Activity.Current
        // is the SDK's "tools/call <name>" server span.
        filters.AddCallToolFilter(next => async (request, cancellationToken) =>
        {
            var span = Activity.Current;
            var toolName = request.MatchedPrimitive?.Id ?? request.Params?.Name ?? "unknown";

            CallToolResult result;
            try
            {
                result = await next(request, cancellationToken);
            }
            catch (McpProtocolException)
            {
                // Rung 2 (protocol): the call never reached a tool — unknown tool,
                // invalid params. The SDK turns this into a JSON-RPC error response;
                // we stamp the rung and let it.
                span?.SetTag("mcp.error_rung", "protocol");
                throw;
            }
            catch (Exception)
            {
                // Rung 3 (execution): the tool threw. The SDK's outermost built-in
                // filter converts this into IsError = true — but that happens outside
                // this filter, so stamp the rung here before rethrowing.
                span?.SetTag("mcp.error_rung", "tool_error");
                throw;
            }

            // Rung 4 heuristics are irreducibly per-tool (Section 10.2.5): an empty
            // result from a search over a corpus that should always match something
            // is a success by every technical measure — and probably a dud.
            var dudSuspect = toolName == "search_incidents"
                && result.Content is [TextContentBlock { Text: "(no matches)" }];

            span?.SetTag("mcp.error_rung",
                result.IsError == true ? "tool_error"   // rung 3: ran and failed
                : dudSuspect ? "dud_suspect"            // rung 4: succeeded uselessly
                : "ok");

            // Section 10.2.6: log the cost signature even when you can't log the content.
            var chars = result.Content?.OfType<TextContentBlock>().Sum(c => c.Text.Length) ?? 0;
            toolResultTokens.Record(chars / 4, new KeyValuePair<string, object?>("mcp.tool.name", toolName));

            return result;
        });

        // The tools/list response is a standing tax every conversation pays before
        // the server has done anything; description edits move it. Same histogram idea.
        filters.AddListToolsFilter(next => async (request, cancellationToken) =>
        {
            var result = await next(request, cancellationToken);
            var chars = result.Tools.Sum(t =>
                t.Name.Length + (t.Description?.Length ?? 0) + t.InputSchema.GetRawText().Length);
            toolsListTokens.Record(chars / 4);
            return result;
        });
    });

var app = builder.Build();

app.MapMcp();

app.Run();
