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
// Sample-owned instrumentation: a Meter for the cost histograms that Section
// 10.2.6 argues for, and a per-rung call counter for Section 10.2.5.
//
// The histograms record a rough character-volume index (UTF-16 chars / 4),
// not a token count: no tokenizer runs here. They stand in for the token-length
// histogram the chapter recommends; a real deployment counts tokens with one
// named reference tokenizer and still reads the result as an index, watching
// the distribution and its drift rather than the third significant digit.
// ---------------------------------------------------------------------------
var meter = new Meter("OtelWiring.Sample");
var toolResultTokens = meter.CreateHistogram<long>(
    "mcp.tool_result.tokens_estimate", unit: "{token}",
    description: "Rough size index of tool-result text: UTF-16 chars / 4. " +
                 "Not a tokenizer count; stands in for a reference-tokenizer histogram.");
var toolsListTokens = meter.CreateHistogram<long>(
    "mcp.tools_list.tokens_estimate", unit: "{token}",
    description: "Rough size index of the tools/list response (names, descriptions, " +
                 "input schemas): UTF-16 chars / 4. Not a tokenizer count. The standing tax.");
var toolCalls = meter.CreateCounter<long>(
    "mcp.tool.calls", unit: "{call}",
    description: "tools/call requests by tool and error-ladder rung (mcp.error_rung).");

// ---------------------------------------------------------------------------
// OpenTelemetry wiring. The C# SDK instruments through .NET's platform
// diagnostics primitives, so MCP telemetry is a source you subscribe to:
// both its ActivitySource and its Meter are named
// "Experimental.ModelContextProtocol" (the Experimental. prefix is .NET's
// convention for telemetry names whose shape may still change; the MCP SDK
// kept it at 2.0.0 GA — verified empirically against the GA package).
// ---------------------------------------------------------------------------
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("otel-wiring-server"))
    .WithTracing(tracing => tracing
        .AddSource("Experimental.ModelContextProtocol") // MCP spans: "tools/call get_forecast" etc.
        .AddAspNetCoreInstrumentation()                 // HTTP spans: the transport rung
        .AddConsoleExporter())                          // swap for .AddOtlpExporter() to ship to a collector
    .WithMetrics(metrics => metrics
        .AddMeter("Experimental.ModelContextProtocol")  // SDK: mcp.server.operation.duration, session.duration
        .AddMeter("OtelWiring.Sample")                  // ours: the cost histograms and rung counter above
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
        // with its rung on the error ladder, as a span attribute and as a counter
        // dimension (span attributes don't become metrics, and sampled traces
        // undercount). Inside a filter, Activity.Current is the SDK's
        // "tools/call <name>" server span.
        filters.AddCallToolFilter(next => async (request, cancellationToken) =>
        {
            var span = Activity.Current;
            // Only registered names become metric dimensions; a caller-supplied
            // unknown name would let any caller mint new time series.
            var toolName = request.MatchedPrimitive?.Id ?? "unknown";
            void Stamp(string rung)
            {
                span?.SetTag("mcp.error_rung", rung);
                toolCalls.Add(1,
                    new KeyValuePair<string, object?>("mcp.tool.name", toolName),
                    new KeyValuePair<string, object?>("mcp.error_rung", rung));
            }

            CallToolResult result;
            try
            {
                result = await next(request, cancellationToken);
            }
            catch (McpProtocolException)
            {
                // Rung 2 (protocol): the call never reached a tool — an unknown tool
                // name, say. The SDK turns this into a JSON-RPC error response; we
                // stamp the rung and let it. Arguments that fail to bind are not
                // this rung: they come back as isError results (rung 3), which is
                // where the spec puts input-validation errors.
                Stamp("protocol");
                throw;
            }
            catch (Exception)
            {
                // Rung 3 (execution): the tool threw. The SDK's outermost built-in
                // filter converts this into IsError = true — but that happens outside
                // this filter, so stamp the rung here before rethrowing.
                Stamp("tool_error");
                throw;
            }

            // Rung 4 heuristics are irreducibly per-tool (Section 10.2.5): an empty
            // result from a search over a corpus that should always match something
            // is a success by every technical measure — and probably a dud.
            var dudSuspect = toolName == "search_incidents"
                && result.Content is [TextContentBlock { Text: "(no matches)" }];

            Stamp(result.IsError == true ? "tool_error"   // rung 3: ran and failed
                : dudSuspect ? "dud_suspect"              // rung 4: succeeded uselessly
                : "ok");

            // Section 10.2.6: log the cost signature even when you can't log the content
            // (here the character-volume index, not a token count).
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
