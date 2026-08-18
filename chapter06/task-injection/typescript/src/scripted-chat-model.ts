// A minimal chat-model abstraction (the role Microsoft.Extensions.AI plays in the C#
// canonical) plus a deterministic scripted implementation — no API key needed. The
// scripted model pattern-matches the incoming history and replies with the turn a
// capable tool-calling model would plausibly produce, so the four injection strategies
// can be compared on identical conversations. A real provider plugs in by implementing
// the same ChatModel interface over its own SDK (see createChatModel in host.ts).

export type Role = 'system' | 'user' | 'assistant' | 'tool';

export interface ToolCall {
  callId: string;
  name: string;
  arguments: Record<string, unknown>;
}

export interface ToolResult {
  callId: string;
  result: string;
}

export interface ChatMessage {
  role: Role;
  text?: string;
  toolCalls?: ToolCall[];
  toolResults?: ToolResult[];
}

export interface ToolSpec {
  name: string;
  description?: string;
  inputSchema?: unknown;
}

export interface ChatModel {
  respond(history: ChatMessage[], tools: ToolSpec[]): Promise<ChatMessage>;
}

const SUMMARY_LINE =
  'Revenue is up 14% quarter over quarter, driven by the new subscription tier; ' +
  'two regions missed target (details in the appendix).';

export class ScriptedChatModel implements ChatModel {
  private callCounter = 0;

  async respond(history: ChatMessage[], tools: ToolSpec[]): Promise<ChatMessage> {
    const last = history[history.length - 1];

    // A tool result just came back — react to it.
    const toolResult = last.role === 'tool' ? last.toolResults?.at(-1) : undefined;
    if (toolResult) {
      const callName = findCallName(history, toolResult.callId);
      const resultText = toolResult.result;
      switch (callName) {
        // The synthesized-immediate "operation started" result: acknowledge and move on.
        case 'start_report':
          return text(
            "I've started generating the report. It's running in the background — " +
              "I'll share the results as soon as they're available.",
          );

        // meta-tool strategy: the meta-tool returned completed work (or nothing).
        case 'check_completed_operations':
          return text(
            resultText.includes('No completed')
              ? "Nothing has finished yet — I'll keep an eye on it."
              : `Yes — the report just finished. ${SUMMARY_LINE}`,
          );

        default:
          return text(`Tool '${callName}' returned: ${resultText}`);
      }
    }

    const lastText = last.text ?? '';

    // prepend strategy: the completed result is riding along inside a real user message.
    if (lastText.includes('[Result of completed operation')) {
      return text(
        `Good news first: the quarterly sales report finished. ${SUMMARY_LINE} ` +
          "To answer your question: I'd flag the two underperforming regions for the board.",
      );
    }

    // synthetic-turn strategy: a fabricated turn is announcing the result.
    if (lastText.includes('<operation-update>')) {
      return text(`Update: the quarterly sales report is ready. ${SUMMARY_LINE}`);
    }

    // fresh-invocation strategy: a brand-new run seeded with the result.
    if (lastText.includes('has completed. Result:')) {
      return text(`For the record: the operation completed successfully. ${SUMMARY_LINE}`);
    }

    // meta-tool strategy: the user nudges, so check for finished work.
    if (lastText.toLowerCase().includes('any update') && hasTool(tools, 'check_completed_operations')) {
      return this.call('check_completed_operations', {});
    }

    // Opening user request: kick off the long-running report.
    if (last.role === 'user' && lastText.toLowerCase().includes('report') && hasTool(tools, 'start_report')) {
      return this.call('start_report', { topic: 'quarterly sales' });
    }

    return text('(scripted model has no line for this input)');
  }

  private call(name: string, args: Record<string, unknown>): ChatMessage {
    return {
      role: 'assistant',
      toolCalls: [{ callId: `call-${++this.callCounter}`, name, arguments: args }],
    };
  }
}

function findCallName(history: ChatMessage[], callId: string): string {
  for (let i = history.length - 1; i >= 0; i--) {
    const calls = history[i].toolCalls ?? [];
    for (let j = calls.length - 1; j >= 0; j--) {
      if (calls[j].callId === callId) return calls[j].name;
    }
  }
  return '(unknown)';
}

function hasTool(tools: ToolSpec[], name: string): boolean {
  return tools.some((tool) => tool.name === name);
}

function text(value: string): ChatMessage {
  return { role: 'assistant', text: value };
}
