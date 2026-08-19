# Client-Side MRTR Elicitation (`mrtr-client`)

Companion sample for **Chapter 6, Section 6.2.5 — Implementing Elicitation (Client Side)**.

On the `2026-07-28` revision, elicitation arrives through the multi round-trip request (MRTR) pattern: the server answers `tools/call` with an `input_required` result carrying embedded `elicitation/create` requests and an opaque `requestState`; the client gathers answers and re-issues the call. This sample implements the client side both ways:

- **`src/mrtr-client.ts` — the manual gather-and-retry loop** (the book's teaching path). `callToolGatheringInput` matches the chapter's printed snippet; `callToolGatheringInputBounded` adds the production bounds the chapter calls for (retry budget, whole-flow timeout, user-facing cancel via `AbortSignal`).
- **`src/native.ts` — the SDK-native path**. In the v2 client, `callTool()` fulfils `input_required` results automatically by default, dispatching each embedded request to the handler registered via `setRequestHandler('elicitation/create', ...)` and retrying with the collected `inputResponses` and a byte-exact `requestState` echo, up to `inputRequired.maxRounds` (default 10). What the SDK does *not* do — form rendering, policy, validating user content against `requestedSchema` — stays in your handler.

Both entries share **`src/input-handler.ts`**: a schema-driven console form (labels from `title`, help from `description`, pre-populated `default`s), the accept/decline/cancel three-action model, validation of user input against the requested schema before responding, and pre-user policy hooks (credential-solicitation denial; an opt-in auto-confirm policy).

**`src/demo-server.ts`** is the companion server: `book_meeting` elicits a details form, then a *second* confirmation elicitation on the retry, so the loop genuinely iterates; `never_satisfied` asks forever, for retry-budget demos. Tool handlers return `inputRequired({ inputRequests, requestState })` and read retried answers with `inputResponse` / the schema-aware `acceptedContent`. Note the entry point: stdio servers must be served through `serveStdio(factory)` — a hand-wired `StdioServerTransport` connection serves the legacy era only, where MRTR does not exist.

## TypeScript

```bash
cd typescript
npm install
npm run build

# Interactive (renders the form on your terminal; !decline / !cancel for the other actions)
npm start

# Scripted (headless): answers consumed in order, one per elicitation
MRTR_ANSWERS='[{"title":"Sprint sync","duration":"30"},{"confirm":true}]' npm start

# Declined run
MRTR_ANSWERS='[{"action":"decline"}]' npm start

# Retry budget tripping against a misbehaving server
MRTR_MAX_ROUNDS=3 MRTR_ANSWERS='[{"again":true},{"again":true},{"again":true}]' node dist/mrtr-client.js never_satisfied

# Policy hook auto-answering the confirmation round
MRTR_POLICY=autoconfirm MRTR_ANSWERS='[{"title":"Design review","duration":"60"}]' npm start

# SDK-native driver (same env vars)
MRTR_ANSWERS='[{"title":"1:1","duration":"15"},{"confirm":true}]' npm run start:native
```

Smoke (raw wire, handshake-less modern request — shows the `input_required` result directly):

```bash
{ echo '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"book_meeting","arguments":{"room":"4B"},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{"elicitation":{}}}}}'; sleep 3; } | node dist/demo-server.js
```

## C\#

Demonstrates the **modern (2026-07-28) MRTR flow over stdio**, both directions native to `ModelContextProtocol` 2.0.0. Same tools, same env vars; two projects (`DemoServer`, `MrtrClient`).

Where the C# SDK differs from TS:

- **Server:** a tool handler produces `input_required` by *throwing* `InputRequiredException(inputRequests, requestState)` — there is no result-value helper. Retried answers arrive on `context.Params.InputResponses` / `context.Params.RequestState`; read one with `response.Deserialize(InputResponse.ElicitResultJsonTypeInfo)`. `requestState` goes to the wire exactly as minted (no built-in sealing — protecting it is your job).
- **Legacy clients are served transparently:** on a stateful transport, an `InputRequiredException` thrown at a client that negotiated 2025-11-25 is resolved by the *server* via old-style server→client `elicitation/create` calls, and the handler is re-invoked with the responses patched in. (TS downgrades via a legacy shim; Python errors — see below.)
- **Client:** the MRTR loop is welded into `McpClient` and is *always on* — embedded requests dispatch to `McpClientOptions.Handlers.ElicitationHandler`, capped at a hard-coded 10 rounds. There is **no `allowInputRequired` equivalent, no budget knob, and no way to receive the interim result**, so the manual gather-and-retry loop (`--manual`, `ManualLoop.cs`) is expressed over the raw `IClientTransport`/`ITransport` with handshake-less `_meta`-envelope requests — an honest picture of what the SDK does on your behalf.
- The plain-`enum` elicitation schema form is deprecated in this SDK (SEP-1330); the demo server emits the `oneOf`/`const`/`title` single-select form instead. The client handler still renders both.

```bash
cd csharp
dotnet build DemoServer && dotnet build MrtrClient

# SDK-native driver (default; fixed 10-round budget, timeout/cancel via CancellationToken)
MRTR_ANSWERS='[{"title":"Sprint sync","duration":"30"},{"confirm":true}]' dotnet run --project MrtrClient --no-build

# Manual loop over the raw transport (configurable budget)
MRTR_ANSWERS='[{"title":"Sprint sync","duration":"30"},{"confirm":true}]' dotnet run --project MrtrClient --no-build -- --manual
MRTR_ANSWERS='[{"action":"decline"}]' dotnet run --project MrtrClient --no-build -- --manual
MRTR_MAX_ROUNDS=3 MRTR_ANSWERS='[{"again":true},{"again":true},{"again":true}]' dotnet run --project MrtrClient --no-build -- never_satisfied --manual
MRTR_POLICY=autoconfirm MRTR_ANSWERS='[{"title":"Design review","duration":"60"}]' dotnet run --project MrtrClient --no-build -- --manual
```

Raw wire smoke (same shape as the TS one):

```bash
{ echo '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"book_meeting","arguments":{"room":"4B"},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{"elicitation":{}}}}}'; sleep 4; } | dotnet DemoServer/bin/Debug/net10.0/DemoServer.dll
```

## Python

Demonstrates the **modern (2026-07-28) MRTR flow over stdio** with `mcp` 2.0.0. This pair negotiates 2026-07-28 over stdio: the client's default `mode="auto"` probes `server/discover`, and `server.run()` (stdio) serves both eras through `serve_dual_era_loop` — a modern opening gets the modern era. Same tools, same env vars.

Where the Python SDK differs from TS:

- **Server:** a tool function *returns* an `InputRequiredResult` (from `mcp_types`) directly; retried answers arrive on `ctx.input_responses` / `ctx.request_state`. `MCPServer` **seals `requestState` by default** (`RequestStateBoundary`: AES-256-GCM, ephemeral process key, 600 s TTL, fail-closed) — handlers only ever see plaintext they minted; multi-instance deployments pass shared keys via `request_state_security=`.
- **No legacy shim:** on a session that negotiated 2025-11-25 (an `initialize` opening), a tool returning `InputRequiredResult` is a server error (`-32603 Handler returned an invalid result`). Nothing enforces the client's `elicitation` capability either — the tool checks `ctx.client_capabilities` itself.
- **Client:** `client.call_tool()` runs the MRTR loop automatically (cap `input_required_max_rounds`, default 10; state-only results retried with 50→250 ms backoff). The manual loop uses `client.session.call_tool(..., allow_input_required=True)`, with `input_responses=`/`request_state=` keyword arguments on the retry. Registering `elicitation_callback` is also what *advertises* the elicitation capability, so the manual entry registers it even though the loop never dispatches to it.

```bash
cd python
uv sync

# Manual loop (mrtr_client.py; bounded — retry budget, 120 s deadline, Ctrl+C cancel)
MRTR_ANSWERS='[{"title":"Sprint sync","duration":"30"},{"confirm":true}]' uv run mrtr_client.py
MRTR_ANSWERS='[{"action":"decline"}]' uv run mrtr_client.py
MRTR_MAX_ROUNDS=3 MRTR_ANSWERS='[{"again":true},{"again":true},{"again":true}]' uv run mrtr_client.py never_satisfied
MRTR_POLICY=autoconfirm MRTR_ANSWERS='[{"title":"Design review","duration":"60"}]' uv run mrtr_client.py

# SDK-native driver
MRTR_ANSWERS='[{"title":"1:1","duration":"15"},{"confirm":true}]' uv run native.py
```

Raw wire smoke (the `requestState` in the output is the boundary's sealed token, not the tool's JSON):

```bash
{ echo '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"book_meeting","arguments":{"room":"4B"},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{"elicitation":{}}}}}'; sleep 3; } | uv run demo_server.py
```

`demo_server.py --http` serves the same tools over streamable HTTP.
