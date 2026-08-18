import { Server } from '@modelcontextprotocol/server';
import { serveStdio } from '@modelcontextprotocol/server/stdio';

// serveStdio owns the era decision: modern (2026-07-28) openings get a modern
// instance, legacy openings a legacy one — the same factory serves both.
serveStdio(() => {
  const server = new Server(
    { name: 'low-level-handlers-server', version: '1.0.0' },
    { capabilities: { tools: {} } }
  );

  // Register a handler for tools/list
  server.setRequestHandler('tools/list', async () => {
    return {
      tools: [
        {
          name: 'echo',
          description: 'Echo back the provided message',
          inputSchema: {
            type: 'object' as const,
            properties: {
              message: { type: 'string', description: 'The message to echo back' },
            },
            required: ['message'],
          },
        },
        {
          name: 'reverse',
          description: 'Reverse the provided message',
          inputSchema: {
            type: 'object' as const,
            properties: {
              message: { type: 'string', description: 'The message to reverse' },
            },
            required: ['message'],
          },
        },
        {
          name: 'shout',
          description: 'Return the provided message in uppercase',
          inputSchema: {
            type: 'object' as const,
            properties: {
              message: { type: 'string', description: 'The message to shout' },
            },
            required: ['message'],
          },
        },
      ],
    };
  });

  // Register a handler for tools/call
  server.setRequestHandler('tools/call', async (request) => {
    const { name, arguments: args } = request.params;

    switch (name) {
      case 'echo':
        return {
          content: [{ type: 'text' as const, text: `Echo: ${(args as any).message}` }],
        };
      case 'reverse':
        return {
          content: [
            { type: 'text' as const, text: [...String((args as any).message)].reverse().join('') },
          ],
        };
      case 'shout':
        return {
          content: [{ type: 'text' as const, text: String((args as any).message).toUpperCase() }],
        };
      default:
        throw new Error(`Unknown tool: ${name}`);
    }
  });

  return server;
});
