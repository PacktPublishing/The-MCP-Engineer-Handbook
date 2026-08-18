// Multi-provider schema mapping (§6.2.2) and output schema lifting (§6.2.3).
// Connects to the demo server over stdio, fetches its tools, and runs them
// through a registry of provider adapters — OpenAI Chat Completions, OpenAI
// Responses, Anthropic, Gemini, and Ollama-style — printing what each adapter
// kept, stripped, lifted, or refused. No model API keys involved: this is the
// mapping layer only. Finishes by reverse-mapping a simulated model tool call
// back to the server and executing it (§6.2.4).
import { Client, type Tool } from '@modelcontextprotocol/client';
import { StdioClientTransport } from '@modelcontextprotocol/client/stdio';

// ---------------------------------------------------------------------------
// The minimal implementation printed in §6.2.2, targeting OpenAI's Responses
// API. Reproduced verbatim except the parameter type: the book prints
// `McpTool`, the v2 SDK exports `Tool`.
// ---------------------------------------------------------------------------
function mcpToolToOpenAI(tool: Tool, serverPrefix: string) {
  return {
    type: "function",
    name: `${serverPrefix}__${tool.name}`,
    description: tool.description ?? "",
    parameters: tool.inputSchema,
  };
}

// ---------------------------------------------------------------------------
// Everything below is what that minimal function grows into: a registry of
// provider adapters, each with a per-keyword strategy — strip, lift, or fail
// fast — for the JSON Schema keywords its API doesn't accept, plus a
// per-provider output-schema decision.
// ---------------------------------------------------------------------------

type JsonObject = Record<string, unknown>;

/** What to do with a JSON Schema keyword the provider's API doesn't accept. */
type KeywordStrategy = 'strip' | 'lift' | 'fail';

interface MappingAction {
  action: 'stripped' | 'lifted' | 'inlined-ref';
  keyword: string;
  path: string;
  detail?: string;
}

class MappingError extends Error {}

interface ProviderAdapter {
  provider: string;
  /**
   * Per-keyword strategy for keywords this provider's API rejects or
   * ignores. Unlisted keywords pass through untouched. This table is data,
   * not code, because provider support matrices shift release to release —
   * the assignments below are illustrative; verify against current provider
   * docs before relying on them.
   */
  keywordStrategies: Record<string, KeywordStrategy>;
  /** Inline local $refs for APIs that don't understand references. */
  inlineLocalRefs: boolean;
  /**
   * §6.2.3: pass the output schema through natively, lift it into the
   * description, or drop it (when the model doesn't need it, lifting only
   * adds token cost). As of this writing no major API takes the field on
   * tool definitions — 'native' here illustrates the pass-through path for
   * when support lands.
   */
  outputSchema: 'native' | 'lift' | 'drop';
  /** The provider's wrapper format around name/description/schema. */
  wrap(name: string, description: string, parameters: JsonObject, outputSchema?: JsonObject): JsonObject;
}

const adapters: ProviderAdapter[] = [
  {
    provider: 'openai-chat',
    // Chat Completions passes schemas through fairly permissively, but
    // documents `default` as unsupported.
    keywordStrategies: { default: 'lift' },
    inlineLocalRefs: false,
    outputSchema: 'lift',
    wrap: (name, description, parameters) => ({
      type: 'function',
      function: { name, description, parameters },
    }),
  },
  {
    provider: 'openai-responses',
    // Strict function calling accepts only a narrow schema subset: lift the
    // validation keywords, fail fast on compositions it can't represent.
    keywordStrategies: {
      minimum: 'lift', maximum: 'lift',
      minLength: 'lift', maxLength: 'lift',
      pattern: 'lift', format: 'lift', default: 'lift',
      oneOf: 'fail',
    },
    inlineLocalRefs: false,
    outputSchema: 'native',
    // The flattened shape mcpToolToOpenAI produces, grown up.
    wrap: (name, description, parameters, outputSchema) => ({
      type: 'function',
      name,
      description,
      parameters,
      ...(outputSchema ? { output_schema: outputSchema } : {}),
    }),
  },
  {
    provider: 'anthropic',
    // Full JSON Schema 2020-12 support: nothing to strip or lift on input.
    keywordStrategies: {},
    inlineLocalRefs: false,
    outputSchema: 'lift',
    wrap: (name, description, parameters) => ({
      name,
      description,
      input_schema: parameters,
    }),
  },
  {
    provider: 'gemini',
    // Function declarations take an OpenAPI-flavored subset: no $ref, no
    // pattern. oneOf gets a crude lift — structural keywords are the hard
    // case §6.2.2 calls out; the alternatives survive only as prose.
    keywordStrategies: { pattern: 'lift', default: 'lift', oneOf: 'lift' },
    inlineLocalRefs: true,
    outputSchema: 'lift',
    wrap: (name, description, parameters) => ({
      // One entry in tools[].functionDeclarations.
      name,
      description,
      parameters,
    }),
  },
  {
    provider: 'ollama',
    // OpenAI Chat Completions wire format, but small local models handle
    // constraints unevenly — this adapter strips them, trading lost
    // information for a schema the model won't trip over.
    keywordStrategies: {
      pattern: 'strip', format: 'strip', default: 'strip', const: 'strip',
    },
    inlineLocalRefs: true,
    outputSchema: 'drop',
    wrap: (name, description, parameters) => ({
      type: 'function',
      function: { name, description, parameters },
    }),
  },
];

// --- Namespace prefixing (§6.2.2) -----------------------------------------
// Server prefix + double underscore, so the separator can't be confused with
// the word-divider underscores inside tool names.

function namespacedName(serverPrefix: string, toolName: string): string {
  return `${serverPrefix}__${toolName}`;
}

/** Reverse mapping: prefixed model tool call -> originating server + tool. */
function splitNamespacedName(name: string): { serverPrefix: string; toolName: string } {
  const sep = name.indexOf('__');
  if (sep < 1) throw new MappingError(`Tool name has no server prefix: ${name}`);
  return { serverPrefix: name.slice(0, sep), toolName: name.slice(sep + 2) };
}

// --- Schema transformation -------------------------------------------------

/**
 * 2026-07-28 obligation: never dereference $refs that resolve to a network
 * URI — a schema is data from a partially trusted source. This sample
 * supports local (#/...) references only and rejects everything else.
 */
function assertLocalRef(ref: string, path: string): void {
  if (!ref.startsWith('#')) {
    throw new MappingError(`Refusing non-local $ref "${ref}" at ${path}`);
  }
}

const MAX_DEPTH = 32; // Bound the walk: hostile schemas are a DoS vector.

/** Inline #/$defs/... references so the schema is self-contained. */
function inlineLocalRefs(node: unknown, root: JsonObject, actions: MappingAction[], path = '#', depth = 0): unknown {
  if (depth > MAX_DEPTH) throw new MappingError(`Schema exceeds depth ${MAX_DEPTH} at ${path}`);
  if (Array.isArray(node)) {
    return node.map((item, i) => inlineLocalRefs(item, root, actions, `${path}/${i}`, depth + 1));
  }
  if (node === null || typeof node !== 'object') return node;
  const obj = node as JsonObject;
  if (typeof obj.$ref === 'string') {
    assertLocalRef(obj.$ref, path);
    let target: unknown = root;
    for (const segment of obj.$ref.slice(2).split('/')) {
      target = (target as JsonObject)?.[segment];
    }
    if (target === undefined) throw new MappingError(`Unresolvable $ref "${obj.$ref}" at ${path}`);
    actions.push({ action: 'inlined-ref', keyword: '$ref', path, detail: obj.$ref });
    // 2020-12 allows keywords alongside $ref; merge them over the target.
    const { $ref, ...siblings } = obj;
    const merged = { ...(target as JsonObject), ...siblings };
    return inlineLocalRefs(merged, root, actions, path, depth + 1);
  }
  const out: JsonObject = {};
  for (const [key, value] of Object.entries(obj)) {
    if (key === '$defs') continue; // Consumed by inlining.
    out[key] = inlineLocalRefs(value, root, actions, `${path}/${key}`, depth + 1);
  }
  return out;
}

/** Human phrasing for a lifted keyword — what the model sees instead. */
function liftPhrase(keyword: string, node: JsonObject): string {
  switch (keyword) {
    case 'minimum':
      return node.maximum !== undefined
        ? `Must be between ${node.minimum} and ${node.maximum}.`
        : `Must be at least ${node.minimum}.`;
    case 'maximum':
      return `Must be at most ${node.maximum}.`;
    case 'minLength':
      return node.maxLength !== undefined
        ? `Must be ${node.minLength} to ${node.maxLength} characters.`
        : `Must be at least ${node.minLength} characters.`;
    case 'maxLength':
      return `Must be at most ${node.maxLength} characters.`;
    case 'pattern':
      return `Must match the regular expression ${node.pattern}.`;
    case 'format':
      return `Format: ${node.format}.`;
    case 'default':
      return `Defaults to ${JSON.stringify(node.default)}.`;
    case 'oneOf':
      // The crude lift for a structural keyword: alternatives as prose.
      return `Exactly one of these shapes: ${JSON.stringify(node.oneOf)}.`;
    default:
      return `${keyword}: ${JSON.stringify(node[keyword])}.`;
  }
}

function appendToDescription(node: JsonObject, text: string): void {
  node.description = node.description ? `${node.description} ${text}` : text;
}

// minimum+maximum and minLength+maxLength lift as one sentence; when the
// first of a pair is lifted, the second is silently removed with it.
const LIFT_PAIRS: Record<string, string> = { minimum: 'maximum', minLength: 'maxLength' };

/**
 * Walk the schema applying the adapter's per-keyword strategy. Mutates the
 * (already cloned) schema in place and records every action taken.
 */
function applyKeywordStrategies(node: unknown, strategies: Record<string, KeywordStrategy>, actions: MappingAction[], path = '#', depth = 0): void {
  if (depth > MAX_DEPTH) throw new MappingError(`Schema exceeds depth ${MAX_DEPTH} at ${path}`);
  if (Array.isArray(node)) {
    node.forEach((item, i) => applyKeywordStrategies(item, strategies, actions, `${path}/${i}`, depth + 1));
    return;
  }
  if (node === null || typeof node !== 'object') return;
  const obj = node as JsonObject;
  if (typeof obj.$ref === 'string') assertLocalRef(obj.$ref, path); // Even when passed through.

  for (const [keyword, strategy] of Object.entries(strategies)) {
    if (!(keyword in obj)) continue;
    switch (strategy) {
      case 'fail':
        throw new MappingError(`Schema uses "${keyword}" at ${path}, which this provider cannot represent`);
      case 'strip':
        actions.push({ action: 'stripped', keyword, path, detail: JSON.stringify(obj[keyword]) });
        delete obj[keyword];
        break;
      case 'lift': {
        const phrase = liftPhrase(keyword, obj);
        appendToDescription(obj, phrase);
        actions.push({ action: 'lifted', keyword, path, detail: phrase });
        delete obj[keyword];
        const partner = LIFT_PAIRS[keyword];
        if (partner && partner in obj && strategies[partner] === 'lift') delete obj[partner];
        if (keyword === 'oneOf' && obj.type === undefined) obj.type = 'object';
        break;
      }
    }
  }

  for (const [key, value] of Object.entries(obj)) {
    if (key === 'description' || key === 'enum' || key === 'required') continue;
    applyKeywordStrategies(value, strategies, actions, `${path}/${key}`, depth + 1);
  }
}

// --- Forward mapping: one MCP tool through one adapter ----------------------

interface MappedTool {
  definition: JsonObject;
  actions: MappingAction[];
  outputSchemaDecision: string;
}

function mapTool(adapter: ProviderAdapter, tool: Tool, serverPrefix: string): MappedTool {
  const actions: MappingAction[] = [];
  let schema = structuredClone(tool.inputSchema) as JsonObject;
  if (adapter.inlineLocalRefs) {
    schema = inlineLocalRefs(schema, schema, actions) as JsonObject;
  }
  applyKeywordStrategies(schema, adapter.keywordStrategies, actions);

  let description = tool.description ?? '';
  let outputSchemaDecision = 'none declared';
  let nativeOutput: JsonObject | undefined;
  if (tool.outputSchema) {
    switch (adapter.outputSchema) {
      case 'native':
        nativeOutput = tool.outputSchema as JsonObject;
        outputSchemaDecision = 'passed through natively';
        break;
      case 'lift':
        description += ` Returns JSON matching this schema: ${JSON.stringify(tool.outputSchema)}`;
        outputSchemaDecision = 'lifted into description';
        break;
      case 'drop':
        outputSchemaDecision = 'dropped (model does not need it; lifting would only add token cost)';
        break;
    }
  }

  const definition = adapter.wrap(namespacedName(serverPrefix, tool.name), description, schema, nativeOutput);
  return { definition, actions, outputSchemaDecision };
}

// --- Reverse mapping: model tool call -> MCP tools/call ---------------------

/** The shape a model API hands back: prefixed name, arguments as JSON text. */
interface ModelToolCall {
  name: string;
  arguments: string;
}

async function routeToolCall(call: ModelToolCall, clients: Map<string, Client>) {
  const { serverPrefix, toolName } = splitNamespacedName(call.name);
  const client = clients.get(serverPrefix);
  if (!client) throw new MappingError(`No connected server for prefix "${serverPrefix}"`);
  return client.callTool({ name: toolName, arguments: JSON.parse(call.arguments) });
}

// --- Demo -------------------------------------------------------------------

const approxTokens = (value: unknown) => Math.round(JSON.stringify(value).length / 4);

const client = new Client(
  { name: 'schema-mapping-client', version: '0.1.0' },
  { versionNegotiation: { mode: 'auto' } },
);
await client.connect(new StdioClientTransport({ command: 'node', args: ['dist/demo-server.js'] }));
console.log(`Negotiated protocol version: ${client.getNegotiatedProtocolVersion()}`);

const serverPrefix = 'demo'; // Short and meaningful: every token of it rides along on every tool name.
const { tools } = await client.listTools();
console.log(`Fetched ${tools.length} tools: ${tools.map((t) => t.name).join(', ')}\n`);

console.log('--- Minimal mapping (the §6.2.2 snippet, Responses API shape) ---');
console.log(JSON.stringify(mcpToolToOpenAI(tools[0], serverPrefix)));

for (const adapter of adapters) {
  console.log(`\n=== ${adapter.provider} ===`);
  const strategies = Object.entries(adapter.keywordStrategies)
    .map(([keyword, strategy]) => `${keyword}->${strategy}`)
    .join(', ');
  console.log(`keyword strategies: ${strategies || '(none — full JSON Schema accepted)'}`);
  console.log(`local $refs: ${adapter.inlineLocalRefs ? 'inlined' : 'passed through'}; output schema: ${adapter.outputSchema}`);

  for (const tool of tools) {
    console.log(`\n  ${tool.name} (MCP definition ~${approxTokens(tool)} tokens)`);
    try {
      const mapped = mapTool(adapter, tool, serverPrefix);
      for (const a of mapped.actions) {
        console.log(`    ${a.action} ${a.keyword} at ${a.path}${a.detail ? ` -> ${a.detail}` : ''}`);
      }
      if (tool.outputSchema) console.log(`    output schema: ${mapped.outputSchemaDecision}`);
      console.log(`    mapped (~${approxTokens(mapped.definition)} tokens): ${JSON.stringify(mapped.definition)}`);
    } catch (error) {
      if (!(error instanceof MappingError)) throw error;
      console.log(`    FAILED FAST: ${error.message}`);
    }
  }
}

console.log('\n--- Reverse mapping: routing a model tool call (§6.2.4) ---');
const dispatch = new Map<string, Client>([[serverPrefix, client]]);
const modelCall: ModelToolCall = {
  name: 'demo__get_booking',
  arguments: '{"booking_ref":"BK-7Q2M4X"}',
};
console.log(`model emitted: ${modelCall.name}(${modelCall.arguments})`);
const result = await routeToolCall(modelCall, dispatch);
console.log(`routed to server "${serverPrefix}", tool "get_booking"`);
console.log(`result: ${JSON.stringify(result.structuredContent)}`);

await client.close();
