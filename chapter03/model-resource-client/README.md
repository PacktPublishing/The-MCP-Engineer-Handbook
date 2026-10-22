# model-resource-client — resource access via tool wrappers

Companion sample for **Chapter 3, §3.3.3 (Pattern 3: Model-Controlled Resource Access via Tool Wrappers)**.

Two host-side tools — `list_resources` and `read_resource` — give resources the model-native integration point the protocol doesn't define. The list tool aggregates across **all** connected servers, tagging each entry with a host-assigned server name; the read tool routes a read to the right server by that name. To prove the aggregation, the host spawns the demo server **twice** under different labels (`docs` and `wiki`): same catalog, distinct routing keys, and the model's read lands on the server it named.

The printed conversation history is the deliverable: catalog request → aggregated tool result (every entry carrying its `serverName`) → routed read → a **cross-server read the host denies** → answer. The model is a deterministic `ScriptedChatClient` (no API key); any real provider's `IChatClient` adapter drives the same two tools.

Security notes from §3.3.3, reflected in the code:

- Servers are identified by **host-assigned labels** (`ClientManager`), never by their self-reported names.
- **Routing is not authorization.** A valid `serverName` only tells the host where to send a read. A model-callable `read_resource(serverName, uri)` is a cross-server confused-deputy vector: content from server A can instruct the model to read from server B. So `ReadResource` consults an `OriginPolicy` before every read, inside the tool itself. The check applies to whatever call the model generates, including one for a resource that was never listed. It is not just a rule in the tool description.

### The origin policy

`OriginPolicy` (in `ResourceTools.cs`) implements the chapter's rule in its smallest form:

- One instance per conversation. It records which servers' content the conversation already holds.
- The first read binds the conversation to its server, and reads from a bound server proceed.
- A read from any other server is **cross-origin**. It goes to an approval callback that names both sides, and the read proceeds only if the user approves; an approved server joins the bound set.
- A denial returns a model-readable result (`Read denied by host policy: ...`) instead of throwing, so the model can tell the user why.

In the demo, the scripted model reads the wiki's release notes. Then, acting as the confused deputy, it reads `file:///user_guide.md` from `docs`, a valid resource that `list_resources` returned. The host prints the approval prompt, the scripted user declines, and the tool result is the denial.

What the hook does **not** cover, which a production host must:

- **Other ways content enters.** Only resource *reads* bind the conversation. Catalog entries from `list_resources` (names and descriptions are server-authored text), tool results, and prompts are origins too.
- **A real approval UI.** The callback is where it goes. Show the user both server labels and the URI, and don't let the model word the question.
- **Per-user or per-server allowlists, data classification, and audit logging.** Approval is one control, not the whole policy.

## Run

```bash
cd ../demo-resource-server/csharp && dotnet build   # once
cd ../../model-resource-client/csharp && dotnet build

dotnet run
```

The host spawns both server instances as stdio child processes (`DEMO_RESOURCE_SERVER_DLL` overrides the path). Smoke: `dotnet run` exits 0. An `[approval] ... -> declined (scripted user)` line comes first. The printed history then shows `list_resources` returning entries for both `docs` and `wiki`, `read_resource({"serverName":"wiki", ...})` returning the release notes, and `read_resource({"serverName":"docs", ...})` returning `Read denied by host policy: ...`.

**Python:**

```bash
cd ../demo-resource-server/python && uv sync   # once
cd ../../model-resource-client/python && uv sync

uv run host.py
```

The Python host spawns two instances of `demo-resource-server/python/server.py` as stdio children (`DEMO_RESOURCE_SERVER_PY` overrides the path); each pair negotiates the modern **2026-07-28** era. `ClientManager` holds one `Client` per host-assigned label on an `AsyncExitStack`. The wrapper tools, the `OriginPolicy`, the scripted model's routing, and the printed history match the C# output (same smoke criteria).

**TypeScript:**

```bash
cd ../demo-resource-server/typescript && npm ci && npm run build   # once
cd ../../model-resource-client/typescript && npm ci && npm run build

npm start
```

The TS host spawns two instances of `demo-resource-server/typescript/dist/server.js` as stdio children (`DEMO_RESOURCE_SERVER_JS` overrides the path); each pair negotiates the modern **2026-07-28** era via `versionNegotiation: { mode: 'auto' }` (still required at 2.3.1 — the TS client defaults to the legacy posture) and the host prints the negotiated version. `ClientManager` holds one `Client` per host-assigned label. The wrapper tools, the `OriginPolicy`, the scripted model's routing, and the printed history match the C# output (same smoke criteria).

## Deviations from the printed extracts

The `ListResources`/`ReadResource` methods in `ResourceTools.cs` compile against `ModelContextProtocol` 2.3.0. `ReadResource` includes the `OriginPolicy` check described above, and `ResourceTools` takes the policy as a second constructor argument. The sample supplies the pieces the extract elides (`ClientManager`, `OriginPolicy`, `ResourceMetadata`, `FormatResourceMetadata`, `FormatResourceContent`).
