# The MCP Engineering Handbook — Companion Samples

Code samples for the book, written against the **2026-07-28 MCP specification** using the v2 GA SDKs. Every project was rebuilt and smoke-tested on the GA releases (2026-08-19); the TypeScript projects were re-run on 2.2.0 (2026-10-12).

## SDK pins

Versions are pinned exactly. C# and Python are on the 2.0.0 GA. TypeScript moved to **2.2.0**: `@modelcontextprotocol/client` 2.0.0–2.1.x is affected by [GHSA-6qxp-vccf-f47h](https://github.com/advisories/GHSA-6qxp-vccf-f47h) (the OAuth client could send credentials to an authorization server the MCP server names). No sample uses the OAuth client, but samples get copied; the server package moved with the client so the two stay in lockstep.

| Language | Package | Version |
|---|---|---|
| C# | `ModelContextProtocol` (+ `.AspNetCore` where used; `.Extensions.Tasks` for the tasks extension) | `2.0.0` |
| Python | `mcp` | `2.0.0` |
| TypeScript | `@modelcontextprotocol/server`, `@modelcontextprotocol/client` (`@modelcontextprotocol/node` 2.1.0 for the Node HTTP adapter in `chapter01/http-server`) | `2.2.0` |

Do **not** use the v1 TypeScript monolith (`@modelcontextprotocol/sdk`) — v2 ships as the two packages above. One deliberate exception rides the v1 `mcp` line because its ecosystem has not adopted v2: `chapter12/agent-as-mcp` (Microsoft Agent Framework) — see its README. (`chapter07/health-dashboard` was the other until ext-apps 2.x added v2 support.)

## Layout

```
chapterNN/<sample-name>/
  csharp/      dotnet 10; dotnet run
  python/      uv; uv run python server.py
  typescript/  Node 20+, ESM; npm ci && npm run build && npm start
  README.md    what it demonstrates + chapter section reference
templates/hello-server/   walking skeleton in all three languages (toolchain check)
```

## Samples

| Sample | Book ref | Languages |
|---|---|---|
| `chapter01/stdio-server/` | §1.7 | C# (canonical), Python, TS |
| `chapter01/http-server/` | §1.7–1.8 | C# (canonical), Python, TS |
| `chapter01/client/` | §1.8 | C# only (by design; Ollama-wired, LLM API alternatives included) |
| `chapter02/long-running-operation/` | §2.6.4 | C# (canonical), Python, TS |
| `chapter03/demo-resource-server/` | shared by ch3 samples | C# (canonical), Python, TS |
| `chapter03/resource-client-host/` | §3.3.1 | C# (canonical), Python, TS |
| `chapter03/model-resource-client/` | §3.3.3 | C# (canonical), Python, TS |
| `chapter03/resource-link-client/` | §3.3.4 | C# (canonical), Python, TS |
| `chapter05/progressive-disclosure/` | §5.1.2 | TS (canonical), C#, Python |
| `chapter05/preview-before-execute/` | §5.1.4 | C# (canonical), Python, TS |
| `chapter05/completions/` | §5.3.3 | Python (canonical), TS, C# |
| `chapter05/low-level-handlers/` | §5.4.1/§5.4.3 | TS + Python (canonical), C# |
| `chapter05/csharp-filters/` | §5.4.2 | C# only (by design) |
| `chapter05/mcpb-bundle/` | §5.5.1/§5.5.2 | Node (bundle fixture) |
| `chapter06/schema-mapping/` | §6.2.2/§6.2.3 | TS (canonical), C#, Python |
| `chapter06/mrtr-client/` | §6.2.5 | TS (canonical), C#, Python |
| `chapter06/task-injection/` | §6.2.11 | C# (canonical), Python, TS |
| `chapter07/health-dashboard/` | §7.4 | TS only (Apps SDK; ext-apps 2.0.3) |
| `chapter07/tasks-polling/` | §7.3 | C# (client-side task methods, minimal) |
| `chapter09/token-validation/` | §9.2.2/§9.2.4/§9.2.7 | C# (canonical) |
| `chapter09/stepup-client/` | §9.7.4 | C# (union rule + naive anti-pattern) |
| `chapter10/eval-harness/` | §10.1 | C# (canonical) + minimal TS |
| `chapter10/otel-wiring/` | §10.2 | C# only (by design) |
| `chapter10/mcp-proxy/` | §10.4.3/§10.5 | C# (canonical) |
| `chapter12/channel-pattern/` | §12.4 (+§12.2 harness miniature) | C# (canonical; server + chat surface + harness) |
| `chapter12/agent-as-mcp/` | §12.3 | Python only (MAF `as_mcp_server()`; rides mcp v1 via MAF pin — see its README) |
| `templates/hello-server`, `templates/hello-client` | — | toolchain skeletons |

## SDK behavior notes (verified at GA, 2026-08-19)

- **All three SDKs serve handshake-less 2026-07-28 requests over stdio**, and all three validate the `_meta` envelope strictly: modern-era requests need `clientInfo`, `clientCapabilities`, and `protocolVersion`, or the request fails with -32602. (The preview-era C# leniency is gone at GA.) `tools/list` responses carry `ttlMs`/`cacheScope` from all three SDKs.
- **TypeScript stdio servers must use `serveStdio(factory)`** — hand-wiring `server.connect(new StdioServerTransport())` serves no `server/discover` (-32601), so negotiating clients fall back to the legacy era (2025-11-25).
- **TypeScript clients default to the legacy negotiation posture** — pass `versionNegotiation: { mode: 'auto' }`. This did not change at GA.
- **Python stdio serves both eras, locked per connection by the opening message**: the high-level `MCPServer.run()` stdio path answers `server/discover` and accepts handshake-less 2026-07-28 requests (as does the low-level `Server` path and streamable HTTP); a connection opened with `initialize` instead negotiates `2025-11-25` and then **rejects modern-envelope requests with -32600**. The Python client's `mode='auto'` negotiates 2026-07-28 against these servers and falls back to `initialize` against old ones.
- **Version-negotiation defaults differ per SDK**: Python client auto-probes the modern era by default, TypeScript defaults to legacy (pass `versionNegotiation: { mode: 'auto' }`), C# negotiates modern with no option needed.
- **C# tasks extension is a separate package at GA**: `ModelContextProtocol.Extensions.Tasks` (`.WithTasks(store)`, `CallToolAsTaskAsync`, `CallToolWithPollingAsync`) — see `chapter07/tasks-polling/`.
- Toolchain prerequisites: .NET 10 SDK, Node.js ≥ 20, `uv` (Python projects pin their own interpreter, ≥ 3.12).
