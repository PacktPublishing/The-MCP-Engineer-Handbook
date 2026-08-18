# stdio-server (chapter 1 echo server)

Companion sample for **Chapter 1, §1.7 (Your First MCP Connection)** — the minimal stdio MCP server the chapter builds after exploring the everything server: one `echo` tool (`message`, optional `uppercase`). The C# project matches the printed extract; Python and TypeScript are the ports §1.7 points to ("you'll find the commands for those samples in the README file").

Connect from the MCP Inspector (`npx @modelcontextprotocol/inspector`) by selecting the **STDIO** transport and entering one of the commands below — the command *is* the connection: the client starts the server as a child process, and on the modern protocol there is no handshake to perform.

Note the logging setup in the C# project: on stdio, standard output belongs to the protocol, so anything the server wants to say must land on `stderr`.

## Run

- **C#** (canonical, matches the book extract): `cd csharp && dotnet run`
  - In the Inspector: Command `dotnet`, Arguments `run --project <path-to>/csharp`
- **Python:** `cd python && uv sync && uv run python server.py`
  - In the Inspector: Command `uv`, Arguments `run --project <path-to>/python python <path-to>/python/server.py`
- **TypeScript:** `cd typescript && npm ci && npm run build && npm start`
  - In the Inspector: Command `node`, Arguments `<path-to>/typescript/dist/server.js`

## Smoke test (stdio, handshake-less)

Pipe these into the server process; all three languages serve the modern-era opening (the full `_meta` envelope is required — TS and Python reject requests without `clientCapabilities`):

```
{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke-test","version":"0.0.1"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"echo","arguments":{"message":"FOO"},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke-test","version":"0.0.1"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}
```

Expected: a tool list with the single `echo` entry, then `{"content":[{"type":"text","text":"Echo: FOO"}]}`. A legacy client that opens with `initialize` instead locks the connection to the 2025-11-25 era on TS and Python (Chapter 4 covers the dual-era rules).
