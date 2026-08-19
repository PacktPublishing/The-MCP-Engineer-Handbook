# hello-server (walking skeleton)

Minimal stdio MCP server with one tool (`say_hello`) in all three languages. Not referenced by the book — this is the toolchain check and the porting template for real samples.

## Run

- **C#**: `cd csharp && dotnet run`
- **Python (stdio)**: `cd python && uv run python server.py`
- **Python (streamable HTTP, 2026-07-28 stateless)**: `cd python && uv run python http_server.py`
- **TypeScript**: `cd typescript && npm ci && npm run build && npm start`

## Smoke test (stdio, handshake-less — all three languages)

```
{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke-test","version":"0.0.1"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"say_hello","arguments":{"name":"Peder"},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke-test","version":"0.0.1"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}
```

All three serve this modern-era opening (the full `_meta` envelope is required — at GA all three SDKs reject requests without `clientCapabilities` with -32602). Opening with a legacy `initialize` instead locks the connection to the 2025-11-25 era on TS and Python.
