# stepup-client — step-up authorization and the scope-union rule

Companion sample for **Section 9.7.4, "The Scoping Challenge and Step-Up
Authorization"** (SEP-2350, specified in the `2026-07-28` revision).

The union rule: when a `403` challenge with `error="insufficient_scope"`
arrives, the client re-authorizes for the **union** of the scopes it already
holds and the scopes the challenge names — never just the new ones. Scope
accumulation is deliberately the client's job — the Authorization Server and
the MCP server stay stateless about past grants, so a re-authorization
*replaces* the token, and any held scope left out of the request is gone.

## The three parts

One solution, `csharp/StepUpSample.sln`:

- **FakeAuthServer** (`http://localhost:5301/token`) — grants an HMAC-JWT for
  whatever scopes are requested, tracking nothing. A real AS would run the
  authorization-code + PKCE flow with a consent screen here; this fake
  simulates a user who consents to everything so the transcript can focus on
  the step-up mechanics. Demo-only in every way: shared static HMAC secret,
  no consent, no client authentication.
- **ReportsServer** (`http://localhost:5302`) — HTTP MCP server with a
  low-scope tool (`read_summary`, needs `reports:read`) and a high-scope tool
  (`export_full_data`, needs `reports:read reports:export`). Insufficient
  scope on `tools/call` produces `403` with an RFC 6750 challenge naming the
  **full** set the operation needs in one shot:

  ```
  WWW-Authenticate: Bearer error="insufficient_scope",
      error_description="...", scope="reports:read reports:export"
  ```

- **StepUpClient** — the point of the sample. Acquires `reports:read`, calls
  `read_summary` (works), calls `export_full_data` (403), parses the
  challenge, computes `held ∪ challenged`, re-authorizes for the union,
  retries — then calls `read_summary` again with the new token to prove
  nothing was lost.

## The `--naive` lesson

`--naive` re-authorizes for **only the scopes it was missing**
(`reports:export`), on the assumption that grants accumulate somewhere. They
don't. The transcript shows the damage twice over: the `export_full_data`
retry *still* fails (the operation needs `reports:read` too, and the new
token no longer carries it), and `read_summary` — which worked moments ago —
now fails with its own `insufficient_scope` challenge. The regression in a
previously working tool is the failure mode that makes naive step-up nasty in
production: it surfaces far from the code that caused it.

## Run

Three terminals (or background the first two):

```bash
cd csharp
dotnet run --project FakeAuthServer   # terminal 1
dotnet run --project ReportsServer    # terminal 2
dotnet run --project StepUpClient             # terminal 3: the union rule
dotnet run --project StepUpClient -- --naive  # terminal 3: the anti-pattern
```

Expected tail of the correct run:

```
[5] retry tools/call export_full_data
    200 OK -> export.csv: 12 reports, 4,812 rows (full dataset).
[6] tools/call read_summary with the new token
    200 OK -> Q2 summary: revenue up 8%, 3 open incidents, 12 reports on file.

done: the union preserved reports:read across the step-up — nothing was lost.
```

Expected tail of `--naive`:

```
[5] retry tools/call export_full_data
    403 Forbidden
    WWW-Authenticate: Bearer error="insufficient_scope", ... scope="reports:read reports:export"
[6] tools/call read_summary with the new token
    403 Forbidden
    WWW-Authenticate: Bearer error="insufficient_scope", ... scope="reports:read"

lesson: the naive re-authorization LOST reports:read — a tool that worked in [2] now fails.
```

## Notes

- The client speaks stateless `2026-07-28` Streamable HTTP directly
  (`tools/call` POSTs with the `_meta` envelope, no handshake) so that the
  `403` status and `WWW-Authenticate` header stay visible — the challenge
  handling *is* the sample.
- Section 9.7.4's other client duties, shown or noted in the code: the retry
  is bounded (this transcript needs exactly one step-up; production clients
  should cap attempts and then surface the failure to a human), and the
  client does no scope algebra beyond the union — hierarchical-scope
  reasoning is the server's problem.
- Tokens carry an `aud` claim bound to the MCP server via the RFC 8707
  `resource` parameter, matching Section 9.2.4's audience-binding posture,
  and the server checks it.
