#!/usr/bin/env node

/**
 * MCP Server with Echo Tool - Streaming HTTP Transport
 *
 * This is a simple MCP server that provides an "echo" tool via HTTP using Streaming HTTP transport.
 * It demonstrates how to create an MCP server that can be accessed over HTTP.
 */

import express from 'express';
import { randomUUID } from 'node:crypto';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StreamableHTTPServerTransport } from '@modelcontextprotocol/sdk/server/streamableHttp.js';
import { isInitializeRequest } from '@modelcontextprotocol/sdk/types.js';
import { z } from 'zod';

// Define the input schema for the echo tool
const echoSchema = z.object({
  message: z.string().describe('The message to echo back'),
  uppercase: z.boolean().optional().default(false).describe('Whether to uppercase the message'),
});

// Port configuration
const PORT = 5000;

// Store active transports by session ID
const transports: Record<string, StreamableHTTPServerTransport> = {};

/**
 * Create a new MCP server instance
 */
function createServer(): McpServer {
  const server = new McpServer(
    {
      name: 'echo-http-server',
      version: '1.0.0',
    },
    {
      capabilities: {
        tools: {},
      },
    }
  );

  // Register the echo tool
  server.tool(
    'echo',
    'A tool that echoes back the input message',
    echoSchema.shape,
    async (args) => {
      const { message, uppercase } = args;
      
      if (!message) {
        return {
          content: [{ type: 'text', text: 'No message provided' }],
        };
      }

      const result = uppercase ? message.toUpperCase() : message;
      return {
        content: [{ type: 'text', text: `Echo: ${result}` }],
      };
    }
  );

  return server;
}

/**
 * Main application setup
 */
async function main(): Promise<void> {
  const app = express();

  // Parse JSON bodies
  app.use(express.json());

  // Handle POST requests for MCP messages
  app.post('/mcp', async (req, res) => {
    try {
      const sessionId = req.headers['mcp-session-id'] as string | undefined;
      const isInitRequest = isInitializeRequest(req.body);

      // Check if we need to create a new session
      if (isInitRequest) {
        const newSessionId = randomUUID();
        console.log(`Creating new session: ${newSessionId}`);

        // Create a new transport for this session
        const transport = new StreamableHTTPServerTransport({
          sessionIdGenerator: () => newSessionId,
        });

        // Set up transport cleanup
        transport.onclose = () => {
          console.log(`Transport closed for session ${newSessionId}`);
          delete transports[newSessionId];
        };

        // Store the transport
        transports[newSessionId] = transport;

        // Connect the transport to a new server instance
        const server = createServer();
        await server.connect(transport);

        // Handle the initialization request
        await transport.handleRequest(req, res, req.body);
        return;
      } else if (sessionId && transports[sessionId]) {
        // Use existing transport for established session
        const transport = transports[sessionId];
        await transport.handleRequest(req, res, req.body);
        return;
      } else {
        // Invalid request - no session ID or unknown session
        res.status(400).json({
          jsonrpc: '2.0',
          error: {
            code: -32000,
            message: 'Bad Request: No valid session ID provided',
          },
          id: null,
        });
        return;
      }
    } catch (error) {
      console.error('Error handling MCP request:', error);
      if (!res.headersSent) {
        res.status(500).json({
          jsonrpc: '2.0',
          error: {
            code: -32603,
            message: 'Internal server error',
          },
          id: null,
        });
      }
    }
  });

  // Handle GET requests for SSE streams
  app.get('/mcp', async (req, res) => {
    const sessionId = req.headers['mcp-session-id'] as string | undefined;

    if (!sessionId || !transports[sessionId]) {
      res.status(400).send('Invalid or missing session ID');
      return;
    }

    console.log(`Establishing SSE stream for session ${sessionId}`);
    const transport = transports[sessionId];
    await transport.handleRequest(req, res);
  });

  // Handle DELETE requests for session termination
  app.delete('/mcp', async (req, res) => {
    const sessionId = req.headers['mcp-session-id'] as string | undefined;

    if (!sessionId || !transports[sessionId]) {
      res.status(400).send('Invalid or missing session ID');
      return;
    }

    console.log(`Terminating session: ${sessionId}`);
    const transport = transports[sessionId];
    await transport.handleRequest(req, res);
  });

  // Start the server
  app.listen(PORT, () => {
    console.log(`MCP Streaming HTTP Server listening on http://127.0.0.1:${PORT}`);
    console.log(`Streaming HTTP endpoint: http://127.0.0.1:${PORT}/mcp`);
  });

  // Handle shutdown
  process.on('SIGINT', async () => {
    console.log('\nShutting down server...');
    
    // Close all active transports
    for (const sessionId in transports) {
      try {
        console.log(`Closing transport for session ${sessionId}`);
        await transports[sessionId].close();
        delete transports[sessionId];
      } catch (error) {
        console.error(`Error closing transport for session ${sessionId}:`, error);
      }
    }
    
    console.log('Server shutdown complete');
    process.exit(0);
  });
}

// Start the application
main().catch((error) => {
  console.error('Failed to start server:', error);
  process.exit(1);
});
