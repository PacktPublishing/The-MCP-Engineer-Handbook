# otel-wiring — OpenTelemetry for an MCP server

Companion sample for **Section 10.2, "Observability"** — the registration
snippet for Section 10.2.2, the error-ladder emission point from Section
10.2.5, the cost histograms from Section 10.2.6, and the trace-context
behavior from Section 10.2.3, all in one ASP.NET Core HTTP MCP server.

This sample is deliberately C#-only: the chapter uses the C# SDK as its OTel
showcase because it instruments through .NET's platform diagnostics
primitives (`ActivitySource`/`Meter`) — telemetry is a source you subscribe
to, not an SDK feature you configure.

## The SDK's telemetry names

Verified against `ModelContextProtocol.Core` 2.0.0 (GA) and 2.3.0. Both the
`ActivitySource` and the `Meter` are named:

```
Experimental.ModelContextProtocol
```

(The `Experimental.` prefix is .NET's convention for telemetry names whose
shape may still change; the MCP SDK kept it at the 2.0.0 GA and still has it at 2.3.0 — the console
exporter prints `Instrumentation scope: Experimental.ModelContextProtocol`
for both spans and metrics.) Registration is two lines:

```csharp
.WithTracing(t => t.AddSource("Experimental.ModelContextProtocol") ...)
.WithMetrics(m => m.AddMeter("Experimental.ModelContextProtocol") ...)
```

What the source emits, per the OTel GenAI/MCP semantic conventions:

- **Spans** named `{method} {target}` (`tools/call get_forecast`,
  `tools/list`), kind `Server`, with `mcp.method.name`,
  `mcp.protocol.version`, `mcp.session.id`, `jsonrpc.request.id`,
  `network.transport`, `gen_ai.tool.name` + `gen_ai.operation.name:
  execute_tool` on tool calls, `mcp.resource.uri` on resource operations,
  and `error.type` (the numeric JSON-RPC code for protocol errors,
  `tool_error` for `isError: true` results).
- **Metrics** `mcp.server.operation.duration` and
  `mcp.server.session.duration` (plus `mcp.client.*` twins), in seconds,
  with the semconv bucket boundaries, dimensioned by the same attributes.

## Trace context: `_meta` parents, headers link

The SDK extracts W3C context (`traceparent`/`tracestate`) from the request's
`_meta` and uses it as the **parent** of the MCP span. The ASP.NET Core
request activity — which honors the HTTP `traceparent` header — is attached
as a span **link**. Verified behavior when the two disagree:

| Context sent | MCP span joins | HTTP header trace |
|---|---|---|
| HTTP header only | header's trace (via the ASP.NET Core activity) | — |
| `_meta` only | `_meta`'s trace, parented by its span ID | HTTP span starts its own root |
| both, different | `_meta`'s trace | preserved as a link on the MCP span |

So on Streamable HTTP either carrier stitches, and `_meta` wins when both
are present; on stdio, `_meta` is the only carrier there is.

## What the sample adds on top

`Program.cs` registers two request filters (the Chapter 5 Section 5.4.2
pattern — one MCP-aware emission point):

- A `tools/call` filter stamping each call's rung on the error ladder:
  `protocol` (`McpProtocolException`: the call never reached a tool, for
  example an unknown tool name), `tool_error` (the tool ran and failed, or
  its arguments didn't bind), `dud_suspect` (technical success, logical
  miss — here, an empty search result), `ok`. Invalid arguments
  (`"days":"many"`, a missing `query`) land on `tool_error`, not `protocol`:
  the SDK reports them as `isError: true` results, which is where the spec
  puts input-validation errors. Note the SDK converts a throwing tool into
  `isError: true` *outside* the filter pipeline, so inside a filter that
  case is an exception, not a result.
- The filter records the rung twice: as the `mcp.error_rung` attribute on
  the SDK's span, and on a counter, `mcp.tool.calls`, dimensioned by
  `mcp.tool.name` and `mcp.error_rung`. Span attributes don't become
  metrics, and sampled traces undercount, so the counter is what gives you
  per-rung rates, `dud_suspect` included (the SDK's own
  `mcp.server.operation.duration` already carries `error.type` for
  protocol and tool errors, but nothing for duds). Unregistered tool names
  are recorded as `unknown`, so callers can't mint new time series.
- Histograms `mcp.tool_result.tokens_estimate` (tagged by tool name) and
  `mcp.tools_list.tokens_estimate`: the cost signature you log when you
  can't log the content. Despite the names, no tokenizer runs here. The
  value is a rough character-volume index, UTF-16 characters divided by
  four, over the result's text blocks (or the tools' names, descriptions,
  and input schemas). It stands in for the token-length histogram Section
  10.2.6 recommends. For a real deployment, count tokens with one named
  reference tokenizer over what the model actually receives, and still read
  the numbers as an index: the distribution and its drift matter, not the
  third significant digit.

## Run

```bash
cd csharp
dotnet run
# note the listening URL, e.g. http://localhost:5000
```

Spans and metrics print to the console (metrics every 10 s and on
shutdown). To ship to a collector instead, add the
`OpenTelemetry.Exporter.OpenTelemetryProtocol` package and replace each
`.AddConsoleExporter(...)` with `.AddOtlpExporter()`.

## Smoke test

Stateless 2026-07-28 era, no handshake. Substitute your port for `5000`.
One call per rung:

`mcp.error_rung: ok`:

```bash
curl -s http://localhost:5000/ -X POST \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H 'MCP-Protocol-Version: 2026-07-28' \
  -H 'Mcp-Method: tools/call' -H 'Mcp-Name: get_forecast' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_forecast","arguments":{"city":"Oslo","days":5},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}'
```

`mcp.error_rung: tool_error` — the tool throws, and the response carries
`isError: true` inside an HTTP 200: replace the last two headers and the
body with `Mcp-Name: check_dependency` / `"name":"check_dependency",
"arguments":{}`.

`mcp.error_rung: dud_suspect` — a technical success that matches nothing:
`Mcp-Name: search_incidents` / `"arguments":{"query":"zzzz"}`.

`mcp.error_rung: protocol` — unknown tool, JSON-RPC `-32602`:
`Mcp-Name: no_such_tool` / `"arguments":{}`.

`mcp.error_rung: tool_error` again, from arguments that don't bind:
`Mcp-Name: get_forecast` / `"arguments":{"city":"Oslo","days":"many"}`
comes back as `isError: true`, not as a protocol error.

Each call prints a `tools/call <name>` span from source
`Experimental.ModelContextProtocol` with the matching `mcp.error_rung` tag.
The next metrics export prints `mcp.tool.calls` from meter
`OtelWiring.Sample`, one point per tool and rung (excerpt):

```
Metric Name: mcp.tool.calls, Description: tools/call requests by tool and error-ladder rung (mcp.error_rung)., Unit: {call}, Metric Type: LongSum
(...] mcp.error_rung: ok mcp.tool.name: get_forecast
Value: 1
(...] mcp.error_rung: dud_suspect mcp.tool.name: search_incidents
Value: 1
(...] mcp.error_rung: protocol mcp.tool.name: unknown
Value: 1
```
To see the stitching behavior, add `-H 'traceparent:
00-11111111111111111111111111111111-aaaaaaaaaaaaaaaa-01'` and/or a
`"traceparent"` key inside `_meta`, and compare `Activity.TraceId` /
`Activity.ParentSpanId` / `Activity.Links` in the output.
