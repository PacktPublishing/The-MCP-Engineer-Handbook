# http-server (chapter 1 Streamable HTTP echo server)

Companion sample for **Chapter 1, §1.7–1.8** — the same `echo` tool as `chapter01/stdio-server`, served over the Streamable HTTP transport on **port 5000**. §1.7 connects the MCP Inspector to it; §1.8 starts it before running the model-wired client (`chapter01/client`).

Each server logs `echo tool called: …` to its console, so you can verify the tool call was made — the check §1.8 suggests after prompting the model.

## Run

- **C#** (canonical — the endpoint the book's client connects to): `cd csharp && dotnet run`
  - MCP endpoint: `http://localhost:5000`
- **Python:** `cd python && uv sync && uv run python server.py`
  - MCP endpoint: `http://localhost:5000/mcp` (the Python SDK serves under `/mcp`; give the Inspector or client this full URI)
- **TypeScript:** `cd typescript && npm ci && npm run build && npm start`
  - MCP endpoint: `http://localhost:5000` (any path — the sample routes every request to the MCP handler)

In the MCP Inspector, select the **Streamable HTTP** transport and enter the endpoint URI. The C# and TS servers serve modern (2026-07-28) requests statelessly and fall back to legacy per-request serving for 2025-era clients; Python is dual-era, locked per connection by the opening message.

## Smoke test (HTTP)

With a server running (adjust the path to `/mcp` for Python):

```
curl -s http://localhost:5000 -X POST \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H 'MCP-Protocol-Version: 2026-07-28' \
  -H 'Mcp-Method: tools/call' -H 'Mcp-Name: echo' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo","arguments":{"message":"FOO"},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}'
```

Expected: a result containing `"text":"Echo: FOO"`, and `echo tool called: message="FOO", uppercase=…` in the server console.
