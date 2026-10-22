# eval-harness — evaluating an MCP server in-process

Companion sample for **Chapter 10, §10.1 (Evaluating MCP Servers)** — specifically §10.1.2 (testing context, not code; duds), §10.1.4 (the mocking spectrum, rung 1), and §10.1.5 (harness design: in-memory transports, N-run pass rates).

An eval harness for MCP is a minimal host: it connects to the server under test, presents the tool surface to a model, runs the loop, and records everything. This one runs the server **in-process over the SDK's in-memory transport** — no network, no subprocess, no port allocation — while exercising the full protocol stack. The corpus is six eval tasks (prompt + outcome checks), each run N times and scored as a pass *rate*, because a single run of an eval task is an anecdote.

The server under test is a small knowledge-base server (`search_documents`, `read_document`, `count_documents`) with a fixture corpus that contains a deliberate trap: a deprecated refund FAQ that keyword search ranks above the current policy. A model that reads the first hit produces a **dud** — every call valid, no errors, a confident answer, wrong — and only the outcome check catches it.

## Beyond paired pipes: full-HTTP in-memory

The paired-pipe wiring here exercises the whole protocol stack but not the HTTP transport. When an eval must include the HTTP seam (auth, headers, routing), the C# SDK's own test suite has an in-memory Kestrel transport (`tests/ModelContextProtocol.AspNetCore.Tests/Utils/KestrelInMemory*.cs` in the csharp-sdk repo) that runs full Streamable HTTP without sockets — it is not shipped in the NuGet package, so copy those utilities from the repo. The SDK's `ClientServerTestBase` also confirms the paired-pipe pattern used here is the SDK team's own canonical in-memory wiring.

## The in-memory transport APIs (verified, both SDKs)

**C# (`ModelContextProtocol` 2.3.0)** — there is no dedicated "in-memory transport" type; the seam is the *stream* transport pair, wired to two `System.IO.Pipelines.Pipe` instances (one per direction):

```csharp
var clientToServer = new Pipe();
var serverToClient = new Pipe();

builder.Services.AddMcpServer()
    .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
    .WithTools<DocsTools>();
// ... await host.StartAsync();

var mcp = await McpClient.CreateAsync(new StreamClientTransport(
    serverInput: clientToServer.Writer.AsStream(),
    serverOutput: serverToClient.Reader.AsStream()));
```

The pieces: `WithStreamServerTransport(Stream, Stream)` (hosting extension; or `ModelContextProtocol.Server.StreamServerTransport` directly), `ModelContextProtocol.Protocol.StreamClientTransport(Stream serverInput, Stream serverOutput)`, and the concrete `McpClient` class (`McpClient.CreateAsync` — the SDK has no `IMcpClient` interface). `System.IO.Pipelines` is a transitive dependency of the SDK.

**TypeScript (2.3.1)** — `InMemoryTransport.createLinkedPair(): [InMemoryTransport, InMemoryTransport]`, exported from **both** `@modelcontextprotocol/client` and `@modelcontextprotocol/server`; hand one side to `Client.connect`, the other to `McpServer.connect`. Note that a hand-wired `server.connect(...)` serves the legacy era only — the pair negotiates `2025-11-25` (re-verified at GA and at 2.3.1: even a client with `versionNegotiation: { mode: 'auto' }` probes `server/discover`, gets no answer, and falls back to `initialize`), which is fine for evals.

## Layout

```
csharp/DocsServer/    the server under test: fixture corpus + three tools; also runs standalone over stdio
csharp/EvalHarness/   the harness: in-process wiring, task corpus, scripted IChatClient, N-run scoring, --mock, --dump
typescript/           minimal counterpart: server + client over InMemoryTransport.createLinkedPair, one task
```

The C# implementation is canonical; the TypeScript project is deliberately tiny — it exists to demonstrate the linked-pair API.

Both C# hosts register `DocsTools` with a `tools/list` filter (`ToolOrder.cs`) that restores declaration order: C# SDK releases before 2.3.0 list tools in hash order (stable across runs, but not declaration order), and the tool order is part of what the model sees. The pinned 2.3.0 lists them in registration order, so the filter is now redundant; it keeps older releases consistent.

## Run (C#)

```bash
cd csharp/EvalHarness
dotnet run                # real server, in-process; full report
dotnet run -- --mock      # rung-1 mock mode
dotnet run -- --runs 20   # change N (default 5; 1 to 100)
dotnet run -- --dump out  # also write each run's model-visible context to out/<task>-run<N>.json
```

By default the model is a deterministic scripted `IChatClient` (no API key needed) that plays a mid-tier tool-calling model — including reading the first search hit for the refund task (the dud) and only reaching for `count_documents` on even run indices (the flaky task, standing in for the marginal-context variance N-run scoring exists to surface). Expected report: `refund-window-annual` at 0% flagged as DUD, `count-billing-docs` at 60%, everything else at 100% (23/30 runs); exit code 1 whenever any run failed (CI-gate semantics), and 2 for a bad command line (`--runs` must be a whole number from 1 to 100).

Every task also gets a behavioral `gave a final answer` check, in every mode: a run that ends on an empty reply, or runs out of its eight-turn budget, fails. A tool call the server rejects with a JSON-RPC error (a hallucinated tool name, for instance) goes back to the model as a failed tool result, the way a host would report it, so one bad call fails a run rather than aborting the matrix. The harness doesn't put deadlines on model or tool calls; a real one would give each a timeout, since the turn budget doesn't bound the time spent inside one call.

Set `OPENAI_API_KEY` (and optionally `OPENAI_MODEL`, default `gpt-4.1-mini`) to run the same corpus against a real model through the same `IChatClient` seam. In my runs, gpt-4.1-mini read past the deprecated FAQ and scored 30/30. That is one anecdote rather than a benchmark, but it makes the point: pass rates are a property of (context × model), not of the server alone.

### What the numbers mean

- **The default pass rates show harness mechanics, not model performance.** The scripted model follows programmed branches and ignores the tool descriptions, so editing a description doesn't move its score. Only a real model measures context quality.
- **The outcome checks demonstrate outcome scoring; they are not a production scorer.** They are bounded pattern matches: `3 documents` doesn't match inside `13 documents`, a negated mention (`not 30 days`) doesn't count, and the refund task also fails any answer that mentions the dud's `60 days`. Pattern matching still can't read meaning, though: "Invoices are payable within 30 days." passes the refund task, and so does the right number attached to the wrong plan. Real evals extract a structured answer and compare it to the expected value, or grade against a rubric.

### `--dump`: the transcript the model saw (§10.1.5)

The report scores a summary of each run (tool calls, final answer, turn count). With `--dump <dir>`, each run also writes `<dir>/<task>-run<N>.json` with what the model was actually given and did:

- `tools`: the tool surface as handed to the `IChatClient` (name, description, input schema)
- `messages`: the full history (the prompt, every assistant message and tool call, every tool result, the final answer); the harness sends no system prompt
- `model` and `responseModelId` (the exact model version a real provider reports), and `options` (only the fields that are set; the harness sets none)
- `modelTurns`, and `termination`: `final_answer` or `turn_budget`

Each file is a few kilobytes, serialized with MEAI's `AIJsonUtilities.DefaultOptions`.

### `--mock`: rung 1 of the mocking spectrum (§10.1.4)

Mock mode presents a tool surface **snapshotted from the real server's `tools/list`** — names, descriptions, and schemas are exactly production's, so the mock cannot drift — and fabricates only the results. Outcome checks are skipped (with fabricated data there is no "right answer"); behavioral checks (tool selection, sequencing, efficiency, recovery) still run, so the flaky task still shows 60% while the dud task passes — rung 1 structurally cannot see duds, and the report says so.

## Run (TypeScript)

```bash
cd typescript
npm install && npm run build
node dist/eval.js
```

Prints the pass rate for one eval task (the dud task, 0/3) over the linked in-memory pair. It's an API demonstration, not a gate, but its exit code still reflects the result: 0 only if every run passed, so this one exits 1.

## Smoke test (stdio server standalone)

```bash
cd csharp/DocsServer && dotnet build
{ printf '%s\n' \
  '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}' \
  '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"search_documents","arguments":{"query":"refund"},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}'; \
  sleep 3; } | dotnet bin/Debug/net10.0/DocsServer.dll 2>/dev/null
```

Expect the three-tool list and a search result whose first match is `legacy-refund-faq` — the dud bait.
