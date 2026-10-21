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

In the MCP Inspector, select the **Streamable HTTP** transport and enter the endpoint URI. Python is the odd one out: its SDK serves under `/mcp` by default, so `http://localhost:5000` (the URI the C# client and the TS/C# servers use) answers 404 there. The C# and TS servers serve modern (2026-07-28) requests statelessly and fall back to legacy per-request serving for 2025-era clients; Python is dual-era, locked per connection by the opening message.

## Local-only by design

All three servers listen on loopback only and check the `Host` and `Origin` headers, as the Streamable HTTP spec asks of local servers (bind to localhost; validate `Origin` and answer `403` when it is present and invalid). Binding and header checks solve different problems: binding keeps other machines out, while the header checks stop a web page in your own browser from reaching the server through DNS rebinding or a cross-site request.

| Language | Bind | `Host` check | `Origin` check |
|---|---|---|---|
| C# | `UseUrls("http://localhost:5000")` (127.0.0.1 and [::1]) | `HostFilteringOptions.AllowedHosts` (Kestrel accepts any host by default) | small middleware in `Program.cs` (the C# SDK has none built in) |
| Python | `host="127.0.0.1"` (also the SDK default) | built in: on by default for a loopback host (421) | built in (403) |
| TypeScript | `listen(5000, '127.0.0.1')` (Node binds every interface without a host) | `localhostHostValidation()` from `@modelcontextprotocol/node` | `localhostOriginValidation()` from `@modelcontextprotocol/node` (`createMcpHandler` itself does no validation) |

Requests without an `Origin` header (the C# client, `curl`) pass, and so do requests from loopback origins such as the Inspector page at `http://127.0.0.1:6274`.

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

Keep the `MCP-Protocol-Version` header: the Streamable HTTP spec requires it on every POST, and since `@modelcontextprotocol/server` 2.1.0 the TypeScript server answers a modern request without it with `400` and JSON-RPC `-32020` (`HeaderMismatch`).
