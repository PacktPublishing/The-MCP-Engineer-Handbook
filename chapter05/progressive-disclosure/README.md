# Progressive disclosure — document management API

Sample for **Chapter 5, §5.1.2 (The Progressive Disclosure Pattern)**. Instead
of one tool per API endpoint, the server exposes four static tools —
`list_endpoints`, `search_endpoints`, `describe_endpoint`, `execute_endpoint` —
and treats the API surface as data. The endpoint metadata lives in
[`data/endpoints.json`](data/endpoints.json): five groups (Documents, Users,
Groups, Permissions, Versions), twenty endpoints, each with a one-line summary
for discovery and a full description plus request/response schemas for
`describe_endpoint`.

`execute_endpoint` runs against a small in-memory simulation of the API
(a few documents, users, groups, permission grants, and version histories),
so realistic calls return plausible JSON. The simulation has no
authentication; all calls act as `user-001` (Alice Chen). Errors are written
as guidance — unknown paths, wrong methods, and malformed bodies come back
with instructions on which discovery tool to use next.

The manifest is generated data, not hand-maintained code:
[`agent-instructions.md`](agent-instructions.md) is the example prompt for a
coding agent that regenerates `data/endpoints.json` from the API source or an
OpenAPI document, as discussed in the chapter's "API Endpoint Metadata
Generation" section.

## Languages

- **TypeScript** (`typescript/`) — canonical implementation
- **C#** (`csharp/`) and **Python** (`python/`) — ports; same tools, same
  output text, same simulated API. All three load the shared manifest from
  `data/endpoints.json`.

## Run

- **TypeScript**: `cd typescript && npm ci && npm run build && npm start`
  (Node 20+)
- **C#**: `cd csharp && dotnet run` (.NET 10)
- **Python (stdio)**: `cd python && uv run python server.py`
- **Python (streamable HTTP)**: `cd python && uv run python http_server.py`

All servers speak stdio. The TypeScript and C# servers accept handshake-less
2026-07-28 requests; Python stdio on mcp 2.0.0b1 still runs the legacy era, so
prepend the `initialize` handshake (below) when smoke-testing it.

## Smoke test

Pipe these into the running process (one per line). For Python stdio, first
send:

```
{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2026-07-28","capabilities":{},"clientInfo":{"name":"smoke","version":"0"}}}
{"jsonrpc":"2.0","method":"notifications/initialized"}
```

Then, on any of the three:

```
{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_endpoints","arguments":{},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_endpoints","arguments":{"group":"Permissions"},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"search_endpoints","arguments":{"query":"write permission"},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}
{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"describe_endpoint","arguments":{"method":"GET","path":"/api/documents/{id}/permissions"},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}
{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"execute_endpoint","arguments":{"method":"GET","path":"/api/documents/doc-001/permissions"},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}
```

The chapter's example interaction (finding who has write access to the
Quarterly Report Q1 2026) reproduces against the seed data: search the
document via `execute_endpoint` on `GET /api/documents/search` with
`q=Quarterly Report Q1 2026`, then fetch `GET
/api/documents/doc-001/permissions`.
