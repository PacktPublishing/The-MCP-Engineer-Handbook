# hello-client (walking skeleton)

Minimal MCP client that spawns the TypeScript hello-server over stdio, negotiates the modern era (`versionNegotiation: { mode: 'auto' }` — the SDK default posture is legacy), lists tools, and calls `say_hello`. Toolchain check and porting template for the client-side samples.

## Run

Build the TS hello-server first (`../hello-server/typescript`), then:

```
cd typescript && npm ci && npm run build && npm start
```

Expected output: the tool list and the greeting result.
