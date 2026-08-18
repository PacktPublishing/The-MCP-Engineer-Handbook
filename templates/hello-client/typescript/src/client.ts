import { Client } from '@modelcontextprotocol/client';
import { StdioClientTransport } from '@modelcontextprotocol/client/stdio';

const client = new Client(
  { name: 'hello-client', version: '0.1.0' },
  // Negotiate the modern era via server/discover instead of the legacy default.
  { versionNegotiation: { mode: 'auto' } },
);

const transport = new StdioClientTransport({
  command: 'node',
  args: ['../../hello-server/typescript/dist/server.js'],
});

await client.connect(transport);

console.log('negotiated protocol version:', client.getNegotiatedProtocolVersion());

const tools = await client.listTools();
console.log('tools:', tools.tools.map((t) => t.name).join(', '));

const result = await client.callTool({
  name: 'say_hello',
  arguments: { name: 'Peder' },
});
console.log('result:', JSON.stringify(result.content));

await client.close();
