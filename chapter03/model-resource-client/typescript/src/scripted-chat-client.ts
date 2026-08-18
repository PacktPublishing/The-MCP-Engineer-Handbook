// A minimal tool-calling chat-model abstraction (the role Microsoft.Extensions.AI
// plays in the C# canonical) plus a deterministic scripted implementation — no API
// key needed. It plays the model's side of Pattern 3: discover what resources exist
// via list_resources, then route a read to the right server via read_resource. Swap
// in any real provider by implementing the same ChatClient interface over its SDK.

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

export interface ChatClient {
  respond(history: ChatMessage[]): Promise<ChatMessage>;
}

export class ScriptedChatClient implements ChatClient {
  private callCounter = 0;

  async respond(history: ChatMessage[]): Promise<ChatMessage> {
    const last = history[history.length - 1];

    const toolResult = last.role === 'tool' ? last.toolResults?.at(-1) : undefined;
    if (toolResult) {
      const callName = findCallName(history, toolResult.callId);
      const resultText = toolResult.result;

      // The aggregated catalog came back; every entry carries its serverName.
      // The user asked for the wiki server's release notes — route the read there.
      if (callName === 'list_resources' && resultText.includes('"wiki"')) {
        return this.call('read_resource', {
          serverName: 'wiki',
          uri: 'file:///release_notes.md',
        });
      }

      if (callName === 'read_resource') {
        return text(
          'Both servers expose the same catalog: a user guide, release notes, a tip of ' +
            "the day, plus telemetry and podcast material. From the wiki server's release " +
            'notes: offline vaults can now be converted to synced vaults in place, search ' +
            'now indexes attachments up to 25 MB, and the legacy nimbus:// scheme is deprecated.',
        );
      }

      return text(`Tool '${callName}' returned: ${resultText}`);
    }

    // Opening user request: discover the catalog first.
    if (last.role === 'user') return this.call('list_resources', {});

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

function text(value: string): ChatMessage {
  return { role: 'assistant', text: value };
}
