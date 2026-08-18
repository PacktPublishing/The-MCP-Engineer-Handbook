// Progressive disclosure over a document-management API (Chapter 5, §5.1.2).
// Four static tools — list, search, describe, execute — front an endpoint
// manifest loaded from data/endpoints.json. The endpoints are data, not tools.

import { readFileSync } from 'node:fs';
import { McpServer } from '@modelcontextprotocol/server';
import { serveStdio } from '@modelcontextprotocol/server/stdio';
import { z } from 'zod';
import { handlers, ApiError } from './api.js';

// --- Endpoint manifest (generated data — see ../agent-instructions.md) ---

interface EndpointParameter {
  name: string;
  in: 'path' | 'query';
  type: string;
  required: boolean;
  description: string;
}

interface Endpoint {
  method: string;
  path: string;
  group: string;
  summary: string;
  description: string;
  parameters: EndpointParameter[];
  requestBody: { contentType: string; schema: unknown } | null;
  response: { description: string; schema: unknown };
}

interface Manifest {
  api: string;
  version: string;
  groups: { name: string; description: string }[];
  endpoints: Endpoint[];
}

const manifest: Manifest = JSON.parse(
  readFileSync(new URL('../../data/endpoints.json', import.meta.url), 'utf8'),
);

const groupNames = manifest.groups.map((g) => g.name);

// Match a path (either the "{id}" template itself or a concrete path like
// "/api/documents/doc-001/permissions") against the manifest. Literal
// segments beat placeholders, so "/api/documents/search" is not swallowed
// by "/api/documents/{id}".
function matchEndpoint(
  method: string,
  path: string,
): { endpoint: Endpoint; params: Record<string, string> } | undefined {
  const segments = path.split('/').filter(Boolean);
  let best: { endpoint: Endpoint; params: Record<string, string>; literals: number } | undefined;
  for (const endpoint of manifest.endpoints) {
    if (endpoint.method !== method) continue;
    const templateSegments = endpoint.path.split('/').filter(Boolean);
    if (templateSegments.length !== segments.length) continue;
    const params: Record<string, string> = {};
    let literals = 0;
    let matched = true;
    for (let i = 0; i < segments.length; i++) {
      const t = templateSegments[i];
      if (t.startsWith('{') && t.endsWith('}')) {
        params[t.slice(1, -1)] = decodeURIComponent(segments[i]);
      } else if (t === segments[i]) {
        literals++;
      } else {
        matched = false;
        break;
      }
    }
    if (matched && (!best || literals > best.literals)) {
      best = { endpoint, params, literals };
    }
  }
  return best && { endpoint: best.endpoint, params: best.params };
}

// --- Server (matches the construction printed in Chapter 5) ---

// serveStdio owns the era decision: modern (2026-07-28) openings get a modern
// instance, legacy openings a legacy one — the same factory serves both.
serveStdio(() => {
  const server = new McpServer(
    { name: 'document-management-api', version: '1.0.0' },
    {
      capabilities: { tools: {} },
      instructions:
        'This server provides access to a document management API with features for ' +
        'managing documents, users, groups, permissions, and document versioning. ' +
        'Use list_endpoints to browse available API groups, search_endpoints to find ' +
        'specific functionality, describe_endpoint to get full details before calling, ' +
        'and execute_endpoint to invoke API operations. ' +
        'Common workflows: querying document permissions, checking user access levels, ' +
        'comparing document versions, and managing document lifecycle.',
    },
  );

  function text(s: string) {
    return { content: [{ type: 'text' as const, text: s }] };
  }

  function errorText(s: string) {
    return { content: [{ type: 'text' as const, text: s }], isError: true };
  }

  // --- Tool 1: list_endpoints (navigation) ---

  server.registerTool(
    'list_endpoints',
    {
      description:
        'List the available endpoint groups of the document management API, or list the ' +
        'endpoints within a specific group. Call without arguments to see all groups. ' +
        'Provide a group name to see its endpoints.',
      inputSchema: z.object({
        group: z.string().optional().describe(
          'Group name to list endpoints for. Omit to see all groups.',
        ),
      }),
    },
    async ({ group }) => {
      if (group === undefined) {
        const lines = manifest.groups.map((g) => {
          const count = manifest.endpoints.filter((e) => e.group === g.name).length;
          return `- ${g.name} (${count} endpoint${count === 1 ? '' : 's'})`;
        });
        return text(
          `Available API groups:\n\n${lines.join('\n')}\n\n` +
            `Use list_endpoints with a group name to see the endpoints in that group.`,
        );
      }
      const match = groupNames.find((n) => n.toLowerCase() === group.toLowerCase());
      if (!match) {
        return errorText(
          `Unknown group "${group}". Available groups: ${groupNames.join(', ')}. ` +
            `Call list_endpoints without arguments to see all groups.`,
        );
      }
      const lines = manifest.endpoints
        .filter((e) => e.group === match)
        .map((e) => `- ${e.method} ${e.path} — ${e.summary}`);
      return text(
        `Endpoints in "${match}":\n\n${lines.join('\n')}\n\n` +
          `Use describe_endpoint to get full details for a specific endpoint.`,
      );
    },
  );

  // --- Tool 2: search_endpoints (search) ---

  server.registerTool(
    'search_endpoints',
    {
      description:
        'Search the document management API endpoints with a free-text query. Matches ' +
        'endpoint paths, summaries, and descriptions — API metadata, not document content. ' +
        'Returns concise results; use describe_endpoint for full details.',
      inputSchema: z.object({
        query: z.string().describe(
          'Free-text search terms, e.g. "write permission" or "compare versions".',
        ),
      }),
    },
    async ({ query }) => {
      const terms = query.toLowerCase().split(/\s+/).filter(Boolean);
      if (terms.length === 0) {
        return errorText('Provide one or more search terms, e.g. "write permission".');
      }
      const matches = manifest.endpoints.filter((e) => {
        const haystack = `${e.method} ${e.path} ${e.group} ${e.summary} ${e.description}`.toLowerCase();
        return terms.every((t) => haystack.includes(t));
      });
      if (matches.length === 0) {
        return text(
          `No endpoints match "${query}". Try fewer or more general keywords, ` +
            `or use list_endpoints to browse the API groups.`,
        );
      }
      const lines = matches.map((e) => `- [${e.group}] ${e.method} ${e.path} — ${e.summary}`);
      return text(
        `Found ${matches.length} endpoint(s) matching "${query}":\n\n${lines.join('\n')}\n\n` +
          `Use describe_endpoint to get full details before executing.`,
      );
    },
  );

  // --- Tool 3: describe_endpoint (full metadata) ---

  server.registerTool(
    'describe_endpoint',
    {
      description:
        'Get the full details of a single API endpoint: description, parameters, request ' +
        'body schema, and response schema. Call this before using execute_endpoint.',
      inputSchema: z.object({
        method: z.string().describe('HTTP method, e.g. "GET".'),
        path: z.string().describe(
          'Endpoint path as shown by list_endpoints or search_endpoints, ' +
            'e.g. "/api/documents/{id}/permissions".',
        ),
      }),
    },
    async ({ method, path }) => {
      const m = method.toUpperCase();
      const found =
        manifest.endpoints.find((e) => e.method === m && e.path === path) ??
        matchEndpoint(m, path)?.endpoint;
      if (!found) {
        return errorText(
          `No endpoint matches ${m} ${path}. Use list_endpoints to browse groups ` +
            `or search_endpoints to find functionality.`,
        );
      }
      const parts: string[] = [
        `${found.method} ${found.path} — ${found.summary}`,
        `Group: ${found.group}`,
        found.description,
      ];
      if (found.parameters.length > 0) {
        const lines = found.parameters.map(
          (p) =>
            `- ${p.name} (${p.in}, ${p.type}, ${p.required ? 'required' : 'optional'}) — ${p.description}`,
        );
        parts.push(`Parameters:\n${lines.join('\n')}`);
      } else {
        parts.push('Parameters: none');
      }
      if (found.requestBody) {
        parts.push(
          `Request body (${found.requestBody.contentType}):\n` +
            JSON.stringify(found.requestBody.schema, null, 2),
        );
      } else {
        parts.push('Request body: none');
      }
      parts.push(
        `Response: ${found.response.description}\n` + JSON.stringify(found.response.schema, null, 2),
      );
      return text(parts.join('\n\n'));
    },
  );

  // --- Tool 4: execute_endpoint (invocation) ---

  server.registerTool(
    'execute_endpoint',
    {
      description:
        'Execute a document management API endpoint. Provide the HTTP method and the path ' +
        'with path parameters filled in (e.g. "/api/documents/doc-001/permissions"). Query ' +
        'strings can be embedded in the path or passed separately; POST and PATCH bodies ' +
        'are passed as a JSON string. Use describe_endpoint first to see the exact schema.',
      inputSchema: z.object({
        method: z.string().describe('HTTP method of the endpoint, e.g. "GET" or "POST".'),
        path: z.string().describe(
          'Endpoint path with path parameters filled in, e.g. "/api/documents/doc-001/permissions". ' +
            'May include a query string.',
        ),
        query: z.string().optional().describe(
          'Query string without the leading "?", e.g. "q=quarterly report". ' +
            'Alternative to embedding it in the path.',
        ),
        body: z.string().optional().describe('JSON request body, for POST and PATCH endpoints.'),
      }),
    },
    async ({ method, path, query, body }) => {
      const m = method.toUpperCase();
      const [purePath, embeddedQuery] = path.split('?', 2) as [string, string?];
      const params = new URLSearchParams(embeddedQuery ?? '');
      for (const [k, v] of new URLSearchParams(query ?? '')) params.append(k, v);

      const match = matchEndpoint(m, purePath);
      if (!match) {
        const otherMethods = manifest.endpoints.filter(
          (e) => e.method !== m && matchEndpoint(e.method, purePath)?.endpoint === e,
        );
        const hint =
          otherMethods.length > 0
            ? `The path exists with other methods: ${otherMethods
                .map((e) => `${e.method} ${e.path}`)
                .join(', ')}.`
            : `Use list_endpoints to browse groups or search_endpoints to find functionality, ` +
              `then describe_endpoint to confirm the exact method and path.`;
        return errorText(`No endpoint matches ${m} ${purePath}. ${hint}`);
      }

      let parsedBody: unknown;
      if (body !== undefined) {
        try {
          parsedBody = JSON.parse(body);
        } catch (err) {
          return errorText(
            `The body argument is not valid JSON (${(err as Error).message}). ` +
              `Pass the request body as a JSON string; use describe_endpoint with ` +
              `${m} ${match.endpoint.path} to see the request schema.`,
          );
        }
      }

      const handler = handlers[`${match.endpoint.method} ${match.endpoint.path}`];
      try {
        const result = handler(match.params, params, parsedBody);
        return text(JSON.stringify(result, null, 2));
      } catch (err) {
        if (err instanceof ApiError) {
          return errorText(err.message);
        }
        throw err;
      }
    },
  );

  return server;
});
