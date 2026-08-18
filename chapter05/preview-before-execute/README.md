# preview-before-execute

Companion sample for **Chapter 5, §5.1.4 (Preview-Before-Execute)**.

A find-and-replace operation is a logical operation that blurs read and write: it composes several internal API calls (search, create edit batch, one replace per matched line, commit) into a single tool. The preview-before-execute pattern lets the model — and through it, the user — see exactly what a high-impact call will do before it does it: which lines change, and the sequence of internal operations that will run. The server implements the pattern both ways the chapter describes, over an in-memory store of five multi-line documents.

## The two variants

**Variant 1 — `dry_run` parameter.** One tool, `find_and_replace(find, replace, dry_run)`. With `dry_run: true` it returns the preview and changes nothing; with `dry_run: false` (the default) it executes. Fewer tools and fewer tokens spent on tool definitions, but the return contract is dual-mode: the same tool returns a preview in one mode and an execution result in the other, which is a more complex contract for the model to follow — and nothing forces a preview to happen before execution.

**Variant 2 — separate tools linked by a token.** `preview_find_and_replace(find, replace)` returns the same preview plus a `preview_token`; `execute_find_and_replace(preview_token)` is the only way to apply the changes. This is the explicit handle pattern from Chapter 4 applied as an enforcement mechanism: execution without a token fails with an instructive result that steers the model to the preview tool first. Tokens are single-use, expire after 5 minutes, and are rejected if any affected document changed since the preview (stale plan). Each tool's return schema stays simple, at the cost of an extra tool definition. Choose this variant when the operation is risky enough that previewing must be guaranteed, not just available.

The previewed plan is stored keyed by token in an in-memory dictionary (`PreviewStore.cs`) — a stand-in for the durable storage a production server would use (database, Redis, ...) so the execute call can land on any instance. A production token would also bind the authenticated user.

Result texts follow the chapter's result-shaping guidance: every result states plainly whether changes were made, and failures say what to do next in positive terms ("Call preview_find_and_replace first ...") rather than only what went wrong.

## Run

- **C#** (canonical): `cd csharp && dotnet run`
- **Python** (stdio, legacy era in 2.0.0b1): `cd python && uv sync && uv run python server.py`
- **TypeScript**: `cd typescript && npm ci && npm run build && npm start`

## Smoke test (stdio)

Pipe requests into the server process (or type them interactively). C# and TypeScript accept handshake-less 2026-07-28 requests as shown below; the Python stdio server (2.0.0b1, legacy era) needs the `initialize` handshake first:

```
{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2026-07-28","capabilities":{},"clientInfo":{"name":"smoke","version":"0"}}}
{"jsonrpc":"2.0","method":"notifications/initialized"}
```

List the tools:

```
{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}
```

Variant 1 — dry run, then the real thing:

```
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"find_and_replace","arguments":{"find":"Aurora","replace":"Polaris","dry_run":true},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"find_and_replace","arguments":{"find":"Aurora","replace":"Polaris","dry_run":false},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}
```

Variant 2 — execution is gated on the token, so run these in one server session and copy the token from the preview response into the execute call:

```
{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"execute_find_and_replace","arguments":{},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}
{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"preview_find_and_replace","arguments":{"find":"staging","replace":"pre-production"},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}
{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"execute_find_and_replace","arguments":{"preview_token":"<token from id 5>"},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}
```

Call 4 (no token) returns the instructive failure; call 6 applies the previewed plan. Repeating call 6 shows the single-use rejection. To see the staleness check, create a preview and run a `find_and_replace` touching the same documents before executing it. `list_documents` / `read_document` inspect the store at any point.
