# MCP Engineering Handbook — Companion Samples

This repository has the code samples for *MCP Engineering Handbook* by Peder Holdgaard Pedersen (Packt, 2026). The samples target the **2026-07-28 revision of the Model Context Protocol** and the v2 SDKs.

Most samples come in C#, Python and TypeScript. The book prints one of them, marked **canonical** in the table below. The other two are ports that behave the same way, so you can follow along in the language you prefer. Each sample's README says what it demonstrates, which section of the book it belongs to, how to run it, and the versions it was tested with.

## Prerequisites

| Tool | Version | Used for |
|---|---|---|
| .NET SDK | 10 | C# samples |
| [uv](https://docs.astral.sh/uv/) | current | Python samples (each project pins its own interpreter, 3.12 or later) |
| Node.js | 24 LTS (20 or later for the TypeScript samples alone) | TypeScript samples, the MCP Inspector (needs 22.19 or later) and the everything server (needs 24) |
| [Ollama](https://ollama.com) | current | `chapter01/client` (`ollama pull qwen3:1.7b`) |

A few samples call a hosted model and need an API key. Their READMEs say which and how to set it.

## Getting started

1. Clone the repository and pick a sample from the table below, or start with `templates/hello-server`, a minimal server in all three languages that checks your toolchain.
2. Run it as its README describes. For a stdio server, that is usually one of these:

   ```
   cd csharp && dotnet run
   cd python && uv run python server.py
   cd typescript && npm ci && npm run build && npm start
   ```

3. To explore a server interactively, use the MCP Inspector, the version shown in the book:

   ```
   npx @modelcontextprotocol/inspector@2.11.0
   ```

   Add the server with **Add Servers** > **Add manually**, using the same command you'd run it with, then open the server's **Settings** and set **Protocol Era** to **Auto** before you connect. The Inspector defaults to the legacy 2025-11-25 handshake, so without this step you won't see the 2026-07-28 protocol the samples are written for.

## Layout

```
chapterNN/<sample-name>/
  README.md    what it demonstrates, the book section, how to run it, tested versions
  csharp/      .NET 10: dotnet run
  python/      uv: uv run python <script>.py
  typescript/  Node, ESM: npm ci && npm run build && npm start
templates/     walking skeletons (hello-server, hello-client) in all three languages
```

## Samples

Chapters 4, 8, 11 and 13 have no samples: they cover transports, architectures, security and the protocol's future in prose and diagrams rather than runnable code.

| Sample | Book section | Languages |
|---|---|---|
| `chapter01/stdio-server/` | §1.7 | C# (canonical), Python, TS |
| `chapter01/http-server/` | §1.7–1.8 | C# (canonical), Python, TS |
| `chapter01/client/` | §1.8 | C# only (by design; Ollama-wired, LLM API alternatives included) |
| `chapter02/long-running-operation/` | §2.6.4 | C# (canonical), Python, TS |
| `chapter03/demo-resource-server/` | shared by the chapter 3 samples | C# (canonical), Python, TS |
| `chapter03/resource-client-host/` | §3.3.1 | C# (canonical), Python, TS |
| `chapter03/model-resource-client/` | §3.3.3 | C# (canonical), Python, TS |
| `chapter03/resource-link-client/` | §3.3.4 | C# (canonical), Python, TS |
| `chapter05/progressive-disclosure/` | §5.1.2 | TS (canonical), C#, Python |
| `chapter05/preview-before-execute/` | §5.1.4 | C# (canonical), Python, TS |
| `chapter05/completions/` | §5.3.3 | Python (canonical), TS, C# |
| `chapter05/low-level-handlers/` | §5.4.1, §5.4.3 | TS and Python (canonical), C# |
| `chapter05/csharp-filters/` | §5.4.2 | C# only (by design) |
| `chapter05/mcpb-bundle/` | §5.5.1–5.5.2 | Node (bundle fixture) |
| `chapter06/schema-mapping/` | §6.2.2–6.2.3 | TS (canonical), C#, Python |
| `chapter06/mrtr-client/` | §6.2.5 | TS (canonical), C#, Python |
| `chapter06/task-injection/` | §6.2.11 | C# (canonical), Python, TS |
| `chapter07/tasks-polling/` | §7.3 | C# (client-side task methods) |
| `chapter07/health-dashboard/` | §7.4 | TS only (MCP Apps; ext-apps 2.0.3) |
| `chapter09/token-validation/` | §9.2.2, §9.2.4, §9.2.7 | C# (canonical) |
| `chapter09/stepup-client/` | §9.7.4 | C# (the union rule, plus the naive anti-pattern) |
| `chapter10/eval-harness/` | §10.1 | C# (canonical), plus a minimal TS version |
| `chapter10/otel-wiring/` | §10.2 | C# only (by design) |
| `chapter10/mcp-proxy/` | §10.4.3, §10.5 | C# (canonical) |
| `chapter12/agent-as-mcp/` | §12.3 | Python only (Microsoft Agent Framework's `as_mcp_server()`) |
| `chapter12/channel-pattern/` | §12.4 (and the §12.2 harness miniature) | C# (canonical: server, chat surface and harness) |
| `templates/hello-server/`, `templates/hello-client/` | — | toolchain skeletons, not referenced by the book |

## SDK versions

Versions are pinned exactly, and every project was rebuilt and smoke-tested on these pins on 2026-10-21.

| Language | Package | Version |
|---|---|---|
| C# | `ModelContextProtocol` (plus `.AspNetCore` where used, and `.Extensions.Tasks` for the Tasks extension) | 2.3.0 |
| Python | `mcp` | 2.3.0 |
| TypeScript | `@modelcontextprotocol/server`, `@modelcontextprotocol/client` (and `core` where pinned) | 2.3.1 |

Exceptions and extras:
- C# projects that reference Microsoft.Extensions.AI packages directly pin 10.11.0, the version `ModelContextProtocol` 2.3.0 depends on (an older direct pin fails restore with NU1605).
- `chapter01/http-server` (TypeScript) uses `@modelcontextprotocol/node` 2.1.1 for the Node HTTP adapter.
- `chapter07/health-dashboard` pins `@modelcontextprotocol/ext-apps` 2.0.3.
- `chapter12/agent-as-mcp` pins Microsoft Agent Framework to a git commit, because the latest release on PyPI still requires the v1 `mcp` package. Its README has the details.

Use the v2 TypeScript packages above, not the v1 monolith (`@modelcontextprotocol/sdk`).

### Why these versions

Python and TypeScript moved past their GA releases to clear security advisories. C# is on its latest release, 2.3.0; no advisory has been published against any C# 2.x release, and the C# code printed in the book is unchanged on it. Samples get copied into real projects, so every sample is on a fixed release, even where it doesn't use the affected feature.

- **Python `mcp`:** 2.0.0–2.1.x are affected by [GHSA-84m7-p3x7-pcfv](https://github.com/advisories/GHSA-84m7-p3x7-pcfv) (unbounded session accumulation in the Streamable HTTP transport), fixed in 2.2.0.
- **TypeScript:** fixed in 2.3.0.
  - `@modelcontextprotocol/server` 2.0.0–2.2.x has three advisories:
    - [GHSA-jm63-cq47-j5jp](https://github.com/advisories/GHSA-jm63-cq47-j5jp): a shared transport or server instance could deliver one HTTP client's messages to another.
    - [GHSA-46jr-4wx8-v453](https://github.com/advisories/GHSA-46jr-4wx8-v453): one request could exhaust server memory during validation.
    - [GHSA-rvq5-wwqv-78pq](https://github.com/advisories/GHSA-rvq5-wwqv-78pq): bearer authentication accepted tokens issued for another service.
  - `@modelcontextprotocol/client` 2.0.0–2.2.x is affected by [GHSA-6prh-2h8m-c8cw](https://github.com/advisories/GHSA-6prh-2h8m-c8cw): it followed cross-origin redirects.
  - `@modelcontextprotocol/client` 2.0.0–2.1.x is also affected by [GHSA-6qxp-vccf-f47h](https://github.com/advisories/GHSA-6qxp-vccf-f47h): the OAuth client could send credentials to an authorization server the MCP server names.

Since 2.3.0, a TypeScript `McpServer` serves one connection at a time, so a server object shared across HTTP requests or sessions now fails instead of serving them. The samples create a server per connection by passing a factory to `serveStdio` or `createMcpHandler`; do the same in code you copy from them.

Two C# differences from 2.0.0 show up in the samples. Tools, prompts and resources now list in registration order (some samples still carry a `tools/list` ordering filter from when they came back in hash order; it's now redundant but harmless). And a modern `server/discover` result no longer advertises the deprecated `logging` capability; a legacy `initialize` result still does.

## SDK behavior notes

Checked against the pins above on 2026-10-21.

- **Request metadata.** All three SDKs serve handshake-less 2026-07-28 requests, over stdio and HTTP. Each request needs `io.modelcontextprotocol/protocolVersion` and `io.modelcontextprotocol/clientCapabilities` in its `_meta`, or it fails with -32602. `io.modelcontextprotocol/clientInfo` is recommended but optional, as the specification says. `tools/list` responses carry `ttlMs` and `cacheScope` in all three.
- **Version negotiation defaults differ.**
  - The Python client probes for the modern protocol by default.
  - The C# client negotiates it with no option needed.
  - The TypeScript client defaults to the legacy handshake, so pass `versionNegotiation: { mode: 'auto' }`.
- **TypeScript stdio servers must use `serveStdio(factory)`.** If you wire up `server.connect(new StdioServerTransport())` by hand, the server doesn't answer `server/discover` (-32601), so negotiating clients fall back to the legacy 2025-11-25 protocol.
- **Python serves both eras on one stdio connection, decided by the first message.** A connection that starts with `server/discover` or a 2026-07-28 request is modern. A connection that starts with `initialize` negotiates 2025-11-25 and then rejects modern requests with -32600.
- **The C# Tasks extension is a separate package:** `ModelContextProtocol.Extensions.Tasks` (`.WithTasks(store)`, `CallToolAsTaskAsync`, `CallToolWithPollingAsync`). See `chapter07/tasks-polling/`.

## License

MIT; see [LICENSE](LICENSE).
