#!/usr/bin/env node

/**
 * MCP Server with Echo Tool - Stdio Transport
 *
 * This is a simple MCP server that provides an "echo" tool via stdio transport.
 * It demonstrates the basic structure of an MCP server in TypeScript.
 */

import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { z } from 'zod';

// Define the input schema for the echo tool
const echoSchema = z.object({
  message: z.string().describe('The message to echo back'),
  uppercase: z.boolean().optional().default(false).describe('Whether to uppercase the message'),
});

/**
 * Main server class
 */
class EchoServer {
  private server: McpServer;

  constructor() {
    this.server = new McpServer(
      {
        name: 'echo-server',
        version: '1.0.0',
      },
      {
        capabilities: {
          tools: {},
        },
      }
    );

    this.setupTools();
    this.setupErrorHandling();
  }

  /**
   * Set up the echo tool
   */
  private setupTools(): void {
    this.server.tool(
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
  }

  /**
   * Set up error handling and shutdown
   */
  private setupErrorHandling(): void {
    this.server.server.onerror = (error: Error) => {
      console.error('[MCP Error]', error);
    };

    process.on('SIGINT', async () => {
      console.error('\nShutting down server...');
      await this.server.close();
      process.exit(0);
    });
  }

  /**
   * Run the server
   */
  async run(): Promise<void> {
    const transport = new StdioServerTransport();
    await this.server.connect(transport);
    console.error('Echo MCP server is running on stdio...');
  }
}

// Start the server
const server = new EchoServer();
server.run().catch((error) => {
  console.error('Failed to start server:', error);
  process.exit(1);
});
