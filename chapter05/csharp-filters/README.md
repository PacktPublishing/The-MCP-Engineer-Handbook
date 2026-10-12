# csharp-filters — the C# SDK's request filter pipeline

Companion sample for **Section 5.4.2, "C#: Filters and the Middleware Pipeline"**.

An ASP.NET Core HTTP MCP server that registers two `tools/call` filters via
`WithRequestFilters(...)`:

1. **Logging filter** — times every tool call with a `Stopwatch` and logs the
   outcome. It *observes* exceptions and rethrows them rather than converting
   them itself: the SDK's tool pipeline already lets `InputRequiredException`
   (MRTR), cancellation, and `McpProtocolException` through, and turns every
   other exception into an `IsError` result. Catching `Exception` and returning
   a result here would swallow an elicitation, turn cancellation into an
   ordinary tool failure, and send raw exception messages to the client.
2. **Authorization filter** — short-circuits any tool whose name starts with
   `admin_`, returning an access-denied result without ever invoking the tool.

Filters compose as a middleware onion: the logging filter (registered first)
wraps the authorization filter, which wraps the actual tool handler. The
server exposes a small in-memory document store (`DocumentTools.cs`) with
`list_documents`, `get_document`, `create_document`, and
`admin_delete_document` — the last one exists so the authorization filter has
something real to block. `create_document` asks the user to confirm before
replacing an existing document (an MRTR form elicitation, Section 5.3.1), which
shows the input request passing through both filters.

This sample is deliberately C#-only: the filter pipeline is a C# SDK feature
with no equivalent in the TypeScript or Python SDKs (Section 5.4.3 covers
what to do there instead).

## Run

```bash
dotnet run
# note the listening URL, e.g. http://localhost:5000
```

## Smoke test

Requests are stateless 2026-07-28 era — no `initialize` handshake needed.
At 2.0.0 GA the C# SDK validates the `_meta` envelope strictly (the preview-era
leniency is gone): omit `io.modelcontextprotocol/clientCapabilities` and the
server rejects the request with `-32602`. Substitute your port for `5000`.

List tools:

```bash
curl -s http://localhost:5000/ -X POST \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H 'MCP-Protocol-Version: 2026-07-28' \
  -H 'Mcp-Method: tools/list' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}'
```

A normal tool call — succeeds, and the server console logs
`Tool get_document completed in Nms`:

```bash
curl -s http://localhost:5000/ -X POST \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H 'MCP-Protocol-Version: 2026-07-28' \
  -H 'Mcp-Method: tools/call' -H 'Mcp-Name: get_document' \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_document","arguments":{"id":"roadmap"},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}'
```

An admin tool call — the authorization filter blocks it with an
`isError: true` result; the tool never runs:

```bash
curl -s http://localhost:5000/ -X POST \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H 'MCP-Protocol-Version: 2026-07-28' \
  -H 'Mcp-Method: tools/call' -H 'Mcp-Name: admin_delete_document' \
  -d '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"admin_delete_document","arguments":{"id":"roadmap"},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}'
```

Replacing an existing document — `create_document` throws
`InputRequiredException`, both filters let it through, and the wire result is
`"resultType":"input_required"` carrying the confirmation form. The console
logs `Tool create_document requested input after Nms`:

```bash
curl -s http://localhost:5000/ -X POST \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H 'MCP-Protocol-Version: 2026-07-28' \
  -H 'Mcp-Method: tools/call' -H 'Mcp-Name: create_document' \
  -d '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"create_document","arguments":{"id":"roadmap","content":"Q3: filters."},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{"elicitation":{"form":{}}},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}'
```

The retry the client would send after the user accepts — the same request
plus `inputResponses` — stores the document:

```bash
curl -s http://localhost:5000/ -X POST \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H 'MCP-Protocol-Version: 2026-07-28' \
  -H 'Mcp-Method: tools/call' -H 'Mcp-Name: create_document' \
  -d '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"create_document","arguments":{"id":"roadmap","content":"Q3: filters."},"inputResponses":{"confirm_overwrite":{"action":"accept","content":{"replace":true}}},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{"elicitation":{"form":{}}},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}'
```

To see the exception path, call `get_document` with an ID that does not
exist. The tool throws `McpException`, whose message the SDK deliberately
passes to the client: the result is `isError: true` with
`"An error occurred invoking 'get_document': No document with ID '...'."`, and
the console logs `Tool get_document failed after Nms`. Any other exception
type gets only `"An error occurred invoking '<tool>'."` on the wire; its
details stay in the server log. Throw `McpException` only for messages you
intend the client (and the model) to read.
