// Completions client (section 5.3.3).
//
// Spawns the sample server over stdio and requests completions for a partial
// `path` value — the TypeScript counterpart of the chapter's Python extract.

import { Client } from '@modelcontextprotocol/client';
import { StdioClientTransport } from '@modelcontextprotocol/client/stdio';

const client = new Client(
  { name: 'completions-client', version: '0.1.0' },
  // Negotiate the modern era via server/discover instead of the legacy default.
  { versionNegotiation: { mode: 'auto' } },
);

await client.connect(new StdioClientTransport({ command: 'node', args: ['dist/server.js'] }));

// Request completions for the "path" argument
const result = await client.complete({
  ref: { type: 'ref/resource', uri: 'file:///{path}' },
  argument: { name: 'path', value: 'docs/re' },
});

console.log('values:', result.completion.values);
console.log('total:', result.completion.total, 'hasMore:', result.completion.hasMore);

await client.close();
