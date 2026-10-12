# demo-resource-server — the shared server for the chapter 3 client samples

Companion server for **Chapter 3 (Resources and Application-Controlled Context)**. The three chapter 3 client samples (`resource-client-host`, `model-resource-client`, `resource-link-client`) spawn this server as a stdio child process; it has no interesting client-side logic of its own.

What it exposes:

- **Text/markdown resources** (§3.1): a user guide, release notes, and a tip of the day for a fictional notes app.
- **A tool returning a resource link** (§3.3.2): `get_tip_of_the_day` returns a `ResourceLinkBlock` pointing at `file:///helpful_tip.txt` — the book extract, verbatim.
- **Deliberately awkward resources** for the link-resolution hardening demo (§3.3.4): an oversized CSV (declared `size` 64 000), a binary `audio/wav` blob, and a three-hop link chain (`chain://a` → `b` → `c` → back to `a`) whose hops tunnel onward links as JSON content. `get_research_bundle` returns links to all of them plus one dangling link.
- **Two more for the resolver's per-pass limits** (§3.3.4), registered as templates so they stay out of `resources/list` (which the other client samples show to the model):
  - `file://notes/{id}/attachments` returns **three content items** of 6 000 bytes each from one read. Each fits a 16 KiB per-resource budget; the total doesn't. Python's `MCPServer` maps a resource function to a single content item, so the Python port serves this URI by overriding `read_resource`, and therefore doesn't advertise it in `resources/templates/list`.
  - `file://exports/{quarter}/telemetry.csv` takes **3 seconds** to read, for client deadlines.
- **Resource templates** (§3.4.1): the book's `file://user/{id}/preferences.md` extract, plus `doc_search`, an RFC 6570 level-4 template (`docs://search{?tags*,limit}`; level-3 `{?tags,limit}` in Python) kept as a record of what each SDK does with it.

## URI-template compatibility (pinned 2.0.0 SDKs)

Two different things hide behind "supports RFC 6570":

- **Expansion** is what the RFC defines: which URI a template produces for given variable values. That is the client's side of the contract.
- **Matching and binding** is what a server SDK does with an incoming URI: decide whether it matches a registered template, then turn the URI back into variable values for your handler. RFC 6570 doesn't define this reverse direction, and the SDKs differ.

Don't assume an SDK matches every expansion of every level. The tables below are observed, not inferred. Each row was produced by sending `resources/read` over stdio to servers pinned at **`ModelContextProtocol` 2.0.0** (C#), **`mcp` 2.0.0** (Python), and **`@modelcontextprotocol/server` 2.0.0** (TypeScript).

### The sample's own templates

`file://user/{id}/preferences.md` is the book extract. `doc_search` echoes its bindings as JSON (`bound tags=..., limit=...`), so you can reproduce every row with the smoke command under **Run**. C# and TypeScript register `docs://search{?tags*,limit}` (level 4, explode). Python registers the level-3 `docs://search{?tags,limit}`, because its SDK rejects the explode form at registration (see the next table).

| Input URI | RFC 6570: what expands to it | C# | Python | TypeScript |
|---|---|---|---|---|
| `file://user/42/preferences.md` | `id = "42"` | `id = 42` (typed `int`) | `id = 42` (typed `int`) | `id = "42"` (string; the sample parses it) |
| `file://user/abc/preferences.md` | `id = "abc"` | `-32603` (the `int` binding fails) | `-32603` (the `int` binding fails) | binds `"abc"`; the sample rejects it → `-32603` |
| `docs://search?tags=a&limit=5` | `tags = "a"`, `limit = 5` | `tags="a"`, `limit=5` | `tags="a"`, `limit=5` | `tags="a"`, `limit="5"` |
| `docs://search?tags=a,b&limit=5` | `{?tags,limit}` with `tags = ["a","b"]`. Not an expansion of `{?tags*,limit}`, where a list repeats the key and a scalar comma is encoded as `%2C`. | `tags="a,b"` (scalar) | `tags="a,b"` (scalar) | `tags=["a","b"]` (array) |
| `docs://search?tags=a&tags=b&limit=5` | `{?tags*,limit}` with `tags = ["a","b"]`, the canonical explode form | `-32602` Unknown resource URI | matches, `tags="a"`: **the second value is silently dropped** | `-32602` Resource not found |
| `docs://search?tags=a` | `limit` undefined (omitted) | `tags="a"`, `limit=null` | `tags="a"`, `limit=null` | `-32602` Resource not found |
| `docs://search?limit=5` | `tags` undefined (omitted) | `tags=null`, `limit=5` | `tags=null`, `limit=5` | `-32602` Resource not found |
| `docs://search` | every variable undefined | `tags=null`, `limit=null` | `tags=null`, `limit=null` | `-32602` Resource not found |

What this means in practice:

- **TypeScript** matches a query expansion only when *every* variable is present. It is the only SDK that binds a list (`?tags=a,b` gives `["a","b"]`), but not from the explode form the template declares. All variables arrive as strings.
- **C#** treats `{?tags*,limit}` as a plain query expansion. Omitted variables bind only because both parameters declare a **default value**. With `string? tags, int? limit` (nullable, no `= null`), the three omitted-variable rows fail with `-32603`; the server log says `Missing a value for the required parameter 'limit'`. Typing `tags` as `string[]` fails the same way for any URI that supplies `tags` (`Object of type 'System.String' cannot be converted to type 'System.String[]'`). The wire error is a generic `An error occurred.`, so check the server log.
- **Python** binds singly and is lenient on repeated keys: it keeps the first value rather than rejecting the URI.

### Other operators and registration (scratch templates, not in the sample)

To cover the operators the sample doesn't use, these were registered on scratch copies of the three servers. The handlers echo their bindings the same way.

| Template | Input URI | C# | Python | TypeScript |
|---|---|---|---|---|
| `docs://search{?tags*,limit}` | (registration) | accepted | **rejected**: `InvalidUriTemplate: Explode modifier on {?tags*} is not supported for matching` | accepted |
| `ops://reserved/{+path}` | `ops://reserved/a/b/c` | `path="a/b/c"` | `path="a/b/c"` | `path="a/b/c"` |
| `ops://frag{#section}` | `ops://frag#intro` | `section="intro"` | `section="intro"` | `section="#intro"` (operator kept in the value) |
| `ops://prefix/{name:3}` | (registration) | accepted | **rejected**: `InvalidUriTemplate: Prefix modifier {var:N} is not supported` | accepted |
| `ops://prefix/{name:3}` | `ops://prefix/abc` | `name="abc"` | n/a | bound under the key `"name:3"`, not `name` |
| `ops://prefix/{name:3}` | `ops://prefix/abcdef` | `name="abcdef"` (length not enforced) | n/a | `"name:3"="abcdef"` (length not enforced) |
| `ops://path{/segs*}` | `ops://path/a` | `segs="a"` | `segs=["a"]` (parameter typed `list[str]`) | `segs="a"` |
| `ops://path{/segs*}` | `ops://path/a/b` | `-32602` Unknown resource URI | `segs=["a","b"]` | `-32602` Resource not found |

Rules of thumb at these versions:
- Level-1 `{var}` and simple query expansions with every variable present behave the same in all three SDKs, apart from typing.
- Beyond that, test the exact URIs your clients will send against the SDK you ship.
- Prefer templates that are unambiguous to match: avoid explode and prefix modifiers. Give optional query variables defaults (C#), or keep them required (TypeScript).

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
  echo '{"jsonrpc":"2.0","id":4,"method":"resources/read","params":{"uri":"docs://search?tags=a,b&limit=5",'$M'}}';
  sleep 8; } | dotnet run --project csharp 2>/dev/null   # dotnet run needs a few seconds to start
```

**Python:** `cd python && uv sync`. Same catalog — identical URIs, names, MIME types, the declared `size: 64000` on the bundle's `big_dataset` link, and the same 3-hop chain. The Python client samples locate `python/server.py` automatically (override with `DEMO_RESOURCE_SERVER_PY`). The high-level `MCPServer` stdio path serves **both eras** at 2.0.0: the same handshake-less smoke works with `| uv run --project python python/server.py 2>/dev/null` (it answers `server/discover`; an `initialize` opening instead locks the connection to the legacy 2025-11-25 era). One deviation: tools returning `ResourceLink` content need `structured_output=False` on the decorator, or the SDK publishes the ResourceLink model itself as the tool's output schema.

**TypeScript:** `cd typescript && npm ci && npm run build`. Same catalog — identical URIs, names, MIME types, the declared `size: 64000` on the bundle's `big_dataset` link, and the same 3-hop chain. The TS client samples locate `typescript/dist/server.js` automatically (override with `DEMO_RESOURCE_SERVER_JS`). Served with `serveStdio`, so the same handshake-less smoke works with `| node typescript/dist/server.js 2>/dev/null`. `resource_link` blocks in tool results and `ResourceTemplate` registration both work at 2.0.0 unmodified; the one structural deviation is that the template read callback receives `(uri, variables)` with string variables — the `{id}` is parsed with `Number(...)` and rejected unless it is an integer, where C# and Python bind a typed `int id` parameter.
