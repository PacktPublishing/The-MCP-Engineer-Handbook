#!/usr/bin/env node

/**
 * MCP Client with Ollama Integration - HTTP Transport
 *
 * This is a simple MCP client that connects to an MCP server and integrates with
 * Ollama to provide AI-powered chat with tool calling capabilities.
 */

import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StreamableHTTPClientTransport } from '@modelcontextprotocol/sdk/client/streamableHttp.js';
import { Ollama, Tool, Message, ToolCall } from 'ollama';
import * as readline from 'node:readline/promises';
import { stdin as input, stdout as output } from 'node:process';

/**
 * Main client application
 */
async function main(): Promise<void> {
  // Set up MCP client with HTTP transport
  const transport = new StreamableHTTPClientTransport(
    new URL('http://localhost:5000/mcp')
  );

  const mcpClient = new Client(
    {
      name: 'ollama-mcp-client',
      version: '1.0.0',
    },
    {
      capabilities: {},
    }
  );

  try {
    // Connect to MCP server
    await mcpClient.connect(transport);

    // List available tools from the MCP server
    const toolsResult = await mcpClient.listTools();
    const mcpTools = toolsResult.tools;

    console.log('Connected to MCP server with tools:');
    for (const tool of mcpTools) {
      console.log(`  - ${tool.name}: ${tool.description}`);
    }
    console.log();

    // Convert MCP tools to Ollama tool format
    const ollamaTools: Tool[] = mcpTools.map((tool) => ({
      type: 'function',
      function: {
        name: tool.name,
        description: tool.description || '',
        parameters: tool.inputSchema as Record<string, unknown>,
      },
    }));

    // Set up the Ollama client
    const model = 'qwen3:1.7b';
    const ollama = new Ollama({ host: 'http://127.0.0.1:11434' });

    // Set up readline interface for user input
    const rl = readline.createInterface({ input, output });

    // Start the conversation
    const messages: Message[] = [];

    // Main conversation loop
    while (true) {
      try {
        // Get user prompt
        const userPrompt = await rl.question('Your prompt:\n');

        if (!userPrompt.trim()) {
          continue;
        }

        // Add user message to history
        messages.push({
          role: 'user',
          content: userPrompt,
        });

        // Stream the AI response
        console.log('AI Response:');
        let fullResponse = '';
        let toolCallsList: ToolCall[] | undefined;

        // Call Ollama with tools
        const response = await ollama.chat({
          model,
          messages,
          tools: ollamaTools,
          stream: true,
        });

        // Process streaming response
        for await (const chunk of response) {
          if (chunk.message.content) {
            process.stdout.write(chunk.message.content);
            fullResponse += chunk.message.content;
          }

          // Capture tool calls (usually in the last chunk)
          if (chunk.message.tool_calls) {
            toolCallsList = chunk.message.tool_calls;
          }
        }

        console.log(); // New line after response

        // Handle tool calls after streaming is complete
        if (toolCallsList && toolCallsList.length > 0) {
          // Add assistant message with tool calls
          messages.push({
            role: 'assistant',
            content: fullResponse,
            tool_calls: toolCallsList,
          });

          for (const toolCall of toolCallsList) {
            const toolName = toolCall.function.name;
            const toolArgs = toolCall.function.arguments as Record<string, unknown>;

            console.log(`[Calling tool: ${toolName}]`);

            try {
              // Call the tool through MCP
              const result = await mcpClient.callTool({
                name: toolName,
                arguments: toolArgs,
              });

              // Extract text content from result
              let toolResult = '';
              const contents = Array.isArray(result.content) ? result.content : [];
              for (const content of contents) {
                if (content.type === 'text') {
                  toolResult += content.text;
                }
              }

              console.log(`[Tool result: ${toolResult}]`);

              // Add tool result to messages
              messages.push({
                role: 'tool',
                content: toolResult,
              });
            } catch (error) {
              const errorMessage = error instanceof Error ? error.message : String(error);
              console.error(`[Error calling tool: ${errorMessage}]`);
            }
          }

          // Get final response from Ollama after tool calls
          console.log('AI Response:');
          const finalResponse = await ollama.chat({
            model,
            messages,
            tools: ollamaTools,
            stream: true,
          });

          let fullFinal = '';
          for await (const finalChunk of finalResponse) {
            if (finalChunk.message.content) {
              process.stdout.write(finalChunk.message.content);
              fullFinal += finalChunk.message.content;
            }
          }

          // Add final assistant message
          messages.push({
            role: 'assistant',
            content: fullFinal,
          });
          console.log();
        } else {
          // No tool calls, just add the assistant response
          messages.push({
            role: 'assistant',
            content: fullResponse,
          });
        }

        console.log(); // Extra line for spacing
      } catch (error) {
        const errorMessage = error instanceof Error ? error.message : String(error);
        console.error(`Error: ${errorMessage}`);
        console.log();
      }
    }
  } catch (error) {
    const errorMessage = error instanceof Error ? error.message : String(error);
    console.error(`Failed to connect to MCP server: ${errorMessage}`);
    process.exit(1);
  }
}

// Start the client
main().catch((error) => {
  console.error('Fatal error:', error);
  process.exit(1);
});
