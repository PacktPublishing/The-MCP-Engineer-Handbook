# csharp-filters — the C# SDK's request filter pipeline

Companion sample for **Section 5.4.2, "C#: Filters and the Middleware Pipeline"**.

An ASP.NET Core HTTP MCP server that registers two `tools/call` filters via
`WithRequestFilters(...)`:

1. **Logging filter** — times every tool call with a `Stopwatch`, logs the
   result, and converts uncaught exceptions into a `CallToolResult` with
   `IsError = true`.
2. **Authorization filter** — short-circuits any tool whose name starts with
   `admin_`, returning an access-denied result without ever invoking the tool.

Filters compose as a middleware onion: the logging filter (registered first)
wraps the authorization filter, which wraps the actual tool handler. The
server exposes a small in-memory document store (`DocumentTools.cs`) with
`list_documents`, `get_document`, `create_document`, and
`admin_delete_document` — the last one exists so the authorization filter has
something real to block.

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
Substitute your port for `5000`.

List tools:

```bash
curl -s http://localhost:5000/ -X POST \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H 'MCP-Protocol-Version: 2026-07-28' \
  -H 'Mcp-Method: tools/list' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}'
```

A normal tool call — succeeds, and the server console logs
`Tool get_document completed in Nms`:

```bash
curl -s http://localhost:5000/ -X POST \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H 'MCP-Protocol-Version: 2026-07-28' \
  -H 'Mcp-Method: tools/call' -H 'Mcp-Name: get_document' \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_document","arguments":{"id":"roadmap"},"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}'
```

An admin tool call — the authorization filter blocks it with an
`isError: true` result; the tool never runs:

```bash
curl -s http://localhost:5000/ -X POST \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H 'MCP-Protocol-Version: 2026-07-28' \
  -H 'Mcp-Method: tools/call' -H 'Mcp-Name: admin_delete_document' \
  -d '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"admin_delete_document","arguments":{"id":"roadmap"},"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}'
```

To see the logging filter's exception path, call `get_document` with an ID
that does not exist: the thrown `KeyNotFoundException` comes back as
`{"text":"Error: No document with ID '...'.", ...}` with `isError: true`, and
the console logs `Tool get_document failed after Nms`.
