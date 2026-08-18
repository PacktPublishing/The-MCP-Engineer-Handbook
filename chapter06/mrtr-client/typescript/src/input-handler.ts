// handleInputRequest: the client host's side of an elicitation.
//
// Two concerns from Chapter 6, Section 6.2.5:
//   1. Form rendering — a schema-driven console form with labels, help text,
//      defaults, the accept/decline/cancel three-action model, and validation
//      of the user's input against the requested schema before responding.
//   2. Policy hooks — pre-user policies that can auto-answer or deny a
//      request without it ever reaching the user.
//
// Modes:
//   - Interactive (default): renders the form on the terminal.
//   - Scripted: MRTR_ANSWERS holds a JSON array of predetermined answers,
//     consumed in order — one per elicitation. An entry is either a content
//     object (meaning accept) or { "action": "decline" | "cancel" }.
//     Example: MRTR_ANSWERS='[{"title":"Sync","duration":"30"},{"confirm":true}]'
//
// MRTR_POLICY=autoconfirm enables the demo auto-answer policy.

import * as readline from 'node:readline/promises';
import type { ElicitResult } from '@modelcontextprotocol/client';

type SchemaProperty = {
  type?: string;
  title?: string;
  description?: string;
  enum?: string[];
  default?: unknown;
  minLength?: number;
  maxLength?: number;
  minimum?: number;
  maximum?: number;
  format?: string;
};

type FormParams = {
  message: string;
  requestedSchema: {
    type: 'object';
    properties: Record<string, SchemaProperty>;
    required?: string[];
  };
};

// ---------------------------------------------------------------------------
// Policy hooks: run before the user ever sees the request. A policy returns a
// full response to short-circuit the form, or undefined to pass.
// ---------------------------------------------------------------------------

type Policy = (params: FormParams) => ElicitResult | undefined;

/** Form mode must never collect credentials; deny requests that look like it. */
const denyCredentialSolicitation: Policy = (params) => {
  const suspicious = /password|api[ -]?key|token|secret|credential/i;
  for (const [key, prop] of Object.entries(params.requestedSchema.properties)) {
    const texts = [key, prop.title ?? '', prop.description ?? ''].join(' ');
    if (prop.format === 'password' || suspicious.test(texts)) {
      console.log(`  [policy] declined: field "${key}" appears to solicit credentials`);
      return { action: 'decline' };
    }
  }
  return undefined;
};

/** Demo auto-answer policy: accept pure-confirmation forms (all-boolean) with their defaults. */
const autoConfirm: Policy = (params) => {
  if (process.env['MRTR_POLICY'] !== 'autoconfirm') return undefined;
  const props = Object.entries(params.requestedSchema.properties);
  if (props.length === 0 || !props.every(([, p]) => p.type === 'boolean')) return undefined;
  const content: Record<string, boolean> = {};
  for (const [key, prop] of props) {
    content[key] = typeof prop.default === 'boolean' ? prop.default : true;
  }
  console.log(`  [policy] auto-answered confirmation form: ${JSON.stringify(content)}`);
  return { action: 'accept', content };
};

const policies: Policy[] = [denyCredentialSolicitation, autoConfirm];

// ---------------------------------------------------------------------------
// Validation: check content against the requested schema before responding —
// a correctness concern (the server expects this shape) and a security one.
// ---------------------------------------------------------------------------

function validateField(key: string, prop: SchemaProperty, value: unknown): string | undefined {
  if (prop.enum !== undefined) {
    if (typeof value !== 'string' || !prop.enum.includes(value)) {
      return `"${key}" must be one of: ${prop.enum.join(', ')}`;
    }
    return undefined;
  }
  switch (prop.type) {
    case 'string':
      if (typeof value !== 'string') return `"${key}" must be a string`;
      if (prop.minLength !== undefined && value.length < prop.minLength)
        return `"${key}" must be at least ${prop.minLength} characters`;
      if (prop.maxLength !== undefined && value.length > prop.maxLength)
        return `"${key}" must be at most ${prop.maxLength} characters`;
      return undefined;
    case 'number':
    case 'integer':
      if (typeof value !== 'number' || Number.isNaN(value)) return `"${key}" must be a number`;
      if (prop.type === 'integer' && !Number.isInteger(value)) return `"${key}" must be an integer`;
      if (prop.minimum !== undefined && value < prop.minimum)
        return `"${key}" must be >= ${prop.minimum}`;
      if (prop.maximum !== undefined && value > prop.maximum)
        return `"${key}" must be <= ${prop.maximum}`;
      return undefined;
    case 'boolean':
      return typeof value === 'boolean' ? undefined : `"${key}" must be a boolean`;
    default:
      return `"${key}" has an unsupported schema type: ${prop.type}`;
  }
}

function validateContent(params: FormParams, content: Record<string, unknown>): string[] {
  const errors: string[] = [];
  const { properties, required = [] } = params.requestedSchema;
  for (const key of required) {
    if (content[key] === undefined) errors.push(`"${key}" is required`);
  }
  for (const [key, value] of Object.entries(content)) {
    const prop = properties[key];
    if (prop === undefined) {
      errors.push(`"${key}" is not in the requested schema`);
      continue;
    }
    const error = validateField(key, prop, value);
    if (error !== undefined) errors.push(error);
  }
  return errors;
}

/** Pre-populate defaults, per the schema-driven rendering rules. */
function defaultsOf(params: FormParams): Record<string, unknown> {
  const content: Record<string, unknown> = {};
  for (const [key, prop] of Object.entries(params.requestedSchema.properties)) {
    if (prop.default !== undefined) content[key] = prop.default;
  }
  return content;
}

// ---------------------------------------------------------------------------
// Scripted answers (for smoke tests / headless runs).
// ---------------------------------------------------------------------------

const scriptedAnswers: unknown[] | undefined = process.env['MRTR_ANSWERS']
  ? (JSON.parse(process.env['MRTR_ANSWERS']) as unknown[])
  : undefined;

function nextScriptedAnswer(params: FormParams): ElicitResult {
  const entry = scriptedAnswers!.shift();
  if (entry === undefined) throw new Error('MRTR_ANSWERS ran out of scripted answers');
  const record = entry as Record<string, unknown>;
  if (record['action'] === 'decline' || record['action'] === 'cancel') {
    console.log(`  [scripted] ${record['action']}`);
    return { action: record['action'] as 'decline' | 'cancel' };
  }
  const content = { ...defaultsOf(params), ...record };
  const errors = validateContent(params, content);
  if (errors.length > 0) throw new Error(`Scripted answer failed validation: ${errors.join('; ')}`);
  console.log(`  [scripted] accept: ${JSON.stringify(content)}`);
  return { action: 'accept', content: content as NonNullable<ElicitResult['content']> };
}

// ---------------------------------------------------------------------------
// Interactive form rendering.
// ---------------------------------------------------------------------------

function parseInput(prop: SchemaProperty, raw: string): { ok: true; value: unknown } | { ok: false } {
  if (prop.enum !== undefined || prop.type === 'string') return { ok: true, value: raw };
  if (prop.type === 'number' || prop.type === 'integer') {
    const n = Number(raw);
    return Number.isNaN(n) ? { ok: false } : { ok: true, value: n };
  }
  if (prop.type === 'boolean') {
    if (/^(y|yes|true)$/i.test(raw)) return { ok: true, value: true };
    if (/^(n|no|false)$/i.test(raw)) return { ok: true, value: false };
    return { ok: false };
  }
  return { ok: false };
}

async function renderForm(params: FormParams): Promise<ElicitResult> {
  const rl = readline.createInterface({ input: process.stdin, output: process.stdout });
  try {
    console.log(`\n${params.message}`);
    console.log('(enter a value, or !decline / !cancel; blank accepts the default)');
    const content: Record<string, unknown> = {};
    const required = params.requestedSchema.required ?? [];
    for (const [key, prop] of Object.entries(params.requestedSchema.properties)) {
      const label = prop.title ?? key; // title is the display label
      if (prop.description) console.log(`  ${prop.description}`); // description is help text
      const hints = [
        prop.enum ? `options: ${prop.enum.join('/')}` : prop.type,
        prop.default !== undefined ? `default: ${prop.default}` : undefined,
      ].filter(Boolean);
      for (;;) {
        const raw = (await rl.question(`  ${label} (${hints.join(', ')}): `)).trim();
        if (raw === '!decline') return { action: 'decline' };
        if (raw === '!cancel') return { action: 'cancel' };
        if (raw === '') {
          if (prop.default !== undefined) content[key] = prop.default;
          else if (required.includes(key)) {
            console.log(`  "${label}" is required.`);
            continue;
          }
          break;
        }
        const parsed = parseInput(prop, raw);
        const error = parsed.ok ? validateField(key, prop, parsed.value) : `"${label}" is not a valid ${prop.type}`;
        if (error !== undefined) {
          console.log(`  ${error}`);
          continue;
        }
        content[key] = (parsed as { value: unknown }).value;
        break;
      }
    }
    const errors = validateContent(params, content);
    if (errors.length > 0) {
      // Should not happen after per-field checks, but never send invalid data.
      console.log(`  Input failed validation: ${errors.join('; ')}`);
      return { action: 'cancel' };
    }
    return { action: 'accept', content: content as NonNullable<ElicitResult['content']> };
  } finally {
    rl.close();
  }
}

// ---------------------------------------------------------------------------
// The handler itself. Accepts either a bare embedded request from an
// `inputRequests` map or the JSON-RPC request the SDK's native driver
// dispatches — both carry { method, params }.
// ---------------------------------------------------------------------------

export async function handleInputRequest(request: unknown): Promise<ElicitResult> {
  const { method, params } = request as { method?: string; params?: Record<string, unknown> };
  if (method !== 'elicitation/create' || params === undefined) {
    // This host supports elicitation only (sampling is deprecated; roots n/a).
    throw new Error(`Unsupported input request: ${method}`);
  }

  if ((params['mode'] ?? 'form') === 'url') {
    // URL mode: display the full URL, never pre-fetch, never auto-navigate.
    // This console host only reports consent; a real host opens the system
    // browser after explicit user approval.
    console.log(`  [url-mode] ${params['message']}`);
    console.log(`  [url-mode] target: ${params['url']} — open it in your browser, then continue.`);
    return { action: 'accept' };
  }

  const form = params as unknown as FormParams;
  console.log(`  elicitation: ${form.message}`);

  for (const policy of policies) {
    const verdict = policy(form);
    if (verdict !== undefined) return verdict; // answered or denied by policy
  }

  if (scriptedAnswers !== undefined) return nextScriptedAnswer(form);
  return renderForm(form);
}
