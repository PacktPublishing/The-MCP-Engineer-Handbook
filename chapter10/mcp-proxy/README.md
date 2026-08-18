# mcp-proxy — a minimal MCP-aware reverse proxy

Companion sample for **Section 10.4.3, "Building Your Own"** and **Section
10.5.1, "Rate Limiting and Problematic Usage"**.

Section 10.4.3 claims a minimal MCP-aware reverse proxy is "a genuinely short
program in any web stack." This is that program: a plain ASP.NET Core app —
**no MCP SDK dependency, 107 lines including comments**
(`csharp/Program.cs`) — that does three MCP-semantic things at the seam:

1. **Routes on headers, not bodies.** The 2026-07-28 revision mirrors every
   request's method into `Mcp-Method` and its target name into `Mcp-Name`.
   The proxy reads those two headers, consults a tool-name-prefix routing
   table (`admin_*` → a dedicated pool, everything else → the default pool),
   and streams the JSON-RPC body through untouched. It never parses a request
   it forwards.
2. **Enforces a per-principal budget** (Section 10.5.1's budget-style limit):
   five `tools/call` requests per principal per minute; discovery is free.
   The principal comes from an `X-Principal` demo header, falling back to the
   `Authorization` header value. Exhaustion returns HTTP `429` +
   `Retry-After` for SDKs, and a JSON-RPC error body written as context for
   the model — when to retry, and not to repeat completed calls — because the
   rate-limit message is read by the caller that decides what happens next.
3. **Logs one line per request**: method, name, principal, upstream chosen,
   budget remaining. Per-method and per-tool traffic breakdowns with zero
   body inspection.

The one exception to body-blindness is the rejection path: a JSON-RPC error
must echo the request `id`, so the proxy parses the envelope there — and only
there. That is Section 10.4.3's point about body awareness: when you finally
need it, it's one well-specified envelope.

Verified against `ModelContextProtocol.AspNetCore` 2.0.0-preview.1: the
header contract is enforced server-side. On modern-era requests
(`MCP-Protocol-Version: 2026-07-28`) a missing `Mcp-Method`, or a header that
disagrees with the body, gets HTTP 400 with JSON-RPC error `-32020` from the
upstream itself. Legacy-posture requests (no version header) are exempt. The
proxy can trust the headers because the server validates the mirror.

## Upstream

The backend is `chapter05/csharp-filters` — an existing HTTP MCP server from
this repo — run twice on different ports so the routing decision is visible.
The port comes from `ASPNETCORE_URLS`.

## Run

One command:

```bash
./demo.sh
```

Or by hand, in three terminals:

```bash
# terminal 1 — default pool
ASPNETCORE_URLS=http://localhost:5101 dotnet run --project ../../chapter05/csharp-filters

# terminal 2 — dedicated pool for admin_* tools
ASPNETCORE_URLS=http://localhost:5102 dotnet run --project ../../chapter05/csharp-filters

# terminal 3 — the proxy
ASPNETCORE_URLS=http://localhost:5100 dotnet run --project csharp
```

## Smoke test

Requests are stateless 2026-07-28 era — no `initialize` handshake. All
traffic targets the proxy on port 5100.

```bash
COMMON=(-s -X POST -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H 'MCP-Protocol-Version: 2026-07-28')

# discovery — free, default pool
curl "${COMMON[@]}" http://localhost:5100/ \
  -H 'Mcp-Method: tools/list' -H 'X-Principal: alice' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}'

# a tool call — spends alice's budget, default pool
curl "${COMMON[@]}" http://localhost:5100/ \
  -H 'Mcp-Method: tools/call' -H 'Mcp-Name: get_document' -H 'X-Principal: alice' \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_document","arguments":{"id":"roadmap"},"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}'

# an admin_* tool call — routed to the dedicated pool on 5102
curl "${COMMON[@]}" http://localhost:5100/ \
  -H 'Mcp-Method: tools/call' -H 'Mcp-Name: admin_delete_document' -H 'X-Principal: alice' \
  -d '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"admin_delete_document","arguments":{"id":"roadmap"},"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}'
```

Repeat the `get_document` call until alice's five-per-minute budget is spent.
Call six returns:

```
HTTP/1.1 429 Too Many Requests
Retry-After: 52

{"jsonrpc":"2.0","id":2,"error":{"code":-32000,"message":"Rate limit reached:
this principal's budget of 5 tool calls per minute is spent. Retry after 52
seconds. Results you already received are still valid — do not repeat
completed calls, and combine the remaining work into fewer calls once the
window resets.","data":{"retryAfterSeconds":52}}}
```

The same call with `X-Principal: bob` still returns 200 — budgets are
per-principal. The proxy log shows every decision:

```
tools/list - principal=alice upstream=http://localhost:5101 budget=5
tools/call get_document principal=alice upstream=http://localhost:5101 budget=4
tools/call admin_delete_document principal=alice upstream=http://localhost:5102 budget=3
tools/call get_document principal=alice upstream=(rejected) budget=0
tools/call get_document principal=bob upstream=http://localhost:5101 budget=4
```

## Ports

C# only for now; Python and TypeScript ports may follow (the pattern is the
same handful of lines in any web stack, which is the section's point).
