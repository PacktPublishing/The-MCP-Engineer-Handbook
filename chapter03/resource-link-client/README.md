# resource-link-client — resolving resource links, hardened

Companion sample for **Chapter 3, §3.3.4 (Resolving Resource Links)**.

A tool can return a pointer to a resource instead of its content; most servers expect the client to read the resource and substitute the contents into the tool result. The sample runs both versions from the chapter:

1. **The book-page `ResolveLinksAsync`** — the bare substitution pass — against `get_tip_of_the_day`, whose single well-behaved link resolves cleanly.
2. **The production version** (`HardenedLinkResolver`) — against `get_research_bundle`, whose five links exercise every guard the book page leaves out:

| Link | Guard exercised |
|---|---|
| `file:///release_notes.md` | none — resolves normally |
| `file:///big_dataset.csv` (declared `size` 64 000) | size budget, rejected **without reading** |
| `file:///podcast.wav` (`audio/wav`) | MIME-type filter |
| `file:///does_not_exist.txt` | error handling for failed reads |
| `chain://a` → `b` → `c` → `a` | depth guard (max depth 2) plus cycle detection |

Dropped links are replaced with an explanatory text block, so the model learns why context is missing instead of silently losing it. Declared link metadata (`size`, `mimeType`) is checked before reading; the actual read contents are re-checked after, since links may omit both.

**On link chains:** a `resources/read` result cannot carry a resource link natively — contents are text or blob only — so a chain can only exist by client/server convention. The demo server tunnels onward links as JSON content (`{"type":"resource_link",...}`), the resolver follows that convention, and the depth guard is what makes following it safe.

## Run

```bash
cd ../demo-resource-server/csharp && dotnet build   # once
cd ../../resource-link-client/csharp && dotnet build

dotnet run
```

The host spawns the demo server as a stdio child process (`DEMO_RESOURCE_SERVER_DLL` overrides the path). Smoke: `dotnet run` exits 0; the `[guard]` trace lines show all four hardenings firing, and the final block list contains one resolved text block and four explanatory drop notices.

**Python:**

```bash
cd ../demo-resource-server/python && uv sync   # once
cd ../../resource-link-client/python && uv sync

uv run client.py
```

The Python client spawns `demo-resource-server/python/server.py` as a stdio child (`DEMO_RESOURCE_SERVER_PY` overrides the path); the pair negotiates the modern **2026-07-28** era. Same two passes, same guard semantics and `[resolve]`/`[guard]` trace lines, same smoke criteria as the C# run.

**TypeScript:**

```bash
cd ../demo-resource-server/typescript && npm ci && npm run build   # once
cd ../../resource-link-client/typescript && npm ci && npm run build

npm start
```

The TS client spawns `demo-resource-server/typescript/dist/server.js` as a stdio child (`DEMO_RESOURCE_SERVER_JS` overrides the path); the pair negotiates the modern **2026-07-28** era via `versionNegotiation: { mode: 'auto' }` (printed at startup). Same two passes, same guard semantics and `[resolve]`/`[guard]` trace lines, same smoke criteria as the C# run.

## Deviations from the printed extracts

Written against `ModelContextProtocol` 2.0.0-preview.1:

- `IMcpClient` does not exist in preview.1 — the parameter is typed `McpClient` (a concrete class; its only interface is `IAsyncDisposable`).
- `read.Contents.Select(c => c.ToContentBlock())` does not compile: there is no `ToContentBlock()` on `ResourceContents`. The conversion goes through Microsoft.Extensions.AI, `c.ToAIContent().ToContentBlock()` (text contents → `TextContentBlock`, blobs → image/audio blocks by MIME type).

Python (`mcp` 2.0.0b1):

- The b1 SDK has no contents→content-block conversion helper at all; `client.py` spells the mapping out in `to_content_block` (text → `TextContent`, blobs → image/audio blocks by MIME prefix).
- Failed reads raise `mcp.shared.exceptions.MCPError` (the counterpart of C#'s `McpException`).

TypeScript (`@modelcontextprotocol/client` 2.0.0-beta.1):

- Also no contents→content-block helper; `client.ts` spells the mapping out in `contentsToBlock`, and read contents are discriminated structurally (`'text' in contents`) — the SDK exports no type guard for text vs. blob contents.
- Failed reads throw `ProtocolError` (the counterpart of C#'s `McpException`).
