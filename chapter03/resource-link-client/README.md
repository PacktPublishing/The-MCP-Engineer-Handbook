# resource-link-client — resolving resource links, hardened

Companion sample for **Chapter 3, §3.3.4 (Resolving Resource Links)**.

A tool can return a pointer to a resource instead of its content; most servers expect the client to read the resource and substitute the contents into the tool result. The sample runs both versions from the chapter, then pushes the production version's per-pass limits:

1. **The book-page `ResolveLinksAsync`** — the bare substitution pass — against `get_tip_of_the_day`, whose single well-behaved link resolves cleanly.
2. **The production version** (`HardenedLinkResolver`) — against `get_research_bundle`, whose five links exercise the per-link guards the book page leaves out:

| Link | Guard exercised |
|---|---|
| `file:///release_notes.md` | none — resolves normally |
| `file:///big_dataset.csv` (declared `size` 64 000) | size budget, rejected **without reading** |
| `file:///podcast.wav` (`audio/wav`) | MIME-type filter |
| `file:///does_not_exist.txt` | error handling for failed reads |
| `chain://a` → `b` → `c` → `a` | depth guard (max depth 2) |

3. **The same resolver's per-pass limits** — against a tool result built client-side the way a careless or hostile server could return it (the demo server's own tools are well-behaved; every read still goes to the real server). Options: 16 KiB per resource, 1 KiB per pass, 8 reads per pass, max depth 4:

| Link | Guard exercised |
|---|---|
| `file:///release_notes.md` | none — resolves normally |
| `file:///release_notes.md` again | **duplicate**, not a cycle — no second read; a note points at the first |
| `file:///big_dataset.csv` declaring `size` 2048 | size understated — read, then rejected on its actual 64 015 bytes |
| `chain://a` → `b` → `c` → `a` | **cycle** — `a` is still being resolved further up the chain |
| `file:///user_guide.md` | pass byte budget — 571 bytes don't fit the 545 left |
| `file://user/1..5/preferences.md` | 1 and 2 resolve; 3–5 hit the read cap of 8 reads per pass |

Dropped links are replaced with an explanatory text block, so the model learns why context is missing instead of silently losing it. Declared link metadata (`size`, `mimeType`) is checked before reading; the actual read contents are re-checked after, since links may omit or misstate both.

### What is bounded, and what isn't

Per **resource**, the budget applies to the sum of *all* content items a read returns (text as UTF-8 bytes, blobs as decoded bytes) — one resource can return many items, so checking each item on its own lets three 8 KiB items through a 16 KiB budget. Over-budget resources are dropped whole.

Per **pass** (one tool result, including every chain it starts), four limits apply together, so a result with thousands of individually acceptable links is bounded as a whole:

| Limit | C# / Python / TypeScript option | Default |
|---|---|---|
| bytes accepted from all reads | `MaxTotalBytes` / `max_total_bytes` / `maxTotalBytes` | 64 KiB |
| `resources/read` calls | `MaxReads` / `max_reads` / `maxReads` | 16 |
| deadline (cancels a read still in flight) | `Timeout` / `timeout` (s) / `timeoutMs` | 30 s |
| chain depth | `MaxDepth` / `max_depth` / `maxDepth` | 2 |

Each link the limits stop is replaced by one notice, so the resolved result never holds more blocks than the original had links (plus chain hops).

Not bounded by the resolver: what the transport receives. `ReadResourceAsync` / `read_resource` / `readResource` return a fully materialized response, so the size checks limit what reaches the *model*, not what the client buffers. At SDK 2.0.0 the C# and Python stdio transports put no cap on message size; the TypeScript stdio transport caps its read buffer at 10 MiB by default (`maxBufferSize`) and errors beyond that. Bounding received bytes needs a transport-level limit.

### Cycles vs. duplicates

A **cycle** is a link back to a URI that is still being resolved further up the same chain (`a` → `b` → `c` → `a`): following it would never terminate, so it is dropped. A **duplicate** is any other repeat — two links to the same URI side by side, or two chains converging on one resource. That is legitimate: the resolver reads the URI once and replaces later occurrences with a note pointing at the earlier result (its content, or the notice explaining why it was not resolved). The resolver tracks the two separately: an active-path set for cycles, a pass-wide seen set for duplicates.

**On link chains:** a `resources/read` result cannot carry a resource link natively — contents are text or blob only — so a chain can only exist by client/server convention. The demo server tunnels onward links as JSON content (`{"type":"resource_link",...}`), the resolver follows that convention, and the depth guard is what makes following it safe.

## Run

```bash
cd ../demo-resource-server/csharp && dotnet build   # once
cd ../../resource-link-client/csharp && dotnet build

dotnet run
```

The host spawns the demo server as a stdio child process (`DEMO_RESOURCE_SERVER_DLL` overrides the path). Smoke: `dotnet run` exits 0. In pass 2 the `[guard]` trace lines show the size, MIME, read-error and depth guards firing, and the final block list contains one resolved text block and four explanatory drop notices. In pass 3 the trace shows one `[dedupe]` line and `[guard]` lines for the understated size, the cycle, the pass byte budget and three read-cap drops; the final block list has three resolved text blocks (release notes, preferences 1 and 2), one duplicate note and six drop notices.

**Python:**

```bash
cd ../demo-resource-server/python && uv sync   # once
cd ../../resource-link-client/python && uv sync

uv run client.py
```

The Python client spawns `demo-resource-server/python/server.py` as a stdio child (`DEMO_RESOURCE_SERVER_PY` overrides the path); the pair negotiates the modern **2026-07-28** era. Same three passes, same guard semantics and `[resolve]`/`[guard]`/`[dedupe]` trace lines, same smoke criteria as the C# run.

**TypeScript:**

```bash
cd ../demo-resource-server/typescript && npm ci && npm run build   # once
cd ../../resource-link-client/typescript && npm ci && npm run build

npm start
```

The TS client spawns `demo-resource-server/typescript/dist/server.js` as a stdio child (`DEMO_RESOURCE_SERVER_JS` overrides the path); the pair negotiates the modern **2026-07-28** era via `versionNegotiation: { mode: 'auto' }` (printed at startup). Same three passes, same guard semantics and `[resolve]`/`[guard]`/`[dedupe]` trace lines, same smoke criteria as the C# run.

## Deviations from the printed extracts

Written against `ModelContextProtocol` 2.0.0:

- `IMcpClient` does not exist in 2.0.0 — the parameter is typed `McpClient` (a concrete class; its only interface is `IAsyncDisposable`).
- `read.Contents.Select(c => c.ToContentBlock())` does not compile: there is no `ToContentBlock()` on `ResourceContents`. The conversion goes through Microsoft.Extensions.AI, `c.ToAIContent().ToContentBlock()` (text contents → `TextContentBlock`, blobs → image/audio blocks by MIME type).

Python (`mcp` 2.0.0):

- The Python SDK has no contents→content-block conversion helper at all; `client.py` spells the mapping out in `to_content_block` (text → `TextContent`, blobs → image/audio blocks by MIME prefix).
- Failed reads raise `mcp.shared.exceptions.MCPError` (the counterpart of C#'s `McpException`).

TypeScript (`@modelcontextprotocol/client` 2.0.0):

- Also no contents→content-block helper; `client.ts` spells the mapping out in `contentsToBlock`, and read contents are discriminated structurally (`'text' in contents`) — the SDK exports no type guard for text vs. blob contents.
- Failed reads throw `ProtocolError` (the counterpart of C#'s `McpException`).
