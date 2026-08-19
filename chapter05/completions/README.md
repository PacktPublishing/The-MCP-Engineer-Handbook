# Completions (§5.3.3)

Demonstrates the `completions` utility capability: a server that offers a
`file:///{path}` resource template and answers `completion/complete` for its
`path` argument — prefix-matching against a known catalog, capped at the spec
limit of 100 values — plus a client that requests completions for the partial
value `docs/re`. Python is canonical (both chapter extracts are Python); the
TypeScript and C# ports show where each SDK hangs the same capability.

Expected output in every language:

```
values: [docs/readme.md, docs/reference.md, docs/release-notes.md]
total: 3 hasMore: false
```

## Where completions live in each SDK

- **Python**: a server-level `@server.completion()` handler receiving
  `(ref, argument, context)` and returning a `Completion` (or `None` for an
  empty completion). Client: `session.complete(ref=..., argument={...})`.
- **TypeScript**: per-template-variable `complete` callbacks on
  `ResourceTemplate` (the SDK declares the capability and computes
  `total`/`hasMore` from the returned array — so if you cap the array
  yourself, the SDK cannot report the uncapped total; return at most 100 and
  accept that, or leave capping to the SDK).
- **C#**: a server-level `WithCompleteHandler` building a `CompleteResult`.
  Client: `client.CompleteAsync(ref, argumentName, argumentValue)`.

## Run

**Python** (canonical — client connects in-process, no separate server step):

```
cd python && uv sync && uv run python client.py
```

**TypeScript** (client spawns `dist/server.js` over stdio):

```
cd typescript && npm ci && npm run build && npm run client
```

**C#** (client spawns the server project over stdio; run from the sample root):

```
dotnet build csharp && dotnet build csharp-client
dotnet run --project csharp-client
```

## Smoke (stdio, handshake-less — all three servers)

```
{"jsonrpc":"2.0","id":1,"method":"completion/complete","params":{"ref":{"type":"ref/resource","uri":"file:///{path}"},"argument":{"name":"path","value":"docs/re"},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke-test","version":"0.0.1"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}
```

Pipe into `node typescript/dist/server.js`, `dotnet run --project csharp`, or
`uv run --project python python python/server.py` (with `2>/dev/null`); all
three return the same three values with `total: 3, hasMore: false`.

## Deviations from the printed extracts

None. Both §5.3.3 extracts run verbatim against `mcp` 2.0.0: the server-side
`@server.completion()` handler is reproduced unchanged in `python/server.py`,
and the client call — including the `session` variable name and the
`ref=`/`argument=` keywords — appears unchanged in `python/client.py`
(`session` is an `mcp.client.Client`; v2 renamed the class from
`ClientSession`, but the call reads exactly as printed).
