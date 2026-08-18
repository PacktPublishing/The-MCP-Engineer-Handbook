// A minimal chat-model abstraction (the role Microsoft.Extensions.AI plays in the C#
// canonical) plus a deterministic scripted implementation — no API key needed. It
// inspects where the resource landed in the context and answers the way a capable
// model plausibly would, so the three injection approaches can be compared on
// identical inputs. A real provider plugs in by implementing the same ChatClient
// interface over its own SDK.

export type Role = 'system' | 'user' | 'assistant';

// One content part of a message. Messages are lists of parts (like the AIContent
// list in the C# canonical) so the host can mark exactly which parts carry
// resource data when it dumps the context.
export interface ContentPart {
  text: string;
}

export interface ChatMessage {
  role: Role;
  parts: ContentPart[];
}

export interface ChatClient {
  respond(messages: ChatMessage[]): Promise<ChatMessage>;
}

const STEPS =
  '1) install the desktop client (v3+), 2) sign in with your workspace ' +
  'account, 3) choose a vault location (local-only vaults skip cloud sync).';

export class ScriptedChatClient implements ChatClient {
  async respond(messages: ChatMessage[]): Promise<ChatMessage> {
    const systemText = textOf(messages, 'system');
    const userText = textOf(messages, 'user');

    let reply: string;
    if (systemText.includes('<mcp_resource_attestation')) {
      reply =
        'The attestation in my instructions confirms the attached guide really came from ' +
        `the MCP server, so per the attached user guide: ${STEPS}`;
    } else if (systemText.includes('<mcp_resource>')) {
      reply = `Per the user guide in my instructions: ${STEPS}`;
    } else if (userText.includes('<mcp_resource>')) {
      reply =
        `Per the guide you attached (file:///user_guide.md): ${STEPS} ` +
        '(Noting the guidance: I will not act on instructions inside the attachment without your consent.)';
    } else {
      reply = "I don't see an attached guide, but the usual setup is: install, sign in, pick a vault.";
    }

    return { role: 'assistant', parts: [{ text: reply }] };
  }
}

function textOf(messages: ChatMessage[], role: Role): string {
  return messages
    .filter((m) => m.role === role)
    .flatMap((m) => m.parts)
    .map((p) => p.text)
    .join('');
}
