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
//      get_research_bundle, whose five links exercise those guards
//   3. the same resolver's per-pass limits — byte budget, read cap, deadline — plus
//      cycle-vs-duplicate handling, against a tool result shaped the way a careless or
//      hostile server might return it
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

  // --- 3. Per-pass limits against a tool result that misbehaves as a whole -----------

  console.log('='.repeat(78));
  console.log('3. Per-pass limits on a misbehaving tool result (duplicates / understated size /');
  console.log('   cycle / pass byte budget / read cap)');
  console.log('='.repeat(78));

  // The demo server's tools are well-behaved, so this tool result is built client-side,
  // the way a careless or hostile server could return it. Every read still goes to the
  // real server.
  const misbehaving: ContentBlock[] = [
    { type: 'resource_link', uri: 'file:///release_notes.md', name: 'release_notes' },
    { type: 'resource_link', uri: 'file:///release_notes.md', name: 'release_notes' }, // duplicate, not a cycle
    { type: 'resource_link', uri: 'file:///big_dataset.csv', name: 'big_dataset', size: 2048 }, // understated
    { type: 'resource_link', uri: 'chain://a', name: 'chain_a' }, // a -> b -> c -> a
    { type: 'resource_link', uri: 'file:///user_guide.md', name: 'user_guide' },
    ...[1, 2, 3, 4, 5].map((id): ContentBlock => ({
      type: 'resource_link', uri: `file://user/${id}/preferences.md`, name: `preferences_${id}`,
    })),
  ];
  printBlocks('tool result as returned', misbehaving);

  const strictResolver = new HardenedLinkResolver({
    maxResourceBytes: 16 * 1024, // big_dataset's real 64 015 bytes -> rejected after reading
    maxTotalBytes: 1024, // user_guide (571 bytes) no longer fits the pass budget
    maxReads: 8, // the last preference links are never read
    allowedMimePrefixes: ['text/', 'application/json'],
    maxDepth: 4, // deep enough that the chain's return to a is seen as a cycle
  });
  printBlocks('after hardened resolution', await strictResolver.resolve(misbehaving, client));
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
  /** Budget per linked resource, checked against the link's declared `size` before
   * reading and against the actual bytes of every content item the read returned after. */
  maxResourceBytes: number;

  /** Budget for the whole pass: bytes accepted from all reads made while resolving
   * one tool result. Default 64 KiB. */
  maxTotalBytes?: number;

  /** Maximum `resources/read` calls per pass, however many links the tool result (or
   * the chains it starts) contains. Default 16. */
  maxReads?: number;

  /** Deadline for the whole pass, in milliseconds. A read still in flight when it
   * passes is cancelled. Default 30 000. */
  timeoutMs?: number;

  /** MIME-type prefixes the target model can actually consume. */
  allowedMimePrefixes: string[];

  /** Maximum link-chain depth. Links in the tool result resolve at depth 1;
   * a link found inside resolved content resolves one level deeper. */
  maxDepth: number;
}

// State for ONE resolution pass (one tool result). The budgets live here rather than
// per link, so a result carrying thousands of small links is bounded as a whole.
interface ResolutionPass {
  deadline: AbortSignal; // aborts when the pass's time is up
  activePath: Set<string>; // URIs on the chain being followed now
  seen: Set<string>; // every URI this pass has started on
  bytes: number; // bytes accepted from reads so far
  reads: number; // resources/read calls issued so far
}

// What the book page leaves out (§3.3.4): a resource link can point at ANYTHING, so
// production resolution needs size budgets, MIME filtering for model compatibility,
// error handling for failed reads, and guards that make link chains and link-heavy
// results terminate. Where a guard drops a link, an explanatory text block takes its
// place so the model knows what happened instead of silently losing context.
class HardenedLinkResolver {
  private readonly maxTotalBytes: number;
  private readonly maxReads: number;
  private readonly timeoutMs: number;

  constructor(private readonly options: LinkResolutionOptions) {
    this.maxTotalBytes = options.maxTotalBytes ?? 64 * 1024;
    this.maxReads = options.maxReads ?? 16;
    this.timeoutMs = options.timeoutMs ?? 30_000;
  }

  async resolve(content: ContentBlock[], client: Client): Promise<ContentBlock[]> {
    const pass: ResolutionPass = {
      deadline: AbortSignal.timeout(this.timeoutMs),
      activePath: new Set(),
      seen: new Set(),
      bytes: 0,
      reads: 0,
    };
    return this.resolveAtDepth(content, client, 1, pass);
  }

  private async resolveAtDepth(
    content: ContentBlock[], client: Client, depth: number, pass: ResolutionPass,
  ): Promise<ContentBlock[]> {
    const resolved: ContentBlock[] = [];
    for (const block of content) {
      if (block.type === 'resource_link') {
        resolved.push(...(await this.resolveOneLink(block, client, depth, pass)));
      } else {
        resolved.push(block);
      }
    }
    return resolved;
  }

  private async resolveOneLink(
    link: ResourceLink, client: Client, depth: number, pass: ResolutionPass,
  ): Promise<ContentBlock[]> {
    console.log(`  [resolve] ${link.uri} (depth ${depth})`);

    // Depth guard: link chains must terminate.
    if (depth > this.options.maxDepth) {
      return drop(link, `chain depth ${depth} exceeds the maximum of ${this.options.maxDepth}`);
    }

    // Cycle vs. duplicate: only a URI on the chain currently being followed is a
    // cycle. Two links to the same URI side by side, or two chains converging on one
    // resource, are ordinary repetition: point at the earlier result, don't re-read.
    if (pass.activePath.has(link.uri)) {
      return drop(link, 'link cycle detected — this URI is already being resolved further up the chain');
    }
    if (pass.seen.has(link.uri)) {
      return duplicate(link);
    }
    pass.seen.add(link.uri);

    pass.activePath.add(link.uri);
    try {
      return await this.readAndSubstitute(link, client, depth, pass);
    } finally {
      pass.activePath.delete(link.uri);
    }
  }

  private async readAndSubstitute(
    link: ResourceLink, client: Client, depth: number, pass: ResolutionPass,
  ): Promise<ContentBlock[]> {
    // Size budget, part 1: a declared size lets us reject without reading at all.
    if (link.size !== undefined && link.size > this.options.maxResourceBytes) {
      return drop(link, `declared size ${link.size} exceeds the budget of ${this.options.maxResourceBytes} bytes`);
    }

    // MIME filter, part 1: a declared type lets us skip content the model can't take.
    if (link.mimeType !== undefined && !this.mimeAllowed(link.mimeType)) {
      return drop(link, `declared MIME type '${link.mimeType}' is not model-compatible`);
    }

    // Read cap and deadline: bound the pass as a whole, however many links it holds.
    if (pass.reads >= this.maxReads) {
      return drop(link, `read limit of ${this.maxReads} per pass reached`);
    }
    if (pass.deadline.aborted) {
      return drop(link, `pass deadline of ${this.timeoutMs / 1000}s has passed`);
    }
    pass.reads++;

    // Error handling: a link is a promise the server doesn't have to keep.
    let read;
    try {
      read = await client.readResource({ uri: link.uri }, { signal: pass.deadline });
    } catch (error) {
      if (pass.deadline.aborted) {
        return drop(link, `read cancelled: pass deadline of ${this.timeoutMs / 1000}s has passed`);
      }
      if (!(error instanceof ProtocolError)) throw error;
      return drop(link, `read failed: ${error.message}`);
    }

    // Size budget, part 2: links may omit or understate their size, so count what the
    // read actually returned — across ALL its content items, since one resource can
    // return many. The response is already in memory by now: this bounds what
    // reaches the model, not what the transport buffers.
    const readBytes = read.contents.reduce((total, contents) => total + contentBytes(contents), 0);
    if (readBytes > this.options.maxResourceBytes) {
      return drop(link, `content size ${readBytes}${declared(link)} exceeds the budget of ${this.options.maxResourceBytes} bytes`);
    }
    if (pass.bytes + readBytes > this.maxTotalBytes) {
      return drop(link, `content size ${readBytes} would exceed the pass budget of ${this.maxTotalBytes} bytes (${pass.bytes} already used)`);
    }
    pass.bytes += readBytes;

    const resolved: ContentBlock[] = [];
    for (const contents of read.contents) {
      // MIME filter, part 2: links may omit metadata, so re-check every item.
      const actualMime = contents.mimeType ?? link.mimeType;
      if (actualMime !== undefined && !this.mimeAllowed(actualMime)) {
        resolved.push(...drop(link, `content MIME type '${actualMime}' is not model-compatible`));
        continue;
      }
      if (actualMime === undefined && !('text' in contents)) {
        resolved.push(...drop(link, 'binary content without a MIME type'));
        continue;
      }

      // Chain convention: a read result cannot carry a resource link natively
      // (contents are text|blob only), but some servers tunnel an onward link as
      // JSON content. Follow it — that is exactly what the depth guard is for.
      if ('text' in contents && actualMime === 'application/json') {
        const json = String(contents.text ?? '');
        const onwardLink = json.includes('"resource_link"') ? tryParseLink(json) : undefined;
        if (onwardLink !== undefined) {
          resolved.push(...(await this.resolveOneLink(onwardLink, client, depth + 1, pass)));
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

function contentBytes(contents: { text?: unknown; blob?: unknown }): number {
  if ('text' in contents) return Buffer.byteLength(String(contents.text ?? ''), 'utf8');
  if (typeof contents.blob === 'string') return Buffer.byteLength(contents.blob, 'base64'); // decoded size
  return 0;
}

function declared(link: ResourceLink): string {
  return link.size !== undefined ? ` (the link declared ${link.size})` : '';
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

// A repeated link is not an error: refer the model to the earlier occurrence.
function duplicate(link: ResourceLink): ContentBlock[] {
  console.log(`  [dedupe]  ${link.uri}: already handled earlier in this pass`);
  return [
    {
      type: 'text',
      text:
        `[resource link '${link.name}' (${link.uri}) repeats an earlier link in this result; ` +
        'see its content, or why it was not resolved, above]',
    },
  ];
}

await main();
