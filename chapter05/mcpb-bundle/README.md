# mcpb-bundle — a validated MCP Bundle fixture

Companion sample for **Sections 5.5.1–5.5.2, "MCP Bundles"**.

The `weather-alerts` bundle from the chapter as a directory you can actually
validate and pack: a `manifest.json`, a small Node stdio server built on the
v2 split packages (`@modelcontextprotocol/server`), and light/dark icons. The
alert data is canned; the sample is about the packaging.

```
mcpb-bundle/
├── manifest.json          ← manifest_version 0.3
├── icon.png               ← default icon (512×512)
├── assets/
│   └── icon-dark.png      ← dark-theme variant, declared in "icons"
├── server/
│   ├── index.js           ← entry point
│   ├── package.json
│   └── node_modules/      ← created by npm install; packed into the bundle
│       ├── @modelcontextprotocol/
│       │   ├── core/
│       │   └── server/
│       └── zod/
└── .mcpbignore            ← keeps this README and built .mcpb files out of the bundle
```

## Manifest version

The manifest targets **MCPB manifest version 0.3**, validated with
**`@anthropic-ai/mcpb` 2.1.2**. 0.3 is the oldest revision with the `icons`
array (theme variants) and `localization`, both mentioned in the chapter; 0.4
adds only the `uv` server type. Note that `mcpb init` in CLI 2.1.2 still writes
`"manifest_version": "0.2"` by default — bump it if you want themed icons.

Fields that are easy to get wrong (and that the schema rejects):

| Wrong | Right |
| --- | --- |
| no `manifest_version` | `"manifest_version": "0.3"` (the CLI refuses to validate without it) |
| `"platforms": ["macos", "windows"]` | `"platforms": ["darwin", "win32"]` (Node's `process.platform` values; `linux` also allowed) |
| `"min_app_version": "1.0.0"` | `"compatibility": { "claude_desktop": ">=0.10.0" }`: a semver range per host app |
| `"secret": true` in `user_config` | `"sensitive": true` |
| `"icon_dark": "..."` | an `icons` array entry with `"theme": "dark"` |
| a `links` object | top-level `homepage`, `documentation`, `support` |

## Validate, pack, inspect

The CLI version is pinned so the output matches what's described here:

```bash
cd server && npm install && cd ..
npx @anthropic-ai/mcpb@2.1.2 validate manifest.json
npx @anthropic-ai/mcpb@2.1.2 pack . weather-alerts-1.0.0.mcpb
npx @anthropic-ai/mcpb@2.1.2 info weather-alerts-1.0.0.mcpb
```

`pack` names the file after the directory unless you pass an output path.
`validate` prints `Manifest schema validation passes!`, followed by an
icon note recommending 512×512. CLI 2.1.2 prints that note for every valid
PNG icon. `pack` writes `weather-alerts-1.0.0.mcpb` (about 2 MB, ~785 files,
almost all of them `node_modules`). `info` reports it as unsigned; signing is
`mcpb sign`.

## Run the packed server the way a host would

A host unpacks the bundle, substitutes `${__dirname}` and
`${user_config.api_key}`, and launches `mcp_config.command`. To approximate
that by hand:

```bash
npx @anthropic-ai/mcpb@2.1.2 unpack weather-alerts-1.0.0.mcpb /tmp/weather-alerts
{ echo '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_alerts","arguments":{"city":"Oslo"},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}'; sleep 1; } \
  | WEATHER_API_KEY=demo CACHE_TTL=300 node /tmp/weather-alerts/server/index.js
```

The response carries the Oslo wind warning. The `sleep` keeps stdin open until the answer is written: since `@modelcontextprotocol/server` 2.1.0 a stdio server shuts down when stdin closes and drops replies to requests still in flight, as a host's own disconnect would. Without `WEATHER_API_KEY` the
tool returns an `isError` result asking the user to configure the key, which
is what a user who skipped the install-time prompt would see.
