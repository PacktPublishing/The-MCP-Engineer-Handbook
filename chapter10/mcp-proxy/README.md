# mcp-proxy — a minimal MCP-aware reverse proxy

Companion sample for **Section 10.4.3, "Building Your Own"** and **Section
10.5.1, "Rate Limiting and Problematic Usage"**.

Section 10.4.3 claims a minimal MCP-aware reverse proxy is "a genuinely short
program in any web stack." This is that program: a plain ASP.NET Core app —
**no MCP SDK dependency, 141 lines including comments**
(`csharp/Program.cs`) — that does four MCP-semantic things at the seam:

1. **Admits the 2026-07-28 revision only.** A request whose
   `MCP-Protocol-Version` header is missing or names any other version gets
   HTTP `400` and the spec's `UnsupportedProtocolVersionError` (JSON-RPC
   `-32022`, with `data.supported: ["2026-07-28"]`) before any routing or
   budget decision. Legacy clients belong behind a compatibility shim
   (Section 10.3.1), not through this door.
2. **Routes on headers, not bodies.** The 2026-07-28 revision mirrors every
   request's method into `Mcp-Method` and its target name into `Mcp-Name`.
   The proxy reads those two headers, consults a tool-name-prefix routing
   table (`admin_*` → a dedicated pool, everything else → the default pool),
   and streams the JSON-RPC body through untouched. It never parses a request
   it forwards.
3. **Enforces a per-principal budget** (Section 10.5.1's budget-style limit):
   five `tools/call` requests per principal per minute; discovery is free.
   Exhaustion returns HTTP `429` + `Retry-After` for SDKs, and a JSON-RPC
   error body written as context for the model — when to retry, and not to
   repeat completed calls — because the rate-limit message is read by the
   caller that decides what happens next.
4. **Logs one line per request**: method, name, principal, upstream chosen,
   budget remaining. Per-method and per-tool traffic breakdowns with zero
   body inspection. The `Authorization` header is forwarded upstream
   untouched and never read, keyed on, or logged.

The one exception to body-blindness is rejection: a JSON-RPC error
must echo the request `id`, so the proxy parses the envelope there — and only
there. That is Section 10.4.3's point about body awareness: when you finally
need it, it's one well-specified envelope.

Verified against `ModelContextProtocol.AspNetCore` 2.0.0 (GA): the
header contract is enforced server-side. On modern-era requests
(`MCP-Protocol-Version: 2026-07-28`) a missing `Mcp-Method`, or a header that
disagrees with the body, gets HTTP 400 with JSON-RPC error `-32020` from the
upstream itself. Legacy-posture requests (no version header, or an older
version) are exempt from that check, so their headers are unverified hints.
The proxy trusts `Mcp-Method` and `Mcp-Name` only because it admits
modern-era traffic only, and the upstream validates the mirror on exactly that
traffic.

## What this demo does not do

- **`X-Principal` is not authentication.** It is a demo stand-in for an
  identity your authenticating front end has already resolved from a
  validated credential and asserts to the proxy (the upstream server still
  validates the token itself; see Section 10.4.1). Here the caller sets it,
  so rotating it rotates the budget, and requests without it share one
  `anonymous` budget. Never derive the principal from, key on, or log a raw
  credential.
- **The budget is per-process.** It is an in-memory dictionary: unbounded
  (one entry per principal ever seen), reset on restart, and not shared
  between proxy replicas. A fleet-wide limit needs shared quota state.
- **It charges attempts, not logical calls.** Every `tools/call` request
  spends budget, MRTR continuations included. Section 10.5.1's accounting
  (charge a verified continuation to the logical call it continues, and bound
  the rounds) needs to recognise a continuation and verify its
  `requestState`. Both live in the body, which this header-only proxy never
  reads.
- **Budget is spent before forwarding.** A call the upstream then rejects
  (a `-32020` header mismatch, say) still costs the caller one unit.

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
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}'

# a tool call — spends alice's budget, default pool
curl "${COMMON[@]}" http://localhost:5100/ \
  -H 'Mcp-Method: tools/call' -H 'Mcp-Name: get_document' -H 'X-Principal: alice' \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_document","arguments":{"id":"roadmap"},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}'

# an admin_* tool call — routed to the dedicated pool on 5102
curl "${COMMON[@]}" http://localhost:5100/ \
  -H 'Mcp-Method: tools/call' -H 'Mcp-Name: admin_delete_document' -H 'X-Principal: alice' \
  -d '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"admin_delete_document","arguments":{"id":"roadmap"},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}'
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
per-principal. Drop the `MCP-Protocol-Version` header (a legacy client) and
the proxy refuses before routing or budgeting:

```
HTTP/1.1 400 Bad Request

{"jsonrpc":"2.0","id":2,"error":{"code":-32022,"message":"Unsupported protocol
version","data":{"supported":["2026-07-28"],"requested":""}}}
```

The proxy log shows every decision:

```
tools/list - principal=alice upstream=http://localhost:5101 budget=5
tools/call get_document principal=alice upstream=http://localhost:5101 budget=4
tools/call admin_delete_document principal=alice upstream=http://localhost:5102 budget=3
tools/call get_document principal=alice upstream=(rejected) budget=0
tools/call get_document principal=bob upstream=http://localhost:5101 budget=4
rejected: MCP-Protocol-Version=(none)
```

## Ports

C# only for now; Python and TypeScript ports may follow (the pattern is the
same handful of lines in any web stack, which is the section's point).
