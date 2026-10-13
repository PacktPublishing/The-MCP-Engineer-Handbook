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

## Versions

Pinned like the rest of the repo: `@modelcontextprotocol/server`, `client` and `core` **2.2.0**
(exact) and `@modelcontextprotocol/ext-apps` **2.0.3** (exact), with `zod ^4.2.0`.
ext-apps 2.x is the first release line built on the v2 SDK packages (released 2026-09-08);
until then this sample had to stay on the v1 SDK, which is the extension-ecosystem skew
that sections 7.4.2 and 7.5.2 discuss. `core` is listed explicitly so ext-apps and the SDK
resolve to a single copy.

The server is served with `serveStdio(createServer)`, so it answers both eras: a
handshake-less 2026-07-28 client gets `server/discover`, and a legacy client that opens
with `initialize` still works.

## Layout

- `src/server.ts` — the MCP server: two app tools + the `ui://` resource. At startup it
  inlines the built View bundle into `src/view/dashboard.html` so the resource is a single
  self-contained HTML document.
- `src/view/dashboard.ts` — the View script (`new App(...)`, `addEventListener("toolresult", ...)`,
  refresh button, `app.connect()`). It validates `structuredContent` before rendering,
  writes values with `textContent`, shows errors for `isError` results and rejected
  refreshes, and ignores out-of-order refresh responses, bundled with esbuild.
- `src/view/dashboard.html` — the HTML shell (markup + styles) the bundle is injected into.

## Build and run

```bash
npm install
npm run build     # tsc (server) + tsc --noEmit (view typecheck) + esbuild (view bundle)
npm start         # stdio server
```

## Smoke test (stdio, 2026-07-28)

Handshake-less: each request carries the client envelope in `_meta`. The client declares
the MCP Apps extension (`io.modelcontextprotocol/ui`) the way an Apps-capable host would.

```bash
M='"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{"extensions":{"io.modelcontextprotocol/ui":{"mimeTypes":["text/html;profile=mcp-app"]}}},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}'
{
printf '%s\n' \
'{"jsonrpc":"2.0","id":0,"method":"server/discover","params":{'"$M"'}}' \
'{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{'"$M"'}}' \
'{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_system_health","arguments":{},'"$M"'}}' \
'{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"refresh_health","arguments":{},'"$M"'}}' \
'{"jsonrpc":"2.0","id":4,"method":"resources/read","params":{"uri":"ui://health/dashboard.html",'"$M"'}}'
sleep 3
} | node dist/server.js
```

Expect:

- `server/discover`: `supportedVersions` `["2026-07-28"]`, `tools` and `resources` capabilities.
- `tools/list`: both tools, each with `_meta.ui.resourceUri` (plus the legacy
  `_meta["ui/resourceUri"]` mirror ext-apps adds for older hosts); `refresh_health`
  additionally has `_meta.ui.visibility: ["app"]`.
- `tools/call get_system_health`: `content` text summary ("N/4 services healthy.") plus
  `structuredContent.services` — values jitter per call so refresh visibly changes.
- `resources/read`: `ui://health/dashboard.html` with MIME type
  `text/html;profile=mcp-app` (`RESOURCE_MIME_TYPE`).

### App-only is a host convention, not access control

`visibility: ["app"]` tells an Apps-capable host to keep `refresh_health` away from the
model. The server still lists it to every client and executes it for anyone who calls it
(request 3 above succeeds without any UI involved), and a client that doesn't support
MCP Apps can ignore the metadata entirely. That's fine here because the refresh is
read-only and returns the same data as `get_system_health`. A tool that does something
consequential needs its own authorization check at execution, whatever its visibility.

## Verifying the View

The View half needs an MCP Apps-capable host — a plain MCP client never renders the HTML.
`npm run build` verifies it typechecks and bundles; for a live check, connect the server to
an MCP Apps-capable host, or use the example host in the ext-apps repository
(`github.com/modelcontextprotocol/ext-apps`, `examples/basic-host`), which loads a stdio
server and renders its Views in a browser. Calling `get_system_health` renders the status
cards; the Refresh button fetches new data through the app-only tool without a model turn.
