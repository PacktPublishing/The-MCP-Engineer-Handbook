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

3. **The same resolver's per-pass limits**, against tool results built client-side the way a careless or hostile server could return them. The demo server's own tools are well-behaved, but every read still goes to the real server, including two resources it serves only for this: `file://notes/{id}/attachments` (one read, three 6 000-byte content items) and `file://exports/{quarter}/telemetry.csv` (takes 3 s to read).

   **3a. Per-resource guards and the pass byte budget.** Options: 16 KiB per resource, 1 KiB per pass, max depth 4:

| Link | Guard exercised |
|---|---|
| `file:///release_notes.md` | none — resolves normally |
| `file:///release_notes.md` again | **duplicate**, not a cycle — no second read; a note points at the first |
| `file:///release_notes.md` a third time | further repeat — no second note, only counted for the summary |
| `file:///big_dataset.csv` declaring `size` 2048 | size understated — read, then rejected on its actual 64 015 bytes |
| `file://notes/7/attachments` | **multi-item read** — each 6 000-byte item fits the 16 KiB budget, the 18 000-byte total doesn't |
| `chain://a` → `b` → `c` → `a` | **cycle** — `a` is still being resolved further up the chain |
| `file:///user_guide.md` | pass byte budget — 571 bytes don't fit the 545 left, so the pass stops |
| `file://user/1..5/preferences.md` | never read — covered by **one summary notice** |

   The result ends with the two summary notices:

   ```
   [5 further resource link(s) in this result were not resolved: pass budget of 1024 bytes reached. Not resolved: file://user/1/preferences.md, file://user/2/preferences.md, file://user/3/preferences.md and 2 more]
   [1 further repeat(s) of resource links already handled above were omitted]
   ```

   **3b. A tool result with 1 000 links.** Options: 4 links per pass. Links 1–4 resolve; the other 996 cost one notice:

   ```
   [996 further resource link(s) in this result were not resolved: link limit of 4 per pass reached. Not resolved: file://user/5/preferences.md, file://user/6/preferences.md, file://user/7/preferences.md and 993 more]
   ```

   **3c. A slow read and the pass deadline.** Options: a 1 s deadline. The 3-second export read is cancelled at 1 s and gets its own notice (`read cancelled: pass deadline of 1s has passed`); the two links after it share the summary:

   ```
   [2 further resource link(s) in this result were not resolved: pass deadline of 1s has passed. Not resolved: file:///release_notes.md, file:///helpful_tip.txt]
   ```

Dropped links are replaced with an explanatory text block, so the model learns why context is missing instead of silently losing it. Declared link metadata (`size`, `mimeType`) is checked before reading; the actual read contents are re-checked after, since links may omit or misstate both.

### What is bounded, and what isn't

Per **resource**, the budget applies to the sum of *all* content items a read returns (text as UTF-8 bytes, blobs as decoded bytes) — one resource can return many items, so checking each item on its own lets three 8 KiB items through a 16 KiB budget. Over-budget resources are dropped whole.

Per **pass** (one tool result, including every chain it starts), these limits apply together, so a result with thousands of individually acceptable links is bounded as a whole:

| Limit | C# / Python / TypeScript option | Default |
|---|---|---|
| bytes accepted from all reads | `MaxTotalBytes` / `max_total_bytes` / `maxTotalBytes` | 64 KiB |
| links handled (read, or dropped by a guard; repeats don't count) | `MaxLinks` / `max_links` / `maxLinks` | 16 |
| deadline (cancels a read still in flight) | `Timeout` / `timeout` (s) / `timeoutMs` | 30 s |
| chain depth | `MaxDepth` / `max_depth` / `maxDepth` | 2 |

The first three are **pass limits**: once one trips, the pass stops reading. The link that tripped it gets its own notice if it was actually read (an over-budget read, a cancelled read); every link after that is covered by a single summary notice naming the limit, the count, and the first three URIs. Repeats work the same way: the first repeat of a URI gets a note, later ones are only counted. So the notices are bounded too: at most one per link handled (up to `MaxLinks`, plus chain hops), plus at most two summaries — 1 000 links cost one notice, not 996.

Not bounded by the resolver: what the transport receives. `ReadResourceAsync` / `read_resource` / `readResource` return a fully materialized response, so the size checks limit what reaches the *model*, not what the client buffers. At the pinned SDKs (C# 2.3.0, Python 2.3.0, TypeScript 2.3.1) the C# and Python stdio transports put no cap on message size; the TypeScript stdio transport caps its read buffer at 10 MiB by default (`maxBufferSize`) and errors beyond that. Bounding received bytes needs a transport-level limit.

### Cycles vs. duplicates

A **cycle** is a link back to a URI that is still being resolved further up the same chain (`a` → `b` → `c` → `a`): following it would never terminate, so it is dropped. A **duplicate** is any other repeat — two links to the same URI side by side, or two chains converging on one resource. That is legitimate: the resolver reads the URI once and replaces the first repeat with a note pointing at the earlier result (its content, or the notice explaining why it was not resolved); any further repeats of that URI are counted in one summary notice. The resolver tracks the two separately: an active-path set for cycles, a pass-wide seen set for duplicates.

**On link chains:** a `resources/read` result cannot carry a resource link natively — contents are text or blob only — so a chain can only exist by client/server convention. The demo server tunnels onward links as JSON content (`{"type":"resource_link",...}`), the resolver follows that convention, and the depth guard is what makes following it safe.

## Run

```bash
cd ../demo-resource-server/csharp && dotnet build   # once
cd ../../resource-link-client/csharp && dotnet build

dotnet run
```

The host spawns the demo server as a stdio child process (`DEMO_RESOURCE_SERVER_DLL` overrides the path). Smoke: `dotnet run` exits 0. In pass 2 the `[guard]` trace lines show the size, MIME, read-error and depth guards firing, and the final block list contains one resolved text block and four explanatory drop notices. In pass 3a the trace shows two `[dedupe]` lines (a note, then an omitted repeat), `[guard]` lines for the understated size, the multi-item read, the cycle and the pass byte budget, and two `[summary]` lines; the final block list has one resolved text block (the release notes), one duplicate note, four drop notices and the two summaries. Pass 3b ends with a `[limit]` line and four resolved preference blocks plus one summary. Pass 3c shows the export read cancelled after about a second, then one summary.

**Python:**

```bash
cd ../demo-resource-server/python && uv sync   # once
cd ../../resource-link-client/python && uv sync

uv run client.py
```

The Python client spawns `demo-resource-server/python/server.py` as a stdio child (`DEMO_RESOURCE_SERVER_PY` overrides the path); the pair negotiates the modern **2026-07-28** era. Same passes, same guard semantics and `[resolve]`/`[guard]`/`[dedupe]`/`[limit]`/`[summary]` trace lines, same smoke criteria as the C# run.

**TypeScript:**

```bash
cd ../demo-resource-server/typescript && npm ci && npm run build   # once
cd ../../resource-link-client/typescript && npm ci && npm run build

npm start
```

The TS client spawns `demo-resource-server/typescript/dist/server.js` as a stdio child (`DEMO_RESOURCE_SERVER_JS` overrides the path); the pair negotiates the modern **2026-07-28** era via `versionNegotiation: { mode: 'auto' }` (printed at startup). Same passes, same guard semantics and `[resolve]`/`[guard]`/`[dedupe]`/`[limit]`/`[summary]` trace lines, same smoke criteria as the C# run.

## Deviations from the printed extracts

Written against `ModelContextProtocol` 2.0.0 and unchanged at the pinned 2.3.0, the C# extract matches the sample: the parameter is typed `McpClient` (a concrete class; there is no `IMcpClient`), and the contents-to-block conversion goes through Microsoft.Extensions.AI, `c.ToAIContent().ToContentBlock()` (text contents → `TextContentBlock`, blobs → image/audio blocks by MIME type), because `ResourceContents` has no `ToContentBlock()` of its own.

Python (`mcp` 2.3.0):

- The Python SDK has no contents→content-block conversion helper at all; `client.py` spells the mapping out in `to_content_block` (text → `TextContent`, blobs → image/audio blocks by MIME prefix).
- Failed reads raise `mcp.shared.exceptions.MCPError` (the counterpart of C#'s `McpException`).

TypeScript (`@modelcontextprotocol/client` 2.3.1):

- Also no contents→content-block helper; `client.ts` spells the mapping out in `contentsToBlock`, and read contents are discriminated structurally (`'text' in contents`) — the SDK exports no type guard for text vs. blob contents.
- Failed reads throw `ProtocolError` (the counterpart of C#'s `McpException`).
