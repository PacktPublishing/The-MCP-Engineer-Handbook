# low-level-handlers

Dropping below the high-level API: registering explicit handlers for `tools/list` and `tools/call` on each SDK's low-level `Server` class, and adding cross-cutting concerns (logging, rate limiting, error handling) by wrapping handlers manually when the SDK has no protocol-level middleware pipeline.

Book sections: §5.4.1 (Low-Level Request Handlers) and §5.4.3 (Cross-Cutting Concerns Without Middleware).

- **TypeScript** (`typescript/src/server.ts`): low-level `Server` with `setRequestHandler('tools/list', …)` / `setRequestHandler('tools/call', …)` — one handler per method, keyed by method string.
- **Python** (`python/server.py`): low-level `Server` with handlers registered via the `on_list_tools=` / `on_call_tool=` constructor parameters; each handler receives `(ctx, params)`.
- **Python** (`python/handler_wrapper.py`): the same server with the `tools/call` handler wrapped in composed decorators — `with_logging`, `with_rate_limit`, `with_error_handling` — the manual-wrapping pattern from §5.4.3.
- **Python** (`python/middleware_example.py`): SDK middleware (new in v2) — a custom entry appended to the low-level `Server.middleware` list that wraps every inbound message.
- **C#** (`csharp/Program.cs`): handler registration via the `WithListToolsHandler(...)` / `WithCallToolHandler(...)` builder extension methods — no `[McpServerTool]` attributes, no `WithTools<T>()`, hand-authored JSON schemas. This is the registration side of the C# story; the filter pipeline (`WithRequestFilters`, see `chapter05/csharp-filters/`) is the interception side that wraps whatever handlers are registered, including these.

All tools are trivial (`echo`, `reverse`, `shout`) so the handler wiring stays in focus.

## Run

- **TypeScript**: `cd typescript && npm ci && npm run build && npm start`
- **Python**: `cd python && uv run python server.py` (or `handler_wrapper.py` / `middleware_example.py`)
- **C#**: `cd csharp && dotnet run`

## Smoke test

TypeScript and C# (stdio, handshake-less 2026-07-28) — pipe these lines into the process:

```
{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"echo","arguments":{"message":"round-trip"},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}
```

Python stdio is dual-era, locked per connection by the opening message: the handshake-less lines above work as-is, or open with the classic handshake to exercise the legacy era:

```
{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2026-07-28","capabilities":{},"clientInfo":{"name":"smoke","version":"0"}}}
{"jsonrpc":"2.0","method":"notifications/initialized"}
{"jsonrpc":"2.0","id":1,"method":"tools/list"}
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"echo","arguments":{"message":"round-trip"}}}
```

Expected `tools/call` result: `{"content":[{"type":"text","text":"Echo: round-trip"}]}`. With `handler_wrapper.py`, per-call log lines appear on stderr, an unknown tool name returns an `isError` result instead of a protocol error, and the 11th call inside a minute returns `Rate limit exceeded.`; with `middleware_example.py`, every inbound message (including `initialize`) logs a timing line on stderr.
