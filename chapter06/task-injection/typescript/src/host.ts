// TaskInjectionHost — the model integration problem for long-running operations (§6.2.11).
//
// Chat-model APIs require every tool call to be immediately followed by its result, so when
// an operation outlives the model turn the host synthesizes an immediate "started" result to
// unblock the model. When the REAL result arrives later, it has nowhere natural to go. This
// host demonstrates the four known injection strategies side by side and prints the exact
// conversation-history shape each one produces.
//
// Usage: node dist/host.js [prepend|synthetic-turn|meta-tool|fresh-invocation|--all]
import { Client } from '@modelcontextprotocol/client';
import { StdioClientTransport } from '@modelcontextprotocol/client/stdio';
import { fileURLToPath } from 'node:url';
import {
  ChatMessage,
  ChatModel,
  ScriptedChatModel,
  ToolResult,
  ToolSpec,
} from './scripted-chat-model.js';

const KNOWN = ['prepend', 'synthetic-turn', 'meta-tool', 'fresh-invocation'];

const args = process.argv.slice(2);
let strategies: string[];
if (args.length === 0) strategies = ['prepend'];
else if (args.length === 1 && args[0] === '--all') strategies = KNOWN;
else if (args.length === 1 && KNOWN.includes(args[0])) strategies = [args[0]];
else {
  console.error(`Usage: node dist/host.js [${KNOWN.join('|')}|--all]`);
  process.exit(1);
}

const summaries: Array<[string, number, string]> = [];
for (const strategy of strategies) summaries.push(await runScenario(strategy));

if (summaries.length > 1) {
  console.log('='.repeat(78));
  console.log('COMPARISON — history shape produced when the real result landed');
  console.log('='.repeat(78));
  for (const [strategy, injected, shape] of summaries) {
    console.log(`  ${strategy.padEnd(17)} +${injected} messages  ${shape}`);
  }
}

async function runScenario(strategy: string): Promise<[string, number, string]> {
  console.log('='.repeat(78));
  console.log(`STRATEGY: ${strategy}`);
  console.log('='.repeat(78));

  // Connect to the demo server as a stdio child process. versionNegotiation 'auto'
  // probes server/discover to establish the modern era (the client default is the
  // legacy posture).
  const mcp = new Client(
    { name: 'task-injection-host', version: '0.1.0' },
    { versionNegotiation: { mode: 'auto' } },
  );
  await mcp.connect(new StdioClientTransport({ command: 'node', args: [resolveServerScript()] }));

  try {
    const listing = await mcp.listTools();
    const tools: ToolSpec[] = listing.tools.map((t) => ({
      name: t.name,
      description: t.description,
      inputSchema: t.inputSchema,
    }));

    // Host-side state and meta-tool for the meta-tool strategy. This tool is implemented
    // by the HOST, not the MCP server: it hands the model whatever finished while it
    // wasn't looking. runModelTurn dispatches it locally.
    const completedOps: Array<{ operationId: string; result: string }> = [];
    if (strategy === 'meta-tool') {
      tools.push({
        name: 'check_completed_operations',
        description:
          'Returns the results of long-running operations that completed since the last check.',
      });
    }

    const chat = createChatModel();
    let history: ChatMessage[] = [
      { role: 'user', text: 'Please generate the quarterly sales report.' },
    ];

    // Act 1 — the model calls start_report; the tool returns IMMEDIATELY with a handle.
    // That immediate "operation started" tool result is what keeps the conversation
    // legal: the API would reject a history in which the tool call dangles unanswered.
    // The model acknowledges and the turn ends — with the real work still running.
    const operationId = await runModelTurn(chat, history, tools, mcp, completedOps);
    if (operationId == null) throw new Error('The model did not start the report operation.');

    // The host polls for the real result on the model's behalf. (With the Tasks extension
    // this would be tasks/get instead of a status tool; the injection problem below is
    // unchanged.)
    const report = await pollForResult(mcp, operationId);
    completedOps.push({ operationId, result: report });
    let before = history.length;
    console.log(
      `\n>>> Real result for ${operationId} arrived on the host. Injecting via '${strategy}'.\n`,
    );

    // Act 2 — the real result lands and must re-enter the conversation somehow.
    let shape: string;
    switch (strategy) {
      case 'prepend': {
        // Hold the result until the NEXT real user message, then prepend it. Nothing
        // reaches the model until the user speaks again — cheap, legal, but not
        // proactive, and the result arrives wearing the user's voice.
        const nextUserText = 'Thanks. Anything in it I should flag for the board?';
        history.push({
          role: 'user',
          text: `[Result of completed operation ${operationId}]\n${report}\n---\n${nextUserText}`,
        });
        await runModelTurn(chat, history, tools, mcp, completedOps);
        shape = 'result piggybacks inside the next real user message';
        break;
      }

      case 'synthetic-turn':
        // Fabricate a turn right away and run the model on it. Proactive — the assistant
        // can announce the result unprompted — but the history now contains a user-role
        // message no user ever wrote.
        history.push({
          role: 'user',
          text: `<operation-update>Operation ${operationId} completed. Result:\n${report}</operation-update>`,
        });
        await runModelTurn(chat, history, tools, mcp, completedOps);
        shape = 'fabricated user-role turn appended immediately';
        break;

      case 'meta-tool':
        // The model pulls the result itself through a host-side tool, keeping every
        // tool call legally paired with its result — but only after something prompts
        // it to look. Here the nudge is the next user message.
        history.push({ role: 'user', text: 'Any update on the report?' });
        await runModelTurn(chat, history, tools, mcp, completedOps);
        shape = 'user nudge -> assistant tool call -> tool result -> assistant summary';
        break;

      default:
        // fresh-invocation — headless-agent style: abandon the old conversation and start
        // a new invocation with the result in its initial context. Clean history, no
        // synthesis — at the cost of everything the previous conversation knew.
        history = [
          {
            role: 'system',
            text:
              'You are a reporting agent. A long-running operation you started earlier ' +
              'has completed; summarize its result for the record.',
          },
          {
            role: 'user',
            text: `Operation ${operationId} (quarterly sales report) has completed. Result:\n${report}`,
          },
        ];
        before = 0;
        await runModelTurn(chat, history, tools, mcp, completedOps);
        shape = 'new invocation seeded with the result; original history abandoned';
        break;
    }

    printHistory(strategy, history, before);
    return [strategy, history.length - before, shape];
  } finally {
    await mcp.close();
  }
}

// One model "turn": call the chat model, execute any tool calls it makes (MCP tools via the
// Client, the meta-tool locally), feed the results back, and repeat until it answers in text.
// Returns the operationId if a start_report call happened during the turn.
async function runModelTurn(
  chat: ChatModel,
  history: ChatMessage[],
  tools: ToolSpec[],
  mcp: Client,
  completedOps: Array<{ operationId: string; result: string }>,
): Promise<string | null> {
  let operationId: string | null = null;
  while (true) {
    const reply = await chat.respond(history, tools);
    history.push(reply);
    if (!reply.toolCalls?.length) return operationId;

    const results: ToolResult[] = [];
    for (const call of reply.toolCalls) {
      let resultText: string;
      if (call.name === 'check_completed_operations') {
        resultText =
          completedOps.length === 0 ? 'No completed operations.' : JSON.stringify(completedOps);
      } else {
        const result = await mcp.callTool({ name: call.name, arguments: call.arguments });
        resultText = textOf(result.content);
        if (call.name === 'start_report') {
          operationId = JSON.parse(resultText).operationId as string;
        }
      }
      results.push({ callId: call.callId, result: resultText });
    }
    history.push({ role: 'tool', toolResults: results });
  }
}

async function pollForResult(mcp: Client, operationId: string): Promise<string> {
  while (true) {
    const result = await mcp.callTool({ name: 'get_report_result', arguments: { operationId } });
    const root = JSON.parse(textOf(result.content));
    if (root.status === 'completed') return root.report as string;
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
}

function textOf(content: unknown): string {
  return (content as Array<{ type: string; text?: string }>)
    .filter((block) => block.type === 'text')
    .map((block) => block.text ?? '')
    .join('');
}

function printHistory(label: string, history: ChatMessage[], injectedFrom: number): void {
  console.log(
    `--- Conversation history after '${label}' ` +
      `(messages ${injectedFrom + 1}+ appeared when the result landed) ---`,
  );
  const continuation = '\n' + ' '.repeat(18);
  history.forEach((message, i) => {
    const role = message.role.toUpperCase().padEnd(9);
    const lines: string[] = [];
    if (message.text) lines.push(message.text);
    for (const call of message.toolCalls ?? []) {
      lines.push(`(tool call ${call.callId}) ${call.name}(${JSON.stringify(call.arguments)})`);
    }
    for (const result of message.toolResults ?? []) {
      lines.push(`(result for ${result.callId}) ${result.result}`);
    }
    for (const line of lines) {
      console.log(`  [${i + 1}] ${role} ` + line.replaceAll('\n', continuation));
    }
  });
  console.log();
}

// Default: the deterministic scripted model (no key needed, identical output every run).
// A real provider plugs in here: return any object implementing the ChatModel interface —
// its respond() maps history to the provider's message format and ToolSpec entries to the
// provider's tool declarations.
function createChatModel(): ChatModel {
  return new ScriptedChatModel();
}

// Locate the built server next to this script, or wherever REPORT_SERVER_JS points.
function resolveServerScript(): string {
  return (
    process.env.REPORT_SERVER_JS ?? fileURLToPath(new URL('./report-server.js', import.meta.url))
  );
}
