import { McpServer } from '@modelcontextprotocol/server';
import { serveStdio } from '@modelcontextprotocol/server/stdio';
import { z } from 'zod';

// serveStdio owns the era decision: modern (2026-07-28) openings get a modern
// instance, legacy openings a legacy one — the same factory serves both.
// (A hand-wired `server.connect(new StdioServerTransport())` serves the
// legacy era only.)
serveStdio(() => {
  const server = new McpServer({ name: 'hello-server', version: '0.1.0' });

  server.registerTool(
    'say_hello',
    {
      description: 'Greets the caller by name.',
      inputSchema: z.object({
        name: z.string().describe('Name to greet.'),
      }),
    },
    async ({ name }) => ({
      content: [
        {
          type: 'text',
          text: `Hello, ${name}! This server runs on the 2026-07-28 MCP revision.`,
        },
      ],
    }),
  );

  return server;
});
