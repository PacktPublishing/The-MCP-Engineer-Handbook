// Find-and-replace as a logical operation, with preview-before-execute in
// both of the chapter's variants: a dry_run parameter on the combined tool,
// and a separate preview tool whose token gates execution. Failure results
// are ordinary text that states what did not happen and what to do instead —
// positive instructions the model can act on.

import { McpServer } from '@modelcontextprotocol/server';
import { serveStdio } from '@modelcontextprotocol/server/stdio';
import { z } from 'zod';
import { DocumentStore } from './documentStore.js';
import { buildPlan, renderExecuted, renderPreview } from './findReplacePlanner.js';
import { PreviewStore } from './previewStore.js';

const documents = new DocumentStore();
const previews = new PreviewStore();
const ttlMinutes = PreviewStore.timeToLiveMs / 60_000;

// serveStdio owns the era decision: modern (2026-07-28) openings get a modern
// instance, legacy openings a legacy one — the same factory serves both.
serveStdio(() => {
  const server = new McpServer({ name: 'preview-before-execute', version: '0.1.0' });

  const text = (value: string) => ({ content: [{ type: 'text' as const, text: value }] });

  // --- Variant 1: one tool with a dry_run parameter ---------------------------

  server.registerTool(
    'find_and_replace',
    {
      description:
        "Replaces every occurrence of 'find' with 'replace' across all documents, applied as one atomic edit batch. " +
        'This is a write operation that modifies documents. ' +
        'Set dry_run to true to see exactly which lines would change and which internal operations would run, without changing anything.',
      inputSchema: z.object({
        find: z.string().describe('Exact text to search for (case-sensitive).'),
        replace: z.string().describe('Text to replace each occurrence with.'),
        dry_run: z
          .boolean()
          .default(false)
          .describe('If true, make no changes; return the would-be changes and the internal operations instead.'),
      }),
    },
    async ({ find, replace, dry_run }) => {
      if (find.length === 0) {
        return text("No changes were made: 'find' must be non-empty. Provide the exact text to search for.");
      }

      const plan = buildPlan(documents, find, replace);
      if (plan.documents.length === 0) {
        return text(
          `No occurrences of "${find}" found in any document, so there is nothing to replace. ` +
            'No changes were made. Call list_documents and read_document to inspect the current content.',
        );
      }

      if (dry_run) {
        return text(
          'Dry run — no changes were made.\n\n' +
            renderPreview(plan) +
            '\nTo apply exactly these changes, call find_and_replace again with the same find and replace and dry_run: false.',
        );
      }

      const staleDocument = documents.tryApply(plan);
      if (staleDocument !== null) {
        return text(
          `No changes were made: document "${staleDocument}" changed while the operation was being prepared. Retry the call.`,
        );
      }

      return text(renderExecuted(plan));
    },
  );

  // --- Variant 2: separate preview and execute tools, linked by a token -------

  server.registerTool(
    'preview_find_and_replace',
    {
      description:
        'Previews a find-and-replace across all documents: which lines would change and which internal operations would run. ' +
        'Makes no changes. Returns a preview_token — passing it to execute_find_and_replace is the only way to apply the changes.',
      inputSchema: z.object({
        find: z.string().describe('Exact text to search for (case-sensitive).'),
        replace: z.string().describe('Text to replace each occurrence with.'),
      }),
    },
    async ({ find, replace }) => {
      if (find.length === 0) {
        return text("No preview was created: 'find' must be non-empty. Provide the exact text to search for.");
      }

      const plan = buildPlan(documents, find, replace);
      if (plan.documents.length === 0) {
        return text(
          `No occurrences of "${find}" found in any document, so there is nothing to replace and no preview token was issued. ` +
            'Call list_documents and read_document to inspect the current content.',
        );
      }

      const token = previews.add(plan);
      return text(
        `Preview ${token} created — no changes have been made yet.\n\n` +
          renderPreview(plan) +
          `\nTo apply these changes, call execute_find_and_replace with preview_token "${token}". ` +
          `The token is single-use and expires in ${ttlMinutes} minutes.`,
      );
    },
  );

  server.registerTool(
    'execute_find_and_replace',
    {
      description:
        'Applies the changes described by a preview created with preview_find_and_replace. ' +
        "Requires that preview's preview_token; call preview_find_and_replace first to review the changes and obtain the token.",
      inputSchema: z.object({
        // Optional in the schema on purpose: a call without the token gets an
        // instructive result steering the model to the preview tool, instead
        // of a generic missing-argument error.
        preview_token: z
          .string()
          .optional()
          .describe('Token returned by preview_find_and_replace for the plan to apply.'),
      }),
    },
    async ({ preview_token }) => {
      if (preview_token === undefined || preview_token.trim() === '') {
        return text(
          'No changes were made. execute_find_and_replace applies a previously previewed plan and needs its preview_token. ' +
            'Call preview_find_and_replace with your find and replace text first — it shows exactly what will change and returns the token to pass here.',
        );
      }

      const outcome = previews.redeem(preview_token);
      if (outcome.result === 'not_found') {
        return text(
          `No changes were made. Preview token "${preview_token}" was not found — tokens are single-use and expire after ` +
            `${ttlMinutes} minutes. Call preview_find_and_replace to create a fresh preview and token.`,
        );
      }
      if (outcome.result === 'expired') {
        return text(
          `No changes were made. Preview "${preview_token}" has expired (previews are valid for ` +
            `${ttlMinutes} minutes). Call preview_find_and_replace again to re-review the changes and get a fresh token.`,
        );
      }

      const staleDocument = documents.tryApply(outcome.plan);
      if (staleDocument !== null) {
        return text(
          `No changes were made. Document "${staleDocument}" was modified after preview "${preview_token}" was created, ` +
            'so the previewed plan no longer matches the current content. ' +
            'Call preview_find_and_replace again to review the changes against the current documents.',
        );
      }

      return text(`Preview ${preview_token} executed. ` + renderExecuted(outcome.plan));
    },
  );

  // --- Read-only helpers so the corpus can be inspected -----------------------

  server.registerTool(
    'list_documents',
    {
      description: 'Lists all documents in the store. Read-only.',
      inputSchema: z.object({}),
    },
    async () => text(documents.all().map((d) => `${d.id} (${d.lines.length} lines)`).join('\n')),
  );

  server.registerTool(
    'read_document',
    {
      description: 'Returns the full text of one document. Read-only.',
      inputSchema: z.object({
        document_id: z.string().describe('Document id, e.g. "onboarding-guide".'),
      }),
    },
    async ({ document_id }) => {
      const doc = documents.get(document_id);
      return text(
        doc === undefined
          ? `Document "${document_id}" was not found. Call list_documents to see the available ids.`
          : doc.lines.join('\n'),
      );
    },
  );

  return server;
});
