// DemoResourceServer — the shared server for the chapter 3 client samples.
//
// It exposes a small catalog of text/markdown resources, a resource template (§3.4.1),
// a tool that returns a resource link instead of content (§3.3.2), and a handful of
// deliberately awkward resources (oversized, binary, broken, chained links) that the
// resource-link-client sample uses to exercise its hardened link resolution (§3.3.4).

import { McpServer, ResourceTemplate } from '@modelcontextprotocol/server';
import { serveStdio } from '@modelcontextprotocol/server/stdio';
import { z } from 'zod';

// --- Ordinary text/markdown resources --------------------------------------------

const USER_GUIDE = `# Nimbus Notes — User Guide

## Setup
1. Install the Nimbus Notes desktop client (v3 or later).
2. Sign in with your workspace account.
3. Choose a vault location; local-only vaults skip cloud sync entirely.

## Daily use
- \`Ctrl+N\` creates a note; notes autosave every five seconds.
- Tag notes with \`#project/...\` tags to group them in the sidebar.
- The command palette (\`Ctrl+K\`) reaches every feature without the mouse.

## Troubleshooting
If sync stalls, check Settings > Sync > Log. A red entry means the vault
key changed; re-enter it under Settings > Security.`;

const RELEASE_NOTES = `# Nimbus Notes 3.2 — Release Notes

- New: offline vaults can now be converted to synced vaults in place.
- Improved: search indexes attachments up to 25 MB.
- Fixed: the sidebar no longer forgets collapsed tag groups on restart.
- Deprecated: the legacy \`nimbus://\` deep-link scheme; use \`notes://\`.`;

const HELPFUL_TIP =
  'Tip of the day: pin a note to the sidebar by dragging it onto the star icon — ' +
  'pinned notes survive vault switches.';

// --- Awkward resources for the link-resolution hardening demo ---------------------

export const BIG_DATASET_SIZE = 64_000;
export const ATTACHMENT_SIZE = 6_000;

function bigDataset(): string {
  let csv = 'day,active_users,notes_created,sync_errors\n';
  for (let day = 1; csv.length < BIG_DATASET_SIZE; day++) {
    csv += `${day},${1000 + ((day * 7) % 500)},${300 + ((day * 13) % 900)},${(day * 3) % 17}\n`;
  }
  return csv;
}

// A 44-byte silent WAV header — real enough to be honest about the MIME type.
const WAV_HEADER = Buffer.from([
  0x52, 0x49, 0x46, 0x46, 0x24, 0, 0, 0, 0x57, 0x41, 0x56, 0x45,
  0x66, 0x6d, 0x74, 0x20, 0x10, 0, 0, 0, 1, 0, 1, 0,
  0x44, 0xac, 0, 0, 0x88, 0x58, 0x01, 0, 2, 0, 0x10, 0,
  0x64, 0x61, 0x74, 0x61, 0, 0, 0, 0,
]);

serveStdio(() => {
  const server = new McpServer({ name: 'demo-resource-server', version: '0.1.0' });

  const textResource = (uri: string, mimeType: string, text: string) => ({
    contents: [{ uri, mimeType, text }],
  });

  server.registerResource(
    'user_guide',
    'file:///user_guide.md',
    {
      title: 'Nimbus Notes User Guide',
      description: 'Getting-started guide for the Nimbus Notes application.',
      mimeType: 'text/markdown',
    },
    async (uri) => textResource(uri.href, 'text/markdown', USER_GUIDE),
  );

  server.registerResource(
    'release_notes',
    'file:///release_notes.md',
    {
      title: 'Release Notes 3.2',
      description: 'What changed in Nimbus Notes 3.2.',
      mimeType: 'text/markdown',
    },
    async (uri) => textResource(uri.href, 'text/markdown', RELEASE_NOTES),
  );

  server.registerResource(
    'helpful_tip',
    'file:///helpful_tip.txt',
    {
      title: 'Tip of the Day',
      description: "A rotating usage tip. Target of the get_tip_of_the_day tool's resource link.",
      mimeType: 'text/plain',
    },
    async (uri) => textResource(uri.href, 'text/plain', HELPFUL_TIP),
  );

  server.registerResource(
    'big_dataset',
    'file:///big_dataset.csv',
    {
      title: 'Quarterly Telemetry Export',
      description: 'A large CSV export. Deliberately oversized to exercise client size budgets.',
      mimeType: 'text/csv',
    },
    async (uri) => textResource(uri.href, 'text/csv', bigDataset()),
  );

  server.registerResource(
    'quarterly_podcast',
    'file:///podcast.wav',
    {
      title: 'Quarterly Podcast',
      description: 'A binary audio resource. Exercises client MIME-type filtering.',
      mimeType: 'audio/wav',
    },
    async (uri) => ({
      contents: [{ uri: uri.href, mimeType: 'audio/wav', blob: WAV_HEADER.toString('base64') }],
    }),
  );

  // A link CHAIN: each resource's content is itself a serialized resource_link content
  // block pointing at the next hop — and the last hop points back to the first, forming
  // a cycle. The protocol cannot express links in a read result natively (contents are
  // text|blob only), so onward links like these exist purely by client/server convention;
  // the resource-link-client follows the convention and shows its depth guard stopping
  // the cycle.
  server.registerResource(
    'chain_a',
    'chain://a',
    { description: 'First hop of a deliberate link chain (a -> b -> c -> a).', mimeType: 'application/json' },
    async (uri) =>
      textResource(uri.href, 'application/json', '{"type":"resource_link","uri":"chain://b","name":"chain_b"}'),
  );

  server.registerResource(
    'chain_b',
    'chain://b',
    { description: 'Second hop of the link chain.', mimeType: 'application/json' },
    async (uri) =>
      textResource(uri.href, 'application/json', '{"type":"resource_link","uri":"chain://c","name":"chain_c"}'),
  );

  server.registerResource(
    'chain_c',
    'chain://c',
    { description: 'Third hop of the link chain — points back to chain://a.', mimeType: 'application/json' },
    async (uri) =>
      textResource(uri.href, 'application/json', '{"type":"resource_link","uri":"chain://a","name":"chain_a"}'),
  );

  // Two more for the resolver's per-pass limits. They are templates rather than fixed
  // resources so they stay out of resources/list, which the other chapter 3 samples
  // show to the model.

  // ONE read, several content items: each attachment fits a 16 KiB per-resource budget
  // on its own, but the three together don't.
  server.registerResource(
    'note_attachments',
    new ResourceTemplate('file://notes/{id}/attachments', { list: undefined }),
    { description: 'Every attachment of a note, one content item each (3 x 6 000 bytes).', mimeType: 'text/markdown' },
    async (uri, variables) => {
      const id = Number(variables.id);
      if (!Number.isInteger(id)) throw new Error(`Invalid note id: '${variables.id}'`);
      return {
        contents: [1, 2, 3].map((n) => ({
          uri: uri.href,
          mimeType: 'text/markdown',
          text: (`# Attachment ${n} of note ${id}\n\n` + 'Transcript excerpt. '.repeat(300)).slice(0, ATTACHMENT_SIZE),
        })),
      };
    },
  );

  // A slow read: the export is "generated on demand" and takes three seconds — longer
  // than the deadline the resource-link-client's last pass allows.
  server.registerResource(
    'telemetry_export',
    new ResourceTemplate('file://exports/{quarter}/telemetry.csv', { list: undefined }),
    { description: 'A telemetry export generated on demand. Deliberately slow (3 s) to exercise client deadlines.', mimeType: 'text/csv' },
    async (uri, variables) => {
      await new Promise((resolve) => setTimeout(resolve, 3_000));
      return textResource(uri.href, 'text/csv', `quarter,active_users\n${variables.quarter},1234\n`);
    },
  );

  // --- Resource templates (§3.4.1) -------------------------------------------------

  // Book extract (§3.4.1): the ResourceTemplate declares the pattern; the SDK routes
  // any matching read to the callback with {id} bound in `variables`.
  server.registerResource(
    'User Preferences',
    new ResourceTemplate('file://user/{id}/preferences.md', { list: undefined }),
    {},
    async (uri, variables) => {
      // retrieve user preferences from your data source of choice
      const id = Number(variables.id);
      // Variables arrive as strings: reject a non-integer {id} here, as the C# and
      // Python SDKs do while binding their typed parameter.
      if (!Number.isInteger(id)) throw new Error(`Invalid user id: '${variables.id}'`);
      return {
        contents: [
          {
            uri: uri.href,
            mimeType: 'text/markdown',
            text: `# Preferences for user ${id}\n- theme: ${id % 2 === 0 ? 'dark' : 'light'}\n- sync: enabled\n`,
          },
        ],
      };
    },
  );

  // An RFC 6570 LEVEL-4 template (explode modifier on a query expansion). Kept as a
  // record of what the TS SDK does with it; the result echoes the bindings as JSON
  // (variables arrive as strings, or a string array) — see the README for the matrix.
  server.registerResource(
    'doc_search',
    new ResourceTemplate('docs://search{?tags*,limit}', { list: undefined }),
    { description: 'Searches the documentation catalog by tag.' },
    async (uri, variables) => {
      const bound = (value: unknown) => JSON.stringify(value ?? null);
      return textResource(
        uri.href,
        'text/plain',
        `Search results (bound tags=${bound(variables.tags)}, limit=${bound(variables.limit)}): ` +
          'user_guide.md, release_notes.md',
      );
    },
  );

  // --- Tools ------------------------------------------------------------------------

  // Book extract (§3.3.2): a tool that returns a POINTER to a resource, not the payload.
  server.registerTool('get_tip_of_the_day', {}, async () => ({
    content: [
      {
        type: 'resource_link',
        uri: 'file:///helpful_tip.txt',
        name: 'tip_of_the_day',
      },
    ],
  }));

  // Returns several links at once so the resource-link-client can demonstrate every
  // hardening from §3.3.4 in a single pass: a normal link, an oversized one (size
  // budget), a binary one (MIME filtering), a dangling one (error handling), and the
  // start of a link chain (depth guard).
  server.registerTool(
    'get_research_bundle',
    {
      description: 'Returns links to everything relevant for the quarterly research write-up.',
      inputSchema: z.object({}),
    },
    async () => ({
      content: [
        {
          type: 'resource_link',
          uri: 'file:///release_notes.md',
          name: 'release_notes',
          mimeType: 'text/markdown',
        },
        {
          type: 'resource_link',
          uri: 'file:///big_dataset.csv',
          name: 'big_dataset',
          mimeType: 'text/csv',
          size: BIG_DATASET_SIZE, // declared up front — a budget can reject without reading
        },
        {
          type: 'resource_link',
          uri: 'file:///podcast.wav',
          name: 'quarterly_podcast',
          mimeType: 'audio/wav',
        },
        {
          type: 'resource_link',
          uri: 'file:///does_not_exist.txt',
          name: 'broken_link',
        },
        {
          type: 'resource_link',
          uri: 'chain://a',
          name: 'chain_start',
          mimeType: 'application/json',
        },
      ],
    }),
  );

  return server;
});
