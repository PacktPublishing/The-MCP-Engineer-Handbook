# token-validation — audience and scope checks at the resource server

Companion sample for **Section 9.2, "OAuth 2.1 — The Standard Path"** —
specifically the Resource Server obligations: RFC 8707 audience binding
(Section 9.2.4), RFC 9728 protected resource metadata and `WWW-Authenticate`
discovery (Section 9.2.2), and per-tool scope enforcement (Section 9.2.7).
The middleware embodies the token passthrough prohibition (Section 9.2.8):
**validate, don't forward** — every rejection below is a check a passthrough
server never makes.

An ASP.NET Core HTTP MCP server exposes three report tools at two permission
levels, fronted by a token-validation middleware. Each layer of the rejection
ladder demonstrates one obligation:

| Request | Response | What it demonstrates |
|---|---|---|
| No / invalid token | `401` + `WWW-Authenticate: Bearer ... resource_metadata="..."` | The discovery chain's first link (§9.2.2): the challenge points at the RFC 9728 metadata document, which points at the Authorization Server |
| Genuine token, wrong `aud` | `401`, "this is a token for a different resource" | RFC 8707 audience binding (§9.2.4): the signature proves the token is genuine; only the audience check proves it's *yours* |
| Right `aud`, missing scope | In-band tool error naming the missing scope | Per-tool scope policy (§9.2.7), via the C# SDK's request-filter pipeline (§5.4.2); the error is model-readable so an agent knows which scope a re-authorization needs |
| Right `aud`, right scope | Tool result | — |

Scope failures come back as `isError: true` tool results rather than HTTP
401s deliberately: the token *is* valid for this server, the request just
exceeds it, and the model benefits from reading why.

## The demo issuer is not an Authorization Server

The sample mints its own HMAC-signed JWTs (`make-token` mode) so it runs with
no external dependencies. That is the only reason issuer constants exist in
this code. A real deployment keeps the Resource Server role pure (§9.2.1):
tokens come from a real Authorization Server (Keycloak, Auth0, Okta, an
enterprise IdP), and validation uses that AS's published signing keys via
standard JWT bearer middleware (`AddAuthentication().AddJwtBearer(...)` in
ASP.NET Core). Nothing else in the sample changes — the audience check, the
scope filter, and the metadata document are the parts you keep.

`ModelContextProtocol.AspNetCore` 2.0.0 also ships production
wiring for two of the three layers shown hand-rolled here:
`McpAuthenticationHandler` (registered via
`.AddAuthentication(...).AddMcp(...)`) serves the RFC 9728 document and adds
`resource_metadata` to 401 challenges, and
`.AddAuthorizationFilters()` honors `[Authorize]` attributes on tools —
including filtering `tools/list` down to what the caller may use. This
sample hand-rolls instead so every check is visible on the page.

## Run

```bash
cd csharp
dotnet run
# listens on http://localhost:5309, MCP endpoint at /mcp
```

## Mint tokens

```bash
# read-only (default scopes: reports:read)
dotnet run -- make-token

# full access
dotnet run -- make-token --scopes "reports:read reports:admin"

# a genuine token minted for a DIFFERENT resource — for the audience demo
dotnet run -- make-token --audience https://other.example/mcp --scopes "reports:read reports:admin"
```

Tokens are HMAC-signed JWTs, valid for one hour, with `iss`, `aud`, `sub`,
and a space-delimited `scope` claim.

## Smoke test

Requests are stateless 2026-07-28 era — no `initialize` handshake needed.
The SDK validates the `_meta` envelope strictly: requests that reach the MCP
endpoint must carry `clientInfo`, `clientCapabilities`, and `protocolVersion`
(a missing `clientCapabilities` is rejected with `-32602`). The auth
middleware runs first, so the 401 demos below trigger regardless — but the
curls all use the full envelope so the same shape works at every rung of the
ladder.

**1. No token — 401 with the discovery pointer:**

```bash
curl -si http://localhost:5309/mcp -X POST \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H 'MCP-Protocol-Version: 2026-07-28' \
  -H 'Mcp-Method: tools/list' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}'
```

```
HTTP/1.1 401 Unauthorized
WWW-Authenticate: Bearer error="invalid_request", resource_metadata="http://localhost:5309/.well-known/oauth-protected-resource/mcp"
```

**2. Follow the pointer — the RFC 9728 document (path-aware well-known
location for a resource at `/mcp`):**

```bash
curl -s http://localhost:5309/.well-known/oauth-protected-resource/mcp
```

```json
{"resource":"http://localhost:5309/mcp",
 "authorization_servers":["https://demo-as.example"],
 "scopes_supported":["reports:read","reports:admin"],
 "bearer_methods_supported":["header"]}
```

**3. Wrong audience — genuine token, different resource:**

```bash
TOKEN=$(dotnet run -- make-token --audience https://other.example/mcp --scopes "reports:read reports:admin")
curl -s http://localhost:5309/mcp -X POST \
  -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -H 'MCP-Protocol-Version: 2026-07-28' \
  -H 'Mcp-Method: tools/call' -H 'Mcp-Name: read_report' \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"read_report","arguments":{"id":"q1-sales"},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}'
```

```json
{"error":"invalid_token","error_description":"Token audience is 'https://other.example/mcp' — this is a token for a different resource. This server only accepts tokens minted for 'http://localhost:5309/mcp' (RFC 8707 resource indicator)."}
```

**4. Right audience, insufficient scope — `delete_report` with a read-only
token (same curl shape, `Mcp-Name: delete_report`). Successful calls come
back as a one-event SSE stream:**

```
event: message
data: {"result":{"content":[{"type":"text","text":"Insufficient scope for 'delete_report': the token grants [reports:read] but this tool requires 'reports:admin'. Re-authorize with the missing scope."}],"isError":true,"resultType":"complete","_meta":{...}},"id":3,"jsonrpc":"2.0"}
```

**5. Full scope — mint with `--scopes "reports:read reports:admin"` and the
same call succeeds:**

```
event: message
data: {"result":{"content":[{"type":"text","text":"Deleted report 'q1-sales'."}],"resultType":"complete","_meta":{...}},"id":5,"jsonrpc":"2.0"}
```
