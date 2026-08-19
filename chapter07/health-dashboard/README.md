# Health Dashboard (MCP Apps)

Companion sample for **Chapter 7, section 7.4 (Building Your First MCP App)**: a system
health dashboard served as an interactive View, demonstrating the core MCP Apps patterns —

- **Tool–UI linkage**: `get_system_health` declares `_meta.ui.resourceUri` pointing at the
  `ui://health/dashboard.html` resource.
- **`structuredContent` vs `content`**: the model sees a one-line text summary; the View
  receives the full service array to render as status cards.
- **App-only tools**: `refresh_health` carries `_meta.ui.visibility: ["app"]` — the View's
  refresh button calls it directly through the host, with no model involvement.
- **View lifecycle**: handlers registered before `app.connect()`, refresh wired via
  `app.callServerTool(...)`.

This sample is **TypeScript-only by design**: the MCP Apps SDK
(`@modelcontextprotocol/ext-apps`) is a TypeScript library, so there are no
`csharp/`/`python/` variants and the project lives directly in this directory.

## Version skew — this sample uses the v1 SDK

**Unlike every other sample in this repo (which pin the v2 GA SDKs), this one is built on
the v1 monolith SDK.** As of 2026-08, `@modelcontextprotocol/ext-apps` 1.7.5 — the latest
release — peer-depends on `@modelcontextprotocol/sdk ^1.29.0`; no ext-apps release supports
the v2 SDK packages. This is exactly the extension-ecosystem skew that sections 7.4.2 and
7.5.2 discuss: extensions version independently of the core SDKs, and an extension can lag
a major SDK transition.

Pinned versions:

| Package | Version |
| --- | --- |
| `@modelcontextprotocol/ext-apps` | `1.7.5` (exact) |
| `@modelcontextprotocol/sdk` | `^1.29.0` — ext-apps' peer range (resolved to `1.30.0` when last verified, 2026-08) |

Consequently the server imports come from the v1 paths
(`@modelcontextprotocol/sdk/server/mcp.js`, `.../server/stdio.js`), and the stdio smoke test
below uses the legacy `initialize`/`notifications/initialized` handshake rather than the
handshake-less 2026-07-28 posture used elsewhere in the repo.

**Expected migration:** when ext-apps ships v2-SDK support, this sample should move to
`@modelcontextprotocol/server` and the repo-wide pins. Re-checked during the GA pass
(2026-08-19): still no v2-compatible ext-apps release, so the sample intentionally stays
on the v1 SDK.

Known wart: ext-apps 1.7.5 ships `.d.ts` files with extensionless relative imports, which
`moduleResolution: NodeNext` rejects — `skipLibCheck: true` is required in tsconfig.

## Layout

- `src/server.ts` — the MCP server: two app tools + the `ui://` resource. At startup it
  inlines the built View bundle into `src/view/dashboard.html` so the resource is a single
  self-contained HTML document.
- `src/view/dashboard.ts` — the View script (`new App(...)`, `ontoolresult`, refresh button,
  `app.connect()`), bundled with esbuild.
- `src/view/dashboard.html` — the HTML shell (markup + styles) the bundle is injected into.

## Build and run

```bash
npm install
npm run build     # tsc (server) + tsc --noEmit (view typecheck) + esbuild (view bundle)
npm start         # stdio server
```

## Smoke test (stdio, legacy handshake)

The v1 SDK requires the `initialize` handshake before any request:

```bash
{
printf '%s\n' \
'{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"0"}}}' \
'{"jsonrpc":"2.0","method":"notifications/initialized"}' \
'{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}' \
'{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_system_health","arguments":{}}}' \
'{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"refresh_health","arguments":{}}}' \
'{"jsonrpc":"2.0","id":4,"method":"resources/list","params":{}}' \
'{"jsonrpc":"2.0","id":5,"method":"resources/read","params":{"uri":"ui://health/dashboard.html"}}'
sleep 3
} | node dist/server.js
```

Expect:

- `tools/list`: both tools, each with `_meta.ui.resourceUri` (plus the legacy
  `_meta["ui/resourceUri"]` mirror ext-apps adds for older hosts); `refresh_health`
  additionally has `_meta.ui.visibility: ["app"]`.
- `tools/call get_system_health`: `content` text summary ("N/4 services healthy.") plus
  `structuredContent.services` — values jitter per call so refresh visibly changes.
- `resources/list` / `resources/read`: `ui://health/dashboard.html` with MIME type
  `text/html;profile=mcp-app` (`RESOURCE_MIME_TYPE`).

## Verifying the View

The View half needs an MCP Apps-capable host — a plain MCP client never renders the HTML.
`npm run build` verifies it typechecks and bundles; for a live check, connect the server to
an MCP Apps-capable host, or use the example host in the ext-apps repository
(`github.com/modelcontextprotocol/ext-apps`, `examples/basic-host`), which loads a stdio
server and renders its Views in a browser. Calling `get_system_health` renders the status
cards; the Refresh button fetches new data through the app-only tool without a model turn.
