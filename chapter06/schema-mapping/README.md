# schema-mapping

Companion sample for **Chapter 6, §6.2.2 (Schema Mapping) and §6.2.3 (Output Schema Lifting)**.

The core of a client host's model integration layer: converting MCP tool definitions into each model API's wrapper format, and converting the model's tool calls back into MCP invocations. The sample has two parts:

- **`typescript/src/demo-server.ts`** — a booking-domain MCP server (low-level `Server` API, raw JSON Schema) whose three tools deliberately exercise the JSON Schema 2020-12 features that stress provider mapping: numeric/string constraints (`minimum`, `maximum`, `minLength`, `pattern`), `default` values, an enum, a `oneOf` composition discriminated by `const`, `$defs`/`$ref`, and one tool with an `outputSchema`.
- **`typescript/src/map-tools.ts`** — a client that fetches the tools and runs them through a **registry of provider adapters**: OpenAI Chat Completions, OpenAI Responses, Anthropic, Gemini, and Ollama-style. It starts from the chapter's minimal `mcpToolToOpenAI` function and grows it into the production shape §6.2.2 describes:
  - a per-keyword strategy table per adapter — **strip**, **lift** (into description text: "Must be between 1 and 30."), or **fail fast** — with every action printed;
  - local `$ref` inlining for APIs that don't understand references, refusing non-local `$ref`s outright (the 2026-07-28 obligation: never auto-fetch network URIs from a schema) and bounding walk depth;
  - the per-provider output-schema decision from §6.2.3: pass through natively, lift into the description, or drop;
  - namespace prefixing (`demo__book_stay`) and the reverse mapping — the demo ends by routing a simulated model tool call back through the dispatch table and executing it.

No model API keys are involved: the adapters produce the request-side tool definitions and the demo prints, per provider, what was kept, stripped, lifted, or refused, with approximate token sizes. The strategy tables are data, not code — the assignments are illustrative of each provider family's posture, not a maintained support matrix; verify against current provider docs before relying on them.

## Run

- **TypeScript** (canonical): `cd typescript && npm ci && npm run build && npm start`
- **C#**: `cd csharp/DemoServer && dotnet build`, then `cd ../MapTools && dotnet run`
- **Python**: `cd python && uv sync && uv run map_tools.py`

Each mapping demo spawns its language's demo server over stdio, maps all tools through every adapter, prints the comparison, and executes one reverse-mapped tool call. All three produce the same output.

Port notes:

- The C# demo server registers `WithListToolsHandler`/`WithCallToolHandler` and the Python one the low-level `Server` with `on_list_tools`/`on_call_tool` — like the TypeScript low-level `Server`, so the raw JSON schemas ($defs, oneOf, const, pattern) reach the client byte-for-byte instead of being re-derived from types.
- The C# client is `McpClient.CreateAsync` + `StdioClientTransport`; the Python client is `mcp.client.Client` wrapping a `stdio_client(StdioServerParameters(...))` transport. Both negotiate 2026-07-28 with these servers. (The mcp 2.0.0 Python stdio server is dual-era, locked per connection by the opening message: the low-level `Server` answers `server/discover` and handshake-less 2026-07-28 requests carrying the full `_meta` envelope, so the modern era works for this pair; schemas round-trip identically either way.)

## Smoke test

The demo output *is* the smoke test — expect per-adapter action lists (e.g. `openai-responses` fails fast on `book_stay`'s `oneOf`; `ollama` strips constraints and shrinks `get_booking` to ~a third of its lifted size) and a final routed call:

```
model emitted: demo__get_booking({"booking_ref":"BK-7Q2M4X"})
routed to server "demo", tool "get_booking"
result: {"booking_ref":"BK-7Q2M4X","status":"confirmed","nights":3,"total":426,"currency":"EUR"}
```

To poke a demo server directly (stdio, handshake-less 2026-07-28), run `npm run start:server` (TS), `dotnet run` in `csharp/DemoServer` (C#), or `uv run demo_server.py` in `python/` — all three accept the same piped lines:

```
{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_booking","arguments":{"booking_ref":"BK-7Q2M4X"},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}
```
