// ModelResourceClient — Pattern 3 (§3.3.3): model-controlled resource access via
// tool wrappers.
//
// Two host-side tools — list_resources and read_resource — give resources the
// model-native integration point the protocol doesn't define. The list tool aggregates
// across ALL connected servers, tagging each entry with a host-assigned server name;
// the read tool routes a read to the right server by that name. To prove the
// aggregation, this host spawns the SAME demo server twice under different labels
// ("docs" and "wiki") — same catalog, distinct routing keys.
//
// Usage: node dist/host.js

import { existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { ClientManager, ResourceTools } from './resource-tools.js';
import { ChatMessage, ScriptedChatClient } from './scripted-chat-client.js';

const clientManager = new ClientManager();
for (const serverName of ['docs', 'wiki']) {
  // host-assigned label; routing never trusts the server's self-reported name
  await clientManager.connect(serverName, { command: 'node', args: [resolveServerScript()] });
}
console.log(
  `(negotiated protocol version: ${clientManager
    .getClient('docs')
    .getNegotiatedProtocolVersion()})\n`,
);

// Wrap the two methods as model-callable functions. Describing what an MCP *resource*
// is (not a file, not an HTTP URL) is what makes the model use these well — see §3.3.3.
const resourceTools = new ResourceTools(clientManager);
const tools: Array<{
  name: string;
  description: string;
  invoke: (args: Record<string, unknown>) => Promise<string>;
}> = [
  {
    name: 'list_resources',
    description:
      'Lists available MCP resources from all connected servers. Resources are curated ' +
      'context items (documents, reference material, configuration) identified by a URI ' +
      'that is only meaningful to the server that owns it — it is not a file path or web URL. ' +
      'Each entry includes the serverName needed to read it.',
    invoke: async () => JSON.stringify(await resourceTools.listResources(), null, 2),
  },
  {
    name: 'read_resource',
    description:
      'Reads one MCP resource and returns its content. Pass the serverName and uri exactly ' +
      'as returned by list_resources; a URI is only valid on the server it was listed from.',
    invoke: async (args) =>
      resourceTools.readResource(String(args.serverName), String(args.uri)),
  },
];

const chat = new ScriptedChatClient();
const history: ChatMessage[] = [
  {
    role: 'user',
    text:
      'What reference material do we have across the connected servers? ' +
      'Then show me the release notes from the wiki server.',
  },
];

// The standard tool loop: run the model, execute its tool calls (locally — these are
// host tools, not MCP server tools), feed results back, repeat until it answers in text.
while (true) {
  const reply = await chat.respond(history);
  history.push(reply);
  if (!reply.toolCalls?.length) break;

  const results = [];
  for (const call of reply.toolCalls) {
    const tool = tools.find((t) => t.name === call.name)!;
    results.push({ callId: call.callId, result: await tool.invoke(call.arguments) });
  }
  history.push({ role: 'tool', toolResults: results });
}

printHistory(history);
await clientManager.closeAll();

function printHistory(history: ChatMessage[]): void {
  console.log('='.repeat(78));
  console.log('CONVERSATION — resources reached the model through the wrapper tools');
  console.log('='.repeat(78));
  const continuation = '\n' + ' '.repeat(18);
  history.forEach((message, i) => {
    const role = message.role.toUpperCase().padEnd(9);
    const lines: string[] = [];
    if (message.text) lines.push(truncate(message.text, 320));
    for (const call of message.toolCalls ?? []) {
      lines.push(`(tool call ${call.callId}) ${call.name}(${JSON.stringify(call.arguments)})`);
    }
    for (const result of message.toolResults ?? []) {
      lines.push(`(result for ${result.callId}) ${truncate(result.result, 320)}`);
    }
    for (const line of lines) {
      console.log(`  [${i + 1}] ${role} ` + line.replaceAll('\n', continuation));
    }
  });
}

function truncate(text: string, max: number): string {
  return text.length <= max ? text : `${text.slice(0, max)}… (${text.length} chars total)`;
}

// Locate the built demo server next to this sample. Build (npm install && npm run
// build in) chapter03/demo-resource-server/typescript first.
function resolveServerScript(): string {
  const override = process.env.DEMO_RESOURCE_SERVER_JS;
  if (override) return override;

  const candidate = fileURLToPath(
    new URL('../../../demo-resource-server/typescript/dist/server.js', import.meta.url),
  );
  if (!existsSync(candidate)) {
    throw new Error(
      `server.js not found at ${candidate}. Run 'npm install && npm run build' in ` +
        'chapter03/demo-resource-server/typescript first, or point DEMO_RESOURCE_SERVER_JS at the built server.',
    );
  }
  return candidate;
}
