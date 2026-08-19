// Minimal TypeScript counterpart to the C# eval harness: server and client in one process
// over the SDK's linked in-memory transport pair (§10.1.5), one eval task, N runs.
//
// InMemoryTransport.createLinkedPair() is the TS SDK's in-memory seam — exported from BOTH
// @modelcontextprotocol/client and @modelcontextprotocol/server in 2.0.0.

import { Client, InMemoryTransport } from '@modelcontextprotocol/client';
import { McpServer } from '@modelcontextprotocol/server';
import { z } from 'zod';

// --- The server under test: a two-document slice of the C# DocsServer corpus, including the
// dud trap — a deprecated FAQ that naive search ranks above the current policy.
const corpus = [
  {
    id: 'legacy-refund-faq',
    title: 'Refund FAQ (2024)',
    body: 'DEPRECATED — superseded by refund-policy. Refunds are available within 60 days of purchase for all plans.',
  },
  {
    id: 'refund-policy',
    title: 'Refund Policy',
    body: 'Effective 2025-01-01: monthly plans may be refunded within 14 days of purchase; annual plans within 30 days.',
  },
];

const server = new McpServer({ name: 'docs-server-mini', version: '0.1.0' });

server.registerTool(
  'search_documents',
  {
    description: 'Searches the knowledge base. Returns matches as JSON.',
    inputSchema: z.object({ query: z.string().describe('Search terms.') }),
  },
  async ({ query }) => {
    const q = query.toLowerCase();
    const matches = corpus
      .filter((d) => d.title.toLowerCase().includes(q) || d.body.toLowerCase().includes(q))
      .map(({ id, title }) => ({ id, title }));
    return { content: [{ type: 'text', text: JSON.stringify({ matches }) }] };
  },
);

server.registerTool(
  'read_document',
  {
    description: 'Reads a document by id. Ids come from search_documents results.',
    inputSchema: z.object({ id: z.string().describe('Document id.') }),
  },
  async ({ id }) => {
    const doc = corpus.find((d) => d.id === id);
    if (!doc)
      return { isError: true, content: [{ type: 'text', text: `No document with id '${id}'.` }] };
    return { content: [{ type: 'text', text: JSON.stringify(doc) }] };
  },
);

// --- Wire client and server together in-process: no network, no subprocess.
const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
const client = new Client({ name: 'eval-harness-mini', version: '0.1.0' });
await server.connect(serverTransport);
await client.connect(clientTransport);

// --- One eval task, run N times and scored as a pass rate. The scripted "model" reads the
// first search hit — a plausible weak-model policy that produces a dud: every call valid,
// a confident answer, and the outcome check ("cites 30 days") catches it.
const RUNS = 3;
const text = (r: Awaited<ReturnType<typeof client.callTool>>) =>
  (r.content as Array<{ type: string; text: string }>)[0].text;

let passed = 0;
for (let run = 0; run < RUNS; run++) {
  const search = await client.callTool({ name: 'search_documents', arguments: { query: 'refund' } });
  const firstId = JSON.parse(text(search)).matches[0].id as string;
  const read = await client.callTool({ name: 'read_document', arguments: { id: firstId } });
  const answer = `According to '${firstId}': ${JSON.parse(text(read)).body}`;
  if (answer.includes('30 days')) passed++;
}

console.log('task: refund-window-annual');
console.log(`pass rate: ${passed}/${RUNS} (${Math.round((100 * passed) / RUNS)}%)`);
if (passed < RUNS)
  console.log('dud caught: every call succeeded and the answer was confident — only the outcome check failed');

await client.close();
await server.close();
