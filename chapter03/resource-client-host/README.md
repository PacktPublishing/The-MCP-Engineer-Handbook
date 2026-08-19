# resource-client-host — user-controlled context injection

Companion sample for **Chapter 3, §3.3.1 (Pattern 1: User-Controlled Context Injection)**.

There is no `resources` property in LLM APIs, so a client host must decide where a user-selected resource lands in the model context. This console host reads the same resource from the demo server and injects it three ways, printing the resulting message structure for each — where the contents, the provenance signal, and the guardrail end up is the point of the sample.

| Approach | Contents live in | Provenance signal | Guardrail |
|---|---|---|---|
| `user` | user message | `<mcp_resource>` tags in the user message | `<guidance>` block at user level |
| `system` | system message | tags in the system message | system trust itself — needs extra care and user approval |
| `hybrid` | user message | system-level attestation, digest-bound to the user-message block | attestation instructions at system level |

The host follows the §3.3.1 flow: list the catalog, let the user pick (scripted here), **preview before injecting**, then assemble the context. `CreateAttestation` — elided in the book extract — binds the system-level attestation to the user-message contents with a SHA-256 digest, so the model can tell which block is attested.

The model side is a deterministic `ScriptedChatClient` (no API key); its replies show the model citing the provenance each approach gives it. Any real provider's `IChatClient` adapter swaps in without touching the host code.

## Run

```bash
cd ../demo-resource-server/csharp && dotnet build   # once
cd ../../resource-client-host/csharp && dotnet build

dotnet run                # all three approaches + comparison table
dotnet run -- user        # or: system | hybrid
```

The host spawns the demo server as a stdio child process (`DEMO_RESOURCE_SERVER_DLL` overrides the path). Smoke: `dotnet run` exits 0 and prints three context dumps whose `<-- resource contents` markers sit in a different message per approach.

**Python:**

```bash
cd ../demo-resource-server/python && uv sync   # once
cd ../../resource-client-host/python && uv sync

uv run host.py            # all three approaches + comparison table
uv run host.py user       # or: system | hybrid
```

The Python host spawns `demo-resource-server/python/server.py` as a stdio child (`DEMO_RESOURCE_SERVER_PY` overrides the path); the pair negotiates the modern **2026-07-28** era (the Python client's default `mode="auto"` probes `server/discover`, which the high-level Python server answers). The chat side is a `ChatModel` protocol with the same deterministic scripted model; the SHA-256 attestation binding and the printed context dumps match the C# output. Smoke is the same: three dumps, marker in a different message per approach.

**TypeScript:**

```bash
cd ../demo-resource-server/typescript && npm ci && npm run build   # once
cd ../../resource-client-host/typescript && npm ci && npm run build

npm start                  # all three approaches + comparison table
node dist/host.js user     # or: system | hybrid
```

The TS host spawns `demo-resource-server/typescript/dist/server.js` as a stdio child (`DEMO_RESOURCE_SERVER_JS` overrides the path) and prints the negotiated protocol version — **2026-07-28**, established by `versionNegotiation: { mode: 'auto' }` (the TS client defaults to the legacy posture, unchanged at 2.0.0, so the option stays). The chat side is a `ChatClient` interface over part-lists (the role Microsoft.Extensions.AI plays in C#) with the same deterministic scripted model; the SHA-256 attestation binding and the printed context dumps match the C# output. Smoke is the same: three dumps, marker in a different message per approach.

## Deviations from the printed extracts

Written against `ModelContextProtocol` 2.0.0:

- `systemMessage.Contents.AddRange(resourceReadResult.Contents)` (and the same line on `userMessage` in the hybrid) does not compile: `ChatMessage.Contents` is an `IList<AIContent>` (no `AddRange`), and read contents are `ResourceContents`, not `AIContent`. The sample converts with the SDK's `ToAIContents()` and adds in a loop.

TypeScript (`@modelcontextprotocol/client` 2.0.0): the C# `AddRange` problem has no TS counterpart (the chat abstraction is a plain part list), but the conversion step remains — read contents are not message parts, so `host.ts` maps them in `toParts`, discriminating text from blob structurally (`'text' in c`).
