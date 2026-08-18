# Agent instructions: (re)generate `data/endpoints.json`

These are example instructions for a coding agent that regenerates the endpoint
manifest whenever the API changes (see Chapter 5, "API Endpoint Metadata
Generation"). Point the agent at the API source of truth — controller/route
code, an OpenAPI document, or API reference docs — and have it emit the
manifest the MCP server serves. The output is reviewed by a human alongside the
API change itself.

---

## Task

Regenerate `data/endpoints.json` from the current API definition at
`<path to API source / OpenAPI file / docs>`. Cover every public endpoint —
do not carry over entries for endpoints that no longer exist, and do not
invent endpoints that are not in the source.

## Output format

A single JSON document:

```json
{
  "api": "<server name>",
  "version": "<API version>",
  "groups": [
    { "name": "<Group>", "description": "<one line: what this group covers>" }
  ],
  "endpoints": [
    {
      "method": "GET|POST|PATCH|PUT|DELETE",
      "path": "/api/... with {placeholders} for path parameters",
      "group": "<Group — must match an entry in groups>",
      "summary": "<one line>",
      "description": "<full description, 1–3 sentences>",
      "parameters": [
        {
          "name": "<name>",
          "in": "path|query",
          "type": "string|integer|boolean",
          "required": true,
          "description": "<what it is, with an example value where helpful>"
        }
      ],
      "requestBody": { "contentType": "application/json", "schema": { } },
      "response": { "description": "<status + one line>", "schema": { } }
    }
  ]
}
```

`requestBody` is `null` for endpoints without a body. Schemas are JSON
Schema-style objects taken from the API definition; preserve `required`,
`enum`, and per-property `description` fields — models plan calls from these.

## Grouping

Group endpoints by resource area the way a human would navigate the API
(e.g. Documents, Users, Groups, Permissions, Versions). Aim for 3–8 endpoints
per group; split a group that grows past that. Order groups by how often they
are likely to be needed, and keep the ordering stable across regenerations so
diffs stay reviewable.

## Writing the two levels of metadata

The manifest feeds two disclosure levels, and they have different budgets:

- **`summary` (for list/search results):** one line, under ~90 characters, no
  trailing period. Lead with what the endpoint does, not how. Include the
  words a model would plausibly search for — permission levels, "inherited",
  "full-text" — because search matches against path, summary, and description.
  Do not repeat the path or method; they are printed alongside it.
- **`description` (for describe_endpoint):** 1–3 sentences. State behavior,
  side effects (versioning, cascade deletes, replacement semantics), and where
  related IDs come from (e.g. "Grant IDs come from GET
  /api/documents/{id}/permissions"). No marketing language.

Where the source documentation is thin, derive the behavior from the code;
if the behavior cannot be determined, write `"description": "TODO: verify"`
rather than guessing — the reviewer will resolve it.

## Validation before you finish

1. The file parses as JSON and every endpoint's `group` exists in `groups`.
2. Every endpoint has all fields; every `{placeholder}` in a path has a
   matching entry in `parameters` with `"in": "path"`.
3. Endpoint count per group matches the API definition; report the counts.
4. Report a diff summary against the previous manifest: endpoints added,
   removed, and changed — this is what the human review reads first.
