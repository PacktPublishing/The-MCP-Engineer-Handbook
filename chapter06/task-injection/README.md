# task-injection — the model integration problem for long-running operations

Companion sample for **Chapter 6, §6.2.11 (Long-Running Operations and the Tasks Extension — The Model Integration Problem)**.

Chat-model APIs require every tool call to be immediately followed by its result, so when an operation outlives the model turn, the host synthesizes an immediate "operation started" result to unblock the model. When the *real* result arrives seconds or hours later, it has nowhere natural to go — this sample demonstrates the four known injection strategies side by side and prints the exact conversation-history shape each produces.

The demo server models the operation as ordinary tools (`start_report` returns a handle immediately; `get_report_result` reports `running` until the work completes). It stands in for a Tasks-extension task: with the extension the server would return a `CreateTaskResult` and the host would poll `tasks/get`, but the host-side injection problem is identical either way, so the tasks extension is deliberately not implemented here.

## The four strategies

| Strategy | How the result re-enters | Trade-off |
|---|---|---|
| `prepend` | Held until the next real user message, then prepended to it | Cheap and API-legal, but not proactive — the user hears nothing until they speak again, and the result arrives wearing the user's voice |
| `synthetic-turn` | A fabricated user-role turn is appended immediately and the model runs on it | Proactive and responsive, but the history now contains a turn no user ever wrote — a lie in the transcript that can confuse the model (and any human reading the log) |
| `meta-tool` | The model calls a host-side `check_completed_operations` tool | Every tool call stays legally paired with its result and the model stays in control, but it only works after something nudges the model to look, and adds a round trip |
| `fresh-invocation` | A new agent invocation starts with the result in its initial context | Clean history with no synthesis — right for headless agents — at the cost of everything the previous conversation knew |

None is clean. The multi-tool pattern (§6.2.11) sidesteps the problem entirely because the model itself decides when to fetch the result — and running this sample against a real model shows exactly that: given the `start_report`/`get_report_result` pair, gpt-4.1-mini polls for the result on its own before any injection strategy fires, and ignores the meta-tool in favor of the server's own status tool.

## Layout

```
csharp/ReportServer/       demo MCP server (stdio): start_report, get_report_result
csharp/TaskInjectionHost/  client host: MCP client + pluggable IChatClient + the four strategies
python/                    same pair: report_server.py + host.py + scripted_chat_model.py
typescript/                same pair: src/report-server.ts + src/host.ts + src/scripted-chat-model.ts
```

The C# implementation is canonical; the Python and TypeScript ports produce the same
conversation histories and comparison summary.

## Run (C#)

```bash
cd csharp/ReportServer && dotnet build
cd ../TaskInjectionHost && dotnet build

dotnet run -- prepend            # or synthetic-turn | meta-tool | fresh-invocation
dotnet run -- --all              # all four, plus a comparison summary
```

The host launches `ReportServer` as a stdio child process, so no separate server startup is needed. `REPORT_DELAY_SECONDS` changes how long the "report" takes (default 3); `REPORT_SERVER_DLL` overrides where the host looks for the built server.

The printed conversation histories are the deliverable: each strategy run ends with the full history, roles labeled, with a marker showing which messages appeared when the real result landed.

## Run (Python)

```bash
cd python
uv run host.py prepend           # or synthetic-turn | meta-tool | fresh-invocation
uv run host.py --all             # all four, plus a comparison summary
```

The host spawns `report_server.py` as a stdio child process using its own interpreter (`REPORT_SERVER_PY` overrides the script path). The `Client` defaults to `mode="auto"`, which probes `server/discover` — the Python stdio server answers it, so this pair negotiates 2026-07-28 (against an older server the client falls back to the legacy initialize handshake transparently).

## Run (TypeScript)

```bash
cd typescript
npm install && npm run build

node dist/host.js prepend        # or synthetic-turn | meta-tool | fresh-invocation
node dist/host.js --all          # all four, plus a comparison summary
```

The host spawns `dist/report-server.js` as a stdio child process (`REPORT_SERVER_JS` overrides the path). The client passes `versionNegotiation: { mode: 'auto' }` to negotiate the modern era; the server uses `serveStdio`, which serves both eras.

## Plugging in a real model

By default the host uses a deterministic scripted chat model — no API key needed, identical output every run. In C#, set `OPENAI_API_KEY` (and optionally `OPENAI_MODEL`, default `gpt-4.1-mini`) to run the same scenarios against a real model through the same interface. Any other provider works the same way: `CreateChatClient` in `Program.cs` returns an `IChatClient`, and the server's tools flow into `ChatOptions.Tools` unchanged because the C# SDK's `McpClientTool` derives from `Microsoft.Extensions.AI.AIFunction`. Real-model runs are non-deterministic — that is the point; seeing actual model behavior is worth more here than architectural reasoning.

The Python and TypeScript ports have no Microsoft.Extensions.AI equivalent, so each defines a minimal `ChatModel` interface (`respond(history, tools) -> ChatMessage`) in `scripted_chat_model.py` / `scripted-chat-model.ts` and ships only the scripted implementation — no LLM SDK dependency. A real provider plugs in at `create_chat_model` / `createChatModel` in the host: implement `respond` over the provider's SDK, mapping the history and the `ToolSpec` list (name, description, JSON schema — taken straight from `list_tools`) to the provider's message and tool formats.

## Smoke test (server alone)

```bash
cd csharp/ReportServer && dotnet build
{ printf '%s\n' \
  '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}' \
  '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"start_report","arguments":{"topic":"smoke"},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}'; \
  sleep 4; printf '%s\n' \
  '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_report_result","arguments":{"operationId":"op-001"},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}'; \
  sleep 1; } | dotnet bin/Debug/net10.0/ReportServer.dll 2>/dev/null
```

Expect the tool list, a `running` handle, and a `completed` report.

TypeScript is the same handshake-less exchange piped into `node dist/report-server.js` (after `npm run build`). The Python stdio server is dual-era, locked per connection by the opening message: handshake-less 2026-07-28 lines work when every request carries the full `_meta` envelope (`clientInfo` + `clientCapabilities` + `protocolVersion`), or open with an `initialize` handshake and drop the `_meta` blocks, as here:

```bash
cd python
{ printf '%s\n' \
  '{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2026-07-28","capabilities":{},"clientInfo":{"name":"smoke","version":"0"}}}' \
  '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
  '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"start_report","arguments":{"topic":"smoke"}}}'; \
  sleep 6; printf '%s\n' \
  '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_report_result","arguments":{"operationId":"op-001"}}}'; \
  sleep 1; } | uv run report_server.py 2>/dev/null
```

The Python sleep is longer because interpreter startup eats into the report delay: the shell's clock starts when the pipe is written, the server's when it finishes importing.
