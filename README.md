# The MCP Engineering Handbook — Companion Samples

Code samples for the book, written against the **2026-07-28 MCP specification** using the v2 GA SDKs. Every project was rebuilt and smoke-tested on the GA releases (2026-08-19); the TypeScript and Python projects were re-run on the security releases below (TypeScript 2.3.1, Python 2.3.0) on 2026-10-21.

## SDK pins

Versions are pinned exactly. C# is on the 2.0.0 GA; no advisory has been published against it. Python and TypeScript moved past their GA releases to clear security advisories. Samples get copied, so every project is on a fixed release even where the sample doesn't use the affected feature.

- **Python `mcp` 2.3.0.** 2.0.0–2.1.x are affected by [GHSA-84m7-p3x7-pcfv](https://github.com/advisories/GHSA-84m7-p3x7-pcfv) (unbounded session accumulation in the Streamable HTTP transport), fixed in 2.2.0. 2.3.0 is the latest release.
- **TypeScript 2.3.1.** `@modelcontextprotocol/server` 2.0.0–2.2.x is affected by [GHSA-jm63-cq47-j5jp](https://github.com/advisories/GHSA-jm63-cq47-j5jp) (shared transport or server instances delivered one HTTP client's messages to another), [GHSA-46jr-4wx8-v453](https://github.com/advisories/GHSA-46jr-4wx8-v453) (one HTTP request within the default size limit could exhaust server memory during validation) and [GHSA-rvq5-wwqv-78pq](https://github.com/advisories/GHSA-rvq5-wwqv-78pq) (bearer authentication accepted tokens issued for another service). `@modelcontextprotocol/client` 2.0.0–2.2.x is affected by [GHSA-6prh-2h8m-c8cw](https://github.com/advisories/GHSA-6prh-2h8m-c8cw) (HTTP transports and OAuth requests followed cross-origin redirects), and 2.0.0–2.1.x also by [GHSA-6qxp-vccf-f47h](https://github.com/advisories/GHSA-6qxp-vccf-f47h) (the OAuth client could send credentials to an authorization server the MCP server names). All are fixed in 2.3.0; 2.3.1 is the latest release. The server, client and core packages move in lockstep.

Since 2.3.0, a TypeScript `McpServer` serves one connection at a time, so a server object shared across HTTP requests or sessions now fails instead of serving them. The samples already build a server per connection by passing a factory to `serveStdio` or `createMcpHandler`; do the same in code you copy from them.

| Language | Package | Version |
|---|---|---|
| C# | `ModelContextProtocol` (+ `.AspNetCore` where used; `.Extensions.Tasks` for the tasks extension) | `2.0.0` |
| Python | `mcp` | `2.3.0` |
| TypeScript | `@modelcontextprotocol/server`, `@modelcontextprotocol/client` (`chapter07/health-dashboard` also pins `@modelcontextprotocol/core` 2.3.1 and `@modelcontextprotocol/ext-apps` 2.0.3; `chapter01/http-server` uses `@modelcontextprotocol/node` 2.1.1 for the Node HTTP adapter) | `2.3.1` |

Do **not** use the v1 TypeScript monolith (`@modelcontextprotocol/sdk`) — v2 ships as the two packages above. One sample temporarily pins a dependency to git rather than PyPI: `chapter12/agent-as-mcp` uses a Microsoft Agent Framework commit with mcp 2.x support until a release ships it — see its README. (It and `chapter07/health-dashboard` used to ride the v1 `mcp` line until their ecosystems adopted v2.)

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
| `chapter12/agent-as-mcp/` | §12.3 | Python only (MAF `as_mcp_server()`; MAF pinned to a git commit for mcp 2.x — see its README) |
| `templates/hello-server`, `templates/hello-client` | — | toolchain skeletons |

## SDK behavior notes (verified at GA, 2026-08-19)

- **All three SDKs serve handshake-less 2026-07-28 requests over stdio**, and all three validate the `_meta` envelope strictly: modern-era requests need `clientInfo`, `clientCapabilities`, and `protocolVersion`, or the request fails with -32602. (The preview-era C# leniency is gone at GA.) `tools/list` responses carry `ttlMs`/`cacheScope` from all three SDKs.
- **TypeScript stdio servers must use `serveStdio(factory)`** — hand-wiring `server.connect(new StdioServerTransport())` serves no `server/discover` (-32601), so negotiating clients fall back to the legacy era (2025-11-25).
- **TypeScript clients default to the legacy negotiation posture** — pass `versionNegotiation: { mode: 'auto' }`. This did not change at GA.
- **Python stdio serves both eras, locked per connection by the opening message**: the high-level `MCPServer.run()` stdio path answers `server/discover` and accepts handshake-less 2026-07-28 requests (as does the low-level `Server` path and streamable HTTP); a connection opened with `initialize` instead negotiates `2025-11-25` and then **rejects modern-envelope requests with -32600**. The Python client's `mode='auto'` negotiates 2026-07-28 against these servers and falls back to `initialize` against old ones.
- **Version-negotiation defaults differ per SDK**: Python client auto-probes the modern era by default, TypeScript defaults to legacy (pass `versionNegotiation: { mode: 'auto' }`), C# negotiates modern with no option needed.
- **C# tasks extension is a separate package at GA**: `ModelContextProtocol.Extensions.Tasks` (`.WithTasks(store)`, `CallToolAsTaskAsync`, `CallToolWithPollingAsync`) — see `chapter07/tasks-polling/`.
- Toolchain prerequisites: .NET 10 SDK, Node.js ≥ 20, `uv` (Python projects pin their own interpreter, ≥ 3.12).
