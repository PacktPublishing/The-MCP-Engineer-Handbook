import { fromJsonSchema, ProtocolError, ProtocolErrorCode, Server } from '@modelcontextprotocol/server';
import { serveStdio } from '@modelcontextprotocol/server/stdio';

const messageSchema = (description: string) => ({
  type: 'object' as const,
  properties: {
    message: { type: 'string', description },
  },
  required: ['message'],
});

// One list feeds both tools/list and argument validation in tools/call
const tools = [
  {
    name: 'echo',
    description: 'Echo back the provided message',
    inputSchema: messageSchema('The message to echo back'),
  },
  {
    name: 'reverse',
    description: 'Reverse the provided message',
    inputSchema: messageSchema('The message to reverse'),
  },
  {
    name: 'shout',
    description: 'Return the provided message in uppercase',
    inputSchema: messageSchema('The message to shout'),
  },
];
// fromJsonSchema wraps a JSON Schema in the SDK's default validator (Ajv on Node)
const validators = new Map(tools.map((tool) => [tool.name, fromJsonSchema<{ message: string }>(tool.inputSchema)]));

// serveStdio owns the era decision: modern (2026-07-28) openings get a modern
// instance, legacy openings a legacy one — the same factory serves both.
serveStdio(() => {
  const server = new Server(
    { name: 'low-level-handlers-server', version: '1.0.0' },
    { capabilities: { tools: {} } }
  );

  // Register a handler for tools/list
  server.setRequestHandler('tools/list', async () => ({ tools }));

  // Register a handler for tools/call. The low-level Server dispatches whatever
  // arrives: rejecting unknown tools and arguments that don't match the
  // advertised schema is now this handler's job.
  server.setRequestHandler('tools/call', async (request) => {
    const { name, arguments: args = {} } = request.params;

    const validator = validators.get(name);
    if (!validator) {
      // Unknown tool: a protocol error, as the high-level API reports it
      throw new ProtocolError(ProtocolErrorCode.InvalidParams, `Unknown tool: ${name}`);
    }
    const checked = await validator['~standard'].validate(args);
    if (checked.issues) {
      // Invalid arguments: a tool error the model can read and correct
      const details = checked.issues.map((issue) => issue.message).join('; ');
      return {
        content: [{ type: 'text' as const, text: `Invalid arguments for tool ${name}: ${details}` }],
        isError: true,
      };
    }

    const { message } = checked.value;
    switch (name) {
      case 'echo':
        return { content: [{ type: 'text' as const, text: `Echo: ${message}` }] };
      case 'reverse':
        return { content: [{ type: 'text' as const, text: [...message].reverse().join('') }] };
      default:
        return { content: [{ type: 'text' as const, text: message.toUpperCase() }] };
    }
  });

  return server;
});
