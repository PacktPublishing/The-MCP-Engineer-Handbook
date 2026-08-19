# long-running-operation

Companion sample for **Chapter 2, §6.4 (Long-Running Operations)** — referenced in the chapter as `ch02-long-running-operation`.

A domain operation doesn't have to be modeled as a single tool call. This server splits two slow operations across multiple tools: `start_data_processing` and `start_search` return immediately with an operation ID, and the model threads that ID through `check_operation_status` (and can recover lost IDs with `list_all_operations`). The operation ID is an in-context handle — the same pattern as §6.3, with the model as the state manager — and the pattern recurs in Chapters 5 and 6 whenever a logical operation spans several tool calls.

Two chapter techniques are on display in the tool schemas:

- **Guidance in descriptions (§6.4):** models have a poor grasp of temporality, so the start tools state the expected duration ("typically takes 3-6 seconds", scaled down for the demo) and a polling cadence ("do not poll more than once every 2 seconds") — steering both the overzealous poller and the forgetful one. `check_operation_status` repeats the cadence in its running-state result.
- **Instructive results:** an unknown operation ID doesn't just fail; the result says where operation IDs come from and points at `list_all_operations` for recovery.

Operations "complete" after a configurable delay (`OPERATION_DELAY_SECONDS`, default 4) — completion is derived from the clock, no background workers. State lives in an in-memory dictionary keyed by the handle; in production this would be durable storage so any replica can answer the status poll (the stateless design rules from Chapter 5).

## The output-schema client (`csharp-client/`)

`check_operation_status` declares an `outputSchema`. The companion client demonstrates the Chapter 2, §2 work-around for model APIs without native output-schema support: list the tools, then lift the schema into the model-facing description with the client-side re-presentation API —

```csharp
var patchedTool = statusTool.WithDescription(
    $"{statusTool.Description} Returns JSON matching this schema: {JsonSerializer.Serialize(statusTool.ReturnJsonSchema)}");
```

`McpClientTool.WithDescription` changes only what the model sees; the server and the wire-level tool are untouched.

## Run

- **C#** (canonical): `cd csharp && dotnet run`
- **Client demo:** from this directory, `dotnet run --project csharp-client` (it spawns the server itself via `dotnet run --project csharp`; pass a different server project path as the first argument if running from elsewhere)
- **Python:** `cd python && uv sync && uv run server.py` — same four tools, same handles/guidance texts, state in a dict keyed by the handle, completion derived from the clock (`OPERATION_DELAY_SECONDS`, default 4). The output-schema client demo is not ported: `McpClientTool.WithDescription` is a C#-specific re-presentation API; the Python equivalent of the lift itself is in `chapter06/schema-mapping/python/`.
- **TypeScript:** `cd typescript && npm ci && npm run build && npm start` — same four tools, same handles/guidance texts, state in a `Map` keyed by the handle, completion derived from the clock (`OPERATION_DELAY_SECONDS`, default 4); `check_operation_status` publishes the same output schema via `registerTool`'s `outputSchema`. The output-schema client demo is not ported: `McpClientTool.WithDescription` is a C#-specific re-presentation API — the TS `Client` returns listed tools as plain data, so the equivalent lift is editing the tool's description string before handing it to the model.

## Smoke test (stdio)

Pipe requests into the server process (or type them interactively); all three servers accept handshake-less 2026-07-28 requests. The `_meta` envelope must be complete — at GA every SDK rejects a request missing `clientCapabilities` with -32602. Set `OPERATION_DELAY_SECONDS=3` to make the wait short.

The Python server (high-level `MCPServer`, stdio) serves **both eras**: the handshake-less 2026-07-28 requests below work as-is (it also answers `server/discover`), and a legacy client that opens with `initialize` negotiates 2025-11-25 — but that locks the connection to the legacy era, after which modern-envelope requests are rejected with -32600. The Python client (`mode="auto"`, the default) negotiates 2026-07-28 against it.

Start an operation:

```
{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"start_data_processing","arguments":{"dataset":"telemetry-2026-q2"},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}
```

Copy the `op_xxxx` ID from the response, poll once immediately (status `running`), wait past the delay, and poll again (status `completed`, with the result):

```
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"check_operation_status","arguments":{"operationId":"<op id from id 2>"},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}
{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"list_all_operations","arguments":{},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}
```

Calling `check_operation_status` with a made-up ID shows the instructive not-found result; `start_search` works the same way as `start_data_processing`.
