// ResourceLinkClient — resolving resource links (§3.3.4).
//
// A tool can return a POINTER to a resource instead of its content. Most servers that
// do this expect the client to read the resource and substitute the contents into the
// tool result. This sample shows both versions from the chapter:
//
//   1. the book-page resolveLinks — the bare substitution pass, run against the
//      demo server's get_tip_of_the_day
//   2. the production version — size budget, MIME-type filtering, error handling for
//      failed reads, and a depth guard against link chains — run against
//      get_research_bundle, whose five links exercise every guard
//
// Usage: node dist/client.js

import {
  Client,
  ProtocolError,
  type ContentBlock,
  type ResourceLink,
} from '@modelcontextprotocol/client';
import { StdioClientTransport } from '@modelcontextprotocol/client/stdio';
import { existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

async function main(): Promise<void> {
  const client = new Client(
    { name: 'docs', version: '0.1.0' },
    { versionNegotiation: { mode: 'auto' } },
  );
  await client.connect(
    new StdioClientTransport({ command: 'node', args: [resolveServerScript()] }),
  );
  console.log(`(negotiated protocol version: ${client.getNegotiatedProtocolVersion()})\n`);

  // --- 1. The book-page version against a single well-behaved link -------------------

  console.log('='.repeat(78));
  console.log('1. Bare substitution pass (the §3.3.4 book extract) on get_tip_of_the_day');
  console.log('='.repeat(78));

  const tipResult = await client.callTool({ name: 'get_tip_of_the_day', arguments: {} });
  printBlocks('tool result as returned', tipResult.content ?? []);
  printBlocks('after resolveLinks', await resolveLinks(tipResult.content ?? [], client));

  // --- 2. The hardened version against links that misbehave --------------------------

  console.log('='.repeat(78));
  console.log('2. Hardened resolution on get_research_bundle (size / MIME / errors / depth)');
  console.log('='.repeat(78));

  const bundleResult = await client.callTool({ name: 'get_research_bundle', arguments: {} });
  printBlocks('tool result as returned', bundleResult.content ?? []);

  const resolver = new HardenedLinkResolver({
    maxResourceBytes: 16 * 1024, // big_dataset declares 64 000 bytes -> rejected unread
    allowedMimePrefixes: ['text/', 'application/json'], // audio/wav -> filtered out
    maxDepth: 2, // chain a -> b -> c trips the guard at hop 3
  });
  printBlocks('after hardened resolution', await resolver.resolve(bundleResult.content ?? [], client));
  await client.close();
}

// §3.3.4 book extract: the core is a substitution pass over the tool result's content
// blocks. Links must be resolved against the server that returned them — never a
// different one (the origin-binding rule from §3.3.3).
async function resolveLinks(
  content: ContentBlock[], client: Client,
): Promise<ContentBlock[]> {
  const resolved: ContentBlock[] = [];
  for (const block of content) {
    if (block.type === 'resource_link') {
      const read = await client.readResource({ uri: block.uri });
      resolved.push(...read.contents.map(contentsToBlock));
    } else {
      resolved.push(block);
    }
  }
  return resolved;
}

// A read content item back into a tool-result content block (the C# canonical converts
// through Microsoft.Extensions.AI): text -> text block, blobs -> image/audio by MIME.
function contentsToBlock(contents: { uri: string; mimeType?: string; text?: unknown; blob?: string }): ContentBlock {
  if (contents.text !== undefined) return { type: 'text', text: String(contents.text) };
  const mimeType = contents.mimeType ?? 'application/octet-stream';
  if (mimeType.startsWith('image/')) return { type: 'image', data: contents.blob ?? '', mimeType };
  if (mimeType.startsWith('audio/')) return { type: 'audio', data: contents.blob ?? '', mimeType };
  return { type: 'text', text: `[binary content: ${mimeType}]` };
}

function printBlocks(label: string, blocks: ContentBlock[]): void {
  console.log(`--- ${label} (${blocks.length} block(s)) ---`);
  const continuation = '\n' + ' '.repeat(17);
  for (const block of blocks) {
    let line: string;
    switch (block.type) {
      case 'resource_link':
        line =
          `resource_link  uri=${block.uri}  name=${block.name}` +
          (block.mimeType === undefined ? '' : `  mimeType=${block.mimeType}`) +
          (block.size === undefined ? '' : `  size=${block.size}`);
        break;
      case 'text':
        line = `text           ${truncate(block.text, 140)}`;
        break;
      case 'image':
        line = `image          mimeType=${block.mimeType}`;
        break;
      case 'audio':
        line = `audio          mimeType=${block.mimeType}`;
        break;
      default:
        line = block.type;
    }
    console.log(`  ${line.replaceAll('\n', continuation)}`);
  }
  console.log();
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

interface LinkResolutionOptions {
  /** Budget per linked resource, checked against the link's declared
   * `size` before reading and against the actual content after. */
  maxResourceBytes: number;

  /** MIME-type prefixes the target model can actually consume. */
  allowedMimePrefixes: string[];

  /** Maximum link-chain depth. Links in the tool result resolve at depth 1;
   * a link found inside resolved content resolves one level deeper. */
  maxDepth: number;
}

// What the book page leaves out (§3.3.4): a resource link can point at ANYTHING, so
// production resolution needs a size budget, MIME filtering for model compatibility,
// error handling for failed reads, and a depth guard against link chains. Where a
// guard drops a link, an explanatory text block takes its place so the model knows
// what happened instead of silently losing context.
class HardenedLinkResolver {
  constructor(private readonly options: LinkResolutionOptions) {}

  async resolve(content: ContentBlock[], client: Client): Promise<ContentBlock[]> {
    const visited = new Set<string>(); // cycle detection across the whole pass
    return this.resolveAtDepth(content, client, 1, visited);
  }

  private async resolveAtDepth(
    content: ContentBlock[], client: Client, depth: number, visited: Set<string>,
  ): Promise<ContentBlock[]> {
    const resolved: ContentBlock[] = [];
    for (const block of content) {
      if (block.type === 'resource_link') {
        resolved.push(...(await this.resolveOneLink(block, client, depth, visited)));
      } else {
        resolved.push(block);
      }
    }
    return resolved;
  }

  private async resolveOneLink(
    link: ResourceLink, client: Client, depth: number, visited: Set<string>,
  ): Promise<ContentBlock[]> {
    console.log(`  [resolve] ${link.uri} (depth ${depth})`);

    // Depth guard: link chains (and cycles) must terminate.
    if (depth > this.options.maxDepth) {
      return drop(link, `chain depth ${depth} exceeds the maximum of ${this.options.maxDepth}`);
    }
    if (visited.has(link.uri)) {
      return drop(link, 'link cycle detected — this URI was already resolved in this pass');
    }
    visited.add(link.uri);

    // Size budget, part 1: a declared size lets us reject without reading at all.
    if (link.size !== undefined && link.size > this.options.maxResourceBytes) {
      return drop(link, `declared size ${link.size} exceeds the budget of ${this.options.maxResourceBytes} bytes`);
    }

    // MIME filter, part 1: a declared type lets us skip content the model can't take.
    if (link.mimeType !== undefined && !this.mimeAllowed(link.mimeType)) {
      return drop(link, `declared MIME type '${link.mimeType}' is not model-compatible`);
    }

    // Error handling: a link is a promise the server doesn't have to keep.
    let read;
    try {
      read = await client.readResource({ uri: link.uri });
    } catch (error) {
      if (!(error instanceof ProtocolError)) throw error;
      return drop(link, `read failed: ${error.message}`);
    }

    const resolved: ContentBlock[] = [];
    for (const contents of read.contents) {
      // MIME filter and size budget, part 2: links may omit metadata, so re-check
      // what the read actually returned.
      const actualMime = contents.mimeType ?? link.mimeType;
      if (actualMime !== undefined && !this.mimeAllowed(actualMime)) {
        resolved.push(...drop(link, `content MIME type '${actualMime}' is not model-compatible`));
        continue;
      }
      const actualSize =
        'text' in contents
          ? Buffer.byteLength(String(contents.text ?? ''), 'utf8')
          : this.options.maxResourceBytes + 1; // non-text got past the filter without a type: don't inject it
      if (actualSize > this.options.maxResourceBytes) {
        resolved.push(...drop(link, `content size ${actualSize} exceeds the budget of ${this.options.maxResourceBytes} bytes`));
        continue;
      }

      // Chain convention: a read result cannot carry a resource link natively
      // (contents are text|blob only), but some servers tunnel an onward link as
      // JSON content. Follow it — that is exactly what the depth guard is for.
      if ('text' in contents && actualMime === 'application/json') {
        const json = String(contents.text ?? '');
        const onwardLink = json.includes('"resource_link"') ? tryParseLink(json) : undefined;
        if (onwardLink !== undefined) {
          resolved.push(...(await this.resolveOneLink(onwardLink, client, depth + 1, visited)));
          continue;
        }
      }

      resolved.push(contentsToBlock(contents));
    }
    return resolved;
  }

  private mimeAllowed(mimeType: string): boolean {
    return this.options.allowedMimePrefixes.some((prefix) => mimeType.startsWith(prefix));
  }
}

function tryParseLink(json: string): ResourceLink | undefined {
  try {
    const parsed: unknown = JSON.parse(json);
    if (
      typeof parsed === 'object' && parsed !== null &&
      (parsed as { type?: unknown }).type === 'resource_link' &&
      typeof (parsed as { uri?: unknown }).uri === 'string' &&
      typeof (parsed as { name?: unknown }).name === 'string'
    ) {
      return parsed as ResourceLink;
    }
    return undefined;
  } catch {
    return undefined;
  }
}

// Replace a dropped link with an explanation the model can see and act on.
function drop(link: ResourceLink, reason: string): ContentBlock[] {
  console.log(`  [guard]   ${link.uri}: ${reason}`);
  return [
    {
      type: 'text',
      text: `[resource link '${link.name}' (${link.uri}) was not resolved: ${reason}]`,
    },
  ];
}

await main();
