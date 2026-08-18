import { McpServer } from '@modelcontextprotocol/server';
import { serveStdio } from '@modelcontextprotocol/server/stdio';
import { z } from 'zod';

// serveStdio owns the era decision: modern (2026-07-28) openings get a modern
// instance, legacy openings a legacy one — the same factory serves both.
serveStdio(() => {
  const server = new McpServer({ name: 'echo-server', version: '0.1.0' });

  server.registerTool(
    'echo',
    {
      description: 'A tool that echoes back the input message',
      inputSchema: z.object({
        message: z.string().describe('The message to echo back'),
        uppercase: z
          .boolean()
          .default(false)
          .describe('Whether to uppercase the message'),
      }),
    },
    async ({ message, uppercase }) => {
      const result = message === '' ? 'No message provided'
        : `Echo: ${uppercase ? message.toUpperCase() : message}`;
      return { content: [{ type: 'text', text: result }] };
    },
  );

  return server;
});
