# otel-wiring — OpenTelemetry for an MCP server

Companion sample for **Section 10.2, "Observability"** — the registration
snippet for Section 10.2.2, the error-ladder emission point from Section
10.2.5, the token-cost histograms from Section 10.2.6, and the trace-context
behavior from Section 10.2.3, all in one ASP.NET Core HTTP MCP server.

This sample is deliberately C#-only: the chapter uses the C# SDK as its OTel
showcase because it instruments through .NET's platform diagnostics
primitives (`ActivitySource`/`Meter`) — telemetry is a source you subscribe
to, not an SDK feature you configure.

## The SDK's telemetry names

Verified against `ModelContextProtocol.Core` 2.0.0 (GA). Both the
`ActivitySource` and the `Meter` are named:

```
Experimental.ModelContextProtocol
```

(The `Experimental.` prefix is .NET's convention for telemetry names whose
shape may still change; the MCP SDK kept it at 2.0.0 GA — the console
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

- A `tools/call` filter stamping `mcp.error_rung` on the SDK's span:
  `protocol` (`McpProtocolException` — unknown tool, invalid params),
  `tool_error` (the tool ran and failed), `dud_suspect` (technical success,
  logical miss — here, an empty search result), `ok`. Note the SDK converts
  a throwing tool into `isError: true` *outside* the filter pipeline, so
  inside a filter that case is an exception, not a result.
- Histograms `mcp.tool_result.tokens_estimate` (chars/4, tagged by tool
  name) and `mcp.tools_list.tokens_estimate` — the cost signature you log
  when you can't log the content.

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

Each call prints a `tools/call <name>` span from source
`Experimental.ModelContextProtocol` with the matching `mcp.error_rung` tag.
To see the stitching behavior, add `-H 'traceparent:
00-11111111111111111111111111111111-aaaaaaaaaaaaaaaa-01'` and/or a
`"traceparent"` key inside `_meta`, and compare `Activity.TraceId` /
`Activity.ParentSpanId` / `Activity.Links` in the output.
