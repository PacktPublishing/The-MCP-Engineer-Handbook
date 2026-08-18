# model-resource-client — resource access via tool wrappers

Companion sample for **Chapter 3, §3.3.3 (Pattern 3: Model-Controlled Resource Access via Tool Wrappers)**.

Two host-side tools — `list_resources` and `read_resource` — give resources the model-native integration point the protocol doesn't define. The list tool aggregates across **all** connected servers, tagging each entry with a host-assigned server name; the read tool routes a read to the right server by that name. To prove the aggregation, the host spawns the demo server **twice** under different labels (`docs` and `wiki`): same catalog, distinct routing keys, and the model's read lands on the server it named.

The printed conversation history is the deliverable: catalog request → aggregated tool result (every entry carrying its `serverName`) → routed read → answer. The model is a deterministic `ScriptedChatClient` (no API key); any real provider's `IChatClient` adapter drives the same two tools.

Security notes from §3.3.3, reflected in the code:

- Servers are identified by **host-assigned labels** (`ClientManager`), never by their self-reported names.
- A model-callable `read_resource(serverName, uri)` is a cross-server confused-deputy vector: content from server A can instruct the model to read from server B. The chapter's mitigations (bind reads to the originating server, gate cross-origin reads behind user approval) are host policy above these tools — this sample shows the mechanism, not the full policy layer.

## Run

```bash
cd ../demo-resource-server/csharp && dotnet build   # once
cd ../../model-resource-client/csharp && dotnet build

dotnet run
```

The host spawns both server instances as stdio child processes (`DEMO_RESOURCE_SERVER_DLL` overrides the path). Smoke: `dotnet run` exits 0 and the printed history shows `list_resources` returning entries for both `docs` and `wiki`, then `read_resource({"serverName":"wiki", ...})`.

**Python:**

```bash
cd ../demo-resource-server/python && uv sync   # once
cd ../../model-resource-client/python && uv sync

uv run host.py
```

The Python host spawns two instances of `demo-resource-server/python/server.py` as stdio children (`DEMO_RESOURCE_SERVER_PY` overrides the path); each pair negotiates the modern **2026-07-28** era. `ClientManager` holds one b1 `Client` per host-assigned label on an `AsyncExitStack`; the wrapper tools, the scripted model's routing, and the printed history match the C# output (same smoke criteria).

**TypeScript:**

```bash
cd ../demo-resource-server/typescript && npm ci && npm run build   # once
cd ../../model-resource-client/typescript && npm ci && npm run build

npm start
```

The TS host spawns two instances of `demo-resource-server/typescript/dist/server.js` as stdio children (`DEMO_RESOURCE_SERVER_JS` overrides the path); each pair negotiates the modern **2026-07-28** era via `versionNegotiation: { mode: 'auto' }` and the host prints the negotiated version. `ClientManager` holds one beta.1 `Client` per host-assigned label; the wrapper tools, the scripted model's routing, and the printed history match the C# output (same smoke criteria).

## Deviations from the printed extracts

None. The `ListResources`/`ReadResource` methods in `ResourceTools.cs` compile verbatim against `ModelContextProtocol` 2.0.0-preview.1; the sample supplies the pieces the extract elides (`ClientManager`, `ResourceMetadata`, `FormatResourceMetadata`, `FormatResourceContent`).
