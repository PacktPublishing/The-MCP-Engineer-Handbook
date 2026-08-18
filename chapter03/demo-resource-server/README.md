# demo-resource-server — the shared server for the chapter 3 client samples

Companion server for **Chapter 3 (Resources and Application-Controlled Context)**. The three chapter 3 client samples (`resource-client-host`, `model-resource-client`, `resource-link-client`) spawn this server as a stdio child process; it has no interesting client-side logic of its own.

What it exposes:

- **Text/markdown resources** (§3.1): a user guide, release notes, and a tip of the day for a fictional notes app.
- **A tool returning a resource link** (§3.3.2): `get_tip_of_the_day` returns a `ResourceLinkBlock` pointing at `file:///helpful_tip.txt` — the book extract, verbatim.
- **Deliberately awkward resources** for the link-resolution hardening demo (§3.3.4): an oversized CSV (declared `size` 64 000), a binary `audio/wav` blob, and a three-hop link chain (`chain://a` → `b` → `c` → back to `a`) whose hops tunnel onward links as JSON content. `get_research_bundle` returns links to all of them plus one dangling link.
- **Resource templates** (§3.4.1): the book's `file://user/{id}/preferences.md` extract, plus an RFC 6570 level-4 template (`docs://search{?tags*,limit}`) kept as a record of what the SDK does with it.

## URI-template support in `ModelContextProtocol` 2.0.0-preview.1

The chapter says the major SDKs can be expected to support all four levels of RFC 6570. Measured against this package:

| Level | Feature | preview.1 behavior |
|---|---|---|
| 1 | `{var}` | works, typed parameter binding included |
| 2 | reserved `{+var}`, fragment `{#var}` | matches |
| 3 | multi-variable, `{.var}`, `{/var}`, `{?a,b}`, `{&a,b}` | matches |
| 4 | explode `{?tags*}` | template accepted and advertised, but only **single-valued** expansions match (`?tags=a&tags=b` → `-32602`), and the value binds as a scalar — a `string[]` parameter throws at bind time |
| 4 | prefix `{name:3}` | matches, but the length constraint is not enforced (`abcdef` matches `{name:3}`) |

In short: levels 1–3 work; level-4 syntax parses but its matching semantics are incomplete.

## URI-template support in `mcp` 2.0.0b1 (Python)

The Python SDK draws the line one step earlier — level-4 modifiers it can't match are rejected at **registration** instead of being silently mismatched:

| Level | Feature | 2.0.0b1 behavior |
|---|---|---|
| 1 | `{var}` | works, typed parameter binding included (`id: int` binds) |
| 2 | reserved `{+var}` | matches (multi-segment values bind as one string) |
| 3 | multi-variable, `{?a,b}` | matches, optional params bind singly |
| 4 | query explode `{?tags*}` | **rejected at registration** (`InvalidUriTemplate`) |
| 4 | path explode `{/segs*}` | matches; binds a list — the parameter must be typed `list[str]` |
| 4 | prefix `{name:3}` | **rejected at registration** (`InvalidUriTemplate`) |

Because the explode form won't register, the Python port declares the search template as level-3 `docs://search{?tags,limit}` — single-valued `?tags=...&limit=...` reads route and bind fine.

## URI-template support in `@modelcontextprotocol/server` 2.0.0-beta.1 (TypeScript)

| Level | Feature | 2.0.0-beta.1 behavior |
|---|---|---|
| 1 | `{var}` | works; variables arrive as strings in the callback's `variables` record |
| 4 | query explode `{?tags*}` | template accepted and advertised, but only **single-valued, all-variables-present** expansions match: `?tags=a&limit=5` routes and binds `tags` as a scalar; `?tags=a&tags=b`, `?tags=a` (limit omitted), `?tags=a,b`, and the bare base URI all miss with `-32602 Resource not found` |

Stricter than C# on the same template: C# matches partial query expansions, beta.1 requires every variable in the expression to be present.

## Run

**C#** (canonical):

```bash
cd csharp && dotnet build
```

The C# client samples locate the built DLL automatically (override with `DEMO_RESOURCE_SERVER_DLL`). To smoke it standalone over stdio (handshake-less 2026-07-28 requests):

```bash
M='"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}'
{ echo '{"jsonrpc":"2.0","id":1,"method":"resources/list","params":{'$M'}}';
  echo '{"jsonrpc":"2.0","id":2,"method":"resources/read","params":{"uri":"file:///helpful_tip.txt",'$M'}}';
  echo '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_tip_of_the_day","arguments":{},'$M'}}';
  sleep 3; } | dotnet run --project csharp 2>/dev/null
```

**Python:** `cd python && uv sync`. Same catalog — identical URIs, names, MIME types, the declared `size: 64000` on the bundle's `big_dataset` link, and the same 3-hop chain. The Python client samples locate `python/server.py` automatically (override with `DEMO_RESOURCE_SERVER_PY`). The high-level `MCPServer` stdio path serves **both eras** in 2.0.0b1: the same handshake-less smoke works with `| uv run --project python python/server.py 2>/dev/null` (it answers `server/discover`; an `initialize` opening instead locks the connection to the legacy 2025-11-25 era). One deviation: tools returning `ResourceLink` content need `structured_output=False` on the decorator, or the SDK publishes the ResourceLink model itself as the tool's output schema.

**TypeScript:** `cd typescript && npm ci && npm run build`. Same catalog — identical URIs, names, MIME types, the declared `size: 64000` on the bundle's `big_dataset` link, and the same 3-hop chain. The TS client samples locate `typescript/dist/server.js` automatically (override with `DEMO_RESOURCE_SERVER_JS`). Served with `serveStdio`, so the same handshake-less smoke works with `| node typescript/dist/server.js 2>/dev/null`. `resource_link` blocks in tool results and `ResourceTemplate` registration both work in beta.1 unmodified; the one structural deviation is that the template read callback receives `(uri, variables)` with string variables — the `{id}` is parsed with `Number(...)` where C# binds a typed `int id` parameter.
