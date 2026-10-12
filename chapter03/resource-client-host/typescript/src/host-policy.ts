// Host-side policy for the actions the host lets the model request. There is one,
// share_resource(uri, destination), and the host only shares with destinations on its
// allowlist. Nothing in the context changes that: not the resource's text, not the
// provenance tags, not how confident the model sounds. A real host advertises the
// action as a tool and executes it only after authorize allows it.

import type { ToolCall } from './scripted-chat-client.js';

const ALLOWED_SHARE_HOSTS = new Set(['notes.nimbus.example']);

export function authorize(call: ToolCall): string {
  if (call.name !== 'share_resource') {
    return `DENIED ${describe(call)}: not an action this host offers.`;
  }

  const target = URL.parse(String(call.arguments.destination ?? ''));
  if (target?.protocol !== 'https:' || !ALLOWED_SHARE_HOSTS.has(target.hostname)) {
    return `DENIED ${describe(call)}: destination is not on the share allowlist.`;
  }

  return `ALLOWED ${describe(call)}`;
}

export function describe(call: ToolCall): string {
  return `${call.name}(${Object.entries(call.arguments).map(([k, v]) => `${k}: ${v}`).join(', ')})`;
}
