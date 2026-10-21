import { createServer } from 'node:http';
import { McpServer, createMcpHandler } from '@modelcontextprotocol/server';
import {
  localhostHostValidation,
  localhostOriginValidation,
  toNodeHandler,
} from '@modelcontextprotocol/node';
import { z } from 'zod';

// The same echo tool as the stdio server, served over Streamable HTTP.
// createMcpHandler serves modern (2026-07-28) requests statelessly and falls
// back to per-request legacy serving for 2025-era clients.
const handler = createMcpHandler(() => {
  const server = new McpServer({ name: 'echo-http-server', version: '0.1.0' });

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
      // Log the call so a reader can verify the tool was invoked (§1.8).
      console.log(`echo tool called: message="${message}", uppercase=${uppercase}`);
      const result = message === '' ? 'No message provided'
        : `Echo: ${uppercase ? message.toUpperCase() : message}`;
      return { content: [{ type: 'text', text: result }] };
    },
  );

  return server;
});

// createMcpHandler does no Host or Origin validation of its own. For a local
// server, accept only loopback Host headers (DNS rebinding protection) and,
// as the Streamable HTTP spec requires, reject requests whose Origin header is
// present and not a loopback origin (403).
const validateHost = localhostHostValidation();
const validateOrigin = localhostOriginValidation();
const mcpHandler = toNodeHandler(handler);

// Bind to loopback explicitly: without a host argument, Node listens on every
// network interface, whatever URL the log line prints.
createServer((req, res) => {
  if (!validateHost(req, res) || !validateOrigin(req, res)) return;
  void mcpHandler(req, res);
}).listen(5000, '127.0.0.1', () => {
  console.log('MCP echo server listening on http://localhost:5000');
});
