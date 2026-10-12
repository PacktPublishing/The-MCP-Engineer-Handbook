# Progressive disclosure — document management API

Sample for **Chapter 5, §5.1.2 (The Progressive Disclosure Pattern)**. Instead
of one tool per API endpoint, the server exposes four static tools —
`list_endpoints`, `search_endpoints`, `describe_endpoint`, `execute_endpoint` —
and treats the API surface as data. The endpoint metadata lives in
[`data/endpoints.json`](data/endpoints.json): five groups (Documents, Users,
Groups, Permissions, Versions), twenty endpoints, each with a one-line summary
for discovery and a full description plus request/response schemas for
`describe_endpoint`.

`execute_endpoint` runs against a small in-memory simulation of the API
(a few documents, users, groups, permission grants, and version histories),
so realistic calls return plausible JSON. The simulation has no
authentication; all calls act as `user-001` (Alice Chen). Errors are written
as guidance — unknown paths, wrong methods, and malformed bodies come back
with instructions on which discovery tool to use next.

The manifest is generated data, not hand-maintained code:
[`agent-instructions.md`](agent-instructions.md) is the example prompt for a
coding agent that regenerates `data/endpoints.json` from the API source or an
OpenAPI document, as discussed in the chapter's "API Endpoint Metadata
Generation" section.

## Languages

- **TypeScript** (`typescript/`) — canonical implementation
- **C#** (`csharp/`) and **Python** (`python/`) — ports; same tools, same
  output text, same simulated API. All three load the shared manifest from
  `data/endpoints.json`.

## Run

- **TypeScript**: `cd typescript && npm ci && npm run build && npm start`
  (Node 20+)
- **C#**: `cd csharp && dotnet run` (.NET 10)
- **Python (stdio)**: `cd python && uv run python server.py`
- **Python (streamable HTTP)**: `cd python && uv run python http_server.py`

All servers speak stdio, and all three accept handshake-less 2026-07-28
requests (the Python stdio server is dual-era, locked per connection by the
opening message: send the classic `initialize` handshake instead to exercise
its legacy era — and then drop the `_meta` envelope from later requests, as a
handshake-opened connection rejects it with `-32600`).

## Smoke test

Pipe these into the running process (one per line), on any of the three:

```
{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_endpoints","arguments":{},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_endpoints","arguments":{"group":"Permissions"},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"search_endpoints","arguments":{"query":"write permission"},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}
{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"describe_endpoint","arguments":{"method":"GET","path":"/api/documents/{id}/permissions"},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}
{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"execute_endpoint","arguments":{"method":"GET","path":"/api/documents/doc-001/permissions"},"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}
```

## Example: who has write access to a document?

The chapter's example interaction (finding who has write access to the
Quarterly Report Q1 2026) reproduces against the seed data, but the
permissions call alone does not answer it: grants name *principals*, and
some of those are groups. The full composition is:

1. Find the document: `GET /api/documents/search` with
   `q=Quarterly Report Q1 2026` → `doc-001`.
2. List its grants: `GET /api/documents/doc-001/permissions` → Alice Chen
   (user, `admin`), Finance (group `grp-002`, `read`), Leadership (group
   `grp-001`, `write`).
3. Expand each group grant at `write` or above: `GET
   /api/groups/grp-001/members` → Alice Chen, Bob Martinez. (Finance only has
   `read`, so its members — Carol Okafor, Dana Kim — do not qualify.)
4. Combine with the direct user grants, keep each user's highest level, and
   deduplicate: **Alice Chen (`admin`: direct, and `write` via Leadership)
   and Bob Martinez (`write` via Leadership).**
5. Check the answer: `GET /api/documents/doc-001/permissions/check` with
   `userId=user-002` (and so on for each user) returns the server's own
   effective level and the grants it came from. A composed answer is a claim
   about the API's semantics; checking it against the authoritative endpoint
   is cheap.

Access semantics of the simulation: levels are ordered `read` < `write` <
`admin`, and a higher level includes the lower ones; a user's effective level
is the highest of their direct grant and the grants to groups they belong to;
groups are flat; creating a document grants its creator `admin`. It does not
model nested groups, deny entries, folder or workspace inheritance, sharing
links, or org-wide roles — a production ACL usually has some of these, which
is exactly why step 5 belongs in the workflow.

## Authorization boundary

`execute_endpoint` only runs operations listed in the manifest: the method
and path must match a manifest entry (no arbitrary URLs or methods), and a
path parameter must decode to a single segment — `%2F`, `..`, and malformed
escapes are rejected, so a filled-in path cannot reach outside the template it
matched.

That is necessary but not sufficient. Approving the generic `execute_endpoint`
tool is not approval of every operation the manifest exposes today, or of the
ones a regenerated manifest adds tomorrow — and a host cannot infer an
operation's side effects from one generic tool's annotations. In a real
deployment, check the concrete operation (method + path template) against a
server-side allowlist and the caller's permissions before running it, and bind
approval and audit records to that operation rather than to
`execute_endpoint`. All three ports mark this point in `execute_endpoint`; the
simulation has no authentication, so it only writes an audit line to stderr:

```
[audit] user-001 GET /api/documents/{id}/permissions {"id":"doc-001"}
```

Treat a manifest change that adds operations as a capability change that
needs the same review as adding tools.
